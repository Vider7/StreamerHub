using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace StreamerHub;

internal static class Program
{
    static AppConfig _cfg = new();
    static ChatHub? _hub;
    static TwitchChatService? _twitch;
    static TikTokChatService? _tiktok;
    static TwitchStatsService? _stats;
    static MusicEngine? _music;
    static MpvPlayer? _mpv;
    static UpdateService? _updater;
    static double _lastPos;
    static bool _lastPaused;
    static string? _resolvingId;
    static double _resumeSeek;
    static bool _resumePlaying = true;
    static DateTime _lastSnapshotUtc = DateTime.MinValue;

    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--apply-update", StringComparer.OrdinalIgnoreCase))
        {
            return UpdateService.RunApplyUpdate(args);
        }

        Log.Init(AppPaths.BaseDir);
        Log.Info("StreamerHub server starting");

        using var singleInstance = new Mutex(true, @"Global\StreamerHub.Server", out var createdNew);
        if (!createdNew)
        {
            Log.Info("another StreamerHub server is already running; exiting this instance");
            return 0;
        }

        _cfg = AppConfig.Load();
        var port = _cfg.Port;
        var portArg = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase));
        if (portArg != null && int.TryParse(portArg.Split('=')[1], out var parsedPort)) port = parsedPort;

        var hub = new ChatHub();
        var resolver = new YoutubeResolver(_cfg.Music);
        var music = new MusicEngine(_cfg.Music, resolver);
        music.PersistRequested += () => _cfg.Save();
        var twitch = new TwitchChatService(_cfg, hub);
        var tiktok = new TikTokChatService(_cfg, hub);
        var stats = new TwitchStatsService(_cfg);

        var mpv = new MpvPlayer(MpvPlayer.Locate(_cfg.Music.MpvPath, "mpv"), _cfg.Music.AudioDevice);
        var mpvOk = mpv.Start(_cfg.Music.DefaultVolume, MpvPlayer.BuildAf(_cfg.Music.Equalizer, _cfg.Music.Loudness));
        var ws = new WebSocketHub(_cfg, hub, music, mpv);
        var updater = new UpdateService(_cfg.Updater, m => ws.Broadcast(m));
        ws.Updater = updater;

        _hub = hub;
        _music = music;
        _twitch = twitch;
        _tiktok = tiktok;
        _stats = stats;
        _mpv = mpv;
        _updater = updater;

        ws.ConfigApplied = () =>
        {
            if (_twitch != null) { _twitch.Stop(); _twitch.Start(); }
            if (_tiktok != null) { _tiktok.Stop(); _tiktok.Start(); }
            if (_stats != null) { _stats.Stop(); _stats.Start(); }
            ws.PublishNotice("settings saved - reconnecting apps");
            ws.Broadcast(ws.BuildInit());
        };

        var commands = new CommandEngine(_cfg.Music, music, hub, resolver);
        hub.MessageAdded += entry =>
        {
            if (entry.Role != ChatRole.Chat || !entry.Platform.HasValue) return;
            commands.Handle(entry.Platform.Value, entry.Username, entry.Message, entry.IsMod, entry.IsBroadcaster);
        };

        hub.MessageAdded += ws.PublishChat;
        hub.StatsChanged += ws.PublishStats;
        hub.ActivityAdded += ws.PublishActivity;
        music.StateChanged += () => SyncMusic();
        var prewarming = new HashSet<string>();
        music.StateChanged += () => PrewarmNext();

        void PrewarmNext()
        {
            var ids = new List<string>();
            foreach (var q in music.QueueSnapshot)
            {
                if (ids.Count >= 3) break;
                if (!AudioStream.IsCached(q.Result.Id) && q.Result.Id != mpv.CurrentId) ids.Add(q.Result.Id);
            }
            foreach (var id in ids)
            {
                lock (prewarming)
                {
                    if (!prewarming.Add(id)) continue;
                }
                _ = PrewarmOneAsync(id);
            }
        }

        async Task PrewarmOneAsync(string id)
        {
            try { await AudioStream.PrewarmAsync(music, id); }
            catch { }
            finally { lock (prewarming) prewarming.Remove(id); }
        }
        music.Notice += m => Log.Info("music: " + m);
        stats.Notice += m => Log.Info("stats: " + m);
        RestoreLastPlayed(music, mpv);
        mpv.Ended += () => music.OnClientEnded();
        mpv.Failed += () => music.OnClientFailed();
        mpv.Retrying += RetryResolve;
        mpv.PositionChanged += SyncMpv;
        twitch.ConnectionChanged += (ok, detail) => ws.SetConn(ChatPlatform.Twitch, ok, detail);
        tiktok.ConnectionChanged += (ok, detail) => ws.SetConn(ChatPlatform.TikTok, ok, detail);
        stats.ViewerCountChanged += ws.SetTwitchViewers;

        void SyncMusic()
        {
            ws.PublishMusic();
            if (!mpv.Available) return;
            var now = music.NowPlaying;
            if (now == null)
            {
                if (mpv.CurrentId != null)
                {
                    mpv.CurrentId = null;
                    mpv.StopAudio();
                }
                return;
            }
            if (mpv.CurrentId != now.Result.Id)
            {
                mpv.CurrentId = now.Result.Id;
                PlayResolved(now.Result.Id);
            }
        }

        async void PlayResolved(string id)
        {
            if (_resolvingId == id) return;
            _resolvingId = id;
            var resume = _resumeSeek > 0;
            var resumePos = _resumeSeek;
            var resumePlay = _resumePlaying;
            _resumeSeek = 0;
            _resumePlaying = true;
            try
            {
                if (mpv.CurrentId != id) return;
                var url = StreamUrl(id);
                if (resume)
                {
                    mpv.Resume(url, resumePos, resumePlay);
                }
                else
                {
                    mpv.Play(url);
                }
            }
            finally
            {
                _resolvingId = null;
            }
        }

        void RetryResolve()
        {
            var id = _mpv?.CurrentId;
            if (id == null || _resolvingId == id) return;
            if (_mpv?.CurrentId != id) return;
            Log.Warn("retrying with a fresh proxy stream for " + id);
            _resumeSeek = 0;
            _resumePlaying = true;
            _mpv.RetryPlay(StreamUrl(id));
        }

        string StreamUrl(string id) => "http://127.0.0.1:" + port + "/api/stream/" + id;

        void SyncMpv()
        {
            var now = music.NowPlaying;
            if (now != null && mpv.Available)
            {
                music.SetPosition(now.Result.Id, mpv.Position, !mpv.Paused);
            }
            if (!mpv.Available) return;
            if (Math.Abs(mpv.Position - _lastPos) < 0.5 && mpv.Paused == _lastPaused) return;
            _lastPos = mpv.Position;
            _lastPaused = mpv.Paused;
            ws.Broadcast(new { type = "music-time", position = _lastPos, playing = !_lastPaused });
            if ((DateTime.UtcNow - _lastSnapshotUtc).TotalSeconds > 20)
            {
                _lastSnapshotUtc = DateTime.UtcNow;
                try
                {
                    CaptureLastPlayed();
                    _cfg.Save();
                }
                catch { }
            }
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownServices();
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("crash: " + a.ExceptionObject);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppPaths.BaseDir,
            WebRootPath = Path.Combine(AppPaths.BaseDir, "wwwroot"),
        });
        builder.WebHost.UseUrls("http://127.0.0.1:" + port);
        var app = builder.Build();

        app.UseWebSockets();
        app.Map("/ws", ws.Handle);
        app.MapGet("/api/health", () => Results.Ok(new { ok = true, name = _cfg.AppName }));
        app.MapGet("/api/stream/{id}", (HttpContext ctx, string id) => AudioStream.Handle(music, ctx, id));
        app.MapGet("/api/thumb/{id}", (HttpContext ctx, string id) => ThumbStream.Handle(ctx, id));
        app.UseDefaultFiles();
        app.UseStaticFiles();

        twitch.Start();
        tiktok.Start();
        stats.Start();
        updater.Start();

        var url = "http://127.0.0.1:" + port;
        if (_cfg.AutoOpenBrowser && !_cfg.BrowserOpened && Environment.GetEnvironmentVariable("STREAMERHUB_NO_BROWSER") != "1")
        {
            Log.Info("opening browser at " + url);
            OpenBrowser(url);
            _cfg.BrowserOpened = true;
            _cfg.Save();
        }
        Log.Info("listening on " + url);

        var quitApp = new Action(() =>
        {
            ShutdownServices();
            _ = app.StopAsync(CancellationToken.None);
        });
        updater.QuitRequested += quitApp;
        TrayApp.Start(url, quitApp, updater);

        await app.RunAsync();
        ShutdownServices();
        return 0;
    }

    static void RestoreLastPlayed(MusicEngine music, MpvPlayer mpv)
    {
        var last = _cfg.Music.LastPlayed;
        if (last == null || string.IsNullOrEmpty(last.Id) || !mpv.Available) return;
        Log.Info("resuming last track: " + (last.Title.Length > 0 ? last.Title : last.Id));
        _ = AudioStream.PrewarmAsync(music, last.Id);
        _resumeSeek = last.Position;
        _resumePlaying = last.Playing;
        music.Resume(new TrackResult
        {
            Id = last.Id,
            Title = last.Title.Length > 0 ? last.Title : "resumed track",
            Channel = last.Channel ?? "",
            Duration = last.Duration,
        });
    }

    static void ShutdownServices()
    {
        try { CaptureLastPlayed(); } catch { }
        try { _cfg.Save(); } catch { }
        try { _twitch?.Stop(); } catch { }
        try { _tiktok?.Stop(); } catch { }
        try { _stats?.Stop(); } catch { }
        try { _mpv?.Dispose(); } catch { }
        try { _updater?.Dispose(); } catch { }
    }

    static void CaptureLastPlayed()
    {
        if (_music?.NowPlaying is not { } np) return;
        if (_mpv is not { Available: true } m) return;
        _cfg.Music.LastPlayed = new LastPlayedState
        {
            Id = np.Result.Id,
            Title = np.Result.Title,
            Channel = np.Result.Channel,
            Duration = np.Result.Duration,
            Position = Math.Max(0, m.Position),
            Playing = !m.Paused,
        };
    }

    

    static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("open browser failed: " + ex.Message);
        }
    }
}