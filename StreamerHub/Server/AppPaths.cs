namespace StreamerHub;

public static class AppPaths
{
    public static string BaseDir { get; } = Resolve();

    static string Resolve()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        return AppContext.BaseDirectory;
    }
}