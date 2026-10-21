using System.Diagnostics;
using System.IO;

namespace ScreenRec;

/// <summary>
/// 定位 ffmpeg 可执行文件。顺序：用户指定 → exe 同目录 → PATH → 工作目录附近的 ffmpeg-* → 常见安装位置。
/// </summary>
internal static class FfmpegLocator
{
    public static string? Find(string? configuredPath)
    {
        var found = FromConfigured(configuredPath);
        if (found != null) return found;

        string dir = AppContext.BaseDirectory;

        // exe 同目录
        foreach (var cand in new[]
        {
            Path.Combine(dir, "ffmpeg.exe"),
            Path.Combine(dir, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(dir, "bin", "ffmpeg.exe")
        })
        {
            if (File.Exists(cand)) return cand;
        }

        // PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var d in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var cand = Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(cand)) return cand;
            }
            catch
            {
                // ignore
            }
        }

        // 从 exe 目录往上找 5 层，扫 ffmpeg-* / ffmpeg 目录
        // （发布目录 tools\sys\ScreenRecorder\publish\ 往上 4 层才到工作根）
        var root = new DirectoryInfo(dir);
        for (int i = 0; i < 5 && root != null; i++, root = root.Parent)
        {
            try
            {
                foreach (var d in root.GetDirectories("ffmpeg*", SearchOption.TopDirectoryOnly))
                {
                    var cand = Path.Combine(d.FullName, "bin", "ffmpeg.exe");
                    if (File.Exists(cand)) return cand;
                }
            }
            catch
            {
                // ignore
            }
        }

        // 常见安装位置
        foreach (var cand in new[]
        {
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe")
        })
        {
            if (File.Exists(cand)) return cand;
        }

        return null;
    }

    private static string? FromConfigured(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            var p = configured.Trim();
            if (File.Exists(p)) return p;
            // 用户可能填的是目录
            if (Directory.Exists(p))
            {
                foreach (var name in new[] { "ffmpeg.exe", Path.Combine("bin", "ffmpeg.exe") })
                {
                    var cand = Path.Combine(p, name);
                    if (File.Exists(cand)) return cand;
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    /// <summary>
    /// 把画质档位(40~100)映射到 x264 的 CRF。
    /// 40→30、80→21、95→18 是常规区间；95 以上进一步压到 12（近乎无损），代价是文件明显变大。
    /// </summary>
    public static int QualityToCrf(int quality)
    {
        double q = Math.Clamp(quality, 40, 100);
        if (q >= 95)
        {
            // 近乎无损段：95→18，100→12
            return (int)Math.Round(18.0 - (q - 95.0) * 1.2);
        }
        return (int)Math.Round(30.0 - (q - 40.0) * (12.0 / 55.0));
    }
}
