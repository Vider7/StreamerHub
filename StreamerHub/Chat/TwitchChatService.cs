using TwitchLib.Client;
using TwitchLib.Client.Models;

namespace StreamerHub;

public sealed class TwitchChatService : IDisposable
{
    readonly AppConfig _cfg;
    readonly ChatHub _hub;
    readonly TwitchAvatarService? _avatars;
    TwitchClient? _client;
    string _channel = "";
    string _username = "";
    CancellationTokenSource? _cts;
    int _generation;

    public event Action<bool, string>? ConnectionChanged;

    public TwitchChatService(AppConfig cfg, ChatHub hub, TwitchAvatarService? avatars = null)
    {
        _cfg = cfg;
        _hub = hub;
        _avatars = avatars;
    }

    public void Start()
    {
        var channel = (_cfg.Twitch.Channel ?? "").Trim().ToLowerInvariant();
        if (channel.Length == 0)
        {
            ConnectionChanged?.Invoke(false, "no channel in config");
            return;
        }
        _channel = channel;
        _username = "justinfan" + Random.Shared.Next(10000, 99999);
        Log.Info($"twitch chat: connecting to #{channel} (anonymous read-only)");

        var gen = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        _ = Task.Run(() => Supervisor(channel, gen, token));
    }

    async Task Supervisor(string channel, int gen, CancellationToken token)
    {
        var delay = TimeSpan.Zero;
        while (!token.IsCancellationRequested)
        {
            if (delay > TimeSpan.Zero)
            {
                Log.Info($"twitch chat: reconnecting in {delay.TotalSeconds:0}s");
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { return; }
            }
            try
            {
                await RunClientAsync(channel, gen, token);
                delay = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("twitch chat: connection lost (" + ex.Message + "), will retry");
                delay = delay <= TimeSpan.Zero
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    async Task RunClientAsync(string channel, int gen, CancellationToken token)
    {
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? reason = null;

        TwitchClient client;
        try
        {
            client = new TwitchClient();
            client.Initialize(new ConnectionCredentials(_username, "SCHMOOPIIE"), channel);
            client.OnConnected += (_, _) =>
            {
                if (gen != _generation) return;
                ConnectionChanged?.Invoke(true, "connected to #" + channel);
            };
            client.OnMessageReceived += (_, e) => HandleMessage(e);
            client.OnConnectionError += (_, e) =>
            {
                if (gen == _generation) reason = "join error: " + e.Error.Message;
                dropped.TrySetResult();
            };
            client.OnDisconnected += (_, _) =>
            {
                if (gen == _generation) reason ??= "disconnected";
                dropped.TrySetResult();
            };
            client.Connect();
            _client = client;
        }
        catch (Exception ex)
        {
            Log.Warn("twitch chat: connect failed (" + ex.Message + "), will retry");
            return;
        }

        try
        {
            await dropped.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
        }
        catch (TimeoutException)
        {
            reason = "connect timeout";
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Log.Warn("twitch chat: connection lost (" + (reason ?? "unknown") + ")");
        try { client.Disconnect(); } catch { }
        
        if (ReferenceEquals(_client, client)) _client = null;
    }

    void HandleMessage(TwitchLib.Client.Events.OnMessageReceivedArgs e)
    {
        var m = e.ChatMessage;
        if (string.IsNullOrEmpty(m.Username) || m.Message == null) return;
        var login = m.Username.Trim().ToLowerInvariant();
        var avatar = _avatars?.GetCached(login) ?? "";
        if (avatar.Length == 0)
        {
            if (_avatars?.HasCredentials == true) _avatars.Request(login);
            else avatar = ChatAvatars.TwitchAvatarFallback(login);
        }
        _hub.Message(ChatPlatform.Twitch, m.Username, m.Message.Trim(),
            m.IsModerator || m.IsBroadcaster, m.IsBroadcaster,
            "", "", 0, avatar, ChatAvatars.TwitchProfileUrl(login));
    }

    public void Send(string text)
    {
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        try { _cts?.Cancel(); } catch { }
        try { _client?.Disconnect(); } catch { }
        if (_client != null) ConnectionChanged?.Invoke(false, "disconnected");
        _client = null;
    }

    public void Dispose() => Stop();
}
