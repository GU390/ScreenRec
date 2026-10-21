using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenRec;

/// <summary>
/// 调试用的界面渲染：把主界面 / 区域框选层渲染成 PNG。
///
/// ★ 为什么需要它：WPF 窗口开了 GPU 加速，PrintWindow / BitBlt 抓出来是空白，
///   没法用来做「界面长什么样」的验收。RenderTargetBitmap 直接渲染可视树，
///   拿到的是真实布局结果 —— 可以拿肉眼验收，也可以做程序化的越界检查。
///
/// ★ 用法（只在独立 Win32 桌面上跑，别在用户桌面上闪）：
///     ScreenRec.exe --uishot    <输出png>   主界面
///     ScreenRec.exe --regionshot <输出png>  区域框选层（带一个演示选区）
/// </summary>
internal static class UiShot
{
    public static int Run(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "--uishot";
        string outPath = args.Length > 1 ? args[1] : "uishot.png";

        try
        {
            FrameworkElement target;
            Window win;

            if (mode.Equals("--regionshot", StringComparison.OrdinalIgnoreCase))
            {
                var rsw = new RegionSelectWindow(new System.Drawing.Rectangle(0, 0, 1920, 1080));
                win = rsw;
                win.Show();
                Pump(win);
                // 摆一个演示选区，把 8 个控制点和浮动工具条都露出来
                rsw.SetDemoSelection(new System.Drawing.Rectangle(320, 180, 760, 460));
                target = (FrameworkElement)win.Content;
            }
            else
            {
                win = new MainWindow();
                win.Show();
                Pump(win);
                target = (FrameworkElement)win.Content;
            }

            target.UpdateLayout();

            double scale = 1.0;
            try
            {
                var src = PresentationSource.FromVisual(win);
                if (src?.CompositionTarget != null)
                {
                    scale = src.CompositionTarget.TransformToDevice.M11;
                }
            }
            catch
            {
                // ignore
            }

            int w = (int)Math.Ceiling(target.ActualWidth * scale);
            int h = (int)Math.Ceiling(target.ActualHeight * scale);
            if (w <= 0 || h <= 0)
            {
                w = 1200;
                h = 900;
            }

            var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(target);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = File.Create(outPath))
            {
                enc.Save(fs);
            }

            Console.WriteLine("UI shot saved: " + Path.GetFullPath(outPath) +
                              " (" + w + "x" + h + ")");
            // 顺便报告几个自测要用的数
            Console.WriteLine("contentDip=" + target.ActualWidth.ToString("0") + "x" +
                              target.ActualHeight.ToString("0"));
            if (win is MainWindow mw)
            {
                Console.WriteLine("scrollHostMaxDip=" + mw.ScrollHostMaxHeight.ToString("0") +
                                  " contentHeightDip=" + mw.ContentHeightDip.ToString("0") +
                                  " overflow=" + (mw.ContentHeightDip > mw.ScrollHostMaxHeight ? "YES" : "NO"));
            }
            if (win is RegionSelectWindow rsw2)
            {
                Console.WriteLine("previewCaptured=" + (rsw2.HasPreview ? "YES" : "NO"));
            }
            win.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("UI shot failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 预览缩略图自检：**全程不显示任何窗口**，只在真实桌面上抓一块画面，
    /// 走一遍「抓屏 → 缩放 → 转 WPF 图像源 → 存 PNG」的完整链路。
    ///
    /// ★ 独立 Win32 桌面没有真实显示表面，抓屏必然失败 —— 那不能代表用户那边的表现。
    ///   这个模式在真实桌面上跑，但不弹窗、不抢焦点，对用户零打扰。
    /// </summary>
    public static int PreviewTest(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : "previewtest.png";
        try
        {
            var rsw = new RegionSelectWindow(new System.Drawing.Rectangle(0, 0, 1920, 1080));
            // 只借坐标换算和抓屏逻辑，窗口自始至终不 Show()
            rsw.SetDemoSelection(new System.Drawing.Rectangle(300, 200, 640, 360));

            var img = rsw.PreviewSource;
            if (img == null)
            {
                Console.WriteLine("preview test: NO IMAGE (抓屏没成功)");
                return 2;
            }

            using (var fs = File.Create(outPath))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                enc.Save(fs);
            }
            Console.WriteLine("preview ok: " + Path.GetFullPath(outPath) +
                              " " + img.PixelWidth + "x" + img.PixelHeight);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("preview test failed: " + ex.GetType().Name + " " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 抓屏自检：**不弹任何窗口**，直接抓当前桌面一块区域存成 PNG。
    ///
    /// ★ 用途：在**真实桌面**上验证「预览缩略图用的那套抓屏」是不是真能抓到内容。
    ///   独立 Win32 桌面没有真实显示表面，抓屏必然失败 —— 那不能代表用户那边的表现。
    ///   这个模式不显示窗口，只读像素，对用户零打扰。
    /// </summary>
    public static int GrabTest(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : "grabtest.png";
        try
        {
            var screens = ScreenHelper.GetScreens();
            var b = screens.Count > 0 ? screens[0].Bounds : new System.Drawing.Rectangle(0, 0, 1920, 1080);
            var rect = new System.Drawing.Rectangle(b.Left + 100, b.Top + 100, 640, 360);

            using var bmp = new System.Drawing.Bitmap(
                rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                    new System.Drawing.Size(rect.Width, rect.Height));
            }

            bool uniform = IsUniform(bmp);
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("grab ok: " + Path.GetFullPath(outPath) +
                              " size=" + bmp.Width + "x" + bmp.Height +
                              " uniform=" + uniform);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("grab failed: " + ex.GetType().Name + " " + ex.Message);
            return 1;
        }
    }

    /// <summary>整张图是不是同一个颜色（纯黑/纯色 = 没抓到真实画面）。</summary>
    private static bool IsUniform(System.Drawing.Bitmap bmp)
    {
        var first = bmp.GetPixel(0, 0);
        for (int y = 0; y < bmp.Height; y += 17)
        {
            for (int x = 0; x < bmp.Width; x += 17)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R != first.R || c.G != first.G || c.B != first.B) return false;
            }
        }
        return true;
    }

    /// <summary>让 WPF 把布局和渲染都跑完。</summary>
    private static void Pump(Window win)
    {
        try
        {
            win.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            win.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }
        catch
        {
            // ignore
        }
    }
}
