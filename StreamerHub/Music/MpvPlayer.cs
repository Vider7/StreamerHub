using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace StreamerHub;

public sealed class MpvPlayer : IDisposable
{
    const string IpcName = "streamerhub-mpv";

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PeekNamedPipe(SafeHandle hPipe, byte[]? lpBuffer, uint nBufferSize, out uint lpBytesRead, out uint lpTotalBytesAvail, out uint lpBytesLeftThisMessage);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const int SwHide = 0;

    uint AvailableBytes(NamedPipeClientStream pipe)
    {
        try
        {
            return PeekNamedPipe(pipe.SafePipeHandle, null, 0, out _, out var avail, out _) ? avail : 0;
        }
        catch
        {
            return 0;
        }
    }

    readonly string _exe;
    readonly string _device;
    readonly CancellationTokenSource _runCts = new();
    readonly ManualResetEventSlim _wake = new(false);

    Process? _proc;
    NamedPipeClientStream? _pipe;
    volatile bool _available;
    volatile bool _disposed;

    int _volume;
    string _af = "";
    string? _lastUrl;
    string? _lastResume;
    int _restartCount;
    DateTime _lastRestartUtc = DateTime.MinValue;
    bool _prevPause;
    bool _volumeWarned;
    bool _pauseWarned;

    // pending work for the single IPC worker thread
    volatile string? _pendingUrl;
    volatile string? _pendingSeekTo;
    volatile bool _pendingPause;
    volatile bool _pauseRequested;
    int? _pendingVolume;
    volatile string? _pendingAf;
    volatile bool _pendingStop;
    volatile bool _inTrack;
    volatile bool _startPlaying = true;

    readonly byte[] _readBuf = new byte[65536];
    readonly List<byte> _lineBuf = new();

    public event Action? Ended;
    public event Action? Failed;
    public event Action? Retrying;
    public event Action? PositionChanged;

    public bool Available => _available;
    public string? CurrentId { get; set; }
    public double Position { get; private set; }
    public bool Paused { get; private set; }
    public double Duration { get; private set; }

    public MpvPlayer(string exe, string device)
    {
        _exe = exe;
        _device = device ?? "";
    }

    public static string Locate(string configured, string fallbackExe)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(AppPaths.BaseDir, "tools", "mpv", "mpv.exe"));
        candidates.Add(Path.Combine(AppPaths.BaseDir, "tools", "mpv.exe"));
        candidates.Add(fallbackExe);
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return fallbackExe;
    }

    public static string BuildAf(double[] eq, bool loudness)
    {
        // Gentle static leveling: slow attack/release so gain never audibly
        // pumps on sparse material (single-pass loudnorm breathed). Makeup is
        // fixed, so quiet passages are lifted without swelling; the limiter
        // only catches peaks transparently.
        var chain = loudness
            ? "acompressor=threshold=-21dB:ratio=2:attack=250:release=1500:makeup=3dB,alimiter=limit=0.891:attack=7:release=100,"
            : "";
        int[] freqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        for (var i = 0; i < freqs.Length; i++)
        {
            var g = (eq != null && eq.Length == MusicConfig.EqBands) ? eq[i] : 0.0;
            chain += "equalizer=f=" + freqs[i] + ":t=q:w=1.1:g=" + g.ToString("0.0", CultureInfo.InvariantCulture);
            if (i < freqs.Length - 1) chain += ",";
        }
        chain += ",aformat=sample_rates=48000";
        return "lavfi=[" + chain + "]";
    }

    public bool Start(int volume, string af)
    {
        _volume = Math.Max(0, Math.Min(100, volume));
        _af = af ?? "";
        KillStaleMpv();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!SpawnCore()) throw new Exception("mpv did not start");
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("mpv launch attempt " + attempt + "/3 failed: " + ex.Message);
                CleanupProc();
                Thread.Sleep(1000);
                if (attempt == 3) return false;
            }
        }
        _available = true;
        ApplyAfNow();
        _ = Task.Run(WorkerLoop);
        Log.Info("mpv audio player ready on " + _exe);
        return true;
    }

    bool SpawnCore()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };
        psi.ArgumentList.Add("--idle=yes");
        psi.ArgumentList.Add("--no-terminal");
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--vo=null");
        psi.ArgumentList.Add("--audio-display=no");
        psi.ArgumentList.Add("--input-media-keys=no");
        psi.ArgumentList.Add("--volume=" + _volume);
        if (_device.Length > 0) psi.ArgumentList.Add("--audio-device=" + _device);
        psi.ArgumentList.Add("--input-ipc-server=" + @"\\.\pipe\" + IpcName);

        try
        {
            _proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("mpv could not start (" + _exe + "): " + ex.Message);
            return false;
        }
        if (_proc == null) return false;
        JobGuard.Adopt(_proc);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        NamedPipeClientStream? pipe = null;
        while (pipe == null && DateTime.UtcNow < deadline)
        {
            try
            {
                var p = new NamedPipeClientStream(".", IpcName, PipeDirection.InOut);
                p.Connect(250);
                pipe = p;
            }
            catch
            {
                if (_proc.HasExited)
                    throw new Exception("mpv exited before opening the IPC pipe");
                Thread.Sleep(250);
            }
        }
        if (pipe == null)
            throw new Exception("mpv IPC pipe never appeared within 10s");
        _pipe = pipe;
        var version = Command("get_property", "mpv-version");
        if (version == null)
            throw new Exception("mpv IPC opened but is not answering (health check failed)");
        Log.Info("mpv IPC healthy: " + version.ToString());
        HideMpvWindow();
        return true;
    }

    void CleanupProc()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    void HideMpvWindow()
    {
        var pid = _proc == null ? 0u : (uint)_proc.Id;
        if (pid == 0) return;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var wpid);
            if (wpid == pid) ShowWindow(hWnd, SwHide);
            return true;
        }, IntPtr.Zero);
    }

    void KillStaleMpv()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_exe)))
            {
                try { p.Kill(); } catch { }
            }
            Thread.Sleep(1000);
        }
        catch { }
    }

    void ApplyAfNow()
    {
        if (string.IsNullOrEmpty(_af)) return;
        try
        {
            Command("set_property", "af", _af);
            Command("set_property", "volume", (double)_volume);
        }
        catch (Exception ex)
        {
            Log.Warn("af apply failed: " + ex.Message);
        }
    }

    public void SetVolume(double v)
    {
        _pendingVolume = Math.Max(0, Math.Min(100, (int)Math.Round(v)));
        _wake.Set();
    }

    public void SetPause(bool paused)
    {
        _pauseRequested = true;
        _pendingPause = paused;
        _wake.Set();
    }

    public void Seek(double seconds)
    {
        _pendingSeekTo = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        _wake.Set();
    }

    public void SetAf(string af)
    {
        _pendingAf = af ?? "";
        _wake.Set();
    }

    public void Play(string? streamUrl) => PlayCore(streamUrl, true);

    public void RetryPlay(string? streamUrl) => PlayCore(streamUrl, false);

    void PlayCore(string? streamUrl, bool resetGuard)
    {
        if (!_available) return;
        _lastUrl = string.IsNullOrEmpty(streamUrl) ? null : streamUrl;
        _lastResume = null;
        _startPlaying = true;
        if (resetGuard)
        {
            _fastFailTries = 0;
            _expectingStop = false;
        }
        if (string.IsNullOrEmpty(streamUrl))
        {
            _pendingStop = true;
        }
        else
        {
            _pendingUrl = streamUrl;
        }
        _wake.Set();
    }

    public void Resume(string? streamUrl, double position, bool playing)
    {
        if (!_available) return;
        _lastUrl = string.IsNullOrEmpty(streamUrl) ? null : streamUrl;
        _lastResume = position.ToString("0.###", CultureInfo.InvariantCulture);
        _startPlaying = playing;
        _fastFailTries = 0;
        _expectingStop = false;
        if (string.IsNullOrEmpty(streamUrl))
        {
            _pendingStop = true;
        }
        else
        {
            _pendingUrl = streamUrl;
        }
        _wake.Set();
    }

    public void StopAudio()
    {
        if (!_available) return;
        _pendingStop = true;
        _wake.Set();
    }

    static readonly TimeSpan FastFailWindow = TimeSpan.FromSeconds(15);
    const int FastFailRetries = 3;
    DateTime _loadedAtUtc;
    int _fastFailTries;
    bool _expectingStop; // true from the moment a loadfile is issued until the
                         // new file shows progress; suppresses counting the
                         // replaced file's stop event as a failure
    string? _pendingResumeSeek;
    int _seekTries;
    DateTime _lastAdvanceUtc = DateTime.UtcNow;
    double? _pendingSeekAfterLoad;

    void EnqueueStop()
    {
        _expectingStop = true;
        _inTrack = false;
        _pendingUrl = null;
        _pendingSeekAfterLoad = null;
        _pendingResumeSeek = null;
        _seekTries = 0;
        _pendingSeekTo = null;
        try { Command("stop"); } catch { }
    }

    // central request/response IPC: write a command, read until its reply
    // arrives; any events mpv pushes meanwhile are funneled to HandleEvent.
    JsonElement? Command(params object[] args)
    {
        if (_pipe == null) throw new InvalidOperationException("mpv ipc not connected");
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command = args, request_id = Interlocked.Increment(ref _reqId) }) + "\n");
        _pipe.Write(payload, 0, payload.Length);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var avail = AvailableBytes(_pipe);
            if (avail > 0)
            {
                var n = _pipe.Read(_readBuf, 0, (int)Math.Min(avail, _readBuf.Length));
                _lineBuf.AddRange(_readBuf.AsSpan(0, n).ToArray());
                while (TryTakeLine(out var line))
                {
                    var msg = ParseLine(line);
                    if (msg != null)
                    {
                        if (msg.RootElement.TryGetProperty("request_id", out var rid) && rid.GetInt32() == _reqId)
                        {
                            if (msg.RootElement.TryGetProperty("error", out var err) && err.GetString() != "success")
                                throw new Exception("mpv error: " + err.GetString());
                            if (msg.RootElement.TryGetProperty("data", out var data))
                                return data.Clone();
                            return JsonDocument.Parse("null").RootElement.Clone();
                        }
                        HandleEvent(msg);
                    }
                    else
                    {
                        Log.Warn("mpv bad line: " + line);
                    }
                }
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        throw new TimeoutException("mpv ipc timeout");
    }

    bool TryTakeLine(out string line)
    {
        line = "";
        for (var i = 0; i < _lineBuf.Count; i++)
        {
            if (_lineBuf[i] != (byte)'\n') continue;
            line = Encoding.UTF8.GetString(_lineBuf.Take(i).ToArray());
            _lineBuf.RemoveRange(0, i + 1);
            return true;
        }
        if (_lineBuf.Count > 1 << 20) _lineBuf.Clear(); // runaway guard
        return false;
    }

    JsonDocument? ParseLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch
        {
            return null;
        }
    }

    int _reqId;

    void HandleEvent(JsonDocument msg)
    {
        if (!msg.RootElement.TryGetProperty("event", out var ev)) return;
        var name = ev.GetString();
        switch (name)
        {
            case "end-file":
                var reason = msg.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : "";
                var endedFile = msg.RootElement.TryGetProperty("file", out var f) ? f.GetString() : null;
                var wasInTrack = _inTrack;
                if (reason == "quit") return;
                if (reason == "stop" && _expectingStop)
                {
                    return;
                }
                if (reason == "stop" && !_expectingStop && (wasInTrack || Duration > 0 || Position > 0))
                {
                    return;
                }
                _inTrack = false;
                bool FastFailFailure()
                {
                    if (_fastFailTries >= FastFailRetries)
                    {
                        Log.Warn("giving up on track after " + _fastFailTries + " fast-fail attempts (reason=" + reason + " endedFile=" + (endedFile ?? "null") + " lastUrl=" + (_lastUrl ?? "null") + ")");
                        Failed?.Invoke();
                    }
                    else
                    {
                        _fastFailTries++;
                        Retrying?.Invoke();
                    }
                    return true;
                }
                if (reason == "error" && _expectingStop && DateTime.UtcNow - _loadedAtUtc < FastFailWindow && _lastUrl != null)
                {
                    if (FastFailFailure()) break;
                }
                if ((reason == "stop" || reason == "error") && !_expectingStop
                    && DateTime.UtcNow - _loadedAtUtc < FastFailWindow && _lastUrl != null)
                {
                    if (!string.IsNullOrEmpty(endedFile) && _lastUrl != null && endedFile != _lastUrl) break;
                    FastFailFailure();
                    break;
                }
                if (reason == "eof")
                {
                    if (Duration > 0 && Position < Duration - 5)
                    {
                        FastFailFailure();
                    }
                    else
                    {
                        Ended?.Invoke();
                    }
                }
                else if (reason == "error") Failed?.Invoke();
                break;
            case "playback-error":
                Failed?.Invoke();
                break;
        }
    }

    void WorkerLoop()
    {
        while (!_disposed && !_runCts.IsCancellationRequested)
        {
            try
            {
                if (_proc == null || _proc.HasExited)
                {
                    Log.Warn("mpv process gone; relaunching");
                    RestartCore();
                }
                HideMpvWindow();

                if (_pendingAf != null)
                {
                    var next = _pendingAf ?? "";
                    _pendingAf = null;
                    if (next != _af)
                    {
                        _af = next;
                        if (!string.IsNullOrEmpty(_af))
                        {
                            Command("set_property", "af", _af);
                            Command("set_property", "volume", (double)_volume);
                        }
                    }
                }
                if (_pendingVolume is int vol)
                {
                    try
                    {
                        Command("set_property", "volume", (double)vol);
                        _volume = vol;
                        _pendingVolume = null;
                        _volumeWarned = false;
                    }
                    catch
                    {
                        if (!_volumeWarned) { _volumeWarned = true; Log.Warn("volume commands failing, will keep retrying"); }
                    }
                }
                if (_pauseRequested)
                {
                    try
                    {
                        Command("set_property", "pause", _pendingPause);
                        _pauseRequested = false;
                        _pauseWarned = false;
                    }
                    catch (Exception ex)
                    {
                        if (!_pauseWarned) { _pauseWarned = true; Log.Warn("pause commands failing, will keep retrying: " + ex.Message); }
                    }
                }
                if (_pendingStop)
                {
                    _pendingStop = false;
                    EnqueueStop();
                }
                if (_pendingUrl is { } url)
                {
                    _pendingUrl = null;
                    StartTrack(url);
                }
                if (_pendingSeekTo is { } seekStr)
                {
                    _pendingSeekTo = null;
                    if (Duration > 0)
                    {
                        _pendingResumeSeek = seekStr;
                        _seekTries = 0;
                    }
                    else
                    {
                        _pendingSeekAfterLoad = double.Parse(seekStr, CultureInfo.InvariantCulture);
                    }
                }

                var lastPos = Position;
                if (_inTrack)
                {
                    try
                    {
                        var pos = Command("get_property", "time-pos");
                        var pause = Command("get_property", "pause");
                        if (pos is JsonElement pe && pe.ValueKind == JsonValueKind.Number)
                        {
                            Position = pe.GetDouble();
                        }
                        if (pause is JsonElement pp && (pp.ValueKind == JsonValueKind.True || pp.ValueKind == JsonValueKind.False))
                        {
                            Paused = pp.GetBoolean();
                        }
                        if (Duration <= 0)
                        {
                            var dur = Command("get_property", "duration");
                            if (dur is JsonElement de && de.ValueKind == JsonValueKind.Number)
                                Duration = de.GetDouble();
                        }
                    }
                    catch
                    {
                    }
                    if (Math.Abs(Position - lastPos) > 1e-6)
                    {
                        _lastAdvanceUtc = DateTime.UtcNow;
                    }
                    if (_expectingStop && (Duration > 0 || Position > 0)
                        && DateTime.UtcNow - _loadedAtUtc > TimeSpan.FromMilliseconds(2500))
                    {
                        _expectingStop = false;
                        _fastFailTries = 0;
                    }
                    if (_pendingSeekAfterLoad is { } ps && Duration > 0)
                    {
                        _pendingSeekAfterLoad = null;
                        _pendingResumeSeek = ps.ToString("0.###", CultureInfo.InvariantCulture);
                        _seekTries = 0;
                    }
                    if (_expectingStop && !Paused && DateTime.UtcNow - _loadedAtUtc > TimeSpan.FromSeconds(10)
                        && Duration <= 0 && Position <= 0)
                    {
                        _expectingStop = false;
                        if (_fastFailTries >= FastFailRetries)
                        {
                            Log.Warn("no progress after " + (DateTime.UtcNow - _loadedAtUtc).TotalSeconds.ToString("0") + "s, giving up on track");
                            Failed?.Invoke();
                        }
                        else
                        {
                            _fastFailTries++;
                            Retrying?.Invoke();
                        }
                    }
                    var sinceLoad = DateTime.UtcNow - _loadedAtUtc;
                    void Stall(string what)
                    {
                        _expectingStop = false;
                        if (_fastFailTries >= FastFailRetries)
                        {
                            Log.Warn(what + ", giving up on track");
                            Failed?.Invoke();
                        }
                        else
                        {
                            _fastFailTries++;
                            Retrying?.Invoke();
                        }
                    }
                    if (_inTrack && !Paused && Duration > 0 && Position <= 0.5 && sinceLoad > TimeSpan.FromSeconds(20))
                    {
                        Stall("file loaded but stream produced no audio");
                    }
                    else if (_inTrack && !Paused && Duration > 1 && Position > 0.5
                        && DateTime.UtcNow - _lastAdvanceUtc > TimeSpan.FromSeconds(30))
                    {
                        Stall("playback froze for 30s");
                    }
                    var pauseChanged = Paused != _prevPause;
                    _prevPause = Paused;
                    if (Math.Abs(Position - lastPos) > 1e-6 || pauseChanged) PositionChanged?.Invoke();
                    if (_pendingResumeSeek is { } rs)
                    {
                        _seekTries++;
                        try
                        {
                            Command("set_property", "time-pos", rs);
                            var tp = Command("get_property", "time-pos");
                            if (tp is JsonElement te && te.ValueKind == JsonValueKind.Number
                                && te.GetDouble() >= double.Parse(rs, CultureInfo.InvariantCulture) - 5.0)
                            {
                                _pendingResumeSeek = null;
                                _seekTries = 0;
                            }
                        }
                        catch
                        {
                        }
                        if (_seekTries >= 20)
                        {
                            Log.Warn("resume seek to " + rs + " never took; giving up");
                            _pendingResumeSeek = null;
                            _seekTries = 0;
                        }
                    }
                }
                else if (Paused || Duration != 0)
                {
                    Paused = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("mpv worker error: " + ex.Message);
                Thread.Sleep(500);
            }
            _wake.Wait(1000);
            _wake.Reset();
        }
    }

    void StartTrack(string url)
    {
        try
        {
            if (string.IsNullOrEmpty(url)) return;
            Command("loadfile", url, "replace");
            _loadedAtUtc = DateTime.UtcNow;
            _expectingStop = true;
            _inTrack = true;
            _lastAdvanceUtc = DateTime.UtcNow;
            _pendingSeekAfterLoad = null;
            _pendingResumeSeek = null;
            _seekTries = 0;
            _pendingSeekTo = null;
            Duration = 0;
            Position = 0;
            if (_lastResume is { } seekStr)
            {
                Command("set_property", "pause", true);
                _pendingResumeSeek = seekStr;
            }
            Command("set_property", "pause", !_startPlaying);
            HideMpvWindow();
            PositionChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("loadfile failed: " + ex.Message);
            _inTrack = false;
            Failed?.Invoke();
        }
    }

    void RestartCore()
    {
        lock (_restartGate)
        {
            if (_disposed) return;
            if ((DateTime.UtcNow - _lastRestartUtc).TotalSeconds < 2) return;
            _lastRestartUtc = DateTime.UtcNow;
            if (++_restartCount > 3)
            {
                Log.Warn("mpv keeps failing; stopping the audio player");
                _available = false;
                CleanupProc();
                Failed?.Invoke();
                return;
            }
            Log.Warn("restarting mpv audio player");
            CleanupProc();
            try
            {
                if (!SpawnCore()) return;
                ApplyAfNow();
            }
            catch
            {
                if (_available) Failed?.Invoke();
                return;
            }
            if (_lastUrl != null)
            {
                _pendingUrl = _lastUrl;
                _pendingStop = false;
            }
        }
    }

    readonly object _restartGate = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _available = false;
        _wake.Set();
        _runCts.Cancel();
        Thread.Sleep(150);
        CleanupProc();
    }
}