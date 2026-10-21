using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenRec;

/// <summary>
/// 全屏遮罩式的区域框选窗口（QQ 截图同款交互）。
///
/// ★ 坐标策略：**选区一律以物理像素记录**（虚拟屏幕绝对坐标）。
///   鼠标位置用 WPF 的 PointToScreen（实测返回的就是物理像素）从事件位置换算，
///   完全绕开「DIP × 缩放系数」这种靠猜的算法 —— 125% / 150% 缩放都不会偏移。
///   缩放系数只用于「把物理像素画成界面元素」，不影响录出来的区域。
///
/// ★ 交互策略：**以鼠标为主，快捷键只是加速**。
///   8 个控制点（4 角 + 4 边中点）、跟随选框的浮动工具条、实时预览缩略图，
///   工具条上有「确认 / 重选 / 取消」三个鼠标可点的按钮 —— 不需要记任何快捷键。
///   键盘（回车 / Esc / R）仍然保留，但它只是可选的加速路径。
///
/// ★ 「确认后还有第二次框选」的根治：
///   1. Confirm / Cancel 都做幂等（_closing 闸门），同一个键被低级钩子和窗口钩子
///      各处理一次也只会生效一次；
///   2. 主窗口侧再加一层重入守卫，理论上根本开不出第二个遮罩层。
/// </summary>
public partial class RegionSelectWindow : Window
{
    private readonly Rectangle _screen;   // 目标显示器物理像素区域

    private IntPtr _hwnd;
    private int _originX, _originY;       // 根 Canvas 原点（虚拟屏幕物理坐标）
    private int _clientW, _clientH;       // 客户区物理尺寸
    private double _k = 1.0;              // 物理像素 → DIP 的除数（仅用于渲染）

    private Rectangle _sel;               // 选区（虚拟屏幕绝对物理坐标）
    private bool _hasSel;

    private HwndSource? _keySource;
    private LowLevelKeyHook? _llHook;

    private enum DragMode { None, Create, Move, Resize }
    private DragMode _mode = DragMode.None;
    private System.Drawing.Point _dragStart;
    private Rectangle _selAtDragStart;
    private int _handle;                  // 控制点索引：0=NW 1=N 2=NE 3=E 4=SE 5=S 6=SW 7=W

    /// <summary>关闭闸门：Confirm / Cancel 幂等的关键。</summary>
    private bool _closing;

    /// <summary>预览刷新定时器。</summary>
    private readonly DispatcherTimer _previewTimer;

    /// <summary>预览缩略图的物理尺寸（DIP，见 XAML）。</summary>
    private const int PreviewBoxW = 132;
    private const int PreviewBoxH = 78;

    public Rectangle? SelectedRect { get; private set; }

    /// <summary>诊断信息（--regionprobe 用）。</summary>
    internal RegionGeometry? Geometry { get; private set; }

    /// <summary>诊断模式：把遮罩层放到屏幕外，避免打扰用户（--regionprobe 用）。</summary>
    /// <remarks>必须是**实例**字段：曾经做成 static，结果「坐标自检」跑完之后，
    /// 后面所有的框选都被放到屏幕外去了 —— 自检把主功能弄坏了。</remarks>
    private readonly bool _offScreen;

    /// <summary>诊断模式：复刻旧版布局（窗口不设显式尺寸，只设 Root），用于定位历史偏移。</summary>
    private readonly bool _legacyLayout;

    /// <summary>缩放系数（物理像素 → DIP 的除数）。</summary>
    internal double ScaleFactor => _k;

    /// <summary>根 Canvas 原点的虚拟屏幕物理坐标。</summary>
    internal int OriginX => _originX;
    internal int OriginY => _originY;

    /// <summary>根 Canvas 的可视元素（诊断用）。</summary>
    internal FrameworkElement RootElement => Root;

    /// <summary>8 个控制点，索引与 HitHandle 的返回值一一对应。</summary>
    private FrameworkElement[] Handles => new FrameworkElement[]
    {
        HandleNW, HandleN, HandleNE, HandleE, HandleSE, HandleS, HandleSW, HandleW
    };

    /// <summary>旧算法换算（DIP × 缩放系数）—— 仅用于对比诊断，证明历史偏差。</summary>
    internal System.Drawing.Point LegacyMapToPhys(int physX, int physY)
    {
        var dip = Root.PointFromScreen(new System.Windows.Point(physX, physY));
        return new System.Drawing.Point(
            (int)Math.Round(_screen.Left + dip.X * _k),
            (int)Math.Round(_screen.Top + dip.Y * _k));
    }

    /// <summary>现行算法换算（PointToScreen / 物理像素直通）。</summary>
    internal System.Drawing.Point CurrentMapToPhys(int physX, int physY)
    {
        var np = new NativeMethods.NativePoint { X = physX, Y = physY };
        NativeMethods.ScreenToClient(_hwnd, ref np);
        return new System.Drawing.Point(_originX + np.X, _originY + np.Y);
    }

    /// <summary>设 SCREENREC_DEBUG=1 时把交互事件写到 screenrec_ui.log（排障用）。</summary>
    internal static void Dbg(string msg)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SCREENREC_DEBUG") != "1") return;
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "screenrec_ui.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    public RegionSelectWindow(Rectangle screenPhysical)
        : this(screenPhysical, false, false)
    {
    }

    /// <summary>诊断用构造：offScreen 把遮罩层放到屏幕外，legacyLayout 复刻旧版布局。</summary>
    internal RegionSelectWindow(Rectangle screenPhysical, bool offScreen, bool legacyLayout)
    {
        _screen = screenPhysical;
        _offScreen = offScreen;
        _legacyLayout = legacyLayout;
        InitializeComponent();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _previewTimer.Tick += (_, _) => RefreshPreview();

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;             // 兜底路径（原生钩子没装上时用）
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => { _closing = true; _previewTimer.Stop(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 原生键盘钩子：输入法拦不住的真实按键
        try
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _keySource = HwndSource.FromHwnd(_hwnd);
            _keySource.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Dbg("键盘钩子安装失败：" + ex.Message);
        }

        // 低级键盘钩子（OS 层，早于消息投递）—— 中文输入法下唯一可靠的一条路
        try
        {
            _llHook = new LowLevelKeyHook();
            if (_llHook.Installed)
            {
                _llHook.KeyDown += OnLowLevelKey;
                Dbg("低级键盘钩子已安装（输入法拦不住的那条路）");
            }
            else
            {
                Dbg("低级键盘钩子安装失败（退回窗口钩子）");
            }
        }
        catch (Exception ex)
        {
            Dbg("低级键盘钩子异常：" + ex.Message);
        }
    }

    /// <summary>低级钩子回调：只处理 Esc / 回车 / R（可选的加速路径）。</summary>
    private void OnLowLevelKey(int vk)
    {
        try
        {
            // 低级钩子回调已经跑在 UI 线程上（消息泵里），这里同步执行即可
            Dispatcher.Invoke(new Action(() =>
            {
                if (!IsVisible || _closing) return;
                HandleRawKey(vk, "LowLevelKey");
            }));
        }
        catch
        {
            // ignore
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _previewTimer.Stop();
        try
        {
            if (_llHook != null)
            {
                _llHook.KeyDown -= OnLowLevelKey;
                _llHook.Dispose();
                _llHook = null;
            }
        }
        catch
        {
            // ignore
        }
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RawKeys.WM_KEYDOWN || msg == RawKeys.WM_SYSKEYDOWN)
        {
            int vk = (int)wParam;
            if (HandleRawKey(vk, "WinProcKey"))
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>按虚拟键码处理按键（不依赖输入法）。</summary>
    private bool HandleRawKey(int vk, string source)
    {
        if (_closing) return false;
        if (RawKeys.ModifierDown()) return false;   // Ctrl+R / Alt+Esc 之类不抢
        switch (vk)
        {
            case RawKeys.VK_ESCAPE:
                Dbg($"{source} Esc -> 取消");
                Cancel();
                return true;
            case RawKeys.VK_RETURN:
                if (_hasSel) { Dbg($"{source} Enter -> 确认"); Confirm(); }
                return true;
            case RawKeys.VK_R:
                Dbg($"{source} R -> 重新框选");
                ResetSelection();
                return true;
        }
        return false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // 精确铺满目标显示器（物理像素）；诊断模式放到屏幕外，避免打扰用户
        NativeMethods.SetWindowPosExact(_hwnd,
            _offScreen ? new Rectangle(-5000, -5000, _screen.Width, _screen.Height) : _screen);

        // 读回真实客户区（物理像素）。WPF 的位置/尺寸都是 DIP，只有这里是真值。
        MeasureClient();

        // 缩放系数：先用 WPF 的变换矩阵，再用「客户区物理宽 ÷ Root 实际 DIP 宽」自校准一次。
        double kWpf = 1.0;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            kWpf = src.CompositionTarget.TransformToDevice.M11;
        }
        if (kWpf <= 0) kWpf = 1.0;
        _k = kWpf;

        // 固定窗口尺寸，避免 WPF 的自动尺寸逻辑把窗口改回去
        if (_legacyLayout)
        {
            // 复刻旧版布局：窗口不设显式尺寸，只设 Root（靠 WPF 自动尺寸）
            Root.Width = _screen.Width / _k;
            Root.Height = _screen.Height / _k;
            UpdateLayout();
            MeasureClient();
        }
        else
        {
            Width = _clientW / _k;
            Height = _clientH / _k;
            Root.Width = _clientW / _k;
            Root.Height = _clientH / _k;
            UpdateLayout();
        }

        // WPF 改完尺寸后客户区可能变了，重新量一次
        int oldW = _clientW, oldH = _clientH;
        MeasureClient();
        if (_clientW != oldW || _clientH != oldH)
        {
            Root.Width = _clientW / _k;
            Root.Height = _clientH / _k;
            if (!_legacyLayout)
            {
                Width = _clientW / _k;
                Height = _clientH / _k;
            }
            UpdateLayout();
            MeasureClient();
        }

        // 自校准：实际生效的缩放 = 客户区物理宽 / Root 实际 DIP 宽
        if (Root.ActualWidth > 1)
        {
            double kReal = _clientW / Root.ActualWidth;
            if (Math.Abs(kReal - _k) > 0.02)
            {
                Dbg($"缩放系数自校准: WPF={_k:F4} 实际={kReal:F4}，采用实际值");
                _k = kReal;
                Root.Width = _clientW / _k;
                Root.Height = _clientH / _k;
                UpdateLayout();
                MeasureClient();
            }
        }

        // 根 Canvas 的物理原点（PointToScreen 返回物理像素 —— 探针已验证）
        try
        {
            var p = Root.PointToScreen(new System.Windows.Point(0, 0));
            _originX = (int)Math.Round(p.X);
            _originY = (int)Math.Round(p.Y);
        }
        catch
        {
            _originX = _screen.X;
            _originY = _screen.Y;
        }

        Geometry = new RegionGeometry
        {
            K = _k,
            ClientW = _clientW,
            ClientH = _clientH,
            OriginX = _originX,
            OriginY = _originY,
            ScreenX = _screen.X,
            ScreenY = _screen.Y,
            ScreenW = _screen.Width,
            ScreenH = _screen.Height,
            RootDipW = Root.ActualWidth,
            RootDipH = Root.ActualHeight
        };

        Dbg($"Loaded screen={_screen} client={_clientW}x{_clientH} origin=({_originX},{_originY}) k={_k:F4} root={Root.ActualWidth}x{Root.ActualHeight}");
        LayoutMasks();

        // 预览刷新：低频抓屏，够看清就行，不用跟录制帧率走
        _previewTimer.Start();
    }

    /// <summary>读回窗口客户区的真实物理尺寸。</summary>
    private void MeasureClient()
    {
        try
        {
            var rc = new NativeMethods.NativeRect();
            NativeMethods.GetClientRect(_hwnd, out rc);
            _clientW = rc.Width;
            _clientH = rc.Height;
        }
        catch (Exception ex)
        {
            Dbg("MeasureClient 失败：" + ex.Message);
            _clientW = _screen.Width;
            _clientH = _screen.Height;
        }
    }

    // ---------- 坐标换算（核心：全部物理像素） ----------

    /// <summary>把鼠标事件的 DIP 位置换算成虚拟屏幕绝对物理坐标。</summary>
    private System.Drawing.Point GetPhysPoint(System.Windows.Input.MouseEventArgs e)
    {
        try
        {
            // PointToScreen 返回的就是物理像素（探针实测确认）
            var p = Root.PointToScreen(e.GetPosition(Root));
            return new System.Drawing.Point((int)Math.Round(p.X), (int)Math.Round(p.Y));
        }
        catch
        {
            // 兜底：直接读系统光标
            if (NativeMethods.GetCursorPos(out var cp))
                return new System.Drawing.Point(cp.X, cp.Y);
            return System.Drawing.Point.Empty;
        }
    }

    /// <summary>绝对物理坐标 → 相对根 Canvas 的 DIP（仅用于摆放界面元素）。</summary>
    private double ToDipX(int absPhys) => (absPhys - _originX) / _k;
    private double ToDipY(int absPhys) => (absPhys - _originY) / _k;

    // ---------- 选区维护 ----------

    private void LayoutMasks()
    {
        double w = Root.Width, h = Root.Height;
        if (_hasSel)
        {
            double l = ToDipX(_sel.Left), t = ToDipY(_sel.Top);
            double r = ToDipX(_sel.Right), b = ToDipY(_sel.Bottom);

            MaskTop.Width = w; MaskTop.Height = Math.Max(0, t);
            Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);

            MaskBottom.Width = w; MaskBottom.Height = Math.Max(0, h - b);
            Canvas.SetLeft(MaskBottom, 0); Canvas.SetTop(MaskBottom, b);

            MaskLeft.Width = Math.Max(0, l); MaskLeft.Height = Math.Max(0, b - t);
            Canvas.SetLeft(MaskLeft, 0); Canvas.SetTop(MaskLeft, t);

            MaskRight.Width = Math.Max(0, w - r); MaskRight.Height = Math.Max(0, b - t);
            Canvas.SetLeft(MaskRight, r); Canvas.SetTop(MaskRight, t);
        }
        else
        {
            MaskTop.Width = w; MaskTop.Height = h; Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
            MaskBottom.Width = 0; MaskBottom.Height = 0;
            MaskLeft.Width = 0; MaskLeft.Height = 0;
            MaskRight.Width = 0; MaskRight.Height = 0;
        }
    }

    private void UpdateVisuals()
    {
        if (!_hasSel)
        {
            SelBorder.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            foreach (var hd in Handles) hd.Visibility = Visibility.Collapsed;
            LayoutMasks();
            return;
        }

        SelBorder.Visibility = Visibility.Visible;
        double l = ToDipX(_sel.Left), t = ToDipY(_sel.Top);
        double w = ToDipX(_sel.Width), h = ToDipY(_sel.Height);
        Canvas.SetLeft(SelBorder, l);
        Canvas.SetTop(SelBorder, t);
        SelBorder.Width = w;
        SelBorder.Height = h;

        // 8 个控制点：角上的 10×10，边中点的 8×8，都以选区边线为中心
        const double hs = 5;
        Place(HandleNW, l - hs, t - hs);
        Place(HandleN, l + w / 2 - 4, t - 4);
        Place(HandleNE, l + w - hs, t - hs);
        Place(HandleE, l + w - 4, t + h / 2 - 4);
        Place(HandleSE, l + w - hs, t + h - hs);
        Place(HandleS, l + w / 2 - 4, t + h - 4);
        Place(HandleSW, l - hs, t + h - hs);
        Place(HandleW, l - 4, t + h / 2 - 4);

        // 起点用「虚拟屏幕绝对物理坐标」显示，和录出来的文件完全一致
        ToolSizeText.Text = $"{_sel.Width} × {_sel.Height} 像素　(起点 {_sel.X},{_sel.Y})";
        Toolbar.Visibility = Visibility.Visible;
        LayoutToolbar();

        LayoutMasks();
    }

    /// <summary>浮动工具条：默认贴在选区下方，放不下就翻到上方，再夹回屏幕内。</summary>
    private void LayoutToolbar()
    {
        Toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = Toolbar.DesiredSize.Width;
        double th = Toolbar.DesiredSize.Height;
        if (tw <= 0 || th <= 0) return;

        double l = ToDipX(_sel.Left), t = ToDipY(_sel.Top);
        double r = ToDipX(_sel.Right), b = ToDipY(_sel.Bottom);

        double x = l;
        double y = b + 10;                              // 默认在下面
        if (y + th > Root.Height - 8)                   // 下面放不下 → 翻到上面
        {
            y = t - th - 10;
            if (y < 8) y = Root.Height - th - 8;        // 上面也放不下 → 贴底
        }
        x = Math.Max(8, Math.Min(x, Root.Width - tw - 8));
        y = Math.Max(8, Math.Min(y, Root.Height - th - 8));

        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    private void Place(FrameworkElement el, double x, double y)
    {
        el.Visibility = Visibility.Visible;
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
    }

    private void SafeCapture()
    {
        try { CaptureMouse(); }
        catch { /* 窗口失焦等情况下捕获失败，不影响框选 */ }
    }

    private void SafeRelease()
    {
        try { ReleaseMouseCapture(); }
        catch { }
    }

    // ---------- 鼠标 ----------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 工具条上的按钮不触发拖拽（否则点「确认」会先在按钮位置起一个选区）
        if (IsInsideButton(e.OriginalSource)) return;

        var p = GetPhysPoint(e);
        Dbg($"MouseDown abs={p.X},{p.Y} mode={_mode} hasSel={_hasSel}");

        if (_hasSel)
        {
            int handle = HitHandle(p);
            if (handle >= 0)
            {
                _mode = DragMode.Resize;
                _handle = handle;
                _selAtDragStart = _sel;
                _dragStart = p;
                SafeCapture();
                return;
            }
            if (_sel.Contains(p))
            {
                _mode = DragMode.Move;
                _selAtDragStart = _sel;
                _dragStart = p;
                SafeCapture();
                return;
            }
        }

        _mode = DragMode.Create;
        _dragStart = p;
        _hasSel = false;
        _sel = Rectangle.Empty;
        SafeCapture();
    }

    /// <summary>命中 8 个控制点中的哪一个；没命中返回 -1。</summary>
    private int HitHandle(System.Drawing.Point p)
    {
        if (!_hasSel) return -1;
        const int tol = 16;
        int l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;

        // 四个角：两个方向都要命中
        if (Math.Abs(p.X - l) <= tol && Math.Abs(p.Y - t) <= tol) return 0;   // NW
        if (Math.Abs(p.X - r) <= tol && Math.Abs(p.Y - t) <= tol) return 2;   // NE
        if (Math.Abs(p.X - r) <= tol && Math.Abs(p.Y - b) <= tol) return 4;   // SE
        if (Math.Abs(p.X - l) <= tol && Math.Abs(p.Y - b) <= tol) return 6;   // SW

        // 四条边的中点：垂直方向命中，且必须落在两个角之间（避免和角重叠）
        if (Math.Abs(p.Y - t) <= tol && p.X > l + tol && p.X < r - tol) return 1;   // N
        if (Math.Abs(p.X - r) <= tol && p.Y > t + tol && p.Y < b - tol) return 3;   // E
        if (Math.Abs(p.Y - b) <= tol && p.X > l + tol && p.X < r - tol) return 5;   // S
        if (Math.Abs(p.X - l) <= tol && p.Y > t + tol && p.Y < b - tol) return 7;   // W
        return -1;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 只看内部拖拽状态，不看 e.LeftButton：
        // WPF 的 e.LeftButton 取自真实按键状态，合成事件/某些输入设备下不可靠。
        if (_mode == DragMode.None) return;
        var p = GetPhysPoint(e);

        switch (_mode)
        {
            case DragMode.Create:
                _sel = Normalize(_dragStart, p);
                _hasSel = _sel.Width > 2 && _sel.Height > 2;
                UpdateVisuals();
                break;

            case DragMode.Move:
                int dx = p.X - _dragStart.X;
                int dy = p.Y - _dragStart.Y;
                var moved = _selAtDragStart;
                moved.Offset(dx, dy);
                // 限制在遮罩层范围内
                moved.Offset(-Math.Max(0, _originX - moved.Left), -Math.Max(0, _originY - moved.Top));
                moved.Offset(-Math.Max(0, moved.Right - (_originX + _clientW)),
                             -Math.Max(0, moved.Bottom - (_originY + _clientH)));
                _sel = moved;
                UpdateVisuals();
                break;

            case DragMode.Resize:
                ApplyResize(_handle, p);
                _hasSel = _sel.Width > 2 && _sel.Height > 2;
                UpdateVisuals();
                break;
        }
    }

    /// <summary>按控制点方向重算选区。</summary>
    private void ApplyResize(int handle, System.Drawing.Point p)
    {
        int l = _selAtDragStart.Left, t = _selAtDragStart.Top;
        int r = _selAtDragStart.Right, b = _selAtDragStart.Bottom;
        switch (handle)
        {
            case 0: l = p.X; t = p.Y; break;   // NW
            case 1: t = p.Y; break;             // N
            case 2: r = p.X; t = p.Y; break;   // NE
            case 3: r = p.X; break;             // E
            case 4: r = p.X; b = p.Y; break;   // SE
            case 5: b = p.Y; break;             // S
            case 6: l = p.X; b = p.Y; break;   // SW
            case 7: l = p.X; break;             // W
        }

        int x = Math.Min(l, r), y = Math.Min(t, b);
        int w = Math.Abs(r - l), h = Math.Abs(b - t);
        if (w < 16) w = 16;
        if (h < 16) h = 16;
        _sel = new Rectangle(x, y, w, h);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == DragMode.None) return;
        _mode = DragMode.None;
        SafeRelease();

        if (_hasSel && (_sel.Width < 16 || _sel.Height < 16))
        {
            _hasSel = false;
            _sel = Rectangle.Empty;
        }
        UpdateVisuals();

        // 松手后立刻抓一张预览，不等下一个定时器
        if (_hasSel) RefreshPreview();
    }

    private static Rectangle Normalize(System.Drawing.Point a, System.Drawing.Point b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }

    // ---------- 实时预览 ----------

    /// <summary>抓一张选区画面，放进工具条上的缩略图。</summary>
    private void RefreshPreview()
    {
        if (!_hasSel || _closing) return;
        try
        {
            using var bmp = CaptureRegionPreview(_sel, PreviewBoxW, PreviewBoxH);
            if (bmp == null)
            {
                Dbg("预览抓屏失败（CaptureRegionPreview 返回空）");
                return;
            }
            var src = ToBitmapSource(bmp);
            if (src == null)
            {
                Dbg("预览转换失败（ToBitmapSource 返回空）");
                return;
            }
            PreviewImage.Source = src;
            Dbg($"预览更新成功 {bmp.Width}x{bmp.Height}");
        }
        catch (Exception ex)
        {
            Dbg("预览异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// GDI+ 位图 → WPF 位图源。
    ///
    /// 走「先缩到缩略图大小，再编码成 PNG 读回来」这条路：
    /// 用的都是 WPF 里肯定存在的 API（BitmapImage + MemoryStream），
    /// 和悬浮条的实时预览用的是同一套写法，行为可预期。
    /// </summary>
    private static BitmapSource? ToBitmapSource(Bitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = bmp.Width;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 抓选区画面并缩成缩略图。
    ///
    /// ★ 为什么要「内缩 6 像素」再抓：选框描边（2px，跨在边界上）和 8 个控制点
    ///   （10×10，跨在角上）都会压在选区边缘上，直接抓会把这圈装饰也拍进预览。
    ///   内缩 6px 正好把它们避开，换来一个完全无闪烁的干净画面 ——
    ///   比「临时隐藏装饰再抓」好得多（那会每帧闪一下边框）。
    /// </summary>
    private static Bitmap? CaptureRegionPreview(Rectangle physRect, int boxW, int boxH)
    {
        Bitmap? full = null;
        Bitmap? small = null;
        try
        {
            int w = physRect.Width, h = physRect.Height;
            if (w <= 0 || h <= 0) return null;

            int inset = Math.Min(6, Math.Min(w / 4, h / 4));
            var grab = new Rectangle(
                physRect.Left + inset, physRect.Top + inset,
                Math.Max(1, w - inset * 2), Math.Max(1, h - inset * 2));

            full = new Bitmap(grab.Width, grab.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(grab.Left, grab.Top, 0, 0, new System.Drawing.Size(grab.Width, grab.Height));
            }

            // 等比缩到缩略图尺寸（留 2 倍余量给高分屏）
            double scale = Math.Min(boxW * 2.0 / grab.Width, boxH * 2.0 / grab.Height);
            if (scale > 1.0) scale = 1.0;
            int tw = Math.Max(1, (int)(grab.Width * scale));
            int th = Math.Max(1, (int)(grab.Height * scale));

            small = new Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, new Rectangle(0, 0, tw, th));
            }
            return small;
        }
        catch (Exception ex)
        {
            Dbg("预览抓屏异常：" + ex.GetType().Name + " " + ex.Message);
            small?.Dispose();
            return null;
        }
        finally
        {
            full?.Dispose();
        }
    }

    /// <summary>判断这次鼠标按下的原始位置是不是落在某个按钮里。</summary>
    private static bool IsInsideButton(object? src)
    {
        DependencyObject? cur = src as DependencyObject;
        int guard = 0;
        while (cur != null && guard++ < 64)
        {
            if (cur is Button) return true;
            DependencyObject? parent = null;
            if (cur is System.Windows.Media.Visual)
            {
                try { parent = VisualTreeHelper.GetParent(cur); } catch { }
            }
            if (parent == null)
            {
                try { parent = LogicalTreeHelper.GetParent(cur); } catch { }
            }
            cur = parent;
        }
        return false;
    }

    // ---------- 键盘（可选的加速路径） ----------

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 低级钩子可用时由它接管（输入法拦不住）；这里只在钩子没装上时补刀。
        if (_llHook != null && _llHook.Installed) return;
        Dbg($"WpfKey {e.Key} hasSel={_hasSel}");
        if (e.Key == Key.ImeProcessed) return;   // 输入法吃掉了，原生钩子会处理
        switch (e.Key)
        {
            case Key.Escape: Cancel(); e.Handled = true; break;
            case Key.Enter: if (_hasSel) Confirm(); e.Handled = true; break;
            case Key.R: ResetSelection(); e.Handled = true; break;
        }
    }

    // ---------- 结果 ----------

    private void ResetSelection()
    {
        if (_closing) return;
        _hasSel = false;
        _sel = Rectangle.Empty;
        PreviewImage.Source = null;
        UpdateVisuals();
    }

    private void Cancel()
    {
        // 幂等：低级钩子和窗口钩子可能对同一个按键各调一次
        if (_closing) return;
        _closing = true;
        SelectedRect = null;
        try { DialogResult = false; } catch { }
        try { Close(); } catch { }
    }

    private void Confirm()
    {
        // 幂等：同一个回车键可能被低级钩子和窗口钩子各处理一次，
        // 如果不设闸门，第二次会对已关闭的窗口再设一次 DialogResult 而抛异常。
        if (_closing) return;
        if (!_hasSel) return;
        if (_sel.Width < 16 || _sel.Height < 16) return;

        // 只保留目标显示器内的部分
        var screen = _screen;
        int x = Math.Max(_sel.Left, screen.Left);
        int y = Math.Max(_sel.Top, screen.Top);
        int x2 = Math.Min(_sel.Right, screen.Right);
        int y2 = Math.Min(_sel.Bottom, screen.Bottom);
        int w = x2 - x, h = y2 - y;
        if (w < 16 || h < 16) return;

        SelectedRect = new Rectangle(x, y, w, h);
        _closing = true;
        Dbg($"Confirm -> {x},{y} {w}x{h}");
        try { DialogResult = true; } catch { }
        try { Close(); } catch { }
    }

    // ---------- 按钮 ----------

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Confirm();
    private void OnResetClick(object sender, RoutedEventArgs e) => ResetSelection();
    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancel();

    /// <summary>调试用：直接摆一个选区（--regionshot 渲染用）。</summary>
    internal void SetDemoSelection(Rectangle physRect)
    {
        _sel = physRect;
        _hasSel = true;
        UpdateVisuals();
        RefreshPreview();
    }

    /// <summary>预览缩略图是否已经抓到画面（调试/自测用）。</summary>
    internal bool HasPreview => PreviewImage.Source != null;

    /// <summary>预览缩略图当前的图像源（调试/自测用）。</summary>
    internal System.Windows.Media.Imaging.BitmapSource? PreviewSource =>
        PreviewImage.Source as System.Windows.Media.Imaging.BitmapSource;
}

/// <summary>框选层的几何信息（诊断用）。</summary>
internal sealed class RegionGeometry
{
    public double K { get; set; }
    public int ClientW { get; set; }
    public int ClientH { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public int ScreenW { get; set; }
    public int ScreenH { get; set; }
    public double RootDipW { get; set; }
    public double RootDipH { get; set; }
}
