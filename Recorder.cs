using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ScreenRec;

/// <summary>
/// 抓屏录制核心：在后台线程按目标帧率抓取屏幕区域，编码为 JPEG 帧写入 AVI。
/// </summary>
internal sealed class Recorder : IDisposable
{
    public sealed class FrameInfo
    {
        public int Index { get; init; }
        public byte[] Jpeg { get; init; } = Array.Empty<byte>();
        public TimeSpan Elapsed { get; init; }
        public long TotalBytes { get; init; }
    }

    private readonly string _outputPath;
    private readonly Stopwatch _clock = new();

    private AviWriter? _writer;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _stopRequested;

    private Bitmap? _bitmap;
    private Graphics? _graphics;
    private ImageCodecInfo? _codec;
    private EncoderParameters? _encParams;

    private int _frameCount;
    private long _bytes;
    private bool _firstFrameDone;
    private Exception? _error;
    private int _consecutiveFailures;
    private FrameInfo? _latestFrame;

    /// <summary>连续抓屏失败多少次后放弃（偶发失败不算致命）。</summary>
    private const int MaxConsecutiveFailures = 30;

    private Rectangle _captureRect;
    private Rectangle? _excludeRect;

    public Recorder(string outputPath, Rectangle captureRect)
    {
        _outputPath = outputPath;
        _captureRect = captureRect;
    }

    public int Fps { get; set; } = 15;
    public int Quality { get; set; } = 80;
    public Rectangle CaptureRect => _captureRect;
    public string OutputPath => _outputPath;

    /// <summary>不希望录进去的屏幕区域（例如悬浮提示条所在处）。</summary>
    public Rectangle? ExcludeRect
    {
        get => _excludeRect;
        set => _excludeRect = value;
    }

    /// <summary>可选的实时 H.264 编码管道：每帧额外喂给 ffmpeg，边录边压。</summary>
    public FfmpegSink? Mp4Sink { get; set; }

    public int FrameCount => Volatile.Read(ref _frameCount);
    public long BytesWritten => Interlocked.Read(ref _bytes);
    public TimeSpan Elapsed => _clock.Elapsed;
    public Exception? Error => _error;
    public bool IsRunning => _running;

    /// <summary>最近抓到的一帧（供实时预览用）。</summary>
    public FrameInfo? LatestFrame => Volatile.Read(ref _latestFrame);

    public event Action<Exception>? Failed;

    public void Start()
    {
        if (_captureRect.Width < 16 || _captureRect.Height < 16)
        {
            throw new ArgumentException("录制区域太小（至少 16×16 像素）。");
        }

        _codec = FindJpegCodec() ?? throw new InvalidOperationException("系统缺少 JPEG 编码器。");
        _encParams = new EncoderParameters(1);
        _encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(Quality, 10, 100));

        _writer = new AviWriter(_outputPath, _captureRect.Width, _captureRect.Height, Fps);

        _clock.Restart();
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ScreenRec.Capture",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    /// <summary>请求停止（线程内收尾写索引）。</summary>
    public void RequestStop()
    {
        _stopRequested = true;
    }

    /// <summary>阻塞等待录制线程结束。</summary>
    public void WaitForStop(int timeoutMs = 15000)
    {
        try
        {
            _thread?.Join(timeoutMs);
        }
        catch
        {
            // ignore
        }
    }

    private static ImageCodecInfo? FindJpegCodec()
    {
        if (_cachedCodec != null) return _cachedCodec;

        // 用 MimeType 匹配，避免硬编码各平台不同的 GUID
        foreach (var c in ImageCodecInfo.GetImageEncoders())
        {
            if (string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                _cachedCodec = c;
                return c;
            }
        }
        foreach (var c in ImageCodecInfo.GetImageEncoders())
        {
            if (c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
            {
                _cachedCodec = c;
                return c;
            }
        }
        return null;
    }

    private static ImageCodecInfo? _cachedCodec;

    /// <summary>
    /// 预热 JPEG 编码器。首次枚举 GDI+ 编码器要几百毫秒，
    /// 不预热的话第一次点「开始录像」会明显延迟。
    /// </summary>
    public static void Warmup()
    {
        try
        {
            FindJpegCodec();
        }
        catch
        {
            // ignore
        }
    }

    private void Loop()
    {
        double intervalMs = 1000.0 / Math.Max(1, Fps);
        long tickFreq = System.Diagnostics.Stopwatch.Frequency;
        long nextDue = System.Diagnostics.Stopwatch.GetTimestamp();
        long frameInterval = (long)(intervalMs * tickFreq / 1000.0);

        try
        {
            while (!_stopRequested)
            {
                nextDue += frameInterval;
                var sw = Stopwatch.StartNew();
                byte[]? jpeg = null;
                try
                {
                    jpeg = CaptureFrame();
                    _consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    // 偶发失败（切屏、锁屏瞬间）不算致命，连续失败才放弃
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _error = new InvalidOperationException(
                            $"连续 {_consecutiveFailures} 次抓屏失败：{ex.Message}", ex);
                        break;
                    }
                    Thread.Sleep(50);
                    continue;
                }

                if (jpeg != null && jpeg.Length > 0)
                {
                    _writer!.AddFrame(jpeg);
                    int idx = Interlocked.Increment(ref _frameCount);
                    Interlocked.Add(ref _bytes, jpeg.Length);

                    // 实时喂给 ffmpeg 编码 H.264；管道断了也不影响 AVI 母版
                    var sink = Mp4Sink;
                    if (sink != null && !sink.Broken)
                    {
                        if (!sink.WriteFrame(jpeg))
                        {
                            Mp4Sink = null;
                        }
                    }

                    var info = new FrameInfo
                    {
                        Index = idx,
                        Jpeg = jpeg,
                        Elapsed = _clock.Elapsed,
                        TotalBytes = BytesWritten
                    };
                    Volatile.Write(ref _latestFrame, info);
                }

                // 精确等待到下一帧到期点：Windows 的 Thread.Sleep 粒度约 15ms，
                // 直接 Sleep 会让实际帧率明显低于设定值，所以最后 2ms 用自旋补齐。
                double delayMs = (nextDue - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / tickFreq;
                if (delayMs > 3)
                {
                    Thread.Sleep((int)(delayMs - 2));
                }
                else
                {
                    while (delayMs > 0.4)
                    {
                        Thread.SpinWait(24);
                        delayMs = (nextDue - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / tickFreq;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex;
        }
        finally
        {
            _running = false;
            try
            {
                // 写入实际达成的帧率，保证回放时间轴与真实录制一致
                double actual = _clock.Elapsed.TotalSeconds > 0.2
                    ? FrameCount / _clock.Elapsed.TotalSeconds
                    : Fps;
                _writer!.ActualFps = actual;
                _writer.Finish();
            }
            catch (Exception ex)
            {
                _error ??= ex;
            }
            if (_error != null)
            {
                Failed?.Invoke(_error);
            }
        }
    }

    private byte[] CaptureFrame()
    {
        var r = _captureRect;
        int w = r.Width, h = r.Height;

        if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
        {
            _bitmap?.Dispose();
            _graphics?.Dispose();
            _holeSnapshot?.Dispose();
            _holeSnapshot = null;
            _bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            _graphics = Graphics.FromImage(_bitmap);
        }

        var g = _graphics!;

        // 整块抓一次（一次屏幕读取最快）
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        if (!_firstFrameDone)
        {
            // 首帧时悬浮条还没出现，把「被排除区域」的真实画面存一份快照
            if (_excludeRect.HasValue)
            {
                var hr = Rectangle.Intersect(_excludeRect.Value, r);
                if (hr.Width > 0 && hr.Height > 0)
                {
                    _holeRect = new Rectangle(hr.Left - r.Left, hr.Top - r.Top, hr.Width, hr.Height);
                    _holeSnapshot = _bitmap.Clone(_holeRect, PixelFormat.Format24bppRgb);
                }
            }
            _firstFrameDone = true;
        }
        else if (_holeSnapshot != null)
        {
            // 后续帧把被排除区域还原成首帧的静态画面 —— 悬浮条就不会进画面
            g.DrawImage(_holeSnapshot, _holeRect);
        }

        using var ms = new MemoryStream(Math.Max(4096, w * h / 4));
        _bitmap!.Save(ms, _codec!, _encParams!);
        return ms.ToArray();
    }

    private Bitmap? _holeSnapshot;
    private Rectangle _holeRect;

    /// <summary>把矩形区域挖掉一个洞之后剩下的若干块（仅供测试/参考）。</summary>
    internal static List<(Rectangle Src, Point Dst)> SplitPieces(Rectangle cap, Rectangle? hole)
    {
        var result = new List<(Rectangle, Point)>();
        if (!hole.HasValue)
        {
            result.Add((cap, Point.Empty));
            return result;
        }

        var h = Rectangle.Intersect(hole.Value, cap);
        if (h.Width <= 0 || h.Height <= 0)
        {
            result.Add((cap, Point.Empty));
            return result;
        }

        if (h.Left > cap.Left)
        {
            result.Add((new Rectangle(cap.Left, cap.Top, h.Left - cap.Left, cap.Height), Point.Empty));
        }
        if (h.Right < cap.Right)
        {
            result.Add((new Rectangle(h.Right, cap.Top, cap.Right - h.Right, cap.Height),
                        new Point(h.Right - cap.Left, 0)));
        }
        if (h.Top > cap.Top)
        {
            result.Add((new Rectangle(h.Left, cap.Top, h.Width, h.Top - cap.Top),
                        new Point(h.Left - cap.Left, 0)));
        }
        if (h.Bottom < cap.Bottom)
        {
            result.Add((new Rectangle(h.Left, h.Bottom, h.Width, cap.Bottom - h.Bottom),
                        new Point(h.Left - cap.Left, h.Bottom - cap.Top)));
        }
        return result;
    }

    public void Dispose()
    {
        RequestStop();
        WaitForStop(5000);
        _encParams?.Dispose();
        _holeSnapshot?.Dispose();
        _graphics?.Dispose();
        _bitmap?.Dispose();
        _writer?.Dispose();
    }
}
