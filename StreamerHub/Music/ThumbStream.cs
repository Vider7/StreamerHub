using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace StreamerHub;

public static class ThumbStream
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly ConcurrentDictionary<string, byte[]> Cache = new();
    const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    public static async Task Handle(HttpContext ctx, string id)
    {
        if (!ValidId(id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        var bytes = await FetchAsync(id);
        if (bytes == null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.Headers.CacheControl = "public, max-age=86400, immutable";
        await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
    }

    static async Task<byte[]?> FetchAsync(string id)
    {
        if (Cache.TryGetValue(id, out var cached)) return cached;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg");
        req.Headers.Add("User-Agent", UA);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsByteArrayAsync();
            if (body.Length == 0) return null;
            Cache[id] = body;
            if (Cache.Count > 256)
            {
                foreach (var k in Cache.Keys.Take(64)) Cache.TryRemove(k, out _);
            }
            return body;
        }
        catch
        {
            return null;
        }
    }

    static bool ValidId(string id)
        => id.Length is >= 6 and <= 30 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
}