using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace StreamerHub;

public sealed class WebSocketHub
{
    readonly AppConfig _cfg;
    readonly ChatHub _hub;
    readonly MusicEngine _music;
    readonly MpvPlayer _mpv;
    readonly ConcurrentDictionary<WebSocket, bool> _clients = new();
    readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _sendLocks = new();
    int _musicDirty;
    readonly System.Threading.Timer _musicFlush;

    public string TwitchDetail { get; private set; } = "off";
    public bool TwitchConnected { get; private set; }
    public string TikTokDetail { get; private set; } = "off";
    public bool TikTokConnected { get; private set; }
    public long TwitchViewers { get; private set; } = -1;
    public UpdateService? Updater { get; set; }
    string _theme = "amber";
    static readonly HashSet<string> ThemeIds = new(StringComparer.OrdinalIgnoreCase)
        { "amber", "rose", "mint", "violet", "blue", "rgb" };

    public Action? ConfigApplied;

    public WebSocketHub(AppConfig cfg, ChatHub hub, MusicEngine music, MpvPlayer mpv)
    {
        _cfg = cfg;
        _hub = hub;
        _music = music;
        _mpv = mpv;
        _musicFlush = new System.Threading.Timer(_ => FlushMusic(), null, 0, 60);
    }

    void FlushMusic()
    {
        if (Interlocked.Exchange(ref _musicDirty, 0) == 0) return;
        Broadcast(new { type = "music", music = MusicDto() });
    }

    public async Task Handle(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        _clients[ws] = true;
        _sendLocks.TryAdd(ws, new SemaphoreSlim(1, 1));
        await SendTo(ws, BuildInit());

        var buffer = new byte[16384];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                try { await HandleClientMessage(ws, json); }
                catch (Exception ex) { Log.Warn("ws message: " + ex.Message); }
            }
        }
        catch { }
        _clients.TryRemove(ws, out _);
        if (_sendLocks.TryRemove(ws, out var gate)) gate.Dispose();
        try { ws.Dispose(); } catch { }
    }

    async Task HandleClientMessage(WebSocket ws, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "search":
                {
                    var q = doc.RootElement.GetProperty("q").GetString() ?? "";
                    _ = _execSearch(ws, q);
                    break;
                }
                case "ping":
                {
                    var t = doc.RootElement.GetProperty("t").GetInt64();
                    await SendTo(ws, new { type = "pong", t });
                    break;
                }
                case "theme":
                {
                    var id = doc.RootElement.TryGetProperty("id", out var tid) ? (tid.GetString() ?? "") : "";
                    if (ThemeIds.Contains(id))
                    {
                        _theme = id.ToLowerInvariant();
                        Broadcast(new { type = "theme", id = _theme });
                    }
                    break;
                }
                case "play":
                    _music.PlayNow(ToResult(doc.RootElement, 0));
                    break;
                case "queue":
                {
                    var order = _music.Request(ToResult(doc.RootElement, 0), "you", null);
                    Broadcast(new { type = "notice", text = order });
                    break;
                }
                case "skip":
                    _music.Skip();
                    break;
                case "stop":
                    _music.Stop();
                    break;
                case "prev":
                    _music.Prev();
                    break;
                case "pause":
                {
                    var paused = doc.RootElement.GetProperty("paused").GetBoolean();
                    _mpv.SetPause(paused);
                    break;
                }
                case "seek":
                {
                    var position = doc.RootElement.GetProperty("position").GetDouble();
                    _mpv.Seek(position);
                    break;
                }
                case "remove":
                {
                    var i = doc.RootElement.GetProperty("index").GetInt32();
                    _music.RemoveAt(i);
                    break;
                }
                case "move":
                {
                    var from = doc.RootElement.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : -1;
                    var to = doc.RootElement.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;
                    _music.Move(from, to);
                    break;
                }
                case "volume":
                {
                    var v = doc.RootElement.GetProperty("value").GetInt32();
                    _cfg.Music.DefaultVolume = Math.Clamp(v, 0, 100);
                    _cfg.Save();
                    _mpv.SetVolume(v);
                    break;
                }
                case "like":
                    _music.ToggleLike(ToResult(doc.RootElement, 0));
                    break;
                case "block":
                    _music.Block(ToResult(doc.RootElement, 0));
                    break;
                case "unblock":
                    _music.Unblock(doc.RootElement.GetProperty("id").GetString() ?? "");
                    break;
                case "eq":
                {
                    var bands = ReadDoubles(doc.RootElement, "bands");
                    if (bands != null && bands.Length == MusicConfig.EqBands)
                    {
                        _cfg.Music.Equalizer = bands;
                        _cfg.Save();
                        _mpv.SetAf(MpvPlayer.BuildAf(_cfg.Music.Equalizer, _cfg.Music.Loudness));
                    }
                    break;
                }
                case "loudness":
                {
                    _cfg.Music.Loudness = doc.RootElement.GetProperty("on").GetBoolean();
                    _cfg.Save();
                    _mpv.SetAf(MpvPlayer.BuildAf(_cfg.Music.Equalizer, _cfg.Music.Loudness));
                    break;
                }
                case "chat-clear":
                    _hub.ClearMessages();
                    Broadcast(new { type = "chat-clear" });
                    Broadcast(new { type = "notice", text = "chat cleared on your pages" });
                    break;
                case "activity-clear":
                    _hub.ClearActivity();
                    Broadcast(new { type = "activity-clear" });
                    break;
                case "radio":
                {
                    var on = doc.RootElement.GetProperty("on").GetBoolean();
                    _cfg.Music.AutoNextRadio = on;
                    _cfg.Save();
                    Broadcast(new { type = "radio", on });
                    break;
                }
                case "layout":
                {
                    var order = doc.RootElement.GetProperty("order").GetString() ?? "CSM";
                    if (order.Length == 3)
                    {
                        _cfg.Layout = order;
                        _cfg.Save();
                        Broadcast(new { type = "layout", order });
                    }
                    break;
                }
                case "config":
                {
                    ApplyConfig(doc.RootElement);
                    await SendTo(ws, new { type = "config", ok = true });
                    break;
                }
                case "logs":
                {
                    var action = doc.RootElement.TryGetProperty("action", out var act) ? act.GetString() : "";
                    await SendTo(ws, BuildLogs(action == "clear"));
                    break;
                }
                case "update":
                {
                    if (Updater == null) break;
                    var action = doc.RootElement.GetProperty("action").GetString();
                    if (action == "check") Updater.CheckNow();
                    else if (action == "apply")
                    {
                        Broadcast(new { type = "notice", text = "installing update v" + Updater.LatestVersion + ", restarting..." });
                        var code = Updater.ApplyNow();
                        if (code == 1) Broadcast(new { type = "notice", text = "update could not be applied, check logs/app.log" });
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ws message failed: " + ex.Message);
        }
    }

    async Task _execSearch(WebSocket ws, string q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            await SendTo(ws, new { type = "search", q = "", results = Array.Empty<object>() });
            return;
        }
        try
        {
            var results = await _music.SearchAsync(q);
            await SendTo(ws, new { type = "search", q, results = results.Select(r => new
            {
                id = r.Id, title = r.Title, channel = r.Channel, duration = r.Duration, durationLabel = r.DurationLabel,
            }) });
        }
        catch (Exception ex)
        {
            Log.Warn("search failed: " + ex.Message);
            await SendTo(ws, new { type = "search", q, results = Array.Empty<object>() });
        }
    }

    void ApplyConfig(JsonElement root)
    {
        if (root.TryGetProperty("twitchChannel", out var tc) && tc.ValueKind == JsonValueKind.String)
            _cfg.Twitch.Channel = (tc.GetString() ?? "").Trim();
        if (root.TryGetProperty("tiktokUser", out var tu) && tu.ValueKind == JsonValueKind.String)
            _cfg.TikTok.Username = (tu.GetString() ?? "").Trim();
        if (root.TryGetProperty("twitchClientId", out var cid) && cid.ValueKind == JsonValueKind.String)
            _cfg.Twitch.ClientId = (cid.GetString() ?? "").Trim();
        if (root.TryGetProperty("twitchClientSecret", out var csec) && csec.ValueKind == JsonValueKind.String)
            _cfg.Twitch.ClientSecret = (csec.GetString() ?? "").Trim();
        if (root.TryGetProperty("musicCommand", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            _cfg.Music.Command = (cmd.GetString() ?? "!sr").Trim();
        if (root.TryGetProperty("cookiesFile", out var cook) && cook.ValueKind == JsonValueKind.String)
            _cfg.Music.YtDlpCookiesFile = (cook.GetString() ?? "").Trim();
        _cfg.Music.Command = ClampText(root, "musicCommand", _cfg.Music.Command, "!sr", 20);
        _cfg.Music.MaxTrackMinutes = ClampInt(root, "maxTrackMinutes", _cfg.Music.MaxTrackMinutes, 1, 60);
        _cfg.Music.MaxQueueLength = ClampInt(root, "maxQueueLength", _cfg.Music.MaxQueueLength, 1, 100);
        _cfg.Music.MaxQueryLength = ClampInt(root, "maxQueryLength", _cfg.Music.MaxQueryLength, 1, 200);
        _cfg.Music.RateLimitSeconds = ClampInt(root, "rateLimitSeconds", _cfg.Music.RateLimitSeconds, 0, 300);
        _cfg.Music.GlobalCooldownSeconds = ClampInt(root, "globalCooldownSeconds", _cfg.Music.GlobalCooldownSeconds, 0, 300);
        _cfg.Music.DefaultVolume = ClampInt(root, "defaultVolume", _cfg.Music.DefaultVolume, 0, 100);
        if (root.TryGetProperty("autoNextRadio", out var radio) && (radio.ValueKind == JsonValueKind.True || radio.ValueKind == JsonValueKind.False))
            _cfg.Music.AutoNextRadio = radio.GetBoolean();
        if (root.TryGetProperty("requestsOpen", out var req) && (req.ValueKind == JsonValueKind.True || req.ValueKind == JsonValueKind.False))
            _cfg.Music.RequestsOpen = req.GetBoolean();
        _cfg.Save();
        ConfigApplied?.Invoke();
    }

    static string ClampText(JsonElement root, string name, string current, string fallback, int maxLen)
    {
        if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = (v.GetString() ?? "").Trim();
            if (s.Length == 0) return fallback;
            return s.Length > maxLen ? s.Substring(0, maxLen) : s;
        }
        return current;
    }

    static int ClampInt(JsonElement root, string name, int current, int min, int max)
    {
        if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            return Math.Clamp(v.GetInt32(), min, max);
        return current;
    }

    object BuildLogs(bool clear)
    {
        if (clear)
        {
            try
            {
                var f = Path.Combine(AppPaths.BaseDir, "logs", "app.log");
                if (File.Exists(f)) File.Delete(f);
                return new { type = "logs", lines = Array.Empty<string>(), error = (string?)null };
            }
            catch (Exception ex)
            {
                return new { type = "logs", lines = Array.Empty<string>(), error = ex.Message };
            }
        }
        var tail = new List<string>();
        try
        {
            var f = Path.Combine(AppPaths.BaseDir, "logs", "app.log");
            if (File.Exists(f))
            {
                tail = File.ReadLines(f).ToList();
                if (tail.Count > 300) tail = tail.GetRange(tail.Count - 300, 300);
                tail.Reverse();
            }
        }
        catch { }
        return new { type = "logs", lines = tail, error = (string?)null };
    }

    static double[]? ReadDoubles(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        using var e = el.EnumerateArray();
        var list = new List<double>();
        while (e.MoveNext())
        {
            if (e.Current.ValueKind == JsonValueKind.Number) list.Add(e.Current.GetDouble());
            else return null;
        }
        return list.ToArray();
    }

    static TrackResult ToResult(JsonElement el, double durationFallback)
    {
        double d = el.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number
            ? du.GetDouble() : durationFallback;
        return new TrackResult
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Title = el.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "",
            Channel = el.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" : "",
            Duration = d,
        };
    }

    public object BuildInit() => new
    {
        type = "init",
        theme = _theme,
        app = new
        {
            name = _cfg.AppName,
            layout = _cfg.Layout,
            command = _cfg.Music.Command,
            maxTrackMinutes = _cfg.Music.MaxTrackMinutes,
            maxQueueLength = _cfg.Music.MaxQueueLength,
            maxQueryLength = _cfg.Music.MaxQueryLength,
            rateLimitSeconds = _cfg.Music.RateLimitSeconds,
            globalCooldownSeconds = _cfg.Music.GlobalCooldownSeconds,
            autoNextRadio = _cfg.Music.AutoNextRadio,
            requestsOpen = _cfg.Music.RequestsOpen,
            defaultVolume = _cfg.Music.DefaultVolume,
            tiktokUser = _cfg.TikTok.Username,
            twitchChannel = _cfg.Twitch.Channel,
            volume = _cfg.Music.DefaultVolume,
        },
        setup = new
        {
            required = string.IsNullOrWhiteSpace(_cfg.Twitch.Channel) && string.IsNullOrWhiteSpace(_cfg.TikTok.Username),
        },
        account = new
        {
            twitchChannel = _cfg.Twitch.Channel,
            tiktokUser = _cfg.TikTok.Username,
        },
        conn = new
        {
            twitch = new { ok = TwitchConnected, detail = TwitchDetail },
            tiktok = new { ok = TikTokConnected, detail = TikTokDetail },
        },
        twitchViewers = TwitchViewers,
        chat = _hub.Snapshot().Skip(Math.Max(0, _hub.Snapshot().Count - 200)).Select(ChatDto),
        activity = _hub.ActivitySnapshot().Skip(Math.Max(0, _hub.ActivitySnapshot().Count - 150)).Select(ActivityDto),
        stats = StatsDto(),
        music = MusicDto(),
        update = new
        {
            current = UpdateService.ReadCurrentVersion(),
            status = Updater?.Status ?? "idle",
            latest = Updater?.LatestVersion ?? "",
            ready = Updater?.Ready ?? false,
        },
    };

    public void Broadcast(object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        foreach (var ws in _clients.Keys)
            if (ws.State == WebSocketState.Open)
                _ = SendSerializedAsync(ws, bytes);
    }

    public void PublishChat(ChatEntry e) => Broadcast(new { type = "chat", entry = ChatDto(e) });

    public void PublishStats() => Broadcast(new { type = "stats", stats = StatsDto() });

    public void PublishMusic() => Interlocked.Exchange(ref _musicDirty, 1);

    public void PublishActivity(ActivityEntry a) => Broadcast(new { type = "activity", text = a.Text, color = a.ColorHex, kind = a.Kind });

    public void PublishNotice(string text) => Broadcast(new { type = "notice", text });

    public void SetConn(ChatPlatform platform, bool ok, string detail)
    {
        if (platform == ChatPlatform.Twitch)
        {
            TwitchConnected = ok;
            TwitchDetail = detail;
        }
        else
        {
            TikTokConnected = ok;
            TikTokDetail = detail;
        }
        Broadcast(new
        {
            type = "conn",
            twitch = new { ok = TwitchConnected, detail = TwitchDetail },
            tiktok = new { ok = TikTokConnected, detail = TikTokDetail },
        });
    }

    public void SetTwitchViewers(long value)
    {
        TwitchViewers = value;
        Broadcast(new { type = "twitch-viewers", value });
    }

    async Task SendTo(WebSocket ws, object payload)
    {
        if (ws.State != WebSocketState.Open) return;
        await SendSerializedAsync(ws, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    async Task SendSerializedAsync(WebSocket ws, byte[] bytes)
    {
        var gate = _sendLocks.GetOrAdd(ws, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync();
            try
            {
                if (ws.State != WebSocketState.Open) return;
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { gate.Release(); }
        }
        catch
        {
            _clients.TryRemove(ws, out _);
        }
    }

    static object ChatDto(ChatEntry e) => new
    {
        time = e.Time.ToString("HH:mm:ss"),
        role = e.Role.ToString().ToLowerInvariant(),
        platform = e.Platform?.ToString().ToLowerInvariant(),
        user = e.Username,
        msg = e.Message,
        color = e.ColorHex,
        tag = e.Tag,
        isMod = e.IsMod,
        isBroad = e.IsBroadcaster,
        fanclubBadge = e.FanclubBadge,
        fanclubName = e.FanclubName,
        fanclubLevel = e.FanclubLevel,
        avatar = e.AvatarUrl,
        profileUrl = e.ProfileUrl,
    };

    public void PublishAvatar(ChatPlatform platform, string user, string avatarUrl) => Broadcast(new
    {
        type = "chat-avatar",
        platform = platform.ToString().ToLowerInvariant(),
        user,
        avatar = avatarUrl,
    });

    static object ActivityDto(ActivityEntry e) => new
    {
        time = e.Time.ToString("HH:mm:ss"),
        kind = e.Kind,
        text = e.Text,
        color = e.ColorHex,
    };

    public object StatsDto()
    {
        var perMin = _hub.MessagesPerMinute();
        return new
        {
            total = _hub.TotalMessages,
            twitch = _hub.TwitchMessages,
            tiktok = _hub.TikTokMessages,
            twitchPerMin = perMin.Twitch,
            tiktokPerMin = perMin.TikTok,
            twitchViewers = TwitchViewers,
            viewers = _hub.ViewersTikTok,
            peakViewers = _hub.PeakViewers,
            likes = _hub.Liked,
            totalLikes = _hub.TotalLikes,
            gifts = _hub.Gifts,
            giftValue = _hub.GiftValue,
            follows = _hub.Follows,
            shares = _hub.Shares,
            joins = _hub.Joins,
        };
    }

    public object MusicDto()
    {
        lock (_music)
        {
            var dto = new
            {
                now = NowDto(_music.NowPlaying),
                queue = _music.QueueSnapshot.Select(NowDto),
                liked = _music.LikedSnapshot.Select(NowDto),
                history = _music.HistorySnapshot.Select(NowDto),
                blocked = _music.BlockedSnapshot.Select(NowDto),
                volume = _cfg.Music.DefaultVolume,
                command = _cfg.Music.Command,
                maxTrackMinutes = _cfg.Music.MaxTrackMinutes,
                position = _music.Position,
                playing = _music.Playing,
                radio = _cfg.Music.AutoNextRadio,
                eq = _cfg.Music.Equalizer,
                loudness = _cfg.Music.Loudness,
            };
            return dto;
        }
    }

    static object? NowDto(Track? t)
    {
        if (t == null) return null;
        return new
        {
            id = t.Result.Id,
            title = t.Title,
            channel = t.Result.Channel,
            duration = t.Result.Duration,
            durationLabel = t.DurationLabel,
            by = t.RequestedBy,
            platform = t.RequestedPlatform?.ToString().ToLowerInvariant(),
        };
    }
}