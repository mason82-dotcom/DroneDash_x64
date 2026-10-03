using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace DroneDash_x64.Desktop.Thermal;

public sealed record ThermalExportResult(
    string SummaryJsonPath,
    string MatrixCsvPath,
    string HistogramCsvPath,
    string OverlayPngPath,
    string HistogramPngPath)
{
    public IReadOnlyList<string> Paths =>
    [
        SummaryJsonPath,
        MatrixCsvPath,
        HistogramCsvPath,
        OverlayPngPath,
        HistogramPngPath
    ];
}

public static class ThermalAnalysisExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ThermalExportResult Export(
        ThermalAnalysisResult thermal,
        string sourcePath,
        string destinationFolder,
        ThermalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(thermal);

        sourcePath = Path.GetFullPath(sourcePath);
        destinationFolder = Path.GetFullPath(destinationFolder);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                "Thermal-Quelldatei wurde nicht gefunden.",
                sourcePath);

        Directory.CreateDirectory(destinationFolder);

        var stem = SanitizeStem(
            Path.GetFileNameWithoutExtension(sourcePath));

        var summaryPath = Path.Combine(
            destinationFolder,
            $"{stem}.thermal.json");

        var matrixPath = Path.Combine(
            destinationFolder,
            $"{stem}.thermal-matrix.csv");

        var histogramPath = Path.Combine(
            destinationFolder,
            $"{stem}.thermal-histogram.csv");

        var overlayPath = Path.Combine(
            destinationFolder,
            $"{stem}.thermal-overlay.png");

        var histogramPngPath = Path.Combine(
            destinationFolder,
            $"{stem}.thermal-histogram.png");

        var bins = thermal.BuildHistogram(64);
        var sourceHash = ComputeSha256(sourcePath);

        var summary = new
        {
            schemaVersion = 1,
            createdAtUtc = DateTimeOffset.UtcNow,
            source = new
            {
                path = sourcePath,
                fileName = Path.GetFileName(sourcePath),
                sha256 = sourceHash
            },
            sdk = new
            {
                name = "DJI Thermal SDK",
                version = DjiThermalSdk.SupportedSdkVersion,
                api = thermal.ApiVersion,
                rjpeg = thermal.RjpegVersion
            },
            image = new
            {
                width = thermal.Width,
                height = thermal.Height,
                pixels = thermal.Temperatures.Length,
                validPixels = thermal.ValidPixelCount,
                palette
            },
            statistics = new
            {
                minimumC = thermal.MinimumC,
                minimum = new
                {
                    x = thermal.MinimumX,
                    y = thermal.MinimumY
                },
                maximumC = thermal.MaximumC,
                maximum = new
                {
                    x = thermal.MaximumX,
                    y = thermal.MaximumY
                },
                averageC = thermal.AverageC,
                centerC = thermal.CenterC,
                standardDeviationC = thermal.StandardDeviationC,
                p05C = thermal.P05C,
                medianC = thermal.MedianC,
                p95C = thermal.P95C
            },
            measurement = new
            {
                distanceM = thermal.DistanceM,
                humidity = thermal.Humidity,
                emissivity = thermal.Emissivity,
                reflectionC = thermal.ReflectionC,
                ambientC = thermal.AmbientTemperatureC
            },
            histogram = new
            {
                binCount = bins.Count,
                bins
            }
        };

        WriteAtomicText(
            summaryPath,
            JsonSerializer.Serialize(
                summary,
                JsonOptions));

        WriteMatrixCsv(
            matrixPath,
            thermal);

        WriteHistogramCsv(
            histogramPath,
            bins);

        WriteBitmapPng(
            overlayPath,
            ThermalVisualization.RenderOverlay(
                thermal));

        WriteBitmapPng(
            histogramPngPath,
            ThermalVisualization.RenderHistogram(
                thermal));

        return new ThermalExportResult(
            summaryPath,
            matrixPath,
            histogramPath,
            overlayPath,
            histogramPngPath);
    }

    private static void WriteMatrixCsv(
        string path,
        ThermalAnalysisResult thermal)
    {
        var temporary = path + ".tmp";
        TryDelete(temporary);

        try
        {
            using (var writer = new StreamWriter(
                temporary,
                false,
                new UTF8Encoding(false),
                1024 * 1024))
            {
                writer.WriteLine(
                    "x,y,temperatureC");

                for (var y = 0; y < thermal.Height; y++)
                {
                    var rowOffset =
                        y * thermal.Width;

                    for (var x = 0; x < thermal.Width; x++)
                    {
                        var value =
                            thermal.Temperatures[
                                rowOffset + x];

                        writer.Write(
                            x.ToString(
                                CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(
                            y.ToString(
                                CultureInfo.InvariantCulture));
                        writer.Write(',');

                        if (float.IsFinite(value))
                        {
                            writer.Write(
                                value.ToString(
                                    "R",
                                    CultureInfo.InvariantCulture));
                        }

                        writer.WriteLine();
                    }
                }
            }

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void WriteHistogramCsv(
        string path,
        IReadOnlyList<ThermalHistogramBin> bins)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "bin,lowerC,upperC,count,fraction");

        foreach (var bin in bins)
        {
            builder.Append(
                bin.Index.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(
                bin.LowerC.ToString(
                    "R",
                    CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(
                bin.UpperC.ToString(
                    "R",
                    CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(
                bin.Count.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(
                bin.Fraction.ToString(
                    "R",
                    CultureInfo.InvariantCulture));
        }

        WriteAtomicText(
            path,
            builder.ToString());
    }

    private static void WriteBitmapPng(
        string path,
        BitmapSource bitmap)
    {
        var temporary = path + ".tmp";
        TryDelete(temporary);

        try
        {
            var encoder =
                new PngBitmapEncoder();

            encoder.Frames.Add(
                BitmapFrame.Create(bitmap));

            using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                encoder.Save(stream);
                stream.Flush(true);
            }

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void WriteAtomicText(
        string path,
        string content)
    {
        var temporary = path + ".tmp";
        TryDelete(temporary);

        try
        {
            File.WriteAllText(
                temporary,
                content,
                new UTF8Encoding(false));

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static string ComputeSha256(
        string path)
    {
        using var stream =
            File.OpenRead(path);

        using var sha256 =
            SHA256.Create();

        var hash =
            sha256.ComputeHash(stream);

        return Convert
            .ToHexString(hash)
            .ToLowerInvariant();
    }

    private static string SanitizeStem(
        string value)
    {
        foreach (var character in
                 Path.GetInvalidFileNameChars())
        {
            value =
                value.Replace(
                    character,
                    '_');
        }

        return string.IsNullOrWhiteSpace(value)
            ? "thermal"
            : value;
    }

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
