using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

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
        // Fast path: one direct player-API call (~0.5s) instead of a full
        // yt-dlp run (seconds). Needs Music.YoutubeApiKey in config;
        // falls back to yt-dlp when unset or on any failure.
        var apiKey = (_cfg.YoutubeApiKey ?? "").Trim();
        if (apiKey.Length > 0)
        {
            var fsw = Stopwatch.StartNew();
            try
            {
                var fast = await ResolveViaPlayerApiAsync(id, apiKey);
                Log.Info("fast resolve " + id + ": " + (string.IsNullOrEmpty(fast) ? "no url" : "ok") + " in " + fsw.ElapsedMilliseconds + "ms");
                if (!string.IsNullOrEmpty(fast)) return fast;
            }
            catch (Exception ex)
            {
                Log.Warn("fast resolve " + id + " threw after " + fsw.ElapsedMilliseconds + "ms: " + ex.GetType().Name + " " + ex.Message);
            }
        }
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
        return string.IsNullOrEmpty(first) ? null : first;
    }

    static readonly HttpClient YtApi = new() { Timeout = TimeSpan.FromSeconds(6) };

    static async Task<string?> ResolveViaPlayerApiAsync(string id, string apiKey)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "ANDROID", clientVersion = "19.09.37", androidSdkVersion = 30 } },
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

    static int _running;

    async Task<(int Code, string Stdout, string Stderr)> RunAsync(List<string> args, int timeoutSec, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _running);
        var sw = Stopwatch.StartNew();
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
            return (proc.ExitCode, stdout, stderr);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            Log.Info("yt-dlp took " + sw.ElapsedMilliseconds + "ms (" + n + " concurrent): " + string.Join(' ', args.Take(5)));
        }
    }
}