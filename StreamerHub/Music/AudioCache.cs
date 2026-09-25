using System.Collections.Concurrent;

namespace StreamerHub;

// Preloads upcoming tracks' full audio to local disk while the current one
// plays, so transitions (even rapid skip-spam) serve instantly instead of
// waiting on resolve + upstream first bytes. Files are keyed by video id;
// only the current track and the next few are ever kept.
public static class AudioCache
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    static readonly ConcurrentDictionary<string, (string Path, string? ContentType)> Ready = new();
    static readonly ConcurrentDictionary<string, int> Gens = new();
    static readonly ConcurrentDictionary<string, byte> Inflight = new();

    sealed class ActiveDownload
    {
        public string Path = "";
        public string? ContentType;
    }
    static readonly ConcurrentDictionary<string, ActiveDownload> Active = new();
    const int KeepUpcoming = 3;
    const long MaxBytes = 40L * 1024 * 1024;
    const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    static string Dir => Path.Combine(AppPaths.BaseDir, "precache");

    public static void Init()
    {
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
        }
        catch { }
    }

    public static bool TryGet(string id, out string path, out string? contentType)
    {
        path = "";
        contentType = null;
        if (!Ready.TryGetValue(id, out var e)) return false;
        if (!File.Exists(e.Path)) { Ready.TryRemove(id, out _); return false; }
        path = e.Path;
        contentType = e.ContentType;
        return true;
    }

    public static bool IsPrefetching(string id) =>
        !string.IsNullOrEmpty(id) && Inflight.ContainsKey(id);

    public static bool TryGetActive(string id, out string path, out string? contentType)
    {
        path = "";
        contentType = null;
        if (string.IsNullOrEmpty(id)) return false;
        if (!Active.TryGetValue(id, out var dl)) return false;
        if (!File.Exists(dl.Path)) return false;
        path = dl.Path;
        contentType = dl.ContentType;
        return true;
    }

    static bool GenAlive(string id, int gen) =>
        Gens.TryGetValue(id, out var g) && g == gen;

    public static void Prefetch(MusicEngine music, string currentId, string nextId)
        => PrefetchMany(music, currentId,
            string.IsNullOrEmpty(nextId) ? Array.Empty<string>() : new[] { nextId });

    public static void PrefetchMany(MusicEngine music, string currentId, IReadOnlyList<string> upcoming)
    {
        var keep = new HashSet<string>();
        if (!string.IsNullOrEmpty(currentId)) keep.Add(currentId);
        var wanted = new List<string>();
        foreach (var id in upcoming)
        {
            if (wanted.Count >= KeepUpcoming) break;
            if (string.IsNullOrEmpty(id) || id == currentId || keep.Contains(id)) continue;
            keep.Add(id);
            wanted.Add(id);
        }
        Evict(keep);
        // Abort downloads for tracks that are no longer wanted (e.g. the
        // removed song) so they stop eating bandwidth.
        foreach (var id in Inflight.Keys)
        {
            if (!keep.Contains(id))
                Gens.AddOrUpdate(id, 1, (_, g) => g + 1);
        }
        foreach (var id in wanted)
        {
            if (TryGet(id, out _, out _)) continue;
            if (Inflight.ContainsKey(id)) continue; // already downloading, leave it alone
            var gen = Gens.AddOrUpdate(id, 1, (_, g) => g + 1);
            Inflight.TryAdd(id, 0);
            _ = Task.Run(() => PrefetchCoreAsync(music, id, gen));
        }
    }

    static async Task PrefetchCoreAsync(MusicEngine music, string nextId, int gen)
    {
        // Writes straight to the final file (readable while growing) so
        // playback can start from partial bytes instead of waiting for the
        // whole download.
        var dst = Path.Combine(Dir, nextId + ".bin");
        var active = new ActiveDownload { Path = dst };
        Active[nextId] = active;
        var ok = false;
        try
        {
            if (!GenAlive(nextId, gen)) return;
            var url = await music.ResolveAudioUrlAsync(nextId);
            if (url == null)
            {
                // One retry: the shared resolve may have died with a stale
                // task (e.g. right after a skip/remove reshuffle).
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!GenAlive(nextId, gen) || TryGet(nextId, out _, out _)) return;
                url = await music.ResolveAudioUrlAsync(nextId);
            }
            if (url == null || !GenAlive(nextId, gen)) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", UA);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode || !GenAlive(nextId, gen)) return;
            active.ContentType = resp.Content.Headers.ContentType?.ToString();
            Directory.CreateDirectory(Dir);
            using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                using var src = await resp.Content.ReadAsStreamAsync();
                var buf = new byte[65536];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) return;
                    await fs.WriteAsync(buf, 0, n);
                    if (!GenAlive(nextId, gen)) return;
                }
            }
            if (!GenAlive(nextId, gen)) return;
            Ready[nextId] = (dst, active.ContentType);
            ok = true;
            Log.Info("precached next track (" + (new FileInfo(dst).Length / 1024) + " KB)");
        }
        catch (Exception ex)
        {
            Log.Info("precache skipped: " + ex.Message);
        }
        finally
        {
            Inflight.TryRemove(nextId, out _);
            if (Active.TryGetValue(nextId, out var cur) && ReferenceEquals(cur, active))
                Active.TryRemove(nextId, out _);
            if (!ok)
            {
                // Leave a usable partial only if some other reader may be on
                // it; otherwise drop the fragment so disk stays clean.
                Ready.TryRemove(nextId, out _);
            }
        }
    }

    static void Evict(HashSet<string> keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!keep.Contains(name))
                {
                    try { File.Delete(file); } catch { }
                    Ready.TryRemove(name, out _);
                }
            }
        }
        catch { }
    }
}
