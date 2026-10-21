using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenRec;

/// <summary>
/// 录像前的倒计时遮罩：全屏大数字 3-2-1，结束后自动关闭并开始录制。
///
/// ★ 为什么不靠快捷键：Esc 只是加速路径，界面上有明确的「取消」按钮，
///   鼠标点一下就能取消，符合「不要依赖快捷键」的要求。
/// </summary>
public partial class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _left;

    /// <summary>倒计时结束（真正开始录制）时触发。</summary>
    public event Action? Completed;

    /// <summary>用户点了取消。</summary>
    public event Action? Cancelled;

    private bool _done;

    public CountdownWindow(int seconds, Rectangle virtualScreen)
    {
        InitializeComponent();

        _left = Math.Max(1, seconds);
        CountText.Text = _left.ToString();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        Loaded += (_, _) =>
        {
            // 精确铺满虚拟屏幕（物理像素，跨显示器也要盖住）
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPosExact(hwnd, virtualScreen);
            _timer.Start();
        };
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _left--;
        if (_left <= 0)
        {
            Finish(false);
            return;
        }
        CountText.Text = _left.ToString();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Finish(true);
    }

    /// <summary>结束倒计时。cancelled=true 表示用户主动取消。</summary>
    private void Finish(bool cancelled)
    {
        if (_done) return;      // 幂等：定时器和按钮可能同时触发
        _done = true;
        _timer.Stop();

        try
        {
            if (cancelled) Cancelled?.Invoke();
            else Completed?.Invoke();
        }
        catch
        {
            // ignore
        }

        try { Close(); } catch { /* ignore */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
