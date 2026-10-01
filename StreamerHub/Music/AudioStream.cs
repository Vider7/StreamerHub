using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace StreamerHub;

public static class AudioStream
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    static readonly ConcurrentDictionary<string, (string Url, DateTime At)> Cache = new();
    const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    public static async Task Handle(MusicEngine music, HttpContext ctx, string id)
    {
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        var ready = AudioCache.TryGet(id, out var cachedPath, out var cachedType);
        var growing = AudioCache.TryGetActive(id, out var partialPath, out var partialType);
        Log.Info("stream " + id + " range='" + rangeHeader + "' precached=" + ready + " growing=" + growing);
        if (ready)
        {
            Log.Info("serving precached audio for " + id);
            await ServeFileAsync(ctx, cachedPath, cachedType);
            return;
        }
        // A skip just landed on a track whose download is already running:
        // play the growing local file instead of opening a second, slower
        // live stream. Real seeks (a range into the middle) still use the
        // live path since the tail may not have those bytes yet; a plain
        // "bytes=0-" open-from-start takes the growing file too.
        var fromStart = string.IsNullOrEmpty(rangeHeader)
            || rangeHeader.Trim().Equals("bytes=0-", StringComparison.OrdinalIgnoreCase);
        if (fromStart && growing)
        {
            var gotBytes = await WaitForBytesAsync(partialPath, 256 * 1024, TimeSpan.FromSeconds(6));
            if (AudioCache.TryGet(id, out cachedPath, out cachedType))
            {
                Log.Info("serving just-precached audio for " + id);
                await ServeFileAsync(ctx, cachedPath, cachedType);
                return;
            }
            if (gotBytes)
            {
                Log.Info("serving partial precache for " + id);
                await ServePartialAsync(ctx, partialPath, partialType);
                return;
            }
        }
        Log.Info("stream " + id + ": cold, resolving live");
        var url = await ResolveAsync(music, id);
        if (url == null)
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new { error = "could not resolve stream for " + id });
            return;
        }

        var range = ctx.Request.Headers.Range.ToString();
        // Player-API URLs 403 a bare GET; they need a Range to serve.
        if (string.IsNullOrEmpty(range)) range = "bytes=0-";
        var source = YoutubeResolver.SourceFor(id);

        // Try the URL we have. A fast-path URL that resolves but will not
        // serve is the known failure mode, so re-resolve through yt-dlp once
        // inside this same request rather than handing mpv another error.
        var resp = await TryOpenAsync(music, id, url, source, range);
        if (resp == null) return;

        Log.Info("stream " + id + ": upstream " + (int)resp.StatusCode
            + " for range='" + range + "' source=" + YoutubeResolver.SourceFor(id)
            + " contentLength=" + (resp.Content.Headers.ContentLength?.ToString() ?? "null")
            + " contentRange=" + (resp.Content.Headers.ContentRange?.ToString() ?? "null")
            + " contentType=" + (resp.Content.Headers.ContentType?.ToString() ?? "null"));

        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            var retryUrl = await RetryViaYtDlpAsync(music, id, url, source, "upstream " + status, range);
            if (retryUrl == null)
            {
                resp.Dispose();
                Cache.TryRemove(id, out _);
                ctx.Response.StatusCode = status;
                return;
            }
            resp.Dispose();
            url = retryUrl;
            var second = await OpenQuietAsync(url, range, id);
            if (second == null)
            {
                Cache.TryRemove(id, out _);
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsJsonAsync(new { error = "could not open upstream for " + id });
                return;
            }
            resp = second;
            Log.Info("stream " + id + ": upstream retry " + (int)resp.StatusCode
                + " for range='" + range + "' source=" + YoutubeResolver.SourceFor(id)
                + " contentLength=" + (resp.Content.Headers.ContentLength?.ToString() ?? "null")
                + " contentRange=" + (resp.Content.Headers.ContentRange?.ToString() ?? "null"));
            if (!resp.IsSuccessStatusCode)
            {
                var bad = (int)resp.StatusCode;
                resp.Dispose();
                Cache.TryRemove(id, out _);
                ctx.Response.StatusCode = bad;
                return;
            }
        }

        using (resp)
        {
        ctx.Response.StatusCode = 200;
        var ct = resp.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(ct)) ctx.Response.ContentType = ct;
        if (resp.Content.Headers.ContentLength is long len) ctx.Response.ContentLength = len;
        if (resp.Content.Headers.ContentRange is { } cr) ctx.Response.Headers.ContentRange = cr.ToString();
        if (resp.Headers.TryGetValues("Accept-Ranges", out var ar))
        {
            var v = ar.FirstOrDefault();
            if (v != null) ctx.Response.Headers.AcceptRanges = v;
        }
        try
        {
            using var body = await resp.Content.ReadAsStreamAsync();
            await body.CopyToAsync(ctx.Response.Body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Was the bytes dying upstream or here? Log it instead of the bare
            // rethrow this used to swallow.
            Cache.TryRemove(id, out _);
            Log.Warn("stream " + id + ": body copy failed after "
                + (resp.Content.Headers.ContentLength?.ToString() ?? "?") + " declared bytes, source="
                + YoutubeResolver.SourceFor(id) + " range='" + range + "': "
                + ex.GetType().Name + ": " + ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            // Client hung up mid-stream: normal when mpv skips or seeks.
            Log.Info("stream " + id + ": client aborted mid-stream ("
                + ex.GetType().Name + ": " + ex.Message + ")");
            throw;
        }
        }
    }

    static async Task<HttpResponseMessage> OpenUpstreamAsync(string url, string range, string id)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", UA);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        req.Headers.TryAddWithoutValidation("Range", range);
        return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }

    // Open, and if the transport itself throws on a fast-path URL, re-resolve
    // through yt-dlp once before giving up. Returns null after writing the
    // error response, since there is nothing left to serve.
    static async Task<HttpResponseMessage?> TryOpenAsync(MusicEngine music, string id, string url, string source, string range)
    {
        try
        {
            return await OpenUpstreamAsync(url, range, id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("stream " + id + ": upstream request threw for range='" + range + "' source=" + source
                + ": " + ex.GetType().Name + ": " + ex.Message);
            var fresh = await RetryViaYtDlpAsync(music, id, url, source, "threw " + ex.GetType().Name, range);
            if (fresh == null) return null;
            return await OpenQuietAsync(fresh, range, id);
        }
    }

    static async Task<HttpResponseMessage?> OpenQuietAsync(string url, string range, string id)
    {
        try
        {
            return await OpenUpstreamAsync(url, range, id);
        }
        catch (Exception ex)
        {
            Log.Warn("stream " + id + ": retry open threw: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // Decide whether a dead URL is worth one fresh resolve. Both sources get
    // this: a yt-dlp URL can 403 too (seen in the log on an expired link),
    // and a fresh resolve recovered it. Only fast-path failures count toward
    // the breaker, since that is the one we can turn off.
    // `why` is either "upstream <code>" or "threw <Type>"; throws are always
    // worth one retry, codes only when the URL is genuinely dead (403/410).
    // Returns the fresh URL, or null when there is no better one to try.
    static async Task<string?> RetryViaYtDlpAsync(MusicEngine music, string id, string url, string source, string why, string range)
    {
        var isFast = source.StartsWith("fast", StringComparison.OrdinalIgnoreCase);
        var threw = why.StartsWith("threw", StringComparison.OrdinalIgnoreCase);
        // 403/410 mean the URL itself is dead. Anything else (5xx, a weird
        // status) is more likely upstream being unwell, where hammering a
        // second resolve costs time and rarely helps.
        var status = TryParseStatus(why);
        if (!threw && (!status.HasValue || (status.Value != 403 && status.Value != 410))) return null;
        Log.Warn("stream " + id + ": " + why + " on " + source + " url, re-resolving (range='" + range + "')");
        if (isFast) YoutubeResolver.NoteFastPathFailure(id, why);
        else Log.Info("stream " + id + ": re-resolving dead " + source + " url");
        Cache.TryRemove(id, out _);
        var fresh = await ResolveAsync(music, id, viaYtDlp: true);
        if (fresh == null)
        {
            Log.Warn("stream " + id + ": yt-dlp re-resolve returned nothing");
            return null;
        }
        Cache[id] = (fresh, DateTime.UtcNow);
        // Same URL back: yt-dlp agrees the URL is fine, so the URL is not the
        // problem. Returning it would just replay the same dead response.
        if (fresh == url) return null;
        return fresh;
    }

    // "upstream 403" -> 403. Null when the reason is not a plain status.
    static int? TryParseStatus(string why)
    {
        var t = why.Trim();
        if (!t.StartsWith("upstream ", StringComparison.OrdinalIgnoreCase)) return null;
        t = t.Substring("upstream ".Length).Trim();
        return int.TryParse(t, out var n) ? n : null;
    }

    public static bool IsCached(string id) => Cache.ContainsKey(id);

    // Drop a cached stream URL so the next resolve goes back out for a fresh
    // one. A manual retry needs this: the cached URL is the one that just
    // failed, and replaying it would fail identically.
    public static void ForgetUrl(string id) => Cache.TryRemove(id, out _);

    // Wait for a growing download to reach `need` bytes (or finish):
    // true = the bytes are (or will never be) there, false = nothing at all.
    static async Task<bool> WaitForLengthAsync(string id, string path, long need)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            long len;
            try { len = new FileInfo(path).Length; }
            catch { return false; }
            if (len >= need) return true;
            // Finished or dead: final answer is whatever is on disk.
            if (AudioCache.TryGet(id, out _, out _) || !AudioCache.IsPrefetching(id))
                return len >= need && len > 0;
            await Task.Delay(250);
        }
        try { return new FileInfo(path).Length >= need; }
        catch { return false; }
    }

    static async Task<bool> WaitForBytesAsync(string path, long minBytes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (new FileInfo(path).Length >= minBytes) return true;
            }
            catch { }
            await Task.Delay(200);
        }
        try { return new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    static async Task ServePartialAsync(HttpContext ctx, string path, string? contentType)
    {
        // Growing-file serving with seek support. mpv marks a stream
        // seekable when it sees Accept-Ranges and jumps with Range
        // requests, so honor them: a start past the downloaded bytes waits
        // for the download to catch up (bounded), past the finished EOF is
        // a 416. No content-length: chunked. Duration resolves at EOF.
        // Aborts if the download stalls with no progress.
        var id = Path.GetFileNameWithoutExtension(path);
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        long start = 0;
        var partial = false;
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader.Substring(6).Split('-');
            if (spec.Length == 2 && long.TryParse(spec[0], out var s) && s > 0) { start = s; partial = true; }
        }
        if (!await WaitForLengthAsync(id, path, start + 1))
        {
            ctx.Response.StatusCode = 416;
            return;
        }
        ctx.Response.StatusCode = partial ? 206 : 200;
        ctx.Response.ContentType = string.IsNullOrEmpty(contentType) ? "audio/webm" : contentType;
        ctx.Response.Headers.AcceptRanges = "bytes";
        if (partial)
        {
            long cur;
            try { cur = new FileInfo(path).Length; }
            catch { cur = start + 1; }
            ctx.Response.Headers.ContentRange = "bytes " + start + "-" + Math.Max(start, cur - 1) + "/*";
        }
        var ct = ctx.RequestAborted;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try { fs.Seek(start, SeekOrigin.Begin); }
        catch { ctx.Response.StatusCode = 416; return; }
        var buf = new byte[65536];
        var idleSince = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await fs.ReadAsync(buf, 0, buf.Length, ct); }
            catch (OperationCanceledException) { break; }
            if (n > 0)
            {
                try { await ctx.Response.Body.WriteAsync(buf, 0, n, ct); }
                catch { break; }
                idleSince = DateTime.UtcNow;
                continue;
            }
            long len;
            try { len = new FileInfo(path).Length; }
            catch { break; }
            if (fs.Position >= len && !AudioCache.IsPrefetching(Path.GetFileNameWithoutExtension(path)))
                break; // download finished, we reached EOF
            if (fs.Position >= len && AudioCache.TryGet(Path.GetFileNameWithoutExtension(path), out _, out _))
                break; // completed while serving; reader holds the full file
            if (DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(15)) break;
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    static async Task ServeFileAsync(HttpContext ctx, string path, string? contentType)
    {
        var len = new FileInfo(path).Length;
        var range = ctx.Request.Headers.Range.ToString();
        long start = 0, end = len - 1;
        var partial = false;
        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range.Substring(6).Split('-');
            if (spec.Length == 2 && spec[0].Length > 0)
            {
                if (long.TryParse(spec[0], out var s)) start = Math.Max(0, s);
                if (spec[1].Length > 0 && long.TryParse(spec[1], out var e)) end = e;
                partial = true;
            }
        }
        if (start >= len) { ctx.Response.StatusCode = 416; return; }
        if (end >= len) end = len - 1;
        var count = end - start + 1;
        ctx.Response.StatusCode = partial ? 206 : 200;
        ctx.Response.ContentType = string.IsNullOrEmpty(contentType) ? "audio/webm" : contentType;
        ctx.Response.ContentLength = count;
        if (partial) ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{len}";
        ctx.Response.Headers.AcceptRanges = "bytes";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[65536];
        var left = count;
        while (left > 0)
        {
            var n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) break;
            await ctx.Response.Body.WriteAsync(buf, 0, n);
            left -= n;
        }
    }

    public static async Task PrewarmAsync(MusicEngine music, string id)
    {
        try { await ResolveAsync(music, id); }
        catch { }
    }

    // Resolve for playback. `viaYtDlp` both skips the URL cache and refuses
    // the fast path: the retry path uses it, because the cached URL already
    // failed once and a fresh fast-path resolve would just repeat the 403.
    static async Task<string?> ResolveAsync(MusicEngine music, string id, bool viaYtDlp = false)
    {
        if (!viaYtDlp && Cache.TryGetValue(id, out var e) && DateTime.UtcNow - e.At < TimeSpan.FromMinutes(30)) return e.Url;
        var url = viaYtDlp ? await music.ResolveAudioUrlViaYtDlpAsync(id) : await music.ResolveAudioUrlAsync(id);
        if (url == null) return null;
        Cache[id] = (url, DateTime.UtcNow);
        if (viaYtDlp) Log.Info("stream " + id + ": re-resolved via yt-dlp, source=" + YoutubeResolver.SourceFor(id));
        foreach (var k in Cache.Keys)
        {
            if (Cache.TryGetValue(k, out var v) && DateTime.UtcNow - v.At > TimeSpan.FromHours(1))
                Cache.TryRemove(k, out _);
        }
        return url;
    }
}