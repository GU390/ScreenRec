namespace ScreenRec;

/// <summary>
/// 一次录制的资源占用预估结果。
/// </summary>
internal sealed class UsageEstimate
{
    /// <summary>CPU 占用（单核百分比量级，100 ≈ 吃满一个核心）。</summary>
    public double CpuPercent { get; init; }

    /// <summary>常驻内存（MB 量级）。</summary>
    public double MemoryMb { get; init; }

    /// <summary>输出码率（kbps）。</summary>
    public double Kbps { get; init; }

    /// <summary>是否已经是「近乎无损」档（画质 ≥ 95）。</summary>
    public bool NearLossless { get; init; }

    /// <summary>每分钟大约产出的文件大小（MB）。</summary>
    public double MbPerMinute => Kbps * 60.0 / 8.0 / 1024.0;
}

/// <summary>
/// 资源占用预估模型。
///
/// ★ 基线来自本机实测（1920×1080 / 15 fps / 画质 80 / AVI MJPEG）：
///     CPU 约 23%（单核百分比）、内存约 83 MB、码率约 8 Mbps（≈1 MB/秒）。
///
/// ★ 下面按「像素数 × 帧率 × 画质」线性外推。用户要的是「大概了解会占多少」，
///   不是精确预测，所以宁可粗一点、别假装精确 —— 界面上也明确写了「约」。
/// </summary>
internal static class ResourceUsage
{
    public static UsageEstimate Estimate(int width, int height, int fps, int quality, bool mp4)
    {
        // 相对 1080p 的像素倍率
        double px = (double)(width * height) / (1920.0 * 1080.0);
        if (px <= 0) px = 0.01;

        // 帧率倍率（15 fps 是基线）
        double fpsK = Math.Max(0.2, fps / 15.0);

        // 画质对编码开销与体积的放大（实测 MJPEG 体积随画质上升明显）
        double qK = 1.0 + (quality - 80) * 0.0035;
        if (qK < 0.4) qK = 0.4;

        double cpu = 23.0 * px * fpsK * qK;
        double mem = 48.0 + 35.0 * px;
        double kbps = 8000.0 * px * fpsK * qK * qK;

        if (mp4)
        {
            // H.264 体积大约是 MJPEG 的 1/3（实测动态画面 2.5 倍，静态画面远不止）
            kbps *= 0.35;
        }

        return new UsageEstimate
        {
            CpuPercent = Math.Round(cpu, 0),
            MemoryMb = Math.Round(mem, 0),
            Kbps = Math.Round(kbps, 0),
            NearLossless = quality >= 95
        };
    }
}
