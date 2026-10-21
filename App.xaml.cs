using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;

namespace ScreenRec;

public partial class App : System.Windows.Application
{
    /// <summary>单实例互斥体：全局快捷键是系统级资源，两个实例会互相抢。</summary>
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
{
        // 必须在创建任何窗口前设置 Per-Monitor V2 DPI 感知
        NativeMethods.TryEnablePerMonitorDpiAwareness();

        // 单实例检查（命令行自测 / 诊断模式不受限制）
        // 模式识别用「扫描所有参数找第一个 -- 开头的」，不依赖参数顺序
        string probeMode = e.Args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
        string[] toolModes =
        {
            "--selftest", "--regionprobe", "--uishot", "--regionshot", "--grabtest", "--pretest"
        };
        bool isToolMode = toolModes.Any(m => probeMode.Equals(m, StringComparison.OrdinalIgnoreCase));
        if (!isToolMode)
        {
            bool created;
            _singleInstance = new Mutex(true, @"Local\ScreenRec.SingleInstance", out created);
            if (!created)
            {
                System.Windows.MessageBox.Show("屏幕录像已经在运行了。\n\n请到任务栏找到那个窗口继续用，或先把它关掉再启动本程序。", "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(3);
                return;
            }
        }

        this.DispatcherUnhandledException += (s, args) =>
        {
            CrashLog.Write(args.Exception);
            args.Handled = true;
            try
            {
                System.Windows.MessageBox.Show(
                    "程序遇到未处理的错误：\n\n" + args.Exception.Message +
                    "\n\n详情见同目录 screenrec_error.log",
                    "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // ignore
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            CrashLog.Write(args.ExceptionObject as Exception);
        };

        base.OnStartup(e);

        var args = e.Args;
        Console.WriteLine("[startup] args=[" + string.Join("|", args) + "]");

        // ★ 命令行模式识别：**扫描所有参数**找第一个 "--" 开头的，
        //   不依赖 args[0] 的位置（不同启动姿势传进来的参数顺序可能不一样）。
        //   一旦识别成工具模式就绝不创建主窗口 —— 免得在用户桌面上闪一个主界面。
        string mode = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;

        if (mode.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            int code = SelfTest.Run(args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        if (mode.Equals("--regionprobe", StringComparison.OrdinalIgnoreCase))
        {
            // 诊断模式自己控制退出时机（要等窗口加载完再量）
            RegionProbe.Run(e.Args);
            return;
        }

        if (mode.Equals("--pretest", StringComparison.OrdinalIgnoreCase))
        {
            // 预览缩略图自检：不显示窗口，只走抓屏链路
            int code = UiShot.PreviewTest(args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        if (mode.Equals("--grabtest", StringComparison.OrdinalIgnoreCase))
        {
            // 抓屏自检：不弹窗口，只读像素
            int code = UiShot.GrabTest(args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        if (mode.Equals("--uishot", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("--regionshot", StringComparison.OrdinalIgnoreCase))
        {
            // 调试渲染：把界面渲染成 PNG 后退出（只在独立桌面上跑）
            int code = UiShot.Run(args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out NativePoint pt);

    /// <summary>窗口客户区坐标 → 虚拟屏幕坐标（物理像素，进程为 Per-Monitor V2 感知）。</summary>
    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint pt);

    /// <summary>虚拟屏幕坐标 → 窗口客户区坐标（物理像素）。</summary>
    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint pt);

    /// <summary>取客户区尺寸（物理像素）。</summary>
    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>把窗口精确放到指定物理像素矩形（不受 WPF DIP 换算影响）。</summary>
    public static void SetWindowPosExact(IntPtr hwnd, Rectangle rect, bool show = true, bool activate = false)
    {
        uint flags = activate ? 0u : SWP_NOACTIVATE;
        if (show) flags |= SWP_SHOWWINDOW;
        SetWindowPos(hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, flags);
    }

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKeyNative(IntPtr hwnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>PER_MONITOR_AWARE_V2 = -4</summary>
    public static void TryEnablePerMonitorDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch
        {
            // 旧系统忽略即可
        }
    }

    /// <summary>取某物理点所在显示器的有效 DPI（缩放比例 = dpi / 96）。</summary>
    public static double GetScaleAt(int x, int y)
    {
        try
        {
            var hmon = MonitorFromPoint(new NativePoint { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
            {
                return dx / 96.0;
            }
        }
        catch
        {
            // ignore
        }
        return 1.0;
    }

    /// <summary>把控制台附着到父进程，便于命令行模式看到输出。</summary>
    public static void AttachParentConsole()
    {
        try
        {
            AttachConsole(-1);
        }
        catch
        {
            // ignore
        }
    }

    public static bool TryRegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key)
    {
        try
        {
            return RegisterHotKey(hwnd, id, modifiers, key);
        }
        catch
        {
            return false;
        }
    }

    public static void UnregisterHotKey(IntPtr hwnd, int id)
    {
        try
        {
            UnregisterHotKeyNative(hwnd, id);
        }
        catch
        {
            // ignore
        }
    }
}
