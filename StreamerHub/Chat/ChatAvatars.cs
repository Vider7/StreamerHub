using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StreamerHub;

public static class ChatAvatars
{
    public static string TwitchProfileUrl(string login) =>
        "https://www.twitch.tv/" + Uri.EscapeDataString((login ?? "").Trim().ToLowerInvariant());

    public static string TikTokAvatarFallback(string uniqueId) =>
        "https://unavatar.io/tiktok/" + Uri.EscapeDataString((uniqueId ?? "").Trim().TrimStart('@'));

    public static string TikTokProfileUrl(string uniqueId) =>
        "https://www.tiktok.com/@" + Uri.EscapeDataString((uniqueId ?? "").Trim().TrimStart('@'));

    public static string TikTokAvatar(TikTokLiveSharp.Events.Objects.User? sender)
    {
        try
        {
            if (sender == null) return "";
            return FirstUrl(sender.AvatarLarge)
                ?? FirstUrl(sender.AvatarMedium)
                ?? FirstUrl(sender.AvatarThumbnail)
                ?? "";
        }
        catch { return ""; }
    }

    public static string? FirstPictureUrl(TikTokLiveSharp.Events.Objects.Picture? pic) =>
        FirstUrl(pic);

    static string? FirstUrl(TikTokLiveSharp.Events.Objects.Picture? pic)
    {
        try
        {
            var urls = pic?.Urls;
            if (urls == null) return null;
            foreach (var u in urls)
                if (!string.IsNullOrWhiteSpace(u) && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return u;
            return null;
        }
        catch { return null; }
    }
}

public sealed class TwitchAvatarService : IDisposable
{
    readonly AppConfig _cfg;
    readonly HttpClient _http = new();
    static readonly HttpClient Quick = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly ConcurrentDictionary<string, (string Url, DateTime Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    const int FlushBatch = 8;
    const int MaxAttempts = 4;
    readonly CancellationTokenSource _cts = new();
    string? _token;
    DateTime _tokenAt;
    bool _noCredsLogged;
    bool _disposed;

    static readonly TimeSpan HitTtl = TimeSpan.FromHours(24);
    static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);

    public event Action<ChatPlatform, string, string>? AvatarResolved;

    public TwitchAvatarService(AppConfig cfg)
    {
        _cfg = cfg;
        _ = Task.Run(Loop);
    }

    public string GetCached(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return "";
        if (_cache.TryGetValue(login.Trim(), out var v) && v.Expires > DateTime.UtcNow)
            return v.Url;
        return "";
    }

    public bool HasCredentials =>
        (_cfg.Twitch.ClientId ?? "").Trim().Length > 0 &&
        (_cfg.Twitch.ClientSecret ?? "").Trim().Length > 0;

    public void Request(string login)
    {
        if (_disposed || string.IsNullOrWhiteSpace(login)) return;
        login = login.Trim();
        if (_cache.TryGetValue(login, out var v) && v.Expires > DateTime.UtcNow) return;
        _pending[login] = DateTime.UtcNow;
    }

    async Task Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
            catch (OperationCanceledException) { return; }
            try { await FlushAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn("twitch avatar: " + ex.Message); }
        }
    }

    async Task FlushAsync(CancellationToken token)
    {
        if (_pending.IsEmpty) return;
        var clientId = (_cfg.Twitch.ClientId ?? "").Trim();
        var secret = (_cfg.Twitch.ClientSecret ?? "").Trim();
        if (clientId.Length == 0 || secret.Length == 0)
        {
            if (!_noCredsLogged)
            {
                _noCredsLogged = true;
                Log.Info("twitch avatar: no clientId/secret, resolving pictures via ivr.fi");
            }
            await FlushIvrAsync(token);
            return;
        }

        var batch = new List<string>();
        foreach (var k in _pending.Keys)
        {
            if (batch.Count >= 100) break;
            if (_pending.TryRemove(k, out _)) batch.Add(k);
        }
        if (batch.Count == 0) return;

        var url = "https://api.twitch.tv/helix/users?" + string.Join("&",
            batch.Select(l => "login=" + Uri.EscapeDataString(l)));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Client-Id", clientId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(clientId, token));
        using var resp = await _http.SendAsync(req, token);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _token = null;
            foreach (var l in batch) _pending.TryAdd(l, DateTime.UtcNow);
            return;
        }
        if (!resp.IsSuccessStatusCode) return;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var u in data.EnumerateArray())
            {
                var login = u.TryGetProperty("login", out var lp) ? (lp.GetString() ?? "") : "";
                var img = u.TryGetProperty("profile_image_url", out var ip) ? (ip.GetString() ?? "") : "";
                if (login.Length == 0) continue;
                seen.Add(login);
                if (img.Length == 0)
                {
                    _cache[login] = ("", DateTime.UtcNow + MissTtl);
                    continue;
                }
                _cache[login] = (img, DateTime.UtcNow + HitTtl);
                AvatarResolved?.Invoke(ChatPlatform.Twitch, login, img);
            }
        }
        foreach (var l in batch)
            if (!seen.Contains(l))
                _cache[l] = ("", DateTime.UtcNow + MissTtl);
    }

    async Task FlushIvrAsync(CancellationToken token)
    {
        // Most recent chatters first: their messages are the ones on screen.
        // Backoff entries (timestamp in the future) wait their turn.
        var now = DateTime.UtcNow;
        var batch = new List<string>();
        foreach (var k in _pending.Where(kv => kv.Value <= now).OrderByDescending(kv => kv.Value).Take(FlushBatch).Select(kv => kv.Key).ToList())
        {
            if (_pending.TryRemove(k, out _)) batch.Add(k);
        }
        if (batch.Count == 0) return;
        await Task.WhenAll(batch.Select(login => ResolveOneIvrAsync(login, token)));
    }

    async Task ResolveOneIvrAsync(string login, CancellationToken token)
    {
        try
        {
            var img = await IvrLogoAsync(login, token) ?? await DecapiAvatarAsync(login, token);
            if (!string.IsNullOrEmpty(img))
            {
                _cache[login] = (img, DateTime.UtcNow + HitTtl);
                _attempts.TryRemove(login, out _);
                AvatarResolved?.Invoke(ChatPlatform.Twitch, login, img);
            }
            else
            {
                // Definitive miss (unknown user): back off.
                _cache[login] = ("", DateTime.UtcNow + MissTtl);
                _attempts.TryRemove(login, out _);
            }
        }
        catch (Exception ex)
        {
            // Transient (timeout, rate-limit, blip): retry soon, not in 10 min.
            var n = _attempts.AddOrUpdate(login, 1, (_, c) => c + 1);
            if (n >= MaxAttempts)
            {
                Log.Warn($"twitch avatar: {login} giving up ({ex.Message})");
                _cache[login] = ("", DateTime.UtcNow + MissTtl);
                _attempts.TryRemove(login, out _);
            }
            else
            {
                _pending[login] = DateTime.UtcNow.AddSeconds(5 * n);
            }
        }
    }

    static async Task<string?> IvrLogoAsync(string login, CancellationToken token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.ivr.fi/v2/twitch/user?login=" + Uri.EscapeDataString(login));
        using var resp = await Quick.SendAsync(req, token);
        if (!resp.IsSuccessStatusCode) throw new Exception("ivr " + (int)resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
        var first = doc.RootElement[0];
        var logo = first.TryGetProperty("logo", out var lp) ? (lp.GetString() ?? "") : "";
        return logo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? logo : null;
    }

    static async Task<string?> DecapiAvatarAsync(string login, CancellationToken token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://decapi.me/twitch/avatar/" + Uri.EscapeDataString(login));
        using var resp = await Quick.SendAsync(req, token);
        if (!resp.IsSuccessStatusCode) throw new Exception("decapi " + (int)resp.StatusCode);
        var body = (await resp.Content.ReadAsStringAsync(token)).Trim();
        return body.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? body : null;
    }

    async Task<string> GetTokenAsync(string clientId, CancellationToken token)
    {
        if (_token != null && DateTime.UtcNow - _tokenAt < TimeSpan.FromMinutes(50))
            return _token;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = (_cfg.Twitch.ClientSecret ?? "").Trim(),
            ["grant_type"] = "client_credentials",
        });
        using var resp = await _http.PostAsync("https://id.twitch.tv/oauth2/token", form, token);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
        _token = doc.RootElement.GetProperty("access_token").GetString();
        _tokenAt = DateTime.UtcNow;
        return _token!;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _http.Dispose();
    }
}
