using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenRec;

/// <summary>命令行自测模式：不开界面，录一段指定时长并输出统计。</summary>
internal static class SelfTest
{
    private static readonly List<string> _log = new();

    private static void Say(string line)
    {
        _log.Add(line);
        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // 无控制台时忽略
        }
    }

    private static void FlushLog(string path)
    {
        try
        {
            File.WriteAllLines(path, _log);
        }
        catch
        {
            // ignore
        }
    }

    public static int Run(string[] args)
    {
        NativeMethods.AttachParentConsole();
        string logPath = Path.Combine(Path.GetTempPath(), "screenrec_selftest.log");
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    logPath = args[++i];
                }
            }
        }
        catch
        {
            // ignore
        }

        int code = RunCore(args);
        FlushLog(logPath);
        return code;
    }

    private static int RunCore(string[] args)
    {
        double seconds = 3;
        string? output = null;
        int fps = 15;
        int quality = 80;
        Rectangle? rect = null;
        Rectangle? exclude = null;
        string? mp4Out = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--seconds" when i + 1 < args.Length:
                    double.TryParse(args[++i], out seconds);
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--fps" when i + 1 < args.Length:
                    int.TryParse(args[++i], out fps);
                    break;
                case "--quality" when i + 1 < args.Length:
                    int.TryParse(args[++i], out quality);
                    break;
                case "--rect" when i + 1 < args.Length:
                    var parts = args[++i].Split(',');
                    if (parts.Length == 4 &&
                        int.TryParse(parts[0], out int rx) && int.TryParse(parts[1], out int ry) &&
                        int.TryParse(parts[2], out int rw) && int.TryParse(parts[3], out int rh))
                    {
                        rect = new Rectangle(rx, ry, rw, rh);
                    }
                    break;
                case "--exclude" when i + 1 < args.Length:
                    var exParts = args[++i].Split(',');
                    if (exParts.Length == 4 &&
                        int.TryParse(exParts[0], out int ex) && int.TryParse(exParts[1], out int ey) &&
                        int.TryParse(exParts[2], out int ew) && int.TryParse(exParts[3], out int eh))
                    {
                        exclude = new Rectangle(ex, ey, ew, eh);
                    }
                    break;
                case "--mp4" when i + 1 < args.Length:
                    mp4Out = args[++i];
                    break;
            }
        }

        if (output == null)
        {
            Say("用法: ScreenRec.exe --selftest --seconds 3 --out <文件.avi> [--fps 15] [--quality 80] [--rect x,y,w,h]");
            return 2;
        }

        try
        {
            rect ??= System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        }
        catch (Exception ex)
        {
            Say("selftest failed (取屏幕信息): " + ex);
            return 1;
        }

        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(output))!;
            Directory.CreateDirectory(dir);

            FfmpegSink? sink = null;
            if (mp4Out != null)
            {
                var ff = FfmpegLocator.Find(null);
                if (ff == null)
                {
                    Say("selftest: 找不到 ffmpeg，跳过 MP4");
                }
                else
                {
                    sink = FfmpegSink.Start(ff, mp4Out, fps, FfmpegLocator.QualityToCrf(quality));
                    if (sink == null) Say("selftest: 启动 ffmpeg 失败：" + (FfmpegSink.LastStartError ?? "未知"));
                }
            }

            using var rec = new Recorder(Path.GetFullPath(output), rect.Value)
            {
                Fps = fps,
                Quality = quality,
                ExcludeRect = exclude,
                Mp4Sink = sink
            };
            rec.Start();

            // 以录制器自身时钟为准，确保录到的时长就是要求的时长
            while (rec.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(50);
            }
            rec.RequestStop();
            rec.WaitForStop(20000);

            if (sink != null)
            {
                bool ok = sink.Finish();
                Say($"selftest mp4: {(ok ? "成功" : "失败")} {mp4Out} " +
                    (ok ? new FileInfo(mp4Out!).Length + " bytes" : (sink.Error ?? "")));
                sink.Dispose();
            }

            var fi = new FileInfo(Path.GetFullPath(output));
            Say($"selftest done: file={fi.FullName}");
            Say($"selftest size={fi.Length} bytes, frames={rec.FrameCount}, elapsed={rec.Elapsed.TotalSeconds:0.00}s, rect={rect.Value}");
            if (rec.Error != null)
            {
                Say("selftest error: " + rec.Error);
                return 3;
            }
            return rec.FrameCount > 0 ? 0 : 4;
        }
        catch (Exception ex)
        {
            Say("selftest failed: " + ex);
            return 1;
        }
    }
}
