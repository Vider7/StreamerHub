using System.Collections.Concurrent;
using TikTokLiveSharp.Client;
using TikTokLiveSharp.Client.Config;
using TikTokLiveSharp.Debugging;
using TikTokLiveSharp.Events;

namespace StreamerHub;

public sealed class TikTokChatService : IDisposable
{
    readonly AppConfig _cfg;
    readonly ChatHub _hub;
    TikTokLiveClient? _client;
    CancellationTokenSource? _cts;
    int _generation;
    readonly ConcurrentDictionary<(string User, long GiftId), long> _streakTally = new();

    public event Action<bool, string>? ConnectionChanged;

    public TikTokChatService(AppConfig cfg, ChatHub hub)
    {
        _cfg = cfg;
        _hub = hub;
    }

    public void Start()
    {
        var username = (_cfg.TikTok.Username ?? "").Trim();
        if (username.StartsWith("@")) username = username.Substring(1);
        if (username.Length == 0)
        {
            ConnectionChanged?.Invoke(false, "no username in config");
            return;
        }
        Log.Info($"tiktok: connecting to @{username}");

        var gen = Interlocked.Increment(ref _generation);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => Supervisor(username, gen, token));
    }

    async Task Supervisor(string username, int gen, CancellationToken token)
    {
        var delay = TimeSpan.Zero;
        while (!token.IsCancellationRequested)
        {
            if (delay > TimeSpan.Zero)
            {
                Log.Info($"tiktok: reconnecting in {delay.TotalSeconds:0}s");
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { return; }
            }
            try
            {
                await RunClientAsync(username, gen, token);
                delay = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("tiktok: connection lost (" + ex.Message + "), will retry");
                delay = delay <= TimeSpan.Zero
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    async Task RunClientAsync(string username, int gen, CancellationToken token)
    {
        var c = new TikTokLiveClient(
            uniqueID: username,
            logDebug: false,
            logLevel: LogLevel.Error | LogLevel.Warning,
            customSigningServer: NullIfEmpty(_cfg.TikTok.CustomSigningServer),
            signingServerApiKey: NullIfEmpty(_cfg.TikTok.SigningServerApiKey));

        c.OnConnected += (_, _) =>
        {
            if (gen != _generation) return;
            ConnectionChanged?.Invoke(true, "connected to @" + username);
        };
        c.OnDisconnected += (_, _) =>
        {
            if (gen != _generation) return;
            ConnectionChanged?.Invoke(false, "disconnected");
        };
        c.OnChatMessage += (_, e) =>
        {
            var user = e.Sender?.UniqueId ?? "?";
            var msg = e.Message;
            var isMod = e.UserIdentity?.IsModeratorOfHost == true;
            var isBroad = e.UserIdentity?.IsHost == true;
            if (!string.IsNullOrWhiteSpace(msg))
                _hub.Message(ChatPlatform.TikTok, user, msg.Trim(), isMod, isBroad);
        };
        c.OnGiftMessage += (t, e) =>
        {
            var name = e.User?.UniqueId ?? "?";
            var gift = e.Gift?.Name ?? "gift";
            var cost = (long)(e.Gift?.DiamondCost ?? 0);
            var streak = Math.Max(e.RepeatCount, e.Amount); // protocol returns the combo's cumulative running total
            if (streak <= 0) return;                        // stripped/unusable frame, nothing to count

            var key = (name, e.GiftId);
            var last = _streakTally.TryGetValue(key, out var prev) ? prev : 0L;
            if (streak < last && streak <= 1)               // a new streak restarted for the same giver+gift
            {
                last = 0;
                _streakTally.TryRemove(key, out _);
            }
            if (streak > last)
            {
                var delta = streak - last;
                var diamonds = cost * delta;
                _streakTally[key] = streak;
                if (diamonds > 0)
                {
                    _hub.AddStat(0, delta, diamonds, 0, 0, 0);
                    _hub.Activity($"{name} sent {streak}x {gift}", "#FF9F1C", "gift");
                }
                Log.Info($"tiktok gift: {name} {gift} streak={streak} delta={delta} diamonds={diamonds}");
            }
            if (e.StreakEnd) _streakTally.TryRemove(key, out _);
        };
        c.OnFollow += (_, e) =>
        {
            var name = e.User?.UniqueId ?? "?";
            _hub.AddStat(0, 0, 0, 1, 0, 0);
            _hub.Activity($"{name} followed", "#A06BFF", "follow");
        };
        c.OnShare += (_, e) =>
        {
            var name = e.User?.UniqueId ?? "?";
            _hub.AddStat(0, 0, 0, 0, 1, 0);
            _hub.Activity($"{name} shared the live", "#E0A25E", "share");
        };
        c.OnLike += (_, e) =>
        {
            if (e.Total > 0) _hub.SetTotalLikes(e.Total);
            else _hub.AddStat(1, 0, 0, 0, 0, 0);
        };
        c.OnSubscribe += (_, e) =>
        {
            var name = e.User?.UniqueId ?? "?";
            _hub.Activity($"{name} subscribed", "#C89BD9", "subscribe");
        };
        c.OnJoin += (_, e) =>
        {
            _hub.AddStat(0, 0, 0, 0, 0, 1);
        };
        c.OnRoomUpdate += (_, e) =>
        {
            _hub.ViewerCount((int)e.NumberOfViewers);
        };
        c.OnLiveEnded += (_, _) =>
        {
            if (gen != _generation) return;
            ConnectionChanged?.Invoke(false, "live ended");
        };

        _client = c;
        Log.Info("tiktok: connected, waiting for a live");
        await Task.Run(() => c.Run(token), token);
    }

    static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        try { _cts?.Cancel(); } catch { }
        try { if (_client != null) { var _ = _client.Stop(); } } catch { }
        if (_client != null) ConnectionChanged?.Invoke(false, "disconnected");
        _client = null;
    }

    public void Dispose() => Stop();
}