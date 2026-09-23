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
        if (AudioCache.TryGet(id, out var cachedPath, out var cachedType))
        {
            Log.Info("serving precached audio for " + id);
            await ServeFileAsync(ctx, cachedPath, cachedType);
            return;
        }
        var url = await ResolveAsync(music, id);
        if (url == null)
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new { error = "could not resolve stream for " + id });
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", UA);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        var range = ctx.Request.Headers.Range.ToString();
        if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            Cache.TryRemove(id, out _);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            return;
        }
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
        catch
        {
            Cache.TryRemove(id, out _);
            throw;
        }
    }

    public static bool IsCached(string id) => Cache.ContainsKey(id);

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

    static async Task<string?> ResolveAsync(MusicEngine music, string id)
    {
        if (Cache.TryGetValue(id, out var e) && DateTime.UtcNow - e.At < TimeSpan.FromMinutes(30)) return e.Url;
        var url = await music.ResolveAudioUrlAsync(id);
        if (url == null) return null;
        Cache[id] = (url, DateTime.UtcNow);
        foreach (var k in Cache.Keys)
        {
            if (Cache.TryGetValue(k, out var v) && DateTime.UtcNow - v.At > TimeSpan.FromHours(1))
                Cache.TryRemove(k, out _);
        }
        return url;
    }
}