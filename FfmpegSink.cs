using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenRec;

/// <summary>
/// 实时 H.264 编码管道：把 JPEG 帧直接喂给 ffmpeg 的 stdin，边录边压，录完即得 MP4。
/// ffmpeg 进程用 image2pipe + mjpeg 解复用，输出 libx264 / yuv420p 的 MP4。
/// </summary>
internal sealed class FfmpegSink : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly StringBuilder _err = new();
    private readonly object _errLock = new();
    private Thread? _errPump;
    private Thread? _outPump;
    private bool _broken;

    public string OutputPath { get; }
    public string? Error { get; private set; }
    public bool Broken => _broken;

    private FfmpegSink(Process proc, string outputPath)
    {
        _proc = proc;
        _stdin = proc.StandardInput.BaseStream;
        OutputPath = outputPath;
    }

    public static FfmpegSink? Start(string ffmpegPath, string outputPath, int fps, int crf, string preset = "veryfast")
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        void A(string a) => psi.ArgumentList.Add(a);

        A("-hide_banner");
        A("-loglevel"); A("error");
        A("-y");
        A("-f"); A("image2pipe");
        A("-vcodec"); A("mjpeg");
        // 用「收到帧的墙上时钟」当时间戳：实际帧率低于设定值时，
        // 时间轴仍然和真实流逝时间一致，播放不会变成快进
        A("-use_wallclock_as_timestamps"); A("1");
        A("-framerate"); A(Math.Max(1, fps).ToString());
        A("-i"); A("pipe:0");
        A("-c:v"); A("libx264");
        A("-preset"); A(preset);
        A("-crf"); A(Math.Clamp(crf, 0, 51).ToString());
        A("-vf"); A("scale=in_range=full:out_range=tv,format=yuv420p");
        A("-movflags"); A("+faststart");
        A(outputPath);

        try
        {
            var p = Process.Start(psi);
            if (p == null) return null;
            var sink = new FfmpegSink(p, outputPath);
            sink._errPump = new Thread(sink.Pump) { IsBackground = true, Name = "ffmpeg.err" };
            sink._errPump.Start();
            sink._outPump = new Thread(sink.PumpOut) { IsBackground = true, Name = "ffmpeg.out" };
            sink._outPump.Start();
            return sink;
        }
        catch (Exception ex)
        {
            LastStartError = "启动 ffmpeg 失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>Start 失败时的原因（成功启动则为 null）。</summary>
    public static string? LastStartError { get; private set; }

    private void Pump()
    {
        try
        {
            string? line;
            while ((line = _proc.StandardError.ReadLine()) != null)
            {
                lock (_errLock)
                {
                    if (_err.Length < 4000) _err.AppendLine(line);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private void PumpOut()
    {
        try
        {
            while (_proc.StandardOutput.ReadLine() != null) { }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>写一帧 JPEG。返回 false 表示管道已断（录像继续，只是 MP4 中断）。</summary>
    public bool WriteFrame(byte[] jpeg)
    {
        if (_broken) return false;
        try
        {
            _stdin.Write(jpeg, 0, jpeg.Length);
            _stdin.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _broken = true;
            Error = "写入 ffmpeg 管道失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>关闭输入并等待 ffmpeg 收尾（写 moov）。</summary>
    public bool Finish(int timeoutMs = 120000)
    {
        try
        {
            _stdin.Close();
        }
        catch
        {
            // ignore
        }

        bool exited;
        try
        {
            exited = _proc.WaitForExit(timeoutMs);
        }
        catch
        {
            exited = false;
        }

        if (!exited)
        {
            try { _proc.Kill(true); } catch { }
            Error = "ffmpeg 收尾超时";
            return false;
        }

        if (_proc.ExitCode != 0)
        {
            lock (_errLock) Error = "ffmpeg 退出码 " + _proc.ExitCode + "：" + _err.ToString().Trim();
            return false;
        }

        try
        {
            if (!File.Exists(OutputPath) || new FileInfo(OutputPath).Length == 0)
            {
                Error = "ffmpeg 没有产出有效文件";
                return false;
            }
        }
        catch (Exception ex)
        {
            Error = "检查产物失败：" + ex.Message;
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        try { _stdin.Dispose(); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
        try { _proc.Dispose(); } catch { }
    }
}
