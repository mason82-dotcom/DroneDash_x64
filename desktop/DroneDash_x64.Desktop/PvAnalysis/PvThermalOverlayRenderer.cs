using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DroneDash_x64.Desktop.Thermal;

namespace DroneDash_x64.Desktop.PvAnalysis;

public static class PvThermalOverlayRenderer
{
    public static BitmapSource Render(
        ThermalAnalysisResult thermal,
        IReadOnlyList<PvHotspotCandidate> candidates)
    {
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(
                thermal.PseudoColorImage,
                new Rect(0, 0, thermal.Width, thermal.Height));

            foreach (var candidate in candidates)
            {
                var brush = candidate.Severity == PvAnomalySeverity.Critical
                    ? System.Windows.Media.Brushes.Red
                    : System.Windows.Media.Brushes.Orange;

                var pen = new System.Windows.Media.Pen(brush, 2);
                pen.Freeze();

                var rect = new Rect(
                    Math.Max(0, candidate.MinX - 2),
                    Math.Max(0, candidate.MinY - 2),
                    Math.Min(thermal.Width - candidate.MinX, candidate.MaxX - candidate.MinX + 5),
                    Math.Min(thermal.Height - candidate.MinY, candidate.MaxY - candidate.MinY + 5));

                drawing.DrawRectangle(
                    new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, brush.Color.R, brush.Color.G, brush.Color.B)),
                    pen,
                    rect);

                drawing.DrawEllipse(
                    brush,
                    null,
                    new System.Windows.Point(candidate.PeakX, candidate.PeakY),
                    3.5,
                    3.5);
            }
        }

        var bitmap = new RenderTargetBitmap(
            thermal.Width,
            thermal.Height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
