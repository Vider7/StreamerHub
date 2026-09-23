namespace StreamerHub;

public enum ChatPlatform { Twitch, TikTok }

public enum ChatRole { Chat, Bot }

public sealed class ChatEntry
{
    public DateTime Time { get; init; }
    public ChatRole Role { get; init; }
    public ChatPlatform? Platform { get; init; }
    public string Username { get; init; } = "";
    public string Message { get; init; } = "";
    public string ColorHex { get; init; } = "";
    public string Tag { get; init; } = "";
    public bool IsMod { get; init; }
    public bool IsBroadcaster { get; init; }
    public string FanclubBadge { get; init; } = "";
    public string FanclubName { get; init; } = "";
    public int FanclubLevel { get; init; }
}

public sealed class TrackResult
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public double Duration { get; init; }
    public string Channel { get; init; } = "";
    public string DurationLabel => FormatDuration(Duration);

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(double.IsFinite(seconds) ? seconds : 0);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

public sealed class Track
{
    public TrackResult Result { get; init; } = new();
    public string RequestedBy { get; init; } = "";
    public ChatPlatform? RequestedPlatform { get; init; }
    public string Title => Result.Title;
    public string DurationLabel => Result.DurationLabel;
}

public sealed class ActivityEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Kind { get; init; } = "system";
    public string Text { get; init; } = "";
    public string ColorHex { get; init; } = "";
}