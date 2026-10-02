using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DroneDash_x64.Desktop.SmartFarming;

public static class M3mIndexRenderer
{
    public static BitmapSource Render(VegetationIndexResult result)
    {
        var pixels = new byte[checked(result.Width * result.Height * 4)];

        for (var i = 0; i < result.Values.Length; i++)
        {
            var value = result.Values[i];
            var offset = i * 4;

            if (!float.IsFinite(value))
            {
                pixels[offset] = 35; pixels[offset + 1] = 35;
                pixels[offset + 2] = 35; pixels[offset + 3] = 255;
                continue;
            }

            var (r, g, b) = ColorFor(Math.Clamp(value, -1f, 1f));
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
            pixels[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            result.Width, result.Height, 96, 96, PixelFormats.Bgra32,
            null, pixels, result.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static (byte R, byte G, byte B) ColorFor(float value)
    {
        if (value < 0f)
            return Lerp((40, 55, 120), (150, 120, 70), value + 1f);
        if (value < 0.35f)
            return Lerp((165, 120, 45), (230, 205, 65), value / 0.35f);
        if (value < 0.65f)
            return Lerp((230, 205, 65), (85, 165, 75), (value - 0.35f) / 0.30f);
        return Lerp((85, 165, 75), (20, 95, 45), (value - 0.65f) / 0.35f);
    }

    private static (byte R, byte G, byte B) Lerp(
        (int R, int G, int B) a,
        (int R, int G, int B) b,
        float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return (
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
