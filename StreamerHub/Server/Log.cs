using System.IO;

namespace StreamerHub;

public static class Log
{
    static readonly object Gate = new();
    static string _dir = "";
    const long MaxBytes = 1_000_000L;

    public static void Init(string baseDir)
    {
        _dir = Path.Combine(baseDir, "logs");
        try { Directory.CreateDirectory(_dir); } catch { }
    }

    public static string Dir => _dir;

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                if (_dir == "") Init(AppPaths.BaseDir);
                var file = Path.Combine(_dir, "app.log");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
                Rotate(file);
            }
        }
        catch
        {
        }
    }

    static void Rotate(string file)
    {
        var fi = new FileInfo(file);
        if (fi.Exists && fi.Length <= MaxBytes) return;
        try
        {
            var old = file + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(file, old);
        }
        catch
        {
        }
    }
}