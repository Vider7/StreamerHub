using System.Collections.Concurrent;

namespace StreamerHub;

// Preloads the next track's full audio to local disk while the current one
// plays, so transitions serve instantly instead of waiting on resolve +
// upstream first bytes. Files are keyed by video id; only the current and
// next tracks are ever kept.
public static class AudioCache
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    static readonly ConcurrentDictionary<string, (string Path, string? ContentType)> Ready = new();
    static int _gen;
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

    public static void Prefetch(MusicEngine music, string currentId, string nextId)
    {
        if (nextId.Length == 0 || nextId == currentId) return;
        if (TryGet(nextId, out _, out _)) { Evict(currentId, nextId); return; }
        var gen = Interlocked.Increment(ref _gen);
        _ = Task.Run(() => PrefetchCoreAsync(music, currentId, nextId, gen));
    }

    static async Task PrefetchCoreAsync(MusicEngine music, string currentId, string nextId, int gen)
    {
        try
        {
            if (gen != Volatile.Read(ref _gen)) return;
            var url = await music.ResolveAudioUrlAsync(nextId);
            if (url == null || gen != Volatile.Read(ref _gen)) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", UA);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode || gen != Volatile.Read(ref _gen)) return;
            var tmp = Path.Combine(Dir, nextId + ".tmp");
            var dst = Path.Combine(Dir, nextId + ".bin");
            try
            {
                Directory.CreateDirectory(Dir);
                using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                using var src = await resp.Content.ReadAsStreamAsync();
                var buf = new byte[65536];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) return;
                    await fs.WriteAsync(buf, 0, n);
                    if (gen != Volatile.Read(ref _gen)) return;
                }
                fs.Close();
                File.Move(tmp, dst, true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            if (gen != Volatile.Read(ref _gen)) return;
            Ready[nextId] = (dst, resp.Content.Headers.ContentType?.ToString());
            Evict(currentId, nextId);
            Log.Info("precached next track (" + (new FileInfo(dst).Length / 1024) + " KB)");
        }
        catch (Exception ex)
        {
            Log.Info("precache skipped: " + ex.Message);
        }
    }

    static void Evict(string currentId, string nextId)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name != currentId && name != nextId)
                {
                    try { File.Delete(file); } catch { }
                    Ready.TryRemove(name, out _);
                }
            }
        }
        catch { }
    }
}
