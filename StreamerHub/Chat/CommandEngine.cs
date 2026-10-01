namespace StreamerHub;

public sealed class CommandEngine
{
    // Chat volume bounds, in one place so the validation, the clamp and the
    // UI hint cannot drift apart. Low enough that a mod cannot deafen the
    // stream, high enough to actually turn it down.
    public const int VolumeMin = 5;
    public const int VolumeMax = 50;
    public static readonly string VolumeUsage = "usage: !volume " + VolumeMin + "-" + VolumeMax;

    readonly MusicConfig _cfg;
    readonly MusicEngine _player;
    readonly ChatHub _hub;
    readonly YoutubeResolver _resolver;

    readonly object _gate = new();
    readonly Dictionary<string, DateTime> _lastRequest = new();
    readonly HashSet<string> _inflight = new();
    DateTime _lastGlobal = DateTime.MinValue;

    public CommandEngine(MusicConfig cfg, MusicEngine player, ChatHub hub, YoutubeResolver resolver)
    {
        _cfg = cfg;
        _player = player;
        _hub = hub;
        _resolver = resolver;
    }

    public Action? PersistRequested;
    public Action<int>? VolumeChanged;
    public Action<bool>? PauseChanged;
    public Action<bool>? RequestsChanged;

    public void Handle(ChatPlatform platform, string username, string message, bool isMod = false, bool isBroadcaster = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var m = message.Trim();
        var parts = m.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        if (verb.Length < 2 || verb[0] != '!') return;

        var isStaff = isMod;

        if (verb == "!volume" || verb == "!requests" || verb == "!play" || verb == "!pause")
        {
            if (!isStaff)
            {
                Reply(platform, "that command is mods only");
                return;
            }
            if (verb == "!volume")
            {
                if (!_cfg.ModVolume) { Reply(platform, "!volume is turned off"); return; }
                if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out var v) || v < VolumeMin || v > VolumeMax)
                {
                    Reply(platform, VolumeUsage);
                    return;
                }
                VolumeChanged?.Invoke(v);
                Reply(platform, "volume set to " + v);
                return;
            }
            if (verb == "!requests")
            {
                if (!_cfg.ModRequests) { Reply(platform, "!requests is turned off"); return; }
                var arg = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "";
                if (arg != "on" && arg != "off")
                {
                    Reply(platform, "usage: !requests on/off");
                    return;
                }
                var on = arg == "on";
                _cfg.RequestsOpen = on;
                PersistRequested?.Invoke();
                RequestsChanged?.Invoke(on);
                Reply(platform, "requests " + (on ? "opened" : "closed"));
                return;
            }
            if (!_cfg.ModTransport) { Reply(platform, verb + " is turned off"); return; }
            PauseChanged?.Invoke(verb == "!pause");
            Reply(platform, verb == "!pause" ? "paused" : "playing");
            return;
        }

        if (verb == "!skip" || verb == "!revoke" || verb == "!dq")
        {
            if (!isStaff)
            {
                Reply(platform, "that command is mods only");
                return;
            }
            if (!_cfg.ModSkip) { Reply(platform, verb + " is turned off"); return; }
            if (verb == "!dq")
            {
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                {
                    Reply(platform, "usage: " + verb + " <#> or <#,#,#>");
                    return;
                }
                var nums = new List<int>();
                foreach (var piece in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(piece, out var v))
                    {
                        var idx = v - 1;
                        if (idx >= 0 && !nums.Contains(idx)) nums.Add(idx);
                    }
                }
                if (nums.Count == 0)
                {
                    Reply(platform, "no valid queue numbers, usage: " + verb + " <#,#,#>");
                    return;
                }
                var removedParts = new List<string>();
                foreach (var idx in nums.OrderByDescending(x => x))
                {
                    var title = _player.RemoveAt(idx);
                    if (title != null) removedParts.Add(title);
                }
                if (removedParts.Count == 0)
                {
                    Reply(platform, "nothing was removed - check the queue numbers");
                    return;
                }
                Reply(platform, "removed " + removedParts.Count + ": " + string.Join(" | ", removedParts));
                return;
            }
            if (verb == "!skip")
            {
                if (_player.NowPlaying == null)
                {
                    Reply(platform, "nothing is playing to skip");
                    return;
                }
                _player.Skip();
                Reply(platform, "skipped by " + username);
            }
            else
            {
                var current = _player.NowPlaying;
                if (current == null)
                {
                    Reply(platform, "nothing is playing to revoke");
                    return;
                }
                _player.Block(current.Result);
                Reply(platform, "blocked: " + current.Result.Title + " - it won't play again");
            }
            return;
        }

        if (verb != _cfg.Command.ToLowerInvariant()) return;

        if (!_cfg.RequestsOpen)
        {
            Log.Info($"!sr closed, ignoring request from {username} [{platform}]");
            return;
        }

        var query = parts.Length > 1 ? parts[1].Trim() : "";
        if (query.Length == 0)
        {
            Log.Info($"!sr usage from {username} [{platform}] (no query)");
            Reply(platform, _cfg.Command + " <song name> to add a track");
            return;
        }
        if (LooksLikeLink(query))
        {
            Log.Info($"!sr link rejected from {username} [{platform}]");
            Reply(platform, "links aren't allowed, send the song name instead");
            return;
        }

        var now = DateTime.Now;
        lock (_gate)
        {
            if (_lastRequest.TryGetValue(username, out var last))
            {
                var wait = _cfg.RateLimitSeconds - (now - last).TotalSeconds;
                if (wait > 0)
                {
                    Log.Info($"!sr rate-limited: {username} [{platform}]");
                    Reply(platform, "wait " + Math.Ceiling(wait) + "s before the next request");
                    return;
                }
            }
            if (_inflight.Contains(username))
            {
                Log.Info($"!sr inflight dup: {username} [{platform}]");
                Reply(platform, "already searching a request for you");
                return;
            }
            if ((now - _lastGlobal).TotalSeconds < _cfg.GlobalCooldownSeconds)
            {
                Log.Info($"!sr global cooldown: {username} [{platform}]");
                Reply(platform, "slow down, try again in a moment");
                return;
            }
            _lastRequest[username] = now;
            _lastGlobal = now;
            _inflight.Add(username);
        }

        if (query.Length > _cfg.MaxQueryLength)
        {
            query = query.Substring(0, _cfg.MaxQueryLength);
        }

        Log.Info($"!sr accepted: {username} [{platform}] query=\"{query}\"");
        _ = Task.Run(async () =>
        {
            try
            {
                var results = await _resolver.SearchAsync(query);
                var pick = results.FirstOrDefault(r => r.Duration > 0);
                if (pick == null)
                {
                    Log.Warn($"!sr no playable result for: \"{query}\" from {username}");
                    Reply(platform, "no playable result for that, try a clearer title");
                    return;
                }
                var notice = _player.Request(pick, username, platform);
                Log.Info($"!sr queued for {username}: {pick.Title}");
                Reply(platform, notice);
            }
            catch (Exception ex)
            {
                Log.Warn("request failed: " + ex.Message);
                Reply(platform, "something went wrong while searching");
            }
            finally
            {
                lock (_gate) _inflight.Remove(username);
            }
        });
    }

    void Reply(ChatPlatform platform, string text)
    {
        if (text.Length == 0) return;
        _hub.Bot(text);
    }

    static bool LooksLikeLink(string s)
    {
        var t = s.Trim();
        if (t.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
        var lower = t.ToLowerInvariant();
        return lower.Contains("youtube.com/") || lower.Contains("youtu.be/") || lower.Contains("watch?v=");
    }
}