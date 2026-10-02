using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DroneDash_x64.Desktop.SmartFarming;

public static class M3mVegetationIndexEngine
{
    public static VegetationIndexResult Compute(M3mCaptureGroup capture, VegetationIndexType type)
    {
        if (!capture.IsQuicklookReady)
            throw new InvalidOperationException("Die Aufnahme ist für den M3M-Quicklook nicht vollständig vorbereitet.");

        var (aBand, bBand) = type switch
        {
            VegetationIndexType.Ndvi => (capture.Nir!, capture.Red!),
            VegetationIndexType.Ndre => (capture.Nir!, capture.RedEdge!),
            VegetationIndexType.Gndvi => (capture.Nir!, capture.Green!),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        var a = ReadCompensatedSignal(aBand);
        var b = ReadCompensatedSignal(bBand);

        return ComputeFromSignals(capture.CaptureKey, type, a, b, aBand.Width, aBand.Height);
    }

    public static VegetationIndexResult ComputeFromSignals(
        string captureKey,
        VegetationIndexType type,
        IReadOnlyList<float> numeratorPositiveBand,
        IReadOnlyList<float> comparisonBand,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        var pixelCount = checked(width * height);
        if (numeratorPositiveBand.Count != pixelCount || comparisonBand.Count != pixelCount)
            throw new ArgumentException("Bandarrays passen nicht zur Bildauflösung.");

        var values = new float[pixelCount];
        var valid = 0;
        double sum = 0;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;

        for (var i = 0; i < pixelCount; i++)
        {
            var a = numeratorPositiveBand[i];
            var b = comparisonBand[i];
            var denominator = a + b;

            if (!float.IsFinite(a) || !float.IsFinite(b) || Math.Abs(denominator) < 1e-12f)
            {
                values[i] = float.NaN;
                continue;
            }

            var value = (a - b) / denominator;
            values[i] = value;
            if (!float.IsFinite(value))
                continue;

            valid++;
            sum += value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (valid == 0)
            throw new InvalidDataException("Vegetationsindex enthält keine gültigen Pixel.");

        return new VegetationIndexResult(
            type, captureKey, width, height, values, valid, min, max, sum / valid,
            IsOrthorectified: false,
            IsBandCoregistered: false,
            UsesReflectancePanelCalibration: false);
    }

    public static float CorrectDn(
        ushort rawDn,
        int bitsPerSample,
        double blackLevel,
        double sensorGain,
        double exposureTimeMicroseconds,
        double sensorGainAdjustment,
        double irradiance)
    {
        if (bitsPerSample is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        if (sensorGain <= 0 || exposureTimeMicroseconds <= 0 ||
            sensorGainAdjustment <= 0 || irradiance <= 0)
            throw new ArgumentOutOfRangeException(nameof(sensorGain));

        var divisor = Math.Pow(2d, bitsPerSample);
        var normalizedRaw = rawDn / divisor;
        var normalizedBlack = blackLevel / divisor;
        var camera = (normalizedRaw - normalizedBlack) /
                     (sensorGain * (exposureTimeMicroseconds / 1_000_000d));
        return (float)Math.Max(0d, camera * sensorGainAdjustment / irradiance);
    }

    private static float[] ReadCompensatedSignal(M3mBandMetadata metadata)
    {
        if (!metadata.HasRadiometricInputs)
            throw new InvalidOperationException($"{metadata.Band}: radiometrische Metadaten unvollständig.");

        using var stream = File.OpenRead(metadata.FilePath);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];

        if (source.PixelWidth != metadata.Width || source.PixelHeight != metadata.Height)
            throw new InvalidDataException($"{metadata.Band}: Bildabmessungen haben sich geändert.");

        var pixelCount = checked(metadata.Width * metadata.Height);
        var output = new float[pixelCount];

        if (metadata.BitsPerSample <= 8)
        {
            if (source.Format != PixelFormats.Gray8)
                source = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

            var raw = new byte[pixelCount];
            source.CopyPixels(raw, metadata.Width, 0);

            for (var i = 0; i < pixelCount; i++)
                output[i] = CorrectDn(
                    raw[i], 8, metadata.BlackLevel!.Value, metadata.SensorGain!.Value,
                    metadata.ExposureTimeMicroseconds!.Value,
                    metadata.SensorGainAdjustment!.Value, metadata.Irradiance!.Value);
        }
        else
        {
            if (source.Format != PixelFormats.Gray16)
                source = new FormatConvertedBitmap(source, PixelFormats.Gray16, null, 0);

            var raw = new ushort[pixelCount];
            source.CopyPixels(raw, checked(metadata.Width * 2), 0);

            for (var i = 0; i < pixelCount; i++)
                output[i] = CorrectDn(
                    raw[i], metadata.BitsPerSample, metadata.BlackLevel!.Value,
                    metadata.SensorGain!.Value, metadata.ExposureTimeMicroseconds!.Value,
                    metadata.SensorGainAdjustment!.Value, metadata.Irradiance!.Value);
        }

        return output;
    }
}
