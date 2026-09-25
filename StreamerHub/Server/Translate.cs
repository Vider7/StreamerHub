using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace StreamerHub;

public static class Translate
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly ConcurrentDictionary<string, (string Text, string Source, DateTime At)> Cache = new();
    const int MaxLen = 500;

    public static async Task Handle(HttpContext ctx, string? q, string? to)
    {
        var text = (q ?? "").Trim();
        var target = string.IsNullOrWhiteSpace(to) ? "en" : to.Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "empty" });
            return;
        }
        if (text.Length > MaxLen) text = text[..MaxLen];
        var key = target + ":" + text;
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(6))
        {
            await ctx.Response.WriteAsJsonAsync(new { text = hit.Text, source = hit.Source });
            return;
        }
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl="
                + Uri.EscapeDataString(target) + "&dt=t&q=" + Uri.EscapeDataString(text);
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) throw new Exception("translate api " + (int)resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var sb = new StringBuilder();
            var source = "";
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                foreach (var part in root[0].EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0)
                        sb.Append(part[0].GetString());
                }
                if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                    source = root[2].GetString() ?? "";
            }
            var result = sb.ToString();
            if (result.Length == 0) throw new Exception("empty translation");
            Cache[key] = (result, source, DateTime.UtcNow);
            await ctx.Response.WriteAsJsonAsync(new { text = result, source });
        }
        catch (Exception ex)
        {
            Log.Warn("translate failed: " + ex.Message);
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new { error = "translation failed" });
        }
    }
}
