using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace DroneDash_x64.Desktop.SmartFarming;

public static class SmartFarmingExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static (string JsonPath, string CsvPath) ExportDataset(
        string destinationFolder,
        M3mDatasetResult dataset)
    {
        Directory.CreateDirectory(destinationFolder);

        var manifest = new SmartFarmingManifest(
            SmartFarmingManifest.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            dataset.SourceFolder,
            dataset.Summary,
            dataset.Captures);

        var jsonPath = Path.Combine(destinationFolder, "smart-farming-dataset.json");
        var csvPath = Path.Combine(destinationFolder, "smart-farming-captures.csv");

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(false));

        using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true));
        writer.WriteLine(
            "captureKey;complete;quicklookReady;rgb;green;red;redEdge;nir;gps;rtk;issues");

        foreach (var capture in dataset.Captures)
        {
            writer.WriteLine(string.Join(";",
                Csv(capture.CaptureKey),
                capture.IsComplete ? "true" : "false",
                capture.IsQuicklookReady ? "true" : "false",
                Csv(capture.RgbPath),
                Csv(capture.Green?.FilePath),
                Csv(capture.Red?.FilePath),
                Csv(capture.RedEdge?.FilePath),
                Csv(capture.Nir?.FilePath),
                Csv(capture.GpsText),
                Csv(capture.RtkText),
                Csv(string.Join(" | ", capture.Issues))));
        }

        return (jsonPath, csvPath);
    }

    public static (string PngPath, string JsonPath) ExportQuicklook(
        string destinationFolder,
        VegetationIndexResult result,
        BitmapSource bitmap)
    {
        Directory.CreateDirectory(destinationFolder);

        var baseName = $"{result.CaptureKey}_{result.Type}";
        var pngPath = Path.Combine(destinationFolder, baseName + "_quicklook.png");
        var jsonPath = Path.Combine(destinationFolder, baseName + "_quicklook.json");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(pngPath))
            encoder.Save(stream);

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(
                new
                {
                    result.Type,
                    result.CaptureKey,
                    result.Width,
                    result.Height,
                    result.ValidPixelCount,
                    result.Minimum,
                    result.Maximum,
                    result.Average,
                    result.QualityLabel,
                    warning =
                        "Einzelaufnahme-Quicklook: keine Orthorektifizierung, keine vollständige Band-Co-Registrierung und keine Reflektanzpanel-Kalibrierung."
                },
                JsonOptions),
            new UTF8Encoding(false));

        return (pngPath, jsonPath);
    }

    private static string Csv(string? value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : "\"" + value.Replace("\"", "\"\"") + "\"";
}
