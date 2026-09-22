using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StreamerHub;

public sealed class TwitchStatsService : IDisposable
{
    readonly AppConfig _cfg;
    readonly HttpClient _http = new();
    string? _appToken;
    DateTime _appTokenAt;
    volatile bool _stop;

    public event Action<long>? ViewerCountChanged;
    public event Action<string>? Notice;

    public TwitchStatsService(AppConfig cfg)
    {
        _cfg = cfg;
    }

    public void Start()
    {
        var channel = (_cfg.Twitch.Channel ?? "").Trim();
        var clientId = (_cfg.Twitch.ClientId ?? "").Trim();
        var secret = (_cfg.Twitch.ClientSecret ?? "").Trim();

        if (channel.Length == 0)
        {
            Notice?.Invoke("viewer count off: no twitch channel in config");
            return;
        }
        if (clientId.Length == 0)
        {
            Log.Info("twitch stats: off - twitch.clientId is empty");
            Notice?.Invoke("viewer count off: add twitch.clientId (free app at dev.twitch.tv)");
            return;
        }
        if (secret.Length == 0)
        {
            Log.Info("twitch stats: off - twitch.clientSecret is empty");
            Notice?.Invoke("viewer count off: add twitch.clientSecret (free app at dev.twitch.tv)");
            return;
        }
        Log.Info("twitch stats: polling viewer count");

        _ = Task.Run(async () =>
        {
            while (!_stop)
            {
                try
                {
                    var viewers = await PollViewersAsync(channel, clientId);
                    ViewerCountChanged?.Invoke(viewers);
                }
                catch (Exception ex)
                {
                    Notice?.Invoke("viewer poll: " + ex.Message);
                }
                await Task.Delay(Math.Max(10, _cfg.Twitch.PollViewersSeconds) * 1000);
            }
        });
    }

    async Task<long> PollViewersAsync(string channel, string clientId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.twitch.tv/helix/streams?user_login=" + Uri.EscapeDataString(channel));
        req.Headers.Add("Client-Id", clientId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(clientId));
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _appToken = null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Exception("twitch api " + (int)resp.StatusCode + ": " + Short(body));
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) return -1;
        return data[0].GetProperty("viewer_count").GetInt64();
    }

    async Task<string> GetTokenAsync(string clientId)
    {
        if (_appToken != null && DateTime.UtcNow - _appTokenAt < TimeSpan.FromMinutes(50))
            return _appToken;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = (_cfg.Twitch.ClientSecret ?? "").Trim(),
            ["grant_type"] = "client_credentials",
        });
        using var resp = await _http.PostAsync("https://id.twitch.tv/oauth2/token", form);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        _appToken = doc.RootElement.GetProperty("access_token").GetString();
        _appTokenAt = DateTime.UtcNow;
        return _appToken!;
    }

    public void Stop()
    {
        _stop = true;
        _http.Dispose();
    }

    static string Short(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= 160 ? s : s[..160]);

    public void Dispose() => Stop();
}