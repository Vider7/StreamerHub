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

    static async Task<(string Text, string Source)> GoogleWithRetryAsync(string text, string target)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
            try
            {
                return await GoogleOnceAsync(text, target);
            }
            catch (Exception ex) when (ex.Message.StartsWith("translate api 429") || ex.Message.StartsWith("translate api 5"))
            {
                last = ex;
            }
        }
        throw last ?? new Exception("translate failed");
    }

    static async Task<(string Text, string Source)> GoogleOnceAsync(string text, string target)
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
        return (sb.ToString(), source);
    }

    static async Task<(string Text, string Source)?> LingvaAsync(string text, string target)
    {
        try
        {
            var url = "https://lingva.ml/api/v1/auto/" + Uri.EscapeDataString(target)
                + "/" + Uri.EscapeDataString(text);
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var translated = root.TryGetProperty("translation", out var t) ? (t.GetString() ?? "") : "";
            var source = root.TryGetProperty("info", out var info)
                && info.TryGetProperty("detectedSource", out var s) ? (s.GetString() ?? "") : "";
            if (translated.Length == 0) return null;
            if (translated == text && (source.Length == 0 || source == target)) return null;
            return (translated, source);
        }
        catch
        {
            return null;
        }
    }

    static async Task<(string Text, string Source)?> MyMemoryAsync(string text, string source, string target)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text)
                + "&langpair=" + Uri.EscapeDataString(source) + "|" + Uri.EscapeDataString(target);
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("responseData", out var data)) return null;
            var translated = data.TryGetProperty("translatedText", out var tt) ? (tt.GetString() ?? "") : "";
            if (translated.Length == 0
                || translated.StartsWith("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase)
                || translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
                || translated.StartsWith("INVALID EMAIL", StringComparison.OrdinalIgnoreCase))
                return null;
            return (translated, "");
        }
        catch
        {
            return null;
        }
    }

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
            string translated = "";
            string source = "";
            try
            {
                var fast = await GoogleOnceAsync(text, target);
                translated = fast.Text;
                source = fast.Source;
            }
            catch { }
            if (translated.Length == 0)
            {
                var mirror = await LingvaAsync(text, target);
                if (mirror != null) (translated, source) = mirror.Value;
            }
            if (translated.Length == 0)
            {
                try
                {
                    var retry = await GoogleWithRetryAsync(text, target);
                    translated = retry.Text;
                    source = retry.Source;
                }
                catch { }
            }
            if (translated.Length == 0 && source.Length > 0 && source != target)
            {
                var mm = await MyMemoryAsync(text, source, target);
                if (mm != null) (translated, source) = mm.Value;
            }
            if (translated.Length == 0) throw new Exception("empty translation");
            Cache[key] = (translated, source, DateTime.UtcNow);
            await ctx.Response.WriteAsJsonAsync(new { text = translated, source });
        }
        catch (Exception ex)
        {
            Log.Warn("translate failed: " + ex.Message);
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new { error = "translation failed" });
        }
    }
}
