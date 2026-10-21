using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace ScreenRec;

internal sealed record ScreenInfo(string DeviceName, string Label, Rectangle Bounds, bool IsPrimary, double Scale)
{
    public override string ToString() => Label;
}

internal static class ScreenHelper
{
    public static List<ScreenInfo> GetScreens()
    {
        var list = new List<ScreenInfo>();
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            var b = s.Bounds;
            double scale = NativeMethods.GetScaleAt(b.Left + b.Width / 2, b.Top + b.Height / 2);
            string name = s.DeviceName;
            // 显示用短名（去掉 "\\.\" 前缀），否则下拉框里挤不下
            string shortName = name.StartsWith(@"\\.\", StringComparison.Ordinal) ? name.Substring(4) : name;
            string label = s.Primary
                ? $"{shortName}（主显示器） {b.Width}×{b.Height}"
                : $"{shortName} {b.Width}×{b.Height}";
            list.Add(new ScreenInfo(name, label, b, s.Primary, scale));
        }
        if (list.Count == 0)
        {
            list.Add(new ScreenInfo("screen", "默认屏幕", new Rectangle(0, 0, 1920, 1080), true, 1.0));
        }
        return list;
    }

    /// <summary>把物理像素矩形按缩放比例换算成 WPF 的 DIP 矩形。</summary>
    public static Rect ToDip(Rectangle physical, double scale)
    {
        if (scale <= 0) scale = 1.0;
        return new Rect(physical.Left / scale, physical.Top / scale, physical.Width / scale, physical.Height / scale);
    }
}

internal sealed class AppSettings
{
    public int Fps { get; set; } = 15;
    public int Quality { get; set; } = 80;

    /// <summary>开始录像前的倒计时秒数（0 = 立即开始）。</summary>
    public int CountdownSeconds { get; set; }
    public string OutputDir { get; set; } = DefaultOutputDir();

    /// <summary>输出格式：avi / mp4 / both</summary>
    public string OutputFormat { get; set; } = "mp4";

    /// <summary>ffmpeg.exe 路径（留空则自动探测）</summary>
    public string FfmpegPath { get; set; } = string.Empty;
    public bool HideMainWindow { get; set; } = true;
    public bool ShowHud { get; set; } = true;
    public bool LivePreview { get; set; } = true;
    public bool OpenFolderWhenDone { get; set; }
    public string ScreenDevice { get; set; } = string.Empty;
    public bool HasRegion { get; set; }
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionW { get; set; }
    public int RegionH { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SettingsPath
    {
        get
        {
            // 优先放程序同目录（便携：整个文件夹拷走就能带走配置）
            string local = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
            try
            {
                string probe = System.IO.Path.Combine(AppContext.BaseDirectory, ".wtest");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return local;
            }
            catch
            {
                // 程序目录不可写（例如放在 Program Files）→ 退回用户目录
                string appdata = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRec");
                Directory.CreateDirectory(appdata);
                return System.IO.Path.Combine(appdata, "settings.json");
            }
        }
    }

    public static string DefaultOutputDir()
    {
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(desk)) desk = AppContext.BaseDirectory;
        return Path.Combine(desk, "录像");
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts);
                if (s != null) return s;
            }
        }
        catch
        {
            // 配置损坏就用默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 设置保存失败不影响使用
        }
    }

    public Rectangle RegionRect => new(RegionX, RegionY, RegionW, RegionH);
}
