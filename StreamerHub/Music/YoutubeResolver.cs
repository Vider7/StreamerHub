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

    public Task<List<TrackResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var key = query.Trim().ToLowerInvariant();
        if (key.Length == 0) return Task.FromResult(new List<TrackResult>());
        if (_searchCache.TryGetValue(key, out var entry)
            && DateTime.UtcNow - entry.At < TimeSpan.FromSeconds(SearchCacheTtlSeconds))
        {
            return entry.Task;
        }
        var fresh = _searchCache.GetOrAdd(key, _ => (DateTime.UtcNow, RunSearchAsync(query)));
        if (_searchCache.Count > SearchCacheMax)
        {
            foreach (var k in _searchCache.Keys.Take(32)) _searchCache.TryRemove(k, out _);
        }
        if (fresh.Task.IsCompletedSuccessfully && fresh.Task.Result.Count == 0)
        {
            _searchCache.TryRemove(key, out _);
        }
        return fresh.Task;
    }

    async Task<List<TrackResult>> RunSearchAsync(string query)
    {
        var args = new List<string> { "--no-warnings", "--flat-playlist", "-J", "ytsearch10:" + query };
        AddAuthArgs(args);
        var (code, stdout, stderr) = await RunAsync(args, 60, CancellationToken.None);
        if (code != 0)
        {
            Log.Warn("search failed: " + stderr.Trim());
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
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("search parse failed: " + ex.Message);
            return new List<TrackResult>();
        }
    }

    public async Task<string?> ResolveAudioUrlAsync(string id, CancellationToken ct = default)
    {
        var args = new List<string> { "--no-playlist", "-f", "bestaudio/best", "-g", "https://www.youtube.com/watch?v=" + id };
        AddAuthArgs(args);
        var (code, stdout, stderr) = await RunAsync(args, 120, ct);
        if (code != 0)
        {
            Log.Warn("resolve failed for " + id + ": " + stderr.Trim());
            return null;
        }
        var first = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    public async Task<List<TrackResult>> SearchRelatedAsync(string id, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "--no-warnings", "--flat-playlist", "--playlist-end", "60", "-J",
            "https://www.youtube.com/watch?v=" + id + "&list=RD" + id,
        };
        AddAuthArgs(args);
        var (code, stdout, stderr) = await RunAsync(args, 60, ct);
        if (code != 0)
        {
            Log.Warn("radio failed: " + stderr.Trim());
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
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("radio parse failed: " + ex.Message);
            return new List<TrackResult>();
        }
    }

    async Task<(int Code, string Stdout, string Stderr)> RunAsync(List<string> args, int timeoutSec, CancellationToken ct)
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
}