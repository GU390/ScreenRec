using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ScreenRec;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private List<ScreenInfo> _screens = new();
    private Recorder? _recorder;
    private RecordingHudWindow? _hud;
    private bool _recording;
    private HwndSource? _hotkeySource;
    private LowLevelKeyHook? _llHook;
    private string _currentFormat = "avi";
    private string _mp4Path = string.Empty;

    /// <summary>
    /// InitializeComponent 期间，XAML 里设 IsChecked="True" 会立刻触发 Checked 事件，
    /// 那时后面的控件字段还没赋值 —— 必须等 XAML 加载完才允许事件驱动 UI 更新。
    /// </summary>
    private bool _uiReady;

    private const int HotkeyF9 = 0x5109;
    private const int HotkeyF10 = 0x510A;
    private const int HotkeyEsc = 0x510B;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _uiReady = true;
        ApplySettingsToUi();
        RefreshScreens();
        UpdateUiState();
        Recorder.Warmup();

        // 窗口高度按内容自适应，但不超过屏幕工作区（超高就滚动，绝不把控件挤到看不见）
        try
        {
            var work = SystemParameters.WorkArea;
            double cap = work.Height - 46;      // 给标题栏和边框留位置
            if (cap > 300)
            {
                ScrollHost.MaxHeight = cap;
                MaxHeight = work.Height - 8;
            }
        }
        catch
        {
            // 取不到工作区就用 XAML 里的默认值
        }
    }

    // ---------- UI <-> 设置 ----------

    private void ApplySettingsToUi()
    {
        FpsSlider.Value = Math.Clamp(_settings.Fps, 5, 60);
        QualitySlider.Value = Math.Clamp(_settings.Quality, 40, 100);
        OutputDirBox.Text = _settings.OutputDir;
        CheckHideMain.IsChecked = _settings.HideMainWindow;
        CheckHud.IsChecked = _settings.ShowHud;
        CheckPreview.IsChecked = _settings.LivePreview;
        CheckOpenFolder.IsChecked = _settings.OpenFolderWhenDone;
        RadioRegion.IsChecked = _settings.HasRegion;
        RadioFull.IsChecked = !_settings.HasRegion;
        // 倒计时：0 表示立即开始
        int cd = Math.Clamp(_settings.CountdownSeconds, 0, 3600);
        RadioImmediate.IsChecked = cd <= 0;
        RadioCountdown.IsChecked = cd > 0;
        if (cd > 0) CountdownSecBox.Text = cd.ToString();
        switch (_settings.OutputFormat)
        {
            case "both": RadioBoth.IsChecked = true; break;
            case "avi": RadioAvi.IsChecked = true; break;
            default: RadioMp4.IsChecked = true; break;
        }
        OnParamChanged(this, new RoutedEventArgs());
        OnCountdownChanged(this, new RoutedEventArgs());
        UpdateFfmpegText();
    }

    private void ReadUiToSettings()
    {
        _settings.Fps = (int)FpsSlider.Value;
        _settings.Quality = (int)QualitySlider.Value;
        _settings.CountdownSeconds = CountdownSeconds;
        _settings.OutputDir = OutputDirBox.Text.Trim();
        _settings.HideMainWindow = CheckHideMain.IsChecked == true;
        _settings.ShowHud = CheckHud.IsChecked == true;
        _settings.LivePreview = CheckPreview.IsChecked == true;
        _settings.OpenFolderWhenDone = CheckOpenFolder.IsChecked == true;
        _settings.OutputFormat = CurrentFormat;
    }

    /// <summary>当前倒计时秒数（0 = 立即开始）。</summary>
    private int CountdownSeconds =>
        RadioCountdown.IsChecked == true
            ? Math.Clamp(ParseIntSafe(CountdownSecBox.Text), 1, 3600)
            : 0;

    private static int ParseIntSafe(string? s)
    {
        return int.TryParse((s ?? string.Empty).Trim(), out int v) ? v : 0;
    }

    private string CurrentFormat =>
        RadioBoth.IsChecked == true ? "both" :
        RadioMp4.IsChecked == true ? "mp4" : "avi";

    /// <summary>滚动区允许的最大高度（DIP）—— 自测用来判断内容有没有超出。</summary>
    internal double ScrollHostMaxHeight => ScrollHost.MaxHeight;

    /// <summary>内容的实际高度（DIP）—— 比 ScrollHostMaxHeight 大就说明有内容被藏起来了。</summary>
    internal double ContentHeightDip
    {
        get
        {
            var inner = ScrollHost.Content as FrameworkElement;
            return inner?.ActualHeight ?? ScrollHost.ActualHeight;
        }
    }

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateFileNameText();
        UpdateFfmpegText();
        UpdateUsageEstimate();
    }

    private void UpdateFfmpegText()
    {
        string? ff = FfmpegLocator.Find(_settings.FfmpegPath);
        if (ff == null)
        {
            FfmpegText.Text = "未找到 —— MP4 不可用，将只输出 AVI";
            FfmpegText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC0, 0x62, 0x2B));
        }
        else
        {
            FfmpegText.Text = ff;
            FfmpegText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4B, 0x7B, 0x3A));
        }
    }

    private void OnPickFfmpeg(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ffmpeg.exe（选不到就清空自动探测）",
            Filter = "ffmpeg.exe|ffmpeg.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            _settings.FfmpegPath = dlg.FileName;
            _settings.Save();
            UpdateFfmpegText();
        }
    }

    private void RefreshScreens()
    {
        _screens = ScreenHelper.GetScreens();
        ScreenCombo.ItemsSource = null;
        ScreenCombo.ItemsSource = _screens;
        int idx = _screens.FindIndex(s => s.DeviceName == _settings.ScreenDevice);
        ScreenCombo.SelectedIndex = idx >= 0 ? idx : 0;
        if (ScreenCombo.SelectedIndex < 0 && _screens.Count > 0) ScreenCombo.SelectedIndex = 0;
    }

    private ScreenInfo SelectedScreen =>
        ScreenCombo.SelectedItem as ScreenInfo ?? _screens.FirstOrDefault() ?? new ScreenInfo("", "屏幕", new Rectangle(0, 0, 1920, 1080), true, 1.0);

    private void OnScreenChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (ScreenCombo.SelectedItem is ScreenInfo s)
        {
            _settings.ScreenDevice = s.DeviceName;
        }
        UpdateRegionText();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateUiState();
        UpdateRegionText();
    }

    private void OnParamChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        FpsText.Text = $"{FpsSlider.Value:0} fps";
        QualityText.Text = $"{QualitySlider.Value:0}";
        _settings.Fps = (int)FpsSlider.Value;
        _settings.Quality = (int)QualitySlider.Value;

        UpdateUsageEstimate();
    }

    /// <summary>倒计时选项变化：控制秒数输入框的可用状态。</summary>
    private void OnCountdownChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        bool on = RadioCountdown.IsChecked == true;
        CountdownSecBox.IsEnabled = on;
        CountdownSecBox.Foreground = on
            ? System.Windows.Media.Brushes.Black
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA3, 0xAF));
        if (on)
        {
            int v = ParseIntSafe(CountdownSecBox.Text);
            if (v <= 0) CountdownSecBox.Text = "3";
        }
    }

    /// <summary>按当前分辨率 / 帧率 / 画质 / 格式，粗算资源占用并刷新柱形图。</summary>
    private void UpdateUsageEstimate()
    {
        if (!_uiReady) return;
        try
        {
            var screen = SelectedScreen;
            bool regionMode = RadioRegion.IsChecked == true && _settings.HasRegion;
            Rectangle rect = regionMode ? _settings.RegionRect : screen.Bounds;
            if (rect.Width <= 0 || rect.Height <= 0) rect = new Rectangle(0, 0, 1920, 1080);

            bool mp4 = CurrentFormat != "avi";
            var est = ResourceUsage.Estimate(
                rect.Width, rect.Height,
                (int)FpsSlider.Value, (int)QualitySlider.Value, mp4);

            UsageBars.SetEstimate(est);
            // 近乎无损要明确说清楚代价
            UsageNote.Text = est.NearLossless
                ? "近乎无损档（画质≥95）：画质几乎无损，但文件明显变大、CPU 占用更高；实际占用随画面变化而变"
                : "按当前分辨率 / 帧率 / 画质粗算，实际占用随画面变化而变";
            UsageNote.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(
                    est.NearLossless ? (byte)0xC0 : (byte)0x6B,
                    est.NearLossless ? (byte)0x62 : (byte)0x72,
                    est.NearLossless ? (byte)0x2B : (byte)0x80));
        }
        catch
        {
            // 预估失败不影响主流程
        }
    }

    private void UpdateRegionText()
    {
        bool regionMode = RadioRegion.IsChecked == true;
        RegionPanel.Visibility = regionMode ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.HasRegion)
        {
            var r = _settings.RegionRect;
            RegionText.Text = $"{r.Width} × {r.Height}　起点 {r.X}, {r.Y}";
        }
        else
        {
            RegionText.Text = "尚未框选";
        }
        UpdateFileNameText();
        UpdateUsageEstimate();
    }

    private void UpdateFileNameText()
    {
        string dir = string.IsNullOrWhiteSpace(OutputDirBox.Text) ? "（未设置）" : OutputDirBox.Text.Trim();
        string ext = CurrentFormat == "avi" ? ".avi" : ".mp4";
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        FileNameText.Text = $"{dir}　→　录像_{stamp}{ext}" + (CurrentFormat == "both" ? "（+ .avi 母版）" : "");
    }

    private void UpdateUiState()
    {
        if (!_uiReady) return;
        bool busy = _recording;
        ScreenCombo.IsEnabled = !busy;
        RadioFull.IsEnabled = !busy;
        RadioRegion.IsEnabled = !busy;
        FpsSlider.IsEnabled = !busy;
        QualitySlider.IsEnabled = !busy;
        OutputDirBox.IsEnabled = !busy;
        CheckHideMain.IsEnabled = !busy;
        CheckHud.IsEnabled = !busy;
        CheckPreview.IsEnabled = !busy;
        CheckOpenFolder.IsEnabled = !busy;
        RadioImmediate.IsEnabled = !busy;
        RadioCountdown.IsEnabled = !busy;
        CountdownSecBox.IsEnabled = !busy && RadioCountdown.IsChecked == true;

        StartButton.Content = busy ? "停止录像" : "开始录像";
        StartButton.Background = busy
            ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B))
            : (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x6B, 0xEA));
    }

    // ---------- 区域框选 ----------

    private void OnProbe(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = RegionProbe.MeasureReport(false);
            System.Windows.MessageBox.Show(this, report, "坐标自检", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "自检失败：" + ex.Message, "坐标自检", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPickRegion(object sender, RoutedEventArgs e)
    {
        PickRegion();
    }

    /// <summary>
    /// 框选区域的重入守卫。
    ///
    /// ★ 为什么需要它：用户报告「确认以后还有第二次框选」。
    ///   最可能的成因是框选层开着的时候又收到了一次「打开框选」的请求 ——
    ///   比如按了 F10 全局热键：主窗口的 WndProc 会在**模态框选层的嵌套消息循环里**
    ///   再调一次 PickRegion，于是同一个线程上套出了第二个遮罩层。
    ///   有了这个闸门，任何来源的第二次请求都会被直接忽略，
    ///   从根上保证「界面上永远只可能有一个框选层」。
    /// </summary>
    private bool _regionDialogOpen;

    private void PickRegion()
    {
        if (_regionDialogOpen)
        {
            RegionSelectWindow.Dbg("PickRegion 被重入调用（框选层已开着），已忽略");
            StatusText.Text = "框选层已经开着，请先在框选层上点确认或取消";
            return;
        }

        var screen = SelectedScreen;
        _regionDialogOpen = true;
        try
        {
            var win = new RegionSelectWindow(screen.Bounds);
            win.Owner = this;
            bool? ok = win.ShowDialog();
            if (ok == true && win.SelectedRect.HasValue)
            {
                var r = win.SelectedRect.Value;
                _settings.HasRegion = true;
                _settings.RegionX = r.X;
                _settings.RegionY = r.Y;
                _settings.RegionW = r.Width;
                _settings.RegionH = r.Height;
                _settings.Save();
                UpdateRegionText();
                StatusText.Text = $"已选择区域 {r.Width}×{r.Height}（起点 {r.X}, {r.Y}）";
            }
        }
        finally
        {
            _regionDialogOpen = false;
        }
    }

    // ---------- 开始 / 停止 ----------

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        ReadUiToSettings();

        var screen = SelectedScreen;
        Rectangle rect = RadioRegion.IsChecked == true ? _settings.RegionRect : screen.Bounds;
        if (RadioRegion.IsChecked == true && (!_settings.HasRegion || rect.Width < 16 || rect.Height < 16))
        {
            System.Windows.MessageBox.Show(this, "请先框选录制区域。", "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Information);
            RadioRegion.IsChecked = false;
            RadioFull.IsChecked = true;
            return;
        }

        string dir = OutputDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            System.Windows.MessageBox.Show(this, "请先设置输出目录。", "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "无法创建输出目录：\n" + ex.Message, "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ★ 倒计时：先校验参数，再倒计时，倒计时结束才真正开始录
        int cd = CountdownSeconds;
        if (cd > 0)
        {
            if (!RunCountdown(cd))
            {
                StatusText.Text = "已取消倒计时";
                return;
            }
        }

        StartRecordingNow(screen, rect, dir);
    }

    /// <summary>全屏倒计时。返回 true 表示倒计时走完（应该开始录），false 表示用户取消。</summary>
    private bool RunCountdown(int seconds)
    {
        try
        {
            var win = new CountdownWindow(seconds, VirtualScreenBounds());
            bool completed = false;
            win.Completed += () => completed = true;
            win.ShowDialog();
            return completed;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "倒计时出错，已直接开始录制：\n" + ex.Message,
                "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
    }

    /// <summary>所有显示器拼起来的虚拟屏幕范围（物理像素）。</summary>
    private static Rectangle VirtualScreenBounds()
    {
        var screens = ScreenHelper.GetScreens();
        if (screens.Count == 0) return new Rectangle(0, 0, 1920, 1080);
        int l = screens.Min(s => s.Bounds.Left);
        int t = screens.Min(s => s.Bounds.Top);
        int r = screens.Max(s => s.Bounds.Right);
        int b = screens.Max(s => s.Bounds.Bottom);
        return new Rectangle(l, t, r - l, b - t);
    }

    private void StartRecordingNow(ScreenInfo screen, Rectangle rect, string dir)
    {
        string path = UniquePath(dir);
        string fmt = CurrentFormat;

        // 需要 MP4 时：定位 ffmpeg，启动实时 H.264 编码管道
        FfmpegSink? sink = null;
        string mp4Path = Path.ChangeExtension(path, ".mp4");
        if (fmt != "avi")
        {
            var ff = FfmpegLocator.Find(_settings.FfmpegPath);
            if (ff == null)
            {
                System.Windows.MessageBox.Show(this,
                    "没有找到 ffmpeg，无法输出 MP4。\n\n本次将只输出 AVI（MJPEG）。\n" +
                    "如需 MP4：装好 ffmpeg 后点「选择…」指定 ffmpeg.exe。",
                    "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Information);
                fmt = "avi";
            }
            else
            {
                sink = FfmpegSink.Start(ff, mp4Path, _settings.Fps, FfmpegLocator.QualityToCrf(_settings.Quality));
                if (sink == null)
                {
                    fmt = "avi";
                }
            }
        }

        var rec = new Recorder(path, rect)
        {
            Fps = _settings.Fps,
            Quality = _settings.Quality,
            Mp4Sink = sink
        };
        _currentFormat = fmt;
        _mp4Path = mp4Path;

        // 悬浮条：先算好它的物理位置，用作「不录进去」的区域
        RecordingHudWindow? hud = null;
        Rectangle? hole = null;
        if (_settings.ShowHud)
        {
            hud = new RecordingHudWindow();
            hud.SetPreviewEnabled(_settings.LivePreview);
            double k = screen.Scale <= 0 ? 1.0 : screen.Scale;
            hud.Width = 236 / k;
            hud.Measure(new System.Windows.Size(236 / k, double.PositiveInfinity));
            hud.UpdateLayout();
            double hDip = Math.Max(80, hud.DesiredSize.Height);
            hud.Height = hDip;
            int pw = 236;
            int ph = (int)Math.Round(hDip * k);
            hole = new Rectangle(screen.Bounds.Right - pw - 12, screen.Bounds.Top + 12, pw, ph);
            rec.ExcludeRect = hole;
        }

        try
        {
            rec.Start();
        }
        catch (Exception ex)
        {
            rec.Dispose();
            System.Windows.MessageBox.Show(this, "启动录制失败：\n" + ex.Message, "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _recorder = rec;
        _recording = true;
        UpdateUiState();
        StatusText.Text = $"录制中 → {Path.GetFileName(path)}";
        _settings.Save();

        if (hud != null && hole.HasValue)
        {
            _hud = hud;
            hud.StopRequested += () => Dispatcher.Invoke(StopRecording);
            hud.StartTicking(rec);
            // 延后一点再显示悬浮条：首帧已经完整抓到背景，避免把悬浮条录进去
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    hud.Show();
                    var hwnd = new WindowInteropHelper(hud).Handle;
                    NativeMethods.SetWindowPosExact(hwnd, hole.Value, show: false);
                    hud.Topmost = true;
                }
                catch
                {
                    // ignore
                }
            };
            timer.Start();
        }

        if (_settings.HideMainWindow)
        {
            Hide();
        }
    }

    private void StopRecording()
    {
        if (!_recording || _recorder == null) return;
        _recording = false;

        var rec = _recorder;
        rec.RequestStop();
        rec.WaitForStop(15000);

        _hud?.StopTicking();
        _hud?.Close();
        _hud = null;
        _recorder = null;

        if (!IsVisible) Show();

        UpdateUiState();

        // 收尾 H.264 管道（关 stdin → ffmpeg 写 moov）
        bool mp4Ok = false;
        string mp4Error = string.Empty;
        var sink = rec.Mp4Sink;
        if (sink != null)
        {
            StatusText.Text = "正在收尾 MP4…";
            mp4Ok = sink.Finish();
            if (!mp4Ok) mp4Error = sink.Error ?? "未知错误";
            sink.Dispose();
        }

        // 先释放录制器（AviWriter 还占着文件句柄，不释放的话空文件删不掉）
        rec.Dispose();

        var sb = new System.Text.StringBuilder();
        bool empty = rec.FrameCount == 0;
        string filePath = rec.OutputPath;

        // MP4 模式且转码成功 → 删掉中间 AVI 母版
        if (_currentFormat == "mp4" && mp4Ok && !empty)
        {
            try
            {
                if (File.Exists(rec.OutputPath)) File.Delete(rec.OutputPath);
                filePath = _mp4Path;
            }
            catch (Exception ex)
            {
                mp4Error = "MP4 已生成，但中间 AVI 删除失败：" + ex.Message;
            }
        }
        else if (_currentFormat == "both")
        {
            filePath = mp4Ok ? $"{_mp4Path}\n（同时保留母版 {Path.GetFileName(rec.OutputPath)}）" : rec.OutputPath;
        }

        if (empty)
        {
            // 一帧都没抓到 —— 留下一个空壳 AVI 只会让人困惑，直接清掉
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                filePath = "（空文件已删除：没抓到任何画面）";
            }
            catch
            {
                filePath = rec.OutputPath + "（没抓到画面，删除失败）";
            }
        }

        // 报最终产物的真实大小（MP4 与母版体积差很多）
        string finalFile = (_currentFormat == "mp4" && mp4Ok) ? _mp4Path : rec.OutputPath;
        string sizeText;
        try
        {
            var fi = new FileInfo(finalFile);
            sizeText = fi.Exists ? $"{fi.Length / 1024.0 / 1024.0:0.00} MB" : "未知";
        }
        catch
        {
            sizeText = "未知";
        }

        sb.AppendLine(empty ? "录制结束，但没有抓到画面" : "录制已完成");
        sb.AppendLine();
        sb.AppendLine($"文件：{filePath}");
        sb.AppendLine($"时长：{rec.Elapsed.TotalSeconds:0.0} 秒　帧数：{rec.FrameCount}");
        sb.AppendLine($"大小：{sizeText}　分辨率：{rec.CaptureRect.Width}×{rec.CaptureRect.Height}");
        if (_currentFormat == "both" && mp4Ok)
        {
            sb.AppendLine($"（MJPEG 母版 {rec.BytesWritten / 1024.0 / 1024.0:0.00} MB，已一并保留）");
        }
        if (!string.IsNullOrEmpty(mp4Error))
        {
            sb.AppendLine();
            sb.AppendLine("MP4：" + mp4Error);
        }
        if (rec.Error != null)
        {
            sb.AppendLine();
            sb.AppendLine("注意：" + rec.Error.Message);
        }
        StatusText.Text = empty
            ? "没抓到画面，详见提示"
            : $"已保存：{Path.GetFileName(finalFile)}（{rec.Elapsed.TotalSeconds:0.0} 秒 / {sizeText}）";

        if (_settings.OpenFolderWhenDone)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetDirectoryName(rec.OutputPath) ?? ".",
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignore
            }
        }

        System.Windows.MessageBox.Show(this, sb.ToString(), "屏幕录像", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string UniquePath(string dir)
    {
        string name = $"录像_{DateTime.Now:yyyyMMdd_HHmmss}.avi";
        string path = Path.Combine(dir, name);
        int i = 1;
        while (File.Exists(path))
        {
            name = $"录像_{DateTime.Now:yyyyMMdd_HHmmss}_{i}.avi";
            path = Path.Combine(dir, name);
            i++;
        }
        return path;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择录像保存目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(OutputDirBox.Text.Trim()) ? OutputDirBox.Text.Trim() : AppSettings.DefaultOutputDir()
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            OutputDirBox.Text = dlg.SelectedPath;
            UpdateFileNameText();
        }
    }

    // ---------- 全局快捷键 ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _hotkeySource = HwndSource.FromHwnd(helper.Handle);
        _hotkeySource.AddHook(WndProc);

        // 全局快捷键：注册失败要告诉用户（常见原因：已经开了一个实例，热键被它占着）
        var failed = new List<string>();
        if (!NativeMethods.TryRegisterHotKey(helper.Handle, HotkeyF9, MOD_NOREPEAT, 0x78)) failed.Add("F9");
        if (!NativeMethods.TryRegisterHotKey(helper.Handle, HotkeyF10, MOD_NOREPEAT, 0x79)) failed.Add("F10");
        if (!NativeMethods.TryRegisterHotKey(helper.Handle, HotkeyEsc, MOD_NOREPEAT, 0x1B)) failed.Add("Esc");

        if (failed.Count > 0)
        {
            HotkeyWarn.Text = "⚠ 全局快捷键 " + string.Join(" / ", failed) +
                " 注册失败（已被其它程序或另一个实例占用）。\n" +
                "请用界面按钮操作，或关掉另一个实例后重开本程序。";
            HotkeyWarn.Visibility = Visibility.Visible;
        }

        // 低级键盘钩子：全局热键被占用时的兜底（Esc 停止录制）。
        // 只在本进程拥有前台窗口时动作，不会抢别的程序。
        try
        {
            _llHook = new LowLevelKeyHook();
            if (_llHook.Installed)
            {
                _llHook.KeyDown += OnLowLevelKey;
            }
        }
        catch
        {
            // ignore
        }
    }

    private void OnLowLevelKey(int vk)
    {
        try
        {
            Dispatcher.Invoke(new Action(() =>
            {
                if (vk != RawKeys.VK_ESCAPE) return;
                if (RawKeys.ModifierDown()) return;
                // 正在输入框里打字时不抢 Esc
                if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
                if (_recording) StopRecording();
            }));
        }
        catch
        {
            // ignore
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyF9)
            {
                handled = true;
                Dispatcher.Invoke(() => { if (_recording) StopRecording(); else StartRecording(); });
            }
            else if (id == HotkeyF10)
            {
                handled = true;
                Dispatcher.Invoke(() => { if (!_recording) PickRegion(); });
            }
            else if (id == HotkeyEsc)
            {
                handled = true;
                Dispatcher.Invoke(() => { if (_recording) StopRecording(); });
            }
        }
        else if (msg == RawKeys.WM_KEYDOWN)
        {
            // 全局热键被占用时的兜底：本窗口有焦点时，Esc 仍然能停止录制
            if ((int)wParam == RawKeys.VK_ESCAPE && !RawKeys.ModifierDown())
            {
                handled = true;
                Dispatcher.Invoke(() => { if (_recording) StopRecording(); });
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_recording)
        {
            StopRecording();
        }
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
        base.OnClosing(e);
    }
}
