using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace ScreenRec;

/// <summary>
/// 资源占用柱形图：CPU / 内存 / 文件大小 三条横条。
///
/// ★ 自绘（OnRender）而不是拼控件 —— 三条横条用嵌套 Border + Grid 也能画，
///   但那样每次更新都要重新布局，滑块拖动时会有抖动；自绘只重画，不动布局树。
/// </summary>
public sealed class UsageBars : FrameworkElement
{
    // 三条刻度的满量程（超过就画满，文字里写实际值）
    private const double CpuMax = 100.0;    // 单核百分比
    private const double MemMax = 320.0;    // MB
    private const double SizeMax = 120.0;   // MB/分钟

    private const double LabelW = 46;       // 左侧名称列宽
    private const double ValueW = 96;       // 右侧数值列宽
    private const double RowH = 22;
    private const double BarH = 11;

    private UsageEstimate? _est;

    public UsageBars()
    {
        Width = 470;
        Height = RowH * 3 + 4;
    }

    /// <summary>更新预估并重绘。</summary>
    internal void SetEstimate(UsageEstimate est)
    {
        _est = est;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        if (_est == null || w < 80) return;

        double barX = LabelW + 6;
        double barW = Math.Max(20, w - barX - ValueW - 6);

        DrawRow(dc, "CPU", 0, _est.CpuPercent / CpuMax,
                $"约 {_est.CpuPercent:0}% 单核", barX, barW, w, CpuColor(_est.CpuPercent / CpuMax));
        DrawRow(dc, "内存", 1, _est.MemoryMb / MemMax,
                $"约 {_est.MemoryMb:0} MB", barX, barW, w, Color.FromRgb(0x2F, 0x6B, 0xEA));
        DrawRow(dc, "文件", 2, _est.MbPerMinute / SizeMax,
                $"约 {_est.MbPerMinute:0} MB/分", barX, barW, w, Color.FromRgb(0x8B, 0x5C, 0xF6));
    }

    private void DrawRow(DrawingContext dc, string name, int row, double ratio,
                         string value, double barX, double barW, double totalW, Color fill)
    {
        double y = 2 + row * RowH;

        // 左侧名称
        dc.DrawText(MakeText(name, 11, Color.FromRgb(0x6B, 0x72, 0x80)),
                    new Point(0, y + (RowH - 14) / 2));

        // 轨道底
        double trackY = y + (RowH - BarH) / 2;
        dc.DrawGeometry(
            new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEF)), null,
            new RectangleGeometry(new Rect(barX, trackY, barW, BarH), 4, 4));

        // 填充条
        double ratioClamped = Math.Max(0, Math.Min(1.0, ratio));
        if (ratioClamped > 0)
        {
            double fw = Math.Max(3, barW * ratioClamped);
            dc.DrawGeometry(
                new SolidColorBrush(fill), null,
                new RectangleGeometry(new Rect(barX, trackY, fw, BarH), 4, 4));
        }

        // 右侧数值
        var ft = MakeText(value, 11, Color.FromRgb(0x1F, 0x24, 0x30));
        dc.DrawText(ft, new Point(totalW - ValueW, y + (RowH - 14) / 2));
    }

    /// <summary>CPU 越高颜色越警示（绿 → 橙 → 红）。</summary>
    private static Color CpuColor(double ratio)
    {
        if (ratio < 0.35) return Color.FromRgb(0x3F, 0x9E, 0x4F);
        if (ratio < 0.7) return Color.FromRgb(0xD8, 0x8A, 0x1E);
        return Color.FromRgb(0xD1, 0x44, 0x3F);
    }

    private static FormattedText MakeText(string s, double size, Color color)
    {
        return new FormattedText(
            s,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            size,
            new SolidColorBrush(color),
            96);
    }
}
