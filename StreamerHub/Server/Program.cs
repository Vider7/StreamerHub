using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace StreamerHub;

internal static class Program
{
    static AppConfig _cfg = new();
    static ChatHub? _hub;
    static TwitchChatService? _twitch;
    static TwitchAvatarService? _avatars;
    static TikTokChatService? _tiktok;
    static TwitchStatsService? _stats;
    static MusicEngine? _music;
    static MpvPlayer? _mpv;
    static UpdateService? _updater;
    static double _lastPos;
    static bool _lastPaused;
    static MpvPlayer? _mpvB;
    static MpvPlayer? _active;
    static MpvPlayer? _fading;
    static string? _overlapId;
    static bool _overlapSetup;
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
        AudioCache.Init();
        Log.Info("StreamerHub server starting");

        _cfg = AppConfig.Load();
        using var singleInstance = new Mutex(true, @"Global\StreamerHub.Server", out var createdNew);
        if (!createdNew)
        {
            // The tray copy is already up (tray-only apps hide from the task
            // list, so this looks "closed"). Point the user at it instead of
            // silently exiting.
            Log.Info("another StreamerHub server is already running; opening its dashboard and exiting this instance");
            OpenBrowser("http://127.0.0.1:" + _cfg.Port);
            return 0;
        }
        var port = _cfg.Port;
        var portArg = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase));
        if (portArg != null && int.TryParse(portArg.Split('=')[1], out var parsedPort)) port = parsedPort;
        var host = _cfg.AllowNetwork ? "0.0.0.0" : "127.0.0.1";

        var hub = new ChatHub();
        var resolver = new YoutubeResolver(_cfg.Music);
        var music = new MusicEngine(_cfg.Music, resolver);
        music.PersistRequested += () => _cfg.Save();
        var avatars = new TwitchAvatarService(_cfg);
        var twitch = new TwitchChatService(_cfg, hub, avatars);
        var tiktok = new TikTokChatService(_cfg, hub);
        var stats = new TwitchStatsService(_cfg);

        var mpv = new MpvPlayer(MpvPlayer.Locate(_cfg.Music.MpvPath, "mpv"), _cfg.Music.AudioDevice);
        var af0 = MpvPlayer.BuildAf(_cfg.Music.Equalizer, _cfg.Music.Loudness,
            _cfg.Music.Crossfade ? Math.Clamp(_cfg.Music.CrossfadeSeconds <= 0 ? 4 : _cfg.Music.CrossfadeSeconds, 0.5, 10) : 0, -1);
        var mpvOk = mpv.Start(_cfg.Music.DefaultVolume, af0);
        var mpvB = new MpvPlayer(MpvPlayer.Locate(_cfg.Music.MpvPath, "mpv"), _cfg.Music.AudioDevice, "streamerhub-mpv-b");
        var mpvBOk = mpvOk && mpvB.Start(_cfg.Music.DefaultVolume, af0, killStale: false);
        if (mpvOk && _cfg.Music.LoopOne) mpv.SetLoop(true);
        if (mpvBOk && _cfg.Music.LoopOne) mpvB.SetLoop(true);
        if (!mpvBOk) Log.Warn("mpv second player unavailable; crossfade falls back to fade-out only");
        var ws = new WebSocketHub(_cfg, hub, music, mpv);
        var updater = new UpdateService(_cfg.Updater, m => ws.Broadcast(m));
        ws.Updater = updater;

        _hub = hub;
        _music = music;
        _avatars = avatars;
        _active = mpv;
        if (mpvBOk) _mpvB = mpvB;
        ws.Mpv2 = _mpvB;
        ws.ActivePlayer = () => _active;
        ws.PauseAll = p =>
        {
            try { _active?.SetPause(p); } catch { }
            try { _fading?.SetPause(p); } catch { }
        };
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
        avatars.AvatarResolved += (platform, user, url) => hub.SetAvatar(platform, user, url);
        hub.AvatarChanged += ws.PublishAvatar;
        hub.StatsChanged += ws.PublishStats;
        hub.ActivityAdded += ws.PublishActivity;
        music.StateChanged += () => SyncMusic();
        var prewarming = new HashSet<string>();
        music.StateChanged += () => PrewarmNext();

        void PrewarmNext()
        {
            var ids = new List<string>();
            // The just-skipped-to track first: its live /api/stream resolve
            // runs the moment playback starts, so warm its URL before the
            // queued ones. With resolve dedup this joins the in-flight run
            // instead of spawning another yt-dlp.
            var now = music.NowPlaying?.Result.Id;
            if (!string.IsNullOrEmpty(now) && !AudioStream.IsCached(now)) ids.Add(now);
            foreach (var q in music.QueueSnapshot)
            {
                if (ids.Count >= 4) break;
                if (!AudioStream.IsCached(q.Result.Id) && q.Result.Id != _active?.CurrentId && !ids.Contains(q.Result.Id)) ids.Add(q.Result.Id);
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
        mpv.Ended += () => ActiveEnded(mpv);
        mpv.Failed += () => ActiveFailed(mpv);
        mpv.Retrying += () => ActiveRetrying(mpv);
        mpv.PositionChanged += SyncMpv;
        mpvB.Ended += () => ActiveEnded(mpvB);
        mpvB.Failed += () => ActiveFailed(mpvB);
        mpvB.Retrying += () => ActiveRetrying(mpvB);
        mpvB.PositionChanged += SyncMpv;
        twitch.ConnectionChanged += (ok, detail) => ws.SetConn(ChatPlatform.Twitch, ok, detail);
        tiktok.ConnectionChanged += (ok, detail) => ws.SetConn(ChatPlatform.TikTok, ok, detail);
        stats.ViewerCountChanged += ws.SetTwitchViewers;

        void SyncMusic()
        {
            ws.PublishMusic();
            var active = _active;
            if (active == null || !active.Available) return;
            var now = music.NowPlaying;
            if (_overlapSetup) return;
            if (now == null)
            {
                EndOverlap();
                if (active.CurrentId != null)
                {
                    active.CurrentId = null;
                    active.StopAudio();
                }
                return;
            }
            if (_fading != null && now.Result.Id == _overlapId) return;
            if (_fading != null || active.CurrentId != now.Result.Id)
            {
                EndOverlap();
                active.CurrentId = now.Result.Id;
                PlayResolved(now.Result.Id);
            }
        }

        void EndOverlap()
        {
            var f = _fading;
            if (f == null) return;
            _fading = null;
            _overlapId = null;
            try { f.StopAudio(); } catch { }
        }

        string? AfForTrack(Track? t, bool fadeIn)
        {
            if (!_cfg.Music.Crossfade) return null;
            var fade = FadeSecs();
            double start = -1;
            if (t != null && t.Result.Duration > fade)
                start = t.Result.Duration - fade;
            if (!fadeIn && start < 0) return null;
            return MpvPlayer.BuildAf(_cfg.Music.Equalizer, _cfg.Music.Loudness, fadeIn ? fade : 0, start);
        }

        bool OwnsCurrent(MpvPlayer p) =>
            p == _active && p.CurrentId != null && p.CurrentId == music.NowPlaying?.Result.Id;

        void ActiveEnded(MpvPlayer p)
        {
            if (p == _fading) { EndOverlap(); return; }
            if (!OwnsCurrent(p)) return;
            EndOverlap();
            music.OnClientEnded();
        }

        void ActiveFailed(MpvPlayer p)
        {
            if (p == _fading) { EndOverlap(); return; }
            if (!OwnsCurrent(p)) return;
            EndOverlap();
            music.OnClientFailed();
        }

        void ActiveRetrying(MpvPlayer p)
        {
            if (p == _fading) { EndOverlap(); return; }
            if (!OwnsCurrent(p)) return;
            RetryResolve();
        }

        async void PlayResolved(string id)
        {
            if (_resolvingId == id) return;
            var active = _active;
            if (active == null) return;
            var track = music.NowPlaying;
            var af = track != null && track.Result.Id == id ? AfForTrack(track, fadeIn: false) : null;
            _resolvingId = id;
            var resume = _resumeSeek > 0;
            var resumePos = _resumeSeek;
            var resumePlay = _resumePlaying;
            _resumeSeek = 0;
            _resumePlaying = true;
            try
            {
                if (active.CurrentId != id) return;
                var url = StreamUrl(id);
                if (resume)
                {
                    active.Resume(url, resumePos, resumePlay, af);
                }
                else
                {
                    active.Play(url, af);
                }
            }
            finally
            {
                _resolvingId = null;
            }
        }

        void RetryResolve()
        {
            var active = _active;
            var id = active?.CurrentId;
            if (active == null || id == null || _resolvingId == id) return;
            if (active.CurrentId != id) return;
            _resumeSeek = 0;
            _resumePlaying = true;
            active.RetryPlay(StreamUrl(id));
        }

        string StreamUrl(string id) => "http://127.0.0.1:" + port + "/api/stream/" + id;

        void SyncMpv()
        {
            var active = _active;
            var now = music.NowPlaying;
            if (now != null && active != null && active.Available)
            {
                music.SetPosition(now.Result.Id, active.Position, !active.Paused);
            }
            if (active == null || !active.Available) return;
            if (Math.Abs(active.Position - _lastPos) < 0.5 && active.Paused == _lastPaused) return;
            _lastPos = active.Position;
            _lastPaused = active.Paused;
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
            MaybeFadeOut();
        }

        static double FadeSecs() =>
            Math.Clamp(_cfg.Music.CrossfadeSeconds <= 0 ? 4 : _cfg.Music.CrossfadeSeconds, 0.5, 10);

        void MaybeFadeOut()
        {
            if (!_cfg.Music.Crossfade || _cfg.Music.LoopOne) return;
            var mus = _music;
            var m = _active;
            if (mus == null || m == null || !m.Available) return;
            if (_fading != null || _overlapSetup) return;
            var now = mus.NowPlaying;
            if (now == null || m.Paused) return;
            var fadeSecs = FadeSecs();
            var dur = m.Duration > 0 ? m.Duration : now.Result.Duration;
            if (dur <= 0) return;
            var remaining = dur - m.Position;
            if (remaining > fadeSecs || remaining <= 0) return;
            // Overlap trigger only: both fades live in the tracks' filter
            // chains, so there is no volume automation to run or race.
            // With nothing queued the current track's own fade-out carries it.
            _overlapSetup = true;
            try
            {
                var next = mus.BeginOverlap();
                if (next != null) StartOverlap(next);
            }
            catch { }
            finally { _overlapSetup = false; }
        }

        void StartOverlap(Track next)
        {
            var old = _active;
            var incoming = old == _mpv ? _mpvB : _mpv;
            var newId = next.Result.Id;
            if (old == null || incoming == null || !incoming.Available)
            {
                Log.Warn("overlap unavailable, hard cut to " + newId);
                if (old != null)
                {
                    old.CurrentId = newId;
                    PlayResolved(newId);
                }
                return;
            }
            _fading = old;
            _active = incoming;
            _overlapId = newId;
            incoming.CurrentId = newId;
            incoming.SetLoop(_cfg.Music.LoopOne);
            incoming.SetVolume(_cfg.Music.DefaultVolume);
            incoming.Play(StreamUrl(newId), AfForTrack(next, fadeIn: true));
            Log.Info("crossfade overlap: " + (old.CurrentId ?? "?") + " -> " + newId);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownServices();
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("crash: " + a.ExceptionObject);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppPaths.BaseDir,
            WebRootPath = Path.Combine(AppPaths.BaseDir, "wwwroot"),
        });
        builder.WebHost.UseUrls("http://" + host + ":" + port);
        var app = builder.Build();

        app.UseWebSockets();
        app.Map("/ws", ws.Handle);
        app.MapGet("/api/health", () => Results.Ok(new { ok = true, name = _cfg.AppName }));
        app.MapGet("/api/stream/{id}", (HttpContext ctx, string id) => AudioStream.Handle(music, ctx, id));
        app.MapGet("/api/thumb/{id}", (HttpContext ctx, string id) => ThumbStream.Handle(ctx, id));
        app.MapGet("/api/translate", (HttpContext ctx, string? q, string? to) => Translate.Handle(ctx, q, to));
        app.UseDefaultFiles();
        // Hashed /assets bundles are immutable for a year; entry pages
        // (index, overlay, manifest) never cache, so phones and second tabs
        // always boot the newest UI instead of a stale bundle.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.Context.Request.Path.Value ?? "";
                ctx.Context.Response.Headers.CacheControl = path.StartsWith("/assets", StringComparison.OrdinalIgnoreCase)
                    ? "public, max-age=31536000, immutable"
                    : "no-store";
            }
        });

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
        if (_cfg.AllowNetwork)
        {
            EnsureFirewall(port);
            foreach (var ip in LanIps())
                Log.Info("on your network: http://" + ip + ":" + port);
        }
        else
        {
            // Bool flipped back off: remove the rule(s) we made (or the ones
            // Windows made for us) so no firewall hole lingers.
            RemoveFirewall();
        }

    // Opens the port in Windows Firewall so the dashboard works from the
    // local network. Needs admin: without it netsh fails and we say so.
    static void EnsureFirewall(int port)
    {
        try
        {
            var show = RunNetsh("advfirewall firewall show rule name=\"StreamerHub\"");
            if (show.Contains("Rule Name:", StringComparison.OrdinalIgnoreCase)) return;
            var add = RunNetsh("advfirewall firewall add rule name=\"StreamerHub\" dir=in action=allow protocol=TCP localport=" + port + " profile=private");
            if (add.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
                Log.Info("firewall: opened TCP " + port + " (private networks)");
            else
                Log.Warn("firewall: could not open the port - run the app as admin once, then normally");
        }
        catch (Exception ex)
        {
            Log.Warn("firewall: " + ex.Message);
        }
    }

    static void RemoveFirewall()
    {
        try
        {
            var show = RunNetsh("advfirewall firewall show rule name=\"StreamerHub\"");
            if (!show.Contains("Rule Name:", StringComparison.OrdinalIgnoreCase)) return;
            var del = RunNetsh("advfirewall firewall delete rule name=\"StreamerHub\"");
            if (del.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
                Log.Info("firewall: removed StreamerHub rule(s) - back to PC-only");
            else
                Log.Warn("firewall: could not remove the rule(s) - run the app as admin once to finish reverting");
        }
        catch (Exception ex)
        {
            Log.Warn("firewall: " + ex.Message);
        }
    }

    static string RunNetsh(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return "";
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return ""; }
        return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }

    static IEnumerable<string> LanIps()
    {
        var out_ = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    var s = ua.Address.ToString();
                    if (!out_.Contains(s)) out_.Add(s);
                }
            }
        }
        catch { }
        return out_;
    }

        var quitApp = new Action(() =>
        {
            ShutdownServices();
            _ = app.StopAsync(CancellationToken.None);
        });
        updater.QuitRequested += quitApp;
        updater.PauseRequested += () =>
        {
            try { _active?.SetPause(true); _fading?.SetPause(true); } catch { }
            Thread.Sleep(600);
        };
        TrayApp.Start(url, quitApp, updater,
            onPlayPause: () => {
                if (_music?.NowPlaying != null && _active is { Available: true })
                {
                    var p = !_active.Paused;
                    _active.SetPause(p);
                    try { _fading?.SetPause(p); } catch { }
                }
            },
            onNext: () => _music?.Skip(),
            onPrev: () => _music?.Prev());

        await app.RunAsync();
        ShutdownServices();
        // Force the process out: lingering service threads can otherwise keep it
        // alive for a long time, holding the exe locked and stalling restarts.
        Environment.Exit(0);
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
        try { _avatars?.Dispose(); } catch { }
        try { _tiktok?.Stop(); } catch { }
        try { _stats?.Stop(); } catch { }
        try { _mpv?.Dispose(); } catch { }
        try { _mpvB?.Dispose(); } catch { }
        try { _updater?.Dispose(); } catch { }
    }

    static void CaptureLastPlayed()
    {
        if (_music?.NowPlaying is not { } np) return;
        if (_active is not { Available: true } m) return;
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