using System.Reflection;

namespace pi_stream_cam.Services;

public static class VersionInfo
{
    public static string Version { get; }
    public static string Revision { get; }
    public static string BuildDate { get; }

    static VersionInfo()
    {
        var versionFile = Path.Combine(AppContext.BaseDirectory, "VERSION");
        if (File.Exists(versionFile))
        {
            var lines = File.ReadAllText(versionFile).Trim().Split('\n');
            Version = lines[0].Trim();
            Revision = lines.Length > 1 ? lines[1].Trim() : "unknown";
        }
        else
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            Version = ver?.ToString() ?? "1.0.0";
            Revision = "unknown";
        }

        BuildDate = File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd HH:mm:ss");
    }
}
