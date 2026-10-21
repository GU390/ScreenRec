using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenRec;

/// <summary>录制中的悬浮提示条：时长 / 分辨率 / 帧数 / 文件大小 / 实时预览 / 停止按钮。</summary>
public partial class RecordingHudWindow : Window
{
    public event Action? StopRequested;

    private System.Windows.Threading.DispatcherTimer? _timer;
    private bool _previewEnabled = true;
    private long _lastPreviewIndex = -1;
    private DateTime _lastPreviewAt = DateTime.MinValue;

    public RecordingHudWindow()
    {
        InitializeComponent();
        StopButton.Click += (_, _) => StopRequested?.Invoke();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// 悬浮条上挂原生键盘钩子：录制时主窗口是隐藏的，此时 Esc 必须能在悬浮条上生效。
    /// 走原生虚拟键码，中文输入法也拦不住。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == RawKeys.WM_KEYDOWN && (int)w == RawKeys.VK_ESCAPE
                    && !RawKeys.ModifierDown())
                {
                    handled = true;
                    Dispatcher.Invoke(() => StopRequested?.Invoke());
                }
                return IntPtr.Zero;
            });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>预览开关。关闭时清空画面但保留占位，保证悬浮条尺寸稳定。</summary>
    public void SetPreviewEnabled(bool enabled)
    {
        _previewEnabled = enabled;
        if (!enabled)
        {
            PreviewImage.Source = null;
            _lastPreviewIndex = -1;
        }
    }

    internal void StartTicking(Recorder recorder)
    {
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += (_, _) => Tick(recorder);
        _timer.Start();
    }

    public void StopTicking()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Tick(Recorder recorder)
    {
        var ts = recorder.Elapsed;
        TimeText.Text = $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
        InfoText.Text = $"{recorder.CaptureRect.Width}×{recorder.CaptureRect.Height}　{recorder.FrameCount} 帧　{recorder.BytesWritten / 1024 / 1024} MB";

        var latest = recorder.LatestFrame;
        if (_previewEnabled && latest != null && latest.Index != _lastPreviewIndex)
        {
            var now = DateTime.UtcNow;
            if (now - _lastPreviewAt >= TimeSpan.FromMilliseconds(120))
            {
                _lastPreviewAt = now;
                _lastPreviewIndex = latest.Index;
                RenderPreview(latest.Jpeg);
            }
        }
    }

    private void RenderPreview(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 320;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            PreviewImage.Source = bi;
        }
        catch
        {
            // 预览失败不影响录像
        }
    }
}
