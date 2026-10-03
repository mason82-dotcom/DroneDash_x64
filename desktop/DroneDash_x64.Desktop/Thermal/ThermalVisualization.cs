using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DroneDash_x64.Desktop.Thermal;

public static class ThermalVisualization
{
    public static BitmapSource RenderOverlay(
        ThermalAnalysisResult thermal)
    {
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(
                thermal.PseudoColorImage,
                new Rect(0, 0, thermal.Width, thermal.Height));

            DrawMarker(
                drawing,
                thermal.MinimumX,
                thermal.MinimumY,
                Brushes.DeepSkyBlue,
                8);

            DrawMarker(
                drawing,
                thermal.MaximumX,
                thermal.MaximumY,
                Brushes.Red,
                9);

            DrawMarker(
                drawing,
                thermal.Width / 2d,
                thermal.Height / 2d,
                Brushes.White,
                6);
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

    public static BitmapSource RenderHistogram(
        ThermalAnalysisResult thermal,
        int width = 360,
        int height = 86,
        int binCount = 64)
    {
        if (width < 120 || height < 50)
            throw new ArgumentOutOfRangeException(nameof(width));

        var bins = thermal.BuildHistogram(binCount);
        var maxCount = Math.Max(1, bins.Max(bin => bin.Count));
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                Brushes.White,
                null,
                new Rect(0, 0, width, height));

            var plotLeft = 1d;
            var plotTop = 4d;
            var plotBottom = height - 14d;
            var plotHeight = Math.Max(1d, plotBottom - plotTop);
            var barWidth = Math.Max(1d, (width - 2d) / bins.Count);

            for (var index = 0; index < bins.Count; index++)
            {
                var bin = bins[index];
                var fraction = bin.Count / (double)maxCount;
                var barHeight = plotHeight * fraction;
                var x = plotLeft + index * barWidth;
                var y = plotBottom - barHeight;

                drawing.DrawRectangle(
                    Brushes.DimGray,
                    null,
                    new Rect(
                        x,
                        y,
                        Math.Max(1d, barWidth - 0.5d),
                        barHeight));
            }

            DrawTemperatureMarker(
                drawing,
                thermal,
                thermal.P05C,
                width,
                plotTop,
                plotBottom,
                Brushes.SteelBlue);

            DrawTemperatureMarker(
                drawing,
                thermal,
                thermal.MedianC,
                width,
                plotTop,
                plotBottom,
                Brushes.Black);

            DrawTemperatureMarker(
                drawing,
                thermal,
                thermal.P95C,
                width,
                plotTop,
                plotBottom,
                Brushes.IndianRed);

            var minText = new FormattedText(
                $"{thermal.MinimumC:F1} °C",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                9,
                Brushes.Black,
                1d);

            var maxText = new FormattedText(
                $"{thermal.MaximumC:F1} °C",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                9,
                Brushes.Black,
                1d);

            drawing.DrawText(
                minText,
                new Point(2, height - minText.Height));

            drawing.DrawText(
                maxText,
                new Point(
                    Math.Max(2, width - maxText.Width - 2),
                    height - maxText.Height));
        }

        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawTemperatureMarker(
        DrawingContext drawing,
        ThermalAnalysisResult thermal,
        float temperature,
        int width,
        double top,
        double bottom,
        System.Windows.Media.Brush brush)
    {
        var span = thermal.MaximumC - thermal.MinimumC;
        if (span <= 0 || !float.IsFinite(span))
            return;

        var normalized =
            Math.Clamp(
                (temperature - thermal.MinimumC) / span,
                0f,
                1f);

        var x =
            1d +
            normalized *
            Math.Max(1d, width - 2d);

        var pen = new Pen(
            brush,
            1.4);

        pen.Freeze();

        drawing.DrawLine(
            pen,
            new Point(x, top),
            new Point(x, bottom));
    }

    private static void DrawMarker(
        DrawingContext drawing,
        double x,
        double y,
        Brush brush,
        double radius)
    {
        var outerPen = new Pen(
            Brushes.Black,
            3);

        outerPen.Freeze();

        var innerPen = new Pen(
            brush,
            1.5);

        innerPen.Freeze();

        drawing.DrawEllipse(
            null,
            outerPen,
            new Point(x, y),
            radius,
            radius);

        drawing.DrawEllipse(
            null,
            innerPen,
            new Point(x, y),
            radius,
            radius);

        drawing.DrawLine(
            outerPen,
            new Point(x - radius - 4, y),
            new Point(x + radius + 4, y));

        drawing.DrawLine(
            outerPen,
            new Point(x, y - radius - 4),
            new Point(x, y + radius + 4));

        drawing.DrawLine(
            innerPen,
            new Point(x - radius - 4, y),
            new Point(x + radius + 4, y));

        drawing.DrawLine(
            innerPen,
            new Point(x, y - radius - 4),
            new Point(x, y + radius + 4));
    }
}
