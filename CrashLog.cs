using System.IO;
using System.Text;

namespace ScreenRec;

/// <summary>崩溃日志：写在与程序同目录的 screenrec_error.log，便于事后排查。</summary>
internal static class CrashLog
{
    private static readonly object _lock = new();

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "screenrec_error.log");

    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ----");
                sb.AppendLine(ex.ToString());
                File.AppendAllText(Path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // ignore
        }
    }
}
