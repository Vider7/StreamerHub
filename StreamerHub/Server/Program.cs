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
    // Crossfade preload: the queue head loads paused into the idle player
    // ahead of the trigger so the overlap starts sample-aligned.
    static MpvPlayer? _preloadPlayer;
    static string? _preloadId;      // queue head sitting paused in _preloadPlayer
    static string? _preloadForId;   // outgoing track the preload belongs to
    static bool _preloadReady;
    static volatile bool _preloadLoaded;
    static DateTime _preloadAtUtc = DateTime.MinValue;
    static string? _afCorrectedFor; // track whose fade-out was rebuilt from measured duration
    static DateTime _preloadFailAt = DateTime.MinValue;
    static string? _preloadFailKey; // pair to leave alone for a few seconds after a failed load
    static DateTime _lastSeekNoticeUtc = DateTime.MinValue;
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
        var port = _cfg.Port;
        var portArg = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase));
        if (portArg != null && int.TryParse(portArg.Split('=')[1], out var parsedPort)) port = parsedPort;
        var restarting = args.Any(a => a.Equals("--restart", StringComparison.OrdinalIgnoreCase));
        using var singleInstance = AcquireSingleInstance(@"Global\StreamerHub.Server", port, restarting);
        if (singleInstance == null) return 0;
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
            _cfg.Music.Crossfade ? Math.Clamp(_cfg.Music.CrossfadeSeconds <= 0 ? 4 : _cfg.Music.CrossfadeSeconds, 0.5, 12) : 0, -1);
        var mpvOk = mpv.Start(_cfg.Music.DefaultVolume, af0);
        var mpvB = new MpvPlayer(MpvPlayer.Locate(_cfg.Music.MpvPath, "mpv"), _cfg.Music.AudioDevice, "streamerhub-mpv-b");
        var mpvBOk = mpvOk && mpvB.Start(_cfg.Music.DefaultVolume, af0, killStale: false);
        if (mpvOk && _cfg.Music.LoopOne) mpv.SetLoop(true);
        if (mpvBOk && _cfg.Music.LoopOne) mpvB.SetLoop(true);
        if (!mpvBOk) Log.Warn("mpv second player unavailable; crossfade falls back to hard cut");
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
        ws.RetryCurrent = RetryCurrent;
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
        commands.PersistRequested += () => _cfg.Save();
        commands.VolumeChanged += v =>
        {
            v = Math.Clamp(v, 5, 25);
            if (v != _cfg.Music.DefaultVolume) Log.Info("volume: " + _cfg.Music.DefaultVolume + " -> " + v + " (chat)");
            _cfg.Music.DefaultVolume = v;
            _cfg.Save();
            (_active ?? mpv).SetVolume(v);
            ws.PublishMusic();
        };
        commands.PauseChanged += p =>
        {
            try { _active?.SetPause(p); } catch { }
            try { _fading?.SetPause(p); } catch { }
        };
        commands.RequestsChanged += on => ws.Broadcast(new { type = "requests", on });
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
        mpv.Loaded += () => PreloadLoaded(mpv);
        mpvB.Loaded += () => PreloadLoaded(mpvB);
        mpv.SeekFailed += () => SeekNotice(mpv);
        mpvB.SeekFailed += () => SeekNotice(mpvB);
        twitch.ConnectionChanged += (ok, detail) => ws.SetConn(ChatPlatform.Twitch, ok, detail);
        tiktok.ConnectionChanged += (ok, detail) => ws.SetConn(ChatPlatform.TikTok, ok, detail);
        stats.ViewerCountChanged += ws.SetTwitchViewers;

        void PreloadLoaded(MpvPlayer p)
        {
            if (p == _preloadPlayer && _preloadId != null && p.CurrentId == _preloadId)
                _preloadLoaded = true;
        }

        void SeekNotice(MpvPlayer p)
        {
            // A timeline drag the player couldn't honor (usually the file is
            // still downloading). Say so on screen instead of sitting dead,
            // throttled so scrubbing doesn't spam.
            if (p != _active) return;
            var now = DateTime.UtcNow;
            if (now - _lastSeekNoticeUtc < TimeSpan.FromSeconds(10)) return;
            _lastSeekNoticeUtc = now;
            ws.PublishNotice("couldn't jump there - the song is still downloading");
        }

        void AbortPreload()
        {
            var p = _preloadPlayer;
            _preloadPlayer = null;
            _preloadId = null;
            _preloadForId = null;
            _preloadReady = false;
            _preloadLoaded = false;
            // Never stop the live player: after a consumed preload the holder
            // IS _active, and abort is also called on paths that already did.
            if (p != null && p != _active && p.Available)
            {
                try { p.CurrentId = null; p.StopAudio(); } catch { }
            }
        }

        void SyncMusic()
        {
            ws.PublishMusic();
            var active = _active;
            if (active == null || !active.Available) return;
            var now = music.NowPlaying;
            if (_overlapSetup) return;
            // Skip, prev, queue edits, or stop during a preload window: the
            // paused load no longer matches, drop it before switching.
            if (_preloadId != null)
            {
                var head = music.QueueSnapshot.FirstOrDefault();
                if (now == null || now.Result.Id != _preloadForId || head == null || head.Result.Id != _preloadId)
                    AbortPreload();
            }
            if (now == null)
            {
                EndOverlap();
                if (active.CurrentId != null)
                {
                    active.CurrentId = null;
                    // Fade, don't cut: a hard stop jolts on stream.
                    active.FadeOutAndStop();
                }
                return;
            }
            if (_fading != null && now.Result.Id == _overlapId) return;
            // The preloaded next went live (track ended right at the
            // trigger): adopt the paused-ready player instead of a cold load.
            if (_preloadReady && _preloadPlayer is { } inc && _fading == null
                && now.Result.Id == _preloadId && inc.Available && inc.InTrack)
            {
                var old = active;
                _preloadPlayer = null;
                _preloadId = null;
                _preloadForId = null;
                _preloadReady = false;
                if (old != inc)
                {
                    try { old.CurrentId = null; old.StopAudio(); } catch { }
                    try { old.FastPoll = false; } catch { }
                }
                _active = inc;
                _afCorrectedFor = now.Result.Id;
                inc.CurrentId = now.Result.Id;
                inc.SetLoop(_cfg.Music.LoopOne);
                inc.SetVolume(_cfg.Music.DefaultVolume);
                inc.SetPause(false);
                Log.Info("crossfade adopt preloaded: " + now.Result.Id);
                return;
            }
            if (_fading != null || active.CurrentId != now.Result.Id)
            {
                // Read this before EndOverlap clears it: a crossfade handoff
                // already fades the outgoing player on purpose, so it must
                // not also get the manual-skip fade.
                var wasCrossfading = _fading != null;
                EndOverlap();
                _afCorrectedFor = null;
                // Manual skip: fade the outgoing song out instead of cutting
                // it. The incoming load is held until the fade finishes, so
                // the two do not overlap and the new song is not silent.
                if (!wasCrossfading && active.CurrentId != null) active.FadeOutAndStop();
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

        string? AfForTrack(Track? t, bool fadeIn, double durationOverride = double.NaN, double fadeInLen = double.NaN)
        {
            if (!_cfg.Music.Crossfade) return null;
            // Measured mpv duration when we have it, metadata otherwise.
            var dur = !double.IsNaN(durationOverride) && durationOverride > 0
                ? durationOverride : t?.Result.Duration ?? 0;
            var fade = EffFade(dur);
            // The fade-in length is the overlap both sides agreed on (from
            // the outgoing track); only the fade-out start follows this
            // track's own duration.
            var inLen = !double.IsNaN(fadeInLen) && fadeInLen > 0 ? fadeInLen : fade;
            double start = -1;
            if (dur > fade)
                start = dur - fade;
            if (!fadeIn && start < 0) return null;
            return MpvPlayer.BuildAf(_cfg.Music.Equalizer, _cfg.Music.Loudness, fadeIn ? inLen : 0, start);
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

        // Manual "try this song again". The automatic retry replays the same
        // stream URL, which is pointless when the URL itself was the problem
        // (a 403 or an expired link), so drop the cached URL first and let
        // the resolve run again.
        void RetryCurrent()
        {
            var active = _active;
            var id = active?.CurrentId;
            if (active == null || id == null) return;
            var now = music.NowPlaying;
            if (now == null || now.Result.Id != id) return;
            AudioStream.ForgetUrl(id);
            _resolvingId = null;
            Log.Info("retry requested for " + id + ", re-resolving");
            ws.PublishNotice("trying " + now.Result.Title + " again");
            RetryResolve();
        }

        void SyncMpv()
        {
            var active = _active;
            var now = music.NowPlaying;
            if (now != null && active != null && active.Available)
            {
                music.SetPosition(now.Result.Id, active.Position, !active.Paused);
                if (now.Result.Id == active.CurrentId) MaybeCorrectFadeOut(now, active);
            }
            if (active == null || !active.Available) return;
            // The crossfade trigger runs on every position sample, ahead of
            // the broadcast throttle below: near the end samples arrive about
            // every 50ms, and the unpause timing needs all of them.
            MaybeFadeOut();
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
        }

        static double FadeSecs() =>
            Math.Clamp(_cfg.Music.CrossfadeSeconds <= 0 ? 4 : _cfg.Music.CrossfadeSeconds, 0.5, 12);

        // Short tracks: the overlap never runs longer than half the track.
        static double EffFade(double durationSecs)
        {
            var f = FadeSecs();
            return durationSecs > 0 ? Math.Min(f, durationSecs / 2) : f;
        }

        // One-time per track: rebuild the fade-out from mpv's measured
        // duration instead of the yt-dlp metadata one. Runs seconds after
        // playback starts, far from any fade, and no-ops when metadata was
        // already right (identical chain string skips the apply).
        void MaybeCorrectFadeOut(Track now, MpvPlayer active)
        {
            if (!_cfg.Music.Crossfade || _fading != null) return;
            var id = now.Result.Id;
            if (_afCorrectedFor == id) return;
            var measured = active.Duration;
            if (measured <= 0) return;
            _afCorrectedFor = id;
            var meta = AfForTrack(now, fadeIn: false);
            var corrected = AfForTrack(now, fadeIn: false, durationOverride: measured);
            if (corrected == null || corrected == meta) return;
            try { active.SetAf(corrected); } catch { }
            var metaDur = now.Result.Duration;
            Log.Info("crossfade fade-out corrected: metadata " + metaDur.ToString("0.0") + "s -> measured " + measured.ToString("0.0") + "s");
        }

        void MaybeFadeOut()
        {
            var mus = _music;
            var m = _active;
            if (mus == null || m == null || !m.Available) return;
            var inZone = false;
            if (_cfg.Music.Crossfade && !_cfg.Music.LoopOne && _fading == null && !_overlapSetup)
            {
                var now = mus.NowPlaying;
                if (now != null && !m.Paused && m.CurrentId == now.Result.Id)
                {
                    var outDur = m.Duration > 0 ? m.Duration : now.Result.Duration;
                    if (outDur > 0)
                    {
                        var fade = EffFade(outDur);
                        var remaining = outDur - m.Position;
                        if (remaining > 0)
                        {
                            inZone = remaining <= fade + 1.0;
                            if (remaining <= fade + 8.0) MaybePreload(now, m, fade, outDur, remaining);
                            else if (_preloadId != null) AbortPreload(); // sought back past the window
                            if (remaining <= fade) MaybeStartOverlap(mus, m, fade, outDur);
                        }
                        // remaining <= 0: the end-file event owns the
                        // transition now; SyncMusic adopts a landed preload
                        // or plays the next track normally.
                    }
                }
            }
            try { m.FastPoll = inZone; } catch { }
        }

        void MaybePreload(Track now, MpvPlayer m, double fade, double outDur, double remaining)
        {
            if (_preloadId != null) return;
            // Inside the trigger zone the overlap either fires from an armed
            // preload or falls back cleanly; starting a load here can never
            // align in time, so don't.
            if (remaining <= fade) return;
            var mus = _music;
            if (mus == null) return;
            var head = mus.QueueSnapshot.FirstOrDefault();
            if (head == null) return;
            var nextId = head.Result.Id;
            var key = now.Result.Id + "->" + nextId;
            if (key == _preloadFailKey && DateTime.UtcNow - _preloadFailAt < TimeSpan.FromSeconds(5)) return;
            // Complete file, or a download with enough head start that the
            // paused player buffers headers plus audio before unpause. A
            // cold track waits for the prefetcher; the trigger falls back
            // if it never lands in time.
            if (!PreloadReady(nextId)) return;
            var incoming = m == _mpv ? _mpvB : _mpv;
            if (incoming == null || !incoming.Available) return; // single player: the trigger hard-cuts
            _preloadForId = now.Result.Id;
            _preloadId = nextId;
            _preloadPlayer = incoming;
            _preloadReady = false;
            _preloadLoaded = false;
            _preloadAtUtc = DateTime.UtcNow;
            incoming.CurrentId = nextId;
            incoming.SetLoop(false);
            incoming.SetVolume(_cfg.Music.DefaultVolume);
            incoming.LoadPaused(StreamUrl(nextId), AfForTrack(head, fadeIn: true, fadeInLen: fade));
            Log.Info("crossfade preload: " + (m.CurrentId ?? "?") + " -> " + nextId + " (remaining " + remaining.ToString("0.0") + "s)");
            _ = WaitPreloadReadyAsync(nextId, incoming, head, fade);
        }

        static bool PreloadReady(string id)
        {
            if (AudioCache.TryGet(id, out _, out _)) return true;
            // Still downloading but past headers with audio buffered: mpv
            // keeps filling cache while paused, so the overlap stays aligned
            // and the download finishes underneath it. Dead (not running,
            // not complete) partials stay out: they'd stall mid-overlap.
            if (AudioCache.TryGetActive(id, out var partial, out _) && AudioCache.IsPrefetching(id))
            {
                try { return new FileInfo(partial).Length >= 256 * 1024; }
                catch { return false; }
            }
            return false;
        }

        async Task WaitPreloadReadyAsync(string nextId, MpvPlayer incoming, Track head, double fade)
        {
            try
            {
                // Phase 1: the player thread needs a moment to pick up the
                // loadfile. Checking InTrack before that moment reads the
                // PREVIOUS (finished) state and stillbirths every preload.
                var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (_preloadId == nextId && _preloadPlayer == incoming && !incoming.InTrack)
                {
                    if (!incoming.Available || DateTime.UtcNow > startDeadline)
                    {
                        Log.Warn("crossfade preload failed: " + nextId);
                        _preloadFailKey = _preloadForId + "->" + nextId;
                        _preloadFailAt = DateTime.UtcNow;
                        AbortPreload();
                        return;
                    }
                    await Task.Delay(50);
                }
                // Phase 2: file-loaded plus a real duration, then rebuild the
                // chain paused. Metadata covers it if mpv stays silent.
                var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                while (_preloadId == nextId && _preloadPlayer == incoming)
                {
                    if (!incoming.Available || !incoming.InTrack)
                    {
                        Log.Warn("crossfade preload failed: " + nextId);
                        _preloadFailKey = _preloadForId + "->" + nextId;
                        _preloadFailAt = DateTime.UtcNow;
                        AbortPreload();
                        return;
                    }
                    if (_preloadLoaded && incoming.Duration > 0) break;
                    if (DateTime.UtcNow > readyDeadline) break;
                    await Task.Delay(50);
                }
                if (_preloadId != nextId || _preloadPlayer != incoming) return; // aborted or consumed
                if (incoming.Duration <= 0)
                    Log.Warn("crossfade preload: no measured duration for " + nextId + ", using metadata");
                // Real duration known: rebuild the chain paused (inaudible)
                // with the fade-out starting exactly at realDuration - fade.
                var af = AfForTrack(head, fadeIn: true, durationOverride:
                    incoming.Duration > 0 ? incoming.Duration : head.Result.Duration, fadeInLen: fade);
                if (af != null)
                {
                    try { incoming.SetAf(af); } catch { }
                }
                _preloadReady = true;
                Log.Info("crossfade preload ready: " + nextId + " in " + (DateTime.UtcNow - _preloadAtUtc).TotalMilliseconds.ToString("0") + "ms");
            }
            catch { }
        }

        void MaybeStartOverlap(MusicEngine mus, MpvPlayer m, double fade, double outDur)
        {
            if (_fading != null || _overlapSetup) return;
            var incoming = m == _mpv ? _mpvB : _mpv;
            _overlapSetup = true;
            try
            {
                if (incoming == null || !incoming.Available)
                {
                    // Single player: old hard-cut behavior, unchanged.
                    var cut = mus.BeginOverlap();
                    if (cut != null) StartOverlap(cut);
                    return;
                }
                // Cold track, failed load, or the queue moved under us: no
                // late misaligned overlap. The current track ends on its own
                // fade-out; SyncMusic adopts the preload if it lands in time
                // or plays the next track normally.
                if (_preloadId == null || !_preloadReady || _preloadPlayer != incoming || !incoming.InTrack)
                {
                    AbortPreload();
                    return;
                }
                var next = mus.BeginOverlap();
                if (next == null || next.Result.Id != _preloadId) { AbortPreload(); ReconcileAfterTrigger(m); return; }
                var remaining = outDur - m.Position;
                var errMs = (m.Position - (outDur - fade)) * 1000.0;
                Log.Info("crossfade unpause: remaining " + remaining.ToString("0.00") + "s of " + outDur.ToString("0.0") + "s, fade " + fade.ToString("0.0") + "s, preload took " + (DateTime.UtcNow - _preloadAtUtc).TotalMilliseconds.ToString("0") + "ms");
                incoming.SetPause(false);
                _fading = m;
                _active = incoming;
                _overlapId = next.Result.Id;
                _afCorrectedFor = next.Result.Id;
                incoming.SetLoop(_cfg.Music.LoopOne);
                incoming.SetVolume(_cfg.Music.DefaultVolume);
                Log.Info("crossfade offset: " + errMs.ToString("0") + "ms (target under 50ms), incoming t=" + incoming.Position.ToString("0.00") + "s");
                Log.Info("crossfade overlap: " + (m.CurrentId ?? "?") + " -> " + next.Result.Id);
                _preloadId = null;
                _preloadPlayer = null;
                _preloadForId = null;
                _preloadReady = false;
                try { m.FastPoll = false; } catch { }
                try { incoming.FastPoll = false; } catch { }
            }
            catch { try { AbortPreload(); } catch { } }
            finally { _overlapSetup = false; }
        }

        // Defensive: BeginOverlap already popped the queue when the mismatch
        // was found, so NowPlaying can disagree with the live player while no
        // overlap owns the transition. Reconcile instead of stalling silent.
        void ReconcileAfterTrigger(MpvPlayer m)
        {
            try
            {
                if (_fading != null || _active != m) return;
                var cur = _music?.NowPlaying;
                if (cur == null || m.CurrentId == cur.Result.Id) return;
                Log.Warn("crossfade trigger raced a queue edit; hard cut to " + cur.Result.Id);
                m.CurrentId = cur.Result.Id;
                _afCorrectedFor = null;
                PlayResolved(cur.Result.Id);
            }
            catch { }
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
        ws.LanIps = LanIps().ToList();
        if (_cfg.AllowNetwork)
        {
            EnsureFirewall(port);
            foreach (var ip in ws.LanIps)
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

        var quitApp = new Action(async () =>
        {
            try { await ws.CloseAllAsync(); } catch { }
            ShutdownServices();
            _ = app.StopAsync(CancellationToken.None);
        });
        updater.QuitRequested += quitApp;
        ws.RestartRequested += () =>
        {
            // Relaunch first, then quit: the new copy waits out this
            // instance (started with --restart, so it takes over instead of
            // opening a browser at a dying server).
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    Log.Warn("restart: cannot locate own exe");
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--restart",
                    UseShellExecute = true,
                    WorkingDirectory = AppPaths.BaseDir,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("restart launch failed: " + ex.Message);
                return;
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                quitApp();
            });
        };
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

    

    // Second launch while the mutex is held: if the old server answers its
    // port, it is really running, so open its dashboard. If nothing answers,
    // it is mid-shutdown (or stuck): wait for the lock to free up and take
    // over, clearing a stuck instance if needed. Relaunching right after
    // Quit must always work.
    static Mutex? AcquireSingleInstance(string name, int port, bool forceTakeover = false)
    {
        var m = new Mutex(true, name, out var createdNew);
        if (createdNew) return m;
        m.Dispose();
        if (PortAlive(port) && !forceTakeover)
        {
            Log.Info("another StreamerHub server is already running; opening its dashboard and exiting this instance");
            OpenBrowser("http://127.0.0.1:" + port);
            return null;
        }
        Log.Info("old server instance not responding; waiting for it to exit...");
        try { using var ex = Mutex.OpenExisting(name); ex.WaitOne(TimeSpan.FromSeconds(15)); }
        catch { }
        var m2 = new Mutex(true, name, out var created2);
        if (created2) return m2;
        m2.Dispose();
        Log.Warn("old server instance stuck; stopping it and taking over");
        foreach (var p in Process.GetProcessesByName("StreamerHub"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { if (!p.WaitForExit(2000)) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
        }
        var m3 = new Mutex(true, name, out var created3);
        if (created3) return m3;
        m3.Dispose();
        OpenBrowser("http://127.0.0.1:" + port);
        return null;
    }

    static bool PortAlive(int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync("127.0.0.1", port);
            if (!t.Wait(TimeSpan.FromMilliseconds(600))) return false;
            return c.Connected;
        }
        catch { return false; }
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