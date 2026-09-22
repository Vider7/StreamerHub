using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamerHub;

public sealed class UpdateService : IDisposable
{
    readonly UpdaterConfig _cfg;
    readonly Action<object> _broadcast;
    readonly HttpClient _http;
    readonly object _gate = new();
    System.Threading.Timer? _timer;
    bool _active;
    string _status = "idle";        // idle|checking|downloading|available|latest|error|not-configured
    string _latest = "";
    string _readyVersion = "";

    public event Action? QuitRequested;

    public string CurrentVersion { get; }
    public string Status => Volatile.Read(ref _status);
    public string LatestVersion => _latest;
    public bool Ready => _readyVersion.Length > 0;

    const string FallbackVersion = "1.5.0";

    public static string ReadCurrentVersion()
    {
        try
        {
            var path = Path.Combine(AppPaths.BaseDir, "version.txt");
            if (File.Exists(path))
            {
                var v = File.ReadAllText(path).Trim();
                if (v.Length > 0) return v;
            }
        }
        catch { }
        return FallbackVersion;
    }

    public UpdateService(UpdaterConfig cfg, Action<object> broadcast)
    {
        _cfg = cfg;
        _broadcast = broadcast;
        CurrentVersion = ReadCurrentVersion();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamerHub/" + CurrentVersion);
    }

    public void Start()
    {
        var stage = Path.Combine(AppPaths.BaseDir, "~updates");
        try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }

        if (!_cfg.Enabled || string.IsNullOrWhiteSpace(_cfg.Feed))
        {
            Log.Info("updater: off - set Updater.Feed in Config.json to a version.json url to enable");
            SetState("not-configured");
            return;
        }
        var hours = Math.Clamp(_cfg.AutoCheckHours, 0.25, 168);
        Log.Info($"updater: checking {_cfg.Feed} every {hours:0.#}h");
        _timer = new System.Threading.Timer(_ => CheckNow(), null, TimeSpan.FromSeconds(15), TimeSpan.FromHours(hours));
    }

    public void CheckNow() { _ = CheckCoreAsync(); }

    async Task CheckCoreAsync()
    {
        if (!_cfg.Enabled || string.IsNullOrWhiteSpace(_cfg.Feed))
        {
            SetState("not-configured");
            return;
        }
        lock (_gate)
        {
            if (_active) return;
            _active = true;
        }
        SetState("checking");
        try
        {
            var body = (await _http.GetStringAsync(_cfg.Feed)).TrimStart('\uFEFF');
            var root = JsonDocument.Parse(body).RootElement;
            var latest = root.TryGetProperty("version", out var pv) ? (pv.GetString() ?? "").Trim() : "";
            var url = root.TryGetProperty("url", out var pu) ? (pu.GetString() ?? "").Trim() : "";
            var sha = root.TryGetProperty("sha256", out var ph) ? (ph.GetString() ?? "").Trim().ToLowerInvariant() : "";
            if (latest.Length == 0)
            {
                Log.Warn("updater: feed has no version field");
                SetState("error");
                return;
            }
            latest = latest.TrimStart('v', 'V');
            _latest = latest;

            if (CompareVersions(latest, CurrentVersion) <= 0)
            {
                Log.Info("updater: up to date on v" + CurrentVersion);
                _readyVersion = "";
                SetState("latest");
                return;
            }
            if (url.Length == 0 || sha.Length != 64)
            {
                Log.Warn("updater: v" + latest + " exists but the feed lacks url/sha256, nothing to do");
                SetState("error");
                return;
            }
            await DownloadAndStageAsync(latest, url, sha);
        }
        catch (Exception ex)
        {
            Log.Warn("updater: check failed: " + ex.Message);
            _readyVersion = "";
            SetState("error");
        }
        finally
        {
            lock (_gate) _active = false;
        }
    }

    async Task DownloadAndStageAsync(string version, string url, string expectedSha)
    {
        var stageRoot = Path.Combine(AppPaths.BaseDir, "~updates");
        Directory.CreateDirectory(stageRoot);
        var zipPath = Path.Combine(stageRoot, version + ".zip");
        var stageDir = Path.Combine(stageRoot, version);

        SetState("downloading");
        Log.Info("updater: downloading v" + version + " (" + url + ")");
        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                using var sha = SHA256.Create();
                using (var fs = File.Create(zipPath))
                using (var src = await resp.Content.ReadAsStreamAsync())
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        sha.TransformBlock(buf, 0, n, null, 0);
                        await fs.WriteAsync(buf, 0, n);
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                }
                var hex = BitConverter.ToString(sha.Hash ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
                if (hex != expectedSha)
                {
                    throw new InvalidDataException($"sha256 mismatch (get {hex[..12]}..., want {expectedSha[..12]}...)");
                }
            }

            if (Directory.Exists(stageDir)) TryDeleteDir(stageDir);
            ZipFile.ExtractToDirectory(zipPath, stageDir);
            File.Delete(zipPath);
            if (!File.Exists(Path.Combine(stageDir, "StreamerHub.exe")))
            {
                throw new InvalidDataException("staged archive contains no StreamerHub.exe");
            }

            _readyVersion = version;
            _latest = version;
            Log.Info("updater: v" + version + " downloaded, hash verified, staged - waiting for approval");
            SetState("available");
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }

    public int ApplyNow()
    {
        var version = _readyVersion;
        if (version.Length == 0) return 0;
        var stage = Path.Combine(AppPaths.BaseDir, "~updates", version);
        var exePath = Path.Combine(stage, "StreamerHub.exe");
        if (!File.Exists(exePath))
        {
            Log.Warn("updater: staged exe missing, cannot apply");
            return 1;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = stage,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(AppPaths.BaseDir);
            psi.ArgumentList.Add(version);
            Process.Start(psi);
            Log.Info("updater: applying v" + version + " - exiting for restart");
            QuitRequested?.Invoke();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warn("updater: apply failed: " + ex.Message);
            return 1;
        }
    }

    public static int RunApplyUpdate(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || args.Length <= i + 1) return 1;
        var appDir = args[i + 1];
        var version = args.Length > i + 2 ? args[i + 2] : "";

        try { Log.Init(appDir); } catch { }

        WaitForOldInstancesExit(appDir);

        var stage = Path.Combine(appDir, "~updates", version);
        if (!Directory.Exists(stage))
        {
            Log.Warn("updater apply: stage missing: " + stage);
            return 2;
        }

        // Swap the exe first: if it is still locked, abort before touching anything
        // else, so a failed update leaves the old build fully intact (no half-swapped
        // build, and version.txt keeps reporting the old version so it retries).
        if (!TryCopyAtomic(Path.Combine(stage, "StreamerHub.exe"), Path.Combine(appDir, "StreamerHub.exe")))
        {
            Log.Warn("updater apply: exe still locked, aborting - old build untouched");
            return 3;
        }

        var failed = CopyTree(stage, appDir);
        if (failed > 0) Log.Warn("updater apply: " + failed + " file(s) could not be replaced");

        var exe = Path.Combine(appDir, "StreamerHub.exe");
        if (File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = appDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("updater apply: relaunch failed: " + ex.Message);
            }
        }
        Log.Info("updater apply: done");
        return failed > 0 ? 3 : 0;
    }

    static void WaitForOldInstancesExit(string appDir)
    {
        List<Process> Siblings()
        {
            var found = new List<Process>();
            var self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("StreamerHub"))
            {
                if (p.Id == self) continue;
                try
                {
                    var dir = Path.GetDirectoryName(p.MainModule?.FileName ?? "");
                    if (dir != null && dir.Equals(appDir, StringComparison.OrdinalIgnoreCase)) found.Add(p);
                    else p.Dispose();
                }
                catch { try { p.Dispose(); } catch { } }
            }
            return found;
        }

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var live = Siblings();
            if (live.Count == 0) { Log.Info("updater apply: old instance exited"); return; }
            foreach (var p in live) p.Dispose();
            Thread.Sleep(500);
        }
        foreach (var p in Siblings())
        {
            try { Log.Warn("updater apply: old instance did not exit, killing pid " + p.Id); p.Kill(); }
            catch (Exception ex) { Log.Warn("updater apply: kill failed: " + ex.Message); }
            finally { try { p.Dispose(); } catch { } }
        }
        Thread.Sleep(2000);
    }

    static int CopyTree(string source, string destRoot)
    {
        var failed = 0;
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        files.Sort((a, b) => RankFile(source, a).CompareTo(RankFile(source, b)));
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(source, file);
            if (IsExcluded(rel)) continue;
            var dest = Path.Combine(destRoot, rel);
            try
            {
                if (!TryCopyAtomic(file, dest)) failed++;
            }
            catch (Exception ex)
            {
                Log.Warn("updater apply: could not copy " + rel + ": " + ex.Message);
                failed++;
            }
        }
        return failed;
    }

    static bool IsExcluded(string rel)
    {
        var sep = Path.DirectorySeparatorChar;
        if (rel.Equals("StreamerHub.exe", StringComparison.OrdinalIgnoreCase)) return true; // swapped first by the caller
        if (rel.Equals("Config.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.Equals("cookies.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.Equals("HOW TO RUN.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.StartsWith("logs" + sep, StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.StartsWith("~updates" + sep, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static int RankFile(string source, string file)
    {
        var rel = Path.GetRelativePath(source, file);
        return rel.Equals("version.txt", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    static bool TryCopyAtomic(string src, string dest)
    {
        var dir = Path.GetDirectoryName(dest);
        if (dir != null) Directory.CreateDirectory(dir);
        var tmp = dest + ".new";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                File.Copy(src, tmp, true);
                File.Move(tmp, dest, true);
                return true;
            }
            catch (IOException) { Thread.Sleep(400); }
            catch (UnauthorizedAccessException) { Thread.Sleep(400); }
        }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        return false;
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    static int CompareVersions(string a, string b)
    {
        var pa = SplitParts(a);
        var pb = SplitParts(b);
        for (var i = 0; i < 3; i++)
        {
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        }
        return 0;
    }

    static int[] SplitParts(string v)
    {
        var parts = v.Split('.');
        var result = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            if (int.TryParse(parts[i].Trim(), out var n)) result[i] = n;
        }
        return result;
    }

    void SetState(string s)
    {
        Volatile.Write(ref _status, s);
        try { _broadcast(new { type = "update", current = CurrentVersion, status = s, latest = _latest, ready = Ready }); }
        catch { }
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        try { _http.Dispose(); } catch { }
    }
}