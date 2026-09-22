using System.IO;
using System.Text.Json;

namespace StreamerHub;

public sealed class TwitchConfig
{
    public string Channel { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public int PollViewersSeconds { get; set; } = 45;
}

public sealed class TikTokConfig
{
    public string Username { get; set; } = "";
    public string CustomSigningServer { get; set; } = "";
    public string SigningServerApiKey { get; set; } = "";
}

public sealed class MusicConfig
{
    public string Command { get; set; } = "!sr";
    public string YtDlpPath { get; set; } = "";
    public string YtDlpCookiesFile { get; set; } = "";
    public int DefaultVolume { get; set; } = 25;
    public int MaxTrackMinutes { get; set; } = 10;
    public int MaxQueueLength { get; set; } = 20;
    public int MaxQueryLength { get; set; } = 100;
    public int RateLimitSeconds { get; set; } = 15;
    public int GlobalCooldownSeconds { get; set; } = 5;
    public bool AutoNextRadio { get; set; } = true;
    public string MpvPath { get; set; } = "";
    public string AudioDevice { get; set; } = "";
    public double[] Equalizer { get; set; } = new double[EqBands];
    public bool Loudness { get; set; } = true;
    public LastPlayedState? LastPlayed { get; set; }
    public List<QueuedTrackState> Queue { get; set; } = new();
    public List<LikedTrackState> Liked { get; set; } = new();
    public List<BlockedTrackState> Blocked { get; set; } = new();

    public const int EqBands = 10;
}

public sealed class LastPlayedState
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public double Duration { get; set; }
    public double Position { get; set; }
    public bool Playing { get; set; } = true;
}

public sealed class LikedTrackState
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public double Duration { get; set; }
}

public sealed class BlockedTrackState
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public double Duration { get; set; }
}

public sealed class QueuedTrackState
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public double Duration { get; set; }
    public string RequestedBy { get; set; } = "";
    public string Platform { get; set; } = "";
}

public sealed class UpdaterConfig
{
    public bool Enabled { get; set; } = true;
    public string Feed { get; set; } = "";
    public double AutoCheckHours { get; set; } = 2.0;
}

public sealed class AppConfig
{
    public string AppName { get; set; } = "StreamerHub";
    public string Layout { get; set; } = "CSM";
    public int Port { get; set; } = 51324;
    public bool AutoOpenBrowser { get; set; } = true;
    public TwitchConfig Twitch { get; set; } = new();
    public TikTokConfig TikTok { get; set; } = new();
    public MusicConfig Music { get; set; } = new();
    public UpdaterConfig Updater { get; set; } = new();

    public static AppConfig Load()
    {
        var path = Path.Combine(AppPaths.BaseDir, "Config.json");
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            fresh.Save();
            return fresh;
        }
        try
        {
            var result = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts) ?? new AppConfig();
            if (result.Updater == null) result.Updater = new UpdaterConfig();
            return result;
        }
        catch (Exception e)
        {
            Log.Error("config parse failed: " + e.Message);
            return new AppConfig();
        }
    }

    public void Save()
    {
        lock (SaveGate)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppPaths.BaseDir, "Config.json"), JsonSerializer.Serialize(this, JsonOpts));
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 2)
                {
                    Thread.Sleep(120);
                }
                catch (Exception e)
                {
                    Log.Warn("config save failed: " + e.Message);
                    return;
                }
            }
        }
    }

    static readonly object SaveGate = new();
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}