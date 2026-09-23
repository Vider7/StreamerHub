using System.Text.RegularExpressions;

namespace StreamerHub;

public sealed class MusicEngine
{
    readonly MusicConfig _cfg;
    readonly YoutubeResolver _resolver;

    readonly List<Track> _queue = new();
    readonly List<Track> _liked = new();
    readonly List<Track> _history = new();
    readonly List<Track> _blocked = new();
    readonly List<Track> _redo = new();
    int _generation;
    bool _radioBusy;
    bool _radioQueued;
    string? _radioEarlyFor;

    public Track? NowPlaying { get; private set; }
    public double Position { get; private set; }
    public bool Playing { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? Notice;
    public Action? PersistRequested;

    public MusicEngine(MusicConfig cfg, YoutubeResolver resolver)
    {
        _cfg = cfg;
        _resolver = resolver;
        foreach (var s in _cfg.Queue)
        {
            if (string.IsNullOrEmpty(s.Id)) continue;
            var platform = s.Platform.ToLowerInvariant() switch
            {
                "twitch" => (ChatPlatform?)ChatPlatform.Twitch,
                "tiktok" => ChatPlatform.TikTok,
                _ => null,
            };
            _queue.Add(new Track
            {
                Result = new TrackResult { Id = s.Id, Title = s.Title, Channel = s.Channel, Duration = s.Duration },
                RequestedBy = s.RequestedBy,
                RequestedPlatform = platform,
            });
        }
        foreach (var s in _cfg.Liked)
        {
            if (string.IsNullOrEmpty(s.Id)) continue;
            _liked.Add(new Track
            {
                Result = new TrackResult { Id = s.Id, Title = s.Title, Channel = s.Channel, Duration = s.Duration },
                RequestedBy = "liked",
            });
        }
        foreach (var s in _cfg.Blocked)
        {
            if (string.IsNullOrEmpty(s.Id)) continue;
            _blocked.Add(new Track
            {
                Result = new TrackResult { Id = s.Id, Title = s.Title, Channel = s.Channel, Duration = s.Duration },
                RequestedBy = "blocked",
            });
        }
        StateChanged += SaveQueue;
    }

    void SaveQueue()
    {
        List<QueuedTrackState> snapshot;
        lock (_queue)
        {
            snapshot = _queue.Select(t => new QueuedTrackState
            {
                Id = t.Result.Id,
                Title = t.Result.Title,
                Channel = t.Result.Channel,
                Duration = t.Result.Duration,
                RequestedBy = t.RequestedBy,
                Platform = t.RequestedPlatform?.ToString().ToLowerInvariant() ?? "",
            }).ToList();
        }
        _cfg.Queue = snapshot;
        PersistRequested?.Invoke();
    }

    public IReadOnlyList<Track> QueueSnapshot
    {
        get { lock (_queue) return _queue.ToArray(); }
    }

    public int QueueCount
    {
        get { lock (_queue) return _queue.Count; }
    }

    public IReadOnlyList<Track> LikedSnapshot
    {
        get { lock (_liked) return _liked.ToArray(); }
    }

    public IReadOnlyList<Track> HistorySnapshot
    {
        get { lock (_history) return _history.TakeLast(15).Reverse().ToArray(); }
    }

    public IReadOnlyList<Track> BlockedSnapshot
    {
        get { lock (_blocked) return _blocked.ToArray(); }
    }

    public bool IsLiked(string id)
    {
        lock (_liked) return _liked.Any(t => t.Result.Id == id);
    }

    public bool IsBlocked(string id)
    {
        lock (_blocked) return _blocked.Any(t => t.Result.Id == id);
    }

    public void Block(TrackResult result)
    {
        var already = false;
        lock (_blocked)
        {
            if (_blocked.Any(t => t.Result.Id == result.Id))
            {
                already = true;
            }
            else
            {
                _blocked.Add(new Track { Result = result, RequestedBy = "blocked" });
                _cfg.Blocked = _blocked.Select(t => new BlockedTrackState
                {
                    Id = t.Result.Id,
                    Title = t.Result.Title,
                    Channel = t.Result.Channel,
                    Duration = t.Result.Duration,
                }).ToList();
            }
        }
        if (already)
        {
            StateChanged?.Invoke();
            PersistRequested?.Invoke();
            return;
        }
        lock (_queue) _queue.RemoveAll(t => t.Result.Id == result.Id);
        lock (_history) _history.RemoveAll(t => t.Result.Id == result.Id);
        if (NowPlaying?.Result.Id == result.Id) Skip();
        Notice?.Invoke("blocked: " + (string.IsNullOrEmpty(result.Title) ? result.Id : result.Title));
        StateChanged?.Invoke();
        PersistRequested?.Invoke();
    }

    public void Unblock(string id)
    {
        lock (_blocked)
        {
            var existing = _blocked.FirstOrDefault(t => t.Result.Id == id);
            if (existing == null) return;
            _blocked.Remove(existing);
            _cfg.Blocked = _blocked.Select(t => new BlockedTrackState
            {
                Id = t.Result.Id,
                Title = t.Result.Title,
                Channel = t.Result.Channel,
                Duration = t.Result.Duration,
            }).ToList();
        }
        StateChanged?.Invoke();
        PersistRequested?.Invoke();
    }

    public bool ToggleLike(TrackResult result)
    {
        bool liked;
        lock (_liked)
        {
            var existing = _liked.FirstOrDefault(t => t.Result.Id == result.Id);
            if (existing != null)
            {
                _liked.Remove(existing);
                liked = false;
            }
            else
            {
                _liked.Add(new Track { Result = result, RequestedBy = "liked" });
                liked = true;
            }
            _cfg.Liked = _liked.Select(t => new LikedTrackState
            {
                Id = t.Result.Id,
                Title = t.Result.Title,
                Channel = t.Result.Channel,
                Duration = t.Result.Duration,
            }).ToList();
        }
        StateChanged?.Invoke();
        PersistRequested?.Invoke();
        return liked;
    }

    public string Request(TrackResult result, string by, ChatPlatform? platform)
    {
        if (IsBlocked(result.Id))
        {
            return "that song is blocked";
        }
        if (result.Duration <= 0)
        {
            return "live or unknown-length streams are not supported";
        }
        if (result.Duration > _cfg.MaxTrackMinutes * 60.0)
        {
            return "track is longer than " + _cfg.MaxTrackMinutes + " minutes";
        }
        lock (_queue)
        {
            if (_queue.Count >= _cfg.MaxQueueLength)
            {
                return "queue is full (" + _cfg.MaxQueueLength + ")";
            }
            _queue.Add(new Track { Result = result, RequestedBy = by, RequestedPlatform = platform });
        }
        StateChanged?.Invoke();
        BeginOrNext();
        PrefetchNext();
        return "queued at #" + _queue.Count + ": " + result.Title;
    }

    public async Task<List<TrackResult>> SearchAsync(string query)
        => await _resolver.SearchAsync(query);

    public async Task<string?> ResolveAudioUrlAsync(string id)
        => await _resolver.ResolveAudioUrlAsync(id);

    void BeginOrNext()
    {
        bool shouldStart;
        lock (_queue)
        {
            shouldStart = NowPlaying == null && _queue.Count > 0;
        }
        if (shouldStart)
        {
            Track next;
            lock (_queue)
            {
                next = _queue[0];
                _queue.RemoveAt(0);
            }
            SetNowPlaying(next);
        }
    }

    public void SetPosition(string id, double seconds, bool playing)
    {
        lock (_queue)
        {
            if (NowPlaying == null || NowPlaying.Result.Id != id) return;
            var d = NowPlaying.Result.Duration;
            Position = d > 0 ? Math.Clamp(seconds, 0, d + 30) : Math.Max(0, seconds);
            Playing = playing;
        }
    }

    void SetNowPlaying(Track track)
    {
        NowPlaying = track;
        Position = 0;
        Playing = true;
        _generation++;
        _radioEarlyFor = null;
        _cfg.LastPlayed = ToLastPlayed(track, 0, true);
        StateChanged?.Invoke();
        Notice?.Invoke("playing: " + track.Title + (track.RequestedBy.Length > 0 ? " (by " + track.RequestedBy + ")" : ""));
        PrefetchNext();
    }

    void PrefetchNext()
    {
        string? cur, next;
        lock (_queue)
        {
            cur = NowPlaying?.Result.Id;
            next = _queue.Count > 0 ? _queue[0].Result.Id : null;
        }
        if (!string.IsNullOrEmpty(next) && next != cur)
        {
            AudioCache.Prefetch(this, cur ?? "", next);
            return;
        }
        if (cur != null) StartRadioEarlyFill();
    }

    static LastPlayedState ToLastPlayed(Track track, double position, bool playing) => new()
    {
        Id = track.Result.Id,
        Title = track.Result.Title,
        Channel = track.Result.Channel,
        Duration = track.Result.Duration,
        Position = position,
        Playing = playing,
    };

    public void PlayNow(TrackResult result)
    {
        if (IsBlocked(result.Id)) return;
        lock (_queue)
        {
            _generation++;
            _queue.Clear();
            _redo.Clear();
        }
        SetNowPlaying(new Track { Result = result, RequestedBy = "you", RequestedPlatform = null });
    }

    public void Resume(TrackResult result)
    {
        if (IsBlocked(result.Id)) return;
        SetNowPlaying(new Track { Result = result, RequestedBy = "you", RequestedPlatform = null });
    }

    public void OnClientEnded()
    {
        EndCurrent();
    }

    public void OnClientFailed()
    {
        if (NowPlaying == null) return;
        var t = NowPlaying;
        EndCurrent();
        Notice?.Invoke("could not load, moving on: " + t.Title);
    }

    void EndCurrent()
    {
        Track? next = null;
        lock (_queue)
        {
            if (NowPlaying == null) return;
            _history.Add(NowPlaying);
            if (_history.Count > 30) _history.RemoveAt(0);
            NowPlaying = null;
            Position = 0;
            Playing = false;
            _generation++;
            _redo.Clear();
            if (_queue.Count > 0)
            {
                next = _queue[0];
                _queue.RemoveAt(0);
            }
        }
        if (next != null)
        {
            SetNowPlaying(next);
        }
        else
        {
            _cfg.LastPlayed = null;
            StateChanged?.Invoke();
            StartRadioFill();
        }
    }

    public void Skip()
    {
        Track? next = null;
        lock (_queue)
        {
            if (_redo.Count > 0)
            {
                if (NowPlaying != null)
                {
                    _history.Add(NowPlaying);
                    if (_history.Count > 30) _history.RemoveAt(0);
                }
                next = _redo[^1];
                _redo.RemoveAt(_redo.Count - 1);
            }
            else
            {
                if (NowPlaying != null)
                {
                    _history.Add(NowPlaying);
                    if (_history.Count > 30) _history.RemoveAt(0);
                }
                NowPlaying = null;
                Position = 0;
                Playing = false;
                _generation++;
                if (_queue.Count > 0)
                {
                    next = _queue[0];
                    _queue.RemoveAt(0);
                }
            }
        }
        if (next != null)
        {
            SetNowPlaying(next);
        }
        else
        {
            _cfg.LastPlayed = null;
            StateChanged?.Invoke();
            StartRadioFill();
        }
    }

    public void Stop()
    {
        lock (_queue)
        {
            _generation++;
            NowPlaying = null;
            Position = 0;
            Playing = false;
            _redo.Clear();
            _cfg.LastPlayed = null;
        }
        StateChanged?.Invoke();
        PrefetchNext();
    }

    public void Prev()
    {
        Track? prev = null;
        lock (_queue)
        {
            if (_history.Count == 0 && NowPlaying == null) return;
            if (_history.Count == 0) return;
            var cur = NowPlaying;
            prev = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (cur != null) _redo.Add(cur);
        }
        if (prev != null)
        {
            SetNowPlaying(prev);
        }
    }

    public string? RemoveAt(int index)
    {
        string? removed = null;
        lock (_queue)
        {
            if (index >= 0 && index < _queue.Count)
            {
                removed = _queue[index].Title;
                _queue.RemoveAt(index);
            }
        }
        StateChanged?.Invoke();
        PrefetchNext();
        return removed;
    }

    void StartRadioFill()
    {
        if (!_cfg.AutoNextRadio) return;
        lock (_queue)
        {
            if (_radioBusy)
            {
                _radioQueued = true;
                return;
            }
            _radioBusy = true;
        }
        _ = Task.Run(FillRadioAsync);
    }

    void StartRadioEarlyFill()
    {
        string? cur;
        lock (_queue)
        {
            cur = NowPlaying?.Result.Id;
            if (cur == null || !_cfg.AutoNextRadio || _queue.Count > 0 || _radioBusy || _radioEarlyFor == cur) return;
            _radioEarlyFor = cur;
            _radioBusy = true;
        }
        _ = Task.Run(() => FillRadioEarlyAsync(cur));
    }

    async Task FillRadioEarlyAsync(string anchor)
    {
        try
        {
            var found = await _resolver.SearchRelatedAsync(anchor);
            var pick = PickRadioTrack(found, anchor);
            if (pick == null) return;
            lock (_queue)
            {
                if (NowPlaying?.Result.Id != anchor) return;
                if (_queue.Any(q => q.Result.Id == pick.Result.Id)) return;
                _queue.Add(pick);
            }
            StateChanged?.Invoke();
            BeginOrNext();
            PrefetchNext();
            Log.Info("radio queued early: " + pick.Result.Title);
        }
        catch (Exception ex)
        {
            Log.Warn("radio early fill failed: " + ex.Message);
        }
        finally
        {
            bool again;
            lock (_queue)
            {
                _radioBusy = false;
                again = _radioQueued;
                _radioQueued = false;
            }
            if (again) StartRadioFill();
        }
    }

    Track? PickRadioTrack(List<TrackResult> found, string anchor)
    {
        List<string> seenTitles;
        HashSet<string> used;
        lock (_queue)
        {
            seenTitles = new List<string>();
            foreach (var h in _history.TakeLast(10)) seenTitles.Add(NormalizeTitle(h.Result.Title));
            foreach (var q in _queue) seenTitles.Add(NormalizeTitle(q.Result.Title));
            seenTitles.RemoveAll(s => s.Length == 0);
            used = new HashSet<string>();
            foreach (var h in _history) used.Add(h.Result.Id);
            foreach (var q in _queue) used.Add(q.Result.Id);
        }
        foreach (var r in found)
        {
            if (r.Duration <= 0 || r.Duration > _cfg.MaxTrackMinutes * 60.0) continue;
            var rn = NormalizeTitle(r.Title);
            if (rn.Length == 0) continue;
            var dup = seenTitles.Any(t => t == rn || t.Contains(rn, StringComparison.Ordinal) || rn.Contains(t, StringComparison.Ordinal));
            if (r.Id == anchor || used.Contains(r.Id) || dup || IsBlocked(r.Id)) continue;
            return new Track { Result = r, RequestedBy = "radio", RequestedPlatform = null };
        }
        return null;
    }

    async Task FillRadioAsync()
    {
        string? anchor = null;
        lock (_queue)
        {
            if (_queue.Count > 0 || NowPlaying != null)
            {
                _radioBusy = false;
                return;
            }
            anchor = _history.Count > 0 ? _history[^1].Result.Id : null;
        }
        if (string.IsNullOrEmpty(anchor))
        {
            lock (_queue) _radioBusy = false;
            return;
        }
        try
        {
            var found = await _resolver.SearchRelatedAsync(anchor);
            var pick = PickRadioTrack(found, anchor);
            var added = false;
            if (pick != null)
            {
                lock (_queue)
                {
                    if (NowPlaying == null && !_queue.Any(q => q.Result.Id == pick.Result.Id))
                    {
                        _queue.Add(pick);
                        added = true;
                    }
                }
            }
            if (added)
            {
                StateChanged?.Invoke();
                BeginOrNext();
                Notice?.Invoke("radio next");
            }
            else if (pick == null)
            {
                Notice?.Invoke("radio: no similar songs found, music stopped");
            }
            else
            {
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("radio fill failed: " + ex.Message);
            Notice?.Invoke("radio: could not load more songs");
        }
        finally
        {
            bool again;
            lock (_queue)
            {
                _radioBusy = false;
                again = _radioQueued;
                _radioQueued = false;
            }
            if (again) _ = Task.Run(FillRadioAsync);
        }
    }

    static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var s = title.ToLowerInvariant();
        s = Regex.Replace(s, @"[\(\{\[].*?[\)\}\]]", " ");
        s = Regex.Replace(s, @"\b(slowed|reverb|sped up|nightcore|8d|extended|remix|cover|clean|lyrics|1 hour|loop)\b", " ");
        s = Regex.Replace(s, @"[^a-z0-9]+", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
