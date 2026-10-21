using System;
using System.Drawing;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ScreenRec;

/// <summary>
/// 区域框选坐标自检。
///
/// 目的：在**真实 DPI 环境**下测量「鼠标物理坐标 → 选区物理矩形」这条换算链的误差，
/// 并把「旧算法（WPF DIP × 缩放）」与「新算法（物理像素直通）」的结果摆在一起对比。
///
/// 两种用法：
///   1. 命令行：ScreenRec.exe --regionprobe [legacy]   → 打印报告
///   2. 界面：主面板上的「坐标自检」按钮 → 弹窗显示同一份报告
///
/// 遮罩层被放到屏幕外（-5000,-5000）且全透明，用户完全看不到；
/// 几何关系与放置位置无关，所以测出来的缩放系数和偏移量对屏幕内放置同样成立。
/// </summary>
internal static class RegionProbe
{
    /// <summary>命令行入口：打印报告到控制台。</summary>
    public static int Run(string[] args)
    {
        NativeMethods.AttachParentConsole();
        bool legacy = args.Length > 1 && args[1].Equals("legacy", StringComparison.OrdinalIgnoreCase);
        string report = MeasureReport(legacy);
        Console.WriteLine(report);
        Console.Out.Flush();
        return 0;
    }

    /// <summary>同步测量并返回报告文本（界面按钮用）。</summary>
    internal static string MeasureReport(bool legacy)
    {
        var sb = new StringBuilder();
        void Say(string s) => sb.AppendLine(s);

        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Say("=== 区域框选坐标自检 ===");
        Say($"布局模式            : {(legacy ? "旧版（窗口自动尺寸）" : "现行（窗口显式尺寸）")}");
        Say($"显示器物理区域      : {screen.X},{screen.Y} {screen.Width}x{screen.Height}");
        Say($"显示器缩放(注册表)  : {GetRegistryScale()}");

        var win = new RegionSelectWindow(screen, offScreen: true, legacyLayout: legacy)
        {
            Topmost = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0.0,          // 全透明：用户看不到，但窗口与布局是真的
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        // 嵌套消息循环，等 Loaded + 布局完成后再量
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Measure(win, Say);
            }
            catch (Exception ex)
            {
                Say("测量失败：" + ex);
            }
            try { win.Close(); } catch { }
            frame.Continue = false;
        };
        timer.Start();
        win.Show();
        Dispatcher.PushFrame(frame);
        return sb.ToString();
    }

    private static void Measure(RegionSelectWindow win, Action<string> Say)
    {
        var geo = win.Geometry;
        if (geo == null)
        {
            Say("没有拿到几何信息（窗口没有 Loaded？）");
            return;
        }

        Say($"客户区物理尺寸      : {geo.ClientW} x {geo.ClientH}");
        Say($"客户区虚拟屏幕原点  : ({geo.OriginX}, {geo.OriginY})");
        Say($"WPF 缩放系数        : {geo.K:F4}");
        Say($"根 Canvas 尺寸(DIP) : {geo.RootDipW:F2} x {geo.RootDipH:F2}");
        double kReal = geo.ClientW / Math.Max(1.0, geo.RootDipW);
        Say($"自校准缩放系数      : {kReal:F4}   （客户区物理宽 / Root 实际 DIP 宽）");

        // 根 Canvas 的物理原点 vs 客户区原点 —— 两者不一致就是「框选整体偏移」的根源
        var rootTopLeftScreen = win.RootElement.PointToScreen(new System.Windows.Point(0, 0));
        int dx = (int)Math.Round(rootTopLeftScreen.X) - geo.OriginX;
        int dy = (int)Math.Round(rootTopLeftScreen.Y) - geo.OriginY;
        Say($"根 Canvas 相对客户区偏移: ({dx}, {dy}) 物理像素   ← 若不为 0，整块选区就会平移");

        Say("");
        Say("--- 换算对比（坐标一律相对客户区原点）---");
        Say("测试点(物理)   真值(客户区)     旧算法(DIP×k)   旧误差      新算法(物理直通)  新误差");

        int[] samples = { 0, 100, 250, 700, 1500 };
        int maxLegacyErr = 0, maxCurErr = 0;
        foreach (int x in samples)
        {
            foreach (int y in samples)
            {
                int absX = geo.OriginX + x;
                int absY = geo.OriginY + y;

                var legacyPt = win.LegacyMapToPhys(absX, absY);
                var currentPt = win.CurrentMapToPhys(absX, absY);

                int legacyX = legacyPt.X - geo.OriginX;
                int legacyY = legacyPt.Y - geo.OriginY;
                int curX = currentPt.X - geo.OriginX;
                int curY = currentPt.Y - geo.OriginY;

                int legacyErr = Math.Max(Math.Abs(legacyX - x), Math.Abs(legacyY - y));
                int curErr = Math.Max(Math.Abs(curX - x), Math.Abs(curY - y));
                maxLegacyErr = Math.Max(maxLegacyErr, legacyErr);
                maxCurErr = Math.Max(maxCurErr, curErr);

                Say($"({x},{y})".PadRight(16) +
                    $"({x},{y})".PadRight(16) +
                    $"({legacyX},{legacyY})".PadRight(16) +
                    $"{legacyErr}".PadRight(12) +
                    $"({curX},{curY})".PadRight(18) +
                    $"{curErr}");
            }
        }

        Say("");
        Say($"最大误差：旧算法 {maxLegacyErr} 像素　　新算法 {maxCurErr} 像素");
    }

    private static string GetRegistryScale()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop\WindowMetrics");
            string? v = k?.GetValue("AppliedDPI")?.ToString();
            return v == null ? "?" : $"{v} (={double.Parse(v) / 96.0:F2}x)";
        }
        catch
        {
            return "?";
        }
    }
}
