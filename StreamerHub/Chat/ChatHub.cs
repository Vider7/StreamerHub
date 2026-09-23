namespace StreamerHub;

public sealed class ChatHub
{
    static readonly string[] Palette =
    {
        "#E0A25E", "#C89BD9", "#D9A896", "#A9C1D9", "#C9B7A0",
        "#C79BC9", "#D9CD9B", "#A3C9AE", "#B7A9D9", "#D99B6C",
    };

    readonly object _gate = new();
    readonly List<ChatEntry> _messages = new();
    readonly List<ActivityEntry> _activity = new();
    readonly List<DateTime> _windowTwitch = new();
    readonly List<DateTime> _windowTikTok = new();

    const int MessageCap = 600;
    const int ActivityCap = 200;

    public ChatHub()
    {
    }

    public long TotalMessages { get; private set; }
    public long TwitchMessages { get; private set; }
    public long TikTokMessages { get; private set; }
    public int ViewersTikTok { get; private set; }
    public int PeakViewers { get; private set; }
    public long Liked { get; private set; }
    public long TotalLikes { get; private set; }
    public long Gifts { get; private set; }
    public long GiftValue { get; private set; }
    public long Follows { get; private set; }
    public long Shares { get; private set; }
    public long Joins { get; private set; }

    public event Action<ChatEntry>? MessageAdded;
    public event Action? MessagesReset;
    public event Action? StatsChanged;
    public event Action<ActivityEntry>? ActivityAdded;

    public IReadOnlyList<ChatEntry> Snapshot()
    {
        lock (_gate) return _messages.ToArray();
    }

    public void Message(ChatPlatform platform, string username, string message, bool isMod = false, bool isBroadcaster = false)
    {
        var entry = new ChatEntry
        {
            Time = DateTime.Now,
            Role = ChatRole.Chat,
            Platform = platform,
            Username = username,
            Message = message,
            ColorHex = ColorFor(platform, username),
            Tag = platform == ChatPlatform.Twitch ? "TW" : "TT",
            IsMod = isMod,
            IsBroadcaster = isBroadcaster,
        };
        lock (_gate)
        {
            _messages.Add(entry);
            TrimLocked(platform);
            TotalMessages++;
            if (platform == ChatPlatform.Twitch)
            {
                TwitchMessages++;
                _windowTwitch.Add(DateTime.Now);
            }
            else
            {
                TikTokMessages++;
                _windowTikTok.Add(DateTime.Now);
            }
        }
        MessageAdded?.Invoke(entry);
        StatsChanged?.Invoke();
    }

    public void Bot(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Activity(text, "#E7E4EC", "bot");
    }

    public void ClearMessages()
    {
        lock (_gate)
        {
            _messages.Clear();
            _windowTwitch.Clear();
            _windowTikTok.Clear();
        }
        MessagesReset?.Invoke();
    }

    void TrimLocked(ChatPlatform? platform)
    {
        if (_messages.Count <= MessageCap) return;
        var drop = _messages.Count - MessageCap;
        for (var i = 0; i < drop; i++) _messages.RemoveAt(0);
        MessagesReset?.Invoke();
    }

    void AddActivityLocked(ActivityEntry e)
    {
        _activity.Add(e);
        if (_activity.Count > ActivityCap)
            _activity.RemoveRange(0, _activity.Count - ActivityCap);
    }

    public void ViewerCount(int viewers)
    {
        lock (_gate)
        {
            ViewersTikTok = viewers;
            if (viewers > PeakViewers) PeakViewers = viewers;
        }
        StatsChanged?.Invoke();
    }

    public void AddStat(long likes, long gifts, long giftValue, long follows, long shares, long joins)
    {
        lock (_gate)
        {
            Liked += likes;
            Gifts += gifts;
            GiftValue += giftValue;
            Follows += follows;
            Shares += shares;
            Joins += joins;
        }
        StatsChanged?.Invoke();
    }

    public void SetTotalLikes(long total)
    {
        lock (_gate) TotalLikes = total;
        StatsChanged?.Invoke();
    }

    public void Activity(string text, string colorHex, string kind = "system")
    {
        var entry = new ActivityEntry { Time = DateTime.Now, Kind = kind, Text = text, ColorHex = colorHex };
        lock (_gate) AddActivityLocked(entry);
        ActivityAdded?.Invoke(entry);
    }

    public IReadOnlyList<ActivityEntry> ActivitySnapshot()
    {
        lock (_gate) return _activity.ToArray();
    }

    public void ClearActivity()
    {
        lock (_gate) _activity.Clear();
    }

    public (int Twitch, int TikTok) MessagesPerMinute()
    {
        lock (_gate)
        {
            var cutoff = DateTime.Now.AddSeconds(-60);
            _windowTwitch.RemoveAll(t => t < cutoff);
            _windowTikTok.RemoveAll(t => t < cutoff);
            return (_windowTwitch.Count, _windowTikTok.Count);
        }
    }

    public string ColorFor(ChatPlatform platform, string username)
    {
        var hash = (int)((uint)username.GetHashCode() + (platform == ChatPlatform.Twitch ? 1u : 3u));
        return Palette[Math.Abs(hash) % Palette.Length];
    }
}