using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamerHub;

public sealed class YoutubeResolver
{
    readonly string _ytd;
    readonly MusicConfig _cfg;
    readonly ConcurrentDictionary<string, (DateTime At, Task<List<TrackResult>> Task)> _searchCache = new();
    const int SearchCacheTtlSeconds = 300;
    const int SearchCacheMax = 96;

    public YoutubeResolver(MusicConfig cfg)
    {
        _cfg = cfg;
        _ytd = LocateTool(cfg.YtDlpPath, "yt-dlp");
        Log.Info("youtube resolver tool: " + _ytd);
        _ = WarmAutoKeyAsync();
    }

    // The player key is one shared public value baked into every YouTube
    // page load, not a per-user secret. Pick it up ourselves so nobody has
    // to copy-paste anything; a valid manual key still wins when set.
    static string? _autoKey;
    static DateTime _autoKeyAt;
    static readonly SemaphoreSlim _keyGate = new(1, 1);
    static readonly HttpClient PageHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    const string PageUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    static async Task WarmAutoKeyAsync()
    {
        try { await AutoKeyAsync(); } catch { }
    }

    static async Task<string?> AutoKeyAsync()
    {
        if (!string.IsNullOrEmpty(_autoKey) && DateTime.UtcNow - _autoKeyAt < TimeSpan.FromHours(24))
            return _autoKey;
        await _keyGate.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_autoKey) && DateTime.UtcNow - _autoKeyAt < TimeSpan.FromHours(24))
                return _autoKey;
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/");
            req.Headers.Add("User-Agent", PageUA);
            using var resp = await PageHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn("auto key: youtube page http " + (int)resp.StatusCode);
                return null;
            }
            var html = await resp.Content.ReadAsStringAsync();
            var m = Regex.Match(html, "\"INNERTUBE_API_KEY\"\\s*:\\s*\"([A-Za-z0-9_-]{20,})\"");
            if (!m.Success)
            {
                Log.Warn("auto key: not found in youtube page");
                return null;
            }
            _autoKey = m.Groups[1].Value;
            _autoKeyAt = DateTime.UtcNow;
            Log.Info("auto key: picked up from youtube page");
            return _autoKey;
        }
        catch (Exception ex)
        {
            Log.Warn("auto key fetch failed: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    void AddAuthArgs(List<string> args)
    {
        var file = _cfg.YtDlpCookiesFile;
        if (string.IsNullOrWhiteSpace(file))
        {
            var auto = Path.Combine(Directory.GetCurrentDirectory(), "cookies.txt");
            if (File.Exists(auto)) file = auto;
            else return;
        }
        else
        {
            if (!Path.IsPathRooted(file)) file = Path.Combine(Directory.GetCurrentDirectory(), file);
        }
        if (!file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("cookies file must be a .txt, ignoring: " + file);
            return;
        }
        if (File.Exists(file)) { args.Add("--cookies"); args.Add(file); }
        else Log.Warn("cookies file not found, ignoring: " + file);
    }

    public static string LocateTool(string configured, string fallback)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(AppPaths.BaseDir, "tools", fallback + ".exe"));
        candidates.Add(fallback);
        foreach (var c in candidates)
        {
            if (File.Exists(c) || Directory.Exists(Path.GetDirectoryName(c))) return c;
        }
        return fallback;
    }

    public bool ToolFound => File.Exists(_ytd) || CommandOnPath(_ytd);

    static bool CommandOnPath(string name)
    {
        var env = Environment.GetEnvironmentVariable("PATH") ?? "";
        return env.Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && Directory.Exists(p))
            .Any(p => File.Exists(Path.Combine(p, name)));
    }

    public async Task<List<TrackResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var key = query.Trim().ToLowerInvariant();
        if (key.Length == 0) return new List<TrackResult>();
        // Cancellable searches (dashboard typing) run private and never touch
        // the cache: cancelling a shared task would kill other waiters, and a
        // cancelled/failed task must never be served to the next identical
        // query. Only fire-and-forget callers share cached runs.
        if (ct.CanBeCanceled) return await RunSearchAsync(query, ct);
        if (_searchCache.TryGetValue(key, out var entry)
            && DateTime.UtcNow - entry.At < TimeSpan.FromSeconds(SearchCacheTtlSeconds))
        {
            Log.Info("search cache hit: " + query.Trim());
            return await entry.Task;
        }
        var fresh = _searchCache.GetOrAdd(key, _ => (DateTime.UtcNow, RunSearchAsync(query, CancellationToken.None)));
        if (_searchCache.Count > SearchCacheMax)
        {
            foreach (var k in _searchCache.Keys.Take(32)) _searchCache.TryRemove(k, out _);
        }
        try
        {
            var results = await fresh.Task;
            if (results.Count == 0) _searchCache.TryRemove(key, out _);
            return results;
        }
        catch
        {
            _searchCache.TryRemove(key, out _);
            throw;
        }
    }

    async Task<List<TrackResult>> RunSearchAsync(string query, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var args = new List<string> { "--no-warnings", "--flat-playlist", "-J", "ytsearch15:" + query };
        AddAuthArgs(args);
        var (code, stdout, stderr) = await RunAsync(args, 60, ct);
        if (code != 0)
        {
            Log.Warn("search failed in " + sw.ElapsedMilliseconds + "ms: " + stderr.Trim());
            return new List<TrackResult>();
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) return new List<TrackResult>();
            var list = new List<TrackResult>();
            foreach (var e in entries.EnumerateArray())
            {
                if (!e.TryGetProperty("id", out var idNode)) continue;
                var id = idNode.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var title = e.TryGetProperty("title", out var t) ? t.GetString() : "";
                var duration = e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;
                var channel = e.TryGetProperty("channel", out var ch) ? ch.GetString() : "";
                list.Add(new TrackResult { Id = id!, Title = title ?? "untitled", Duration = duration, Channel = channel ?? "" });
            }
            Log.Info("search '" + query.Trim() + "' gave " + list.Count + " results in " + sw.ElapsedMilliseconds + "ms");
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("search parse failed in " + sw.ElapsedMilliseconds + "ms: " + ex.Message);
            return new List<TrackResult>();
        }
    }

    readonly ConcurrentDictionary<string, Task<string?>> _resolveInflight = new();

    public Task<string?> ResolveAudioUrlAsync(string id, CancellationToken ct = default)
    {
        // Rapid skips fire playback + precache + prewarm resolves for the same
        // id at once; share one yt-dlp run instead of racing duplicates.
        if (ct.CanBeCanceled)
            return ResolveAudioUrlCoreAsync(id, ct);
        var task = _resolveInflight.GetOrAdd(id, key =>
            ResolveAudioUrlCoreAsync(key, CancellationToken.None));
        task.ContinueWith(_ => _resolveInflight.TryRemove(new KeyValuePair<string, Task<string?>>(id, task)),
            TaskContinuationOptions.ExecuteSynchronously);
        return task;
    }

    async Task<string?> ResolveAudioUrlCoreAsync(string id, CancellationToken ct)
    {
        // Fast path: one direct player-API call (~0.15s) instead of a full
        // yt-dlp run (seconds). Needs a player key; the auto key from the
        // YouTube page is picked up automatically, a config key is an
        // optional override. Any failure falls through to yt-dlp, and a
        // bad-URL streak trips a breaker so one failure cannot cost mpv
        // three attempts in a row.
        // OFF until a full end-to-end play is proven with the fallback and
        // breaker in place. Flip this to true for the manual test; leave it
        // false until the logs show a real track playing through.
        // A field, not a const: the whole fast path must sit behind this one
        // check, including the config key, or a configured key sneaks past it.
        if (!FastResolveEnabled) return await ResolveViaYtDlpAsync(id, ct);
        if (FastPathTripped())
        {
            Log.Info("fast path breaker open, using yt-dlp");
            return await ResolveViaYtDlpAsync(id, ct);
        }
        var apiKey = (_cfg.YoutubeApiKey ?? "").Trim();
        var candidates = new List<(string Key, string Source)>();
        // Config key is an override, not a requirement: no length policing,
        // no warning spam. If it does not work the auto key answers instead.
        if (apiKey.Length > 0) candidates.Add((apiKey, "config"));
        var auto = await AutoKeyAsync();
        if (!string.IsNullOrEmpty(auto) && auto != apiKey) candidates.Add((auto, "auto"));
        if (candidates.Count == 0)
        {
            FastPathState = "off";
            FastPathDetail = "no key available, resolving through yt-dlp";
        }
        else if (FastPathState == "off")
        {
            FastPathState = "unproven";
            FastPathDetail = "key present, no resolve attempted yet";
        }
        foreach (var (key, source) in candidates)
        {
            var fsw = Stopwatch.StartNew();
            try
            {
                var fast = await ResolveViaPlayerApiAsync(id, key);
                if (!string.IsNullOrEmpty(fast))
                {
                    FastPathState = "ok";
                    FastPathDetail = "last resolve ok in " + fsw.ElapsedMilliseconds + "ms via " + source;
                    Log.Info("fast resolve " + id + " via " + source + ": ok in " + fsw.ElapsedMilliseconds + "ms");
                    LastSource[id] = "fast(" + source + ")";
                    return fast;
                }
                FastPathState = "failing";
                FastPathDetail = "last resolve got no url via " + source + " in " + fsw.ElapsedMilliseconds + "ms";
                Log.Info("fast resolve " + id + " via " + source + ": no url in " + fsw.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                FastPathState = "failing";
                FastPathDetail = source + " key failed: " + ex.GetType().Name;
                Log.Warn("fast resolve " + id + " via " + source + " threw after " + fsw.ElapsedMilliseconds + "ms: " + ex.GetType().Name + " " + ex.Message);
            }
        }
        return await ResolveViaYtDlpAsync(id, ct);
    }

    // Single switch for the whole fast path. When false every resolve goes
    // through yt-dlp, including any configured YoutubeApiKey.
    public static bool FastResolveEnabled = false;

    // --- fast-path breaker -------------------------------------------------
    // Fast-path URLs that resolved but would not play are worse than slow
    // ones: mpv retries the same dead URL and each retry is a visible skip.
    // Two failures inside a minute means the fast path is the problem, so
    // turn it off for ten minutes and let yt-dlp carry the stream.
    const int FastFailThreshold = 2;
    static readonly TimeSpan FastFailWindow = TimeSpan.FromSeconds(60);
    static readonly TimeSpan FastBreakerOff = TimeSpan.FromMinutes(10);
    static readonly Queue<DateTime> _fastFails = new();
    static DateTime _fastBreakerUntil = DateTime.MinValue;

    public static bool FastPathTripped()
    {
        lock (_fastFails) return DateTime.UtcNow < _fastBreakerUntil;
    }

    // Resolve strictly through yt-dlp. The stream handler calls this after a
    // fast-path URL has already failed, so it must not be able to hand back
    // another fast URL: that is exactly the retry loop we are avoiding.
    public Task<string?> ResolveViaYtDlpForcedAsync(string id)
        => ResolveViaYtDlpAsync(id, CancellationToken.None);

    public static void NoteFastPathFailure(string id, string why)
    {
        bool trip;
        int count;
        lock (_fastFails)
        {
            var now = DateTime.UtcNow;
            while (_fastFails.Count > 0 && now - _fastFails.Peek() > FastFailWindow) _fastFails.Dequeue();
            _fastFails.Enqueue(now);
            count = _fastFails.Count;
            trip = count >= FastFailThreshold && now >= _fastBreakerUntil;
            if (trip)
            {
                _fastBreakerUntil = now.Add(FastBreakerOff);
                _fastFails.Clear();
            }
        }
        Log.Warn("fast path playback failure for " + id + " (" + why + "), " + count + " in " + (int)FastFailWindow.TotalSeconds + "s");
        if (trip)
            Log.Warn("fast path disabled for " + (int)FastBreakerOff.TotalMinutes + " min after " + count + " failures; yt-dlp only");
    }

    // Drop the cached fast-path URL for an id so the next attempt re-resolves
    // through yt-dlp instead of replaying the URL that just failed.
    public void InvalidateUrl(string id)
    {
        LastSource.TryRemove(id, out _);
    }

    async Task<string?> ResolveViaYtDlpAsync(string id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var args = new List<string> { "--no-playlist", "-f", "bestaudio/best", "-g", "https://www.youtube.com/watch?v=" + id };
        AddAuthArgs(args);
        var (code, stdout, stderr) = await RunAsync(args, 120, ct);
        if (code != 0)
        {
            Log.Warn("resolve failed for " + id + " in " + sw.ElapsedMilliseconds + "ms: " + stderr.Trim());
            return null;
        }
        var first = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(first)) return null;
        LastSource[id] = "yt-dlp";
        return first;
    }

    static readonly HttpClient YtApi = new() { Timeout = TimeSpan.FromSeconds(6) };

    static async Task<string?> ResolveViaPlayerApiAsync(string id, string apiKey)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "ANDROID", clientVersion = "20.10.38", androidSdkVersion = 30 } },
            videoId = id,
            racyCheckOk = true,
            contentCheckOk = true,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/player?key=" + Uri.EscapeDataString(apiKey) + "&prettyPrint=false");
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await YtApi.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("player api http " + (int)resp.StatusCode + " for " + id);
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var playability = doc.RootElement.TryGetProperty("playabilityStatus", out var ps)
            ? (ps.TryGetProperty("status", out var st) ? (st.GetString() ?? "?") : "?") : "?";
        if (!doc.RootElement.TryGetProperty("streamingData", out var sd))
        {
            var reason = ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("reason", out var rs)
                ? (rs.GetString() ?? "") : "";
            Log.Warn("player api no streamingData for " + id + " (playability=" + playability + " " + reason + ")");
            return null;
        }
        if (!sd.TryGetProperty("adaptiveFormats", out var formats))
        {
            Log.Warn("player api no adaptiveFormats for " + id + " (playability=" + playability + ")");
            return null;
        }
        string? best = null;
        long bestRate = -1;
        var audioTotal = 0;
        var audioCiphered = 0;
        foreach (var f in formats.EnumerateArray())
        {
            var mime = f.TryGetProperty("mimeType", out var m) ? (m.GetString() ?? "") : "";
            if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;
            audioTotal++;
            var url = f.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(url)) { audioCiphered++; continue; }
            var rate = f.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : 0;
            if (rate > bestRate) { bestRate = rate; best = url; }
        }
        if (best == null)
            Log.Warn("player api no playable audio for " + id + " (audio=" + audioTotal + " ciphered=" + audioCiphered + " playability=" + playability + ")");
        // ratebypass: without it googlevideo throttles these URLs until
        // playback stalls out (that was the old skip storm).
        if (best != null && !best.Contains("ratebypass=", StringComparison.OrdinalIgnoreCase))
            best += (best.Contains('?') ? "&" : "?") + "ratebypass=yes";
        return best;
    }

    public async Task<List<TrackResult>> SearchRelatedAsync(string id, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var args = new List<string>
        {
            "--no-warnings", "--flat-playlist", "--playlist-end", "30", "-J",
            "https://www.youtube.com/watch?v=" + id + "&list=RD" + id,
        };
        AddAuthArgs(args);
        var (code, stdout, stderr) = await RunAsync(args, 60, ct);
        if (code != 0)
        {
            Log.Warn("radio failed in " + sw.ElapsedMilliseconds + "ms: " + stderr.Trim());
            return new List<TrackResult>();
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) return new List<TrackResult>();
            var list = new List<TrackResult>();
            foreach (var e in entries.EnumerateArray())
            {
                if (!e.TryGetProperty("id", out var idNode)) continue;
                var vid = idNode.GetString();
                if (string.IsNullOrWhiteSpace(vid)) continue;
                var title = e.TryGetProperty("title", out var t) ? t.GetString() : "";
                var duration = e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;
                var channel = e.TryGetProperty("channel", out var ch) ? ch.GetString() : "";
                list.Add(new TrackResult { Id = vid!, Title = title ?? "untitled", Duration = duration, Channel = channel ?? "" });
            }
            Log.Info("radio mix for " + id + ": " + list.Count + " candidates in " + sw.ElapsedMilliseconds + "ms");
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("radio parse failed in " + sw.ElapsedMilliseconds + "ms: " + ex.Message);
            return new List<TrackResult>();
        }
    }

    // Fast-path health for the settings UI: what the last resolve attempt
    // showed. Updated on every attempt, read by the dashboard at init and
    // after each settings save.
    public static string FastPathState { get; private set; } = "off"; // off|unproven|ok|failing
    public static string FastPathDetail { get; private set; } = "no key configured";

    // Which resolver produced the current URL for an id ("fast" or "ytdlp"),
    // so the stream handler can say what mpv is actually being fed.
    static readonly ConcurrentDictionary<string, string> LastSource = new();

    public static string SourceFor(string id) => LastSource.TryGetValue(id, out var s) ? s : "?";

    static int _running;

    // A run this slow is the interesting one (throttling, retries, sleeping):
    // repeat it without --no-warnings so 429/retry chatter reaches the log.
    const int SlowRunMs = 6000;

    async Task<(int Code, string Stdout, string Stderr)> RunAsync(List<string> args, int timeoutSec, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _running);
        var sw = Stopwatch.StartNew();
        var command = string.Join(' ', args.Take(5));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return (1, "", "failed to start");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            // Slow runs are where throttling and retries hide. Log whatever
            // stderr we got; --no-warnings hides the retry chatter, so if it
            // came back empty re-run once with warnings on to capture it.
            if (sw.ElapsedMilliseconds > SlowRunMs && ct.IsCancellationRequested == false)
            {
                var first = stderr.Trim();
                if (first.Length == 0)
                {
                    var retry = new List<string>();
                    foreach (var a in args)
                        if (a != "--no-warnings") retry.Add(a);
                    if (!retry.Contains("-v")) retry.Insert(0, "-v");
                    Log.Warn("yt-dlp slow run with empty stderr, repeating verbose: " + command);
                    var (rc2, _, err2) = await RunAsync(retry, timeoutSec, ct);
                    first = err2.Trim();
                    if (first.Length == 0) first = "(verbose rerun produced no stderr, exit " + rc2 + ")";
                }
                Log.Warn("yt-dlp slow run " + sw.ElapsedMilliseconds + "ms [" + command + "]: " + first);
            }
            return (proc.ExitCode, stdout, stderr);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            Log.Info("yt-dlp took " + sw.ElapsedMilliseconds + "ms (" + n + " concurrent): " + string.Join(' ', args.Take(5)));
        }
    }
}