using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.PvAnalysis;

public static class PvAnalysisExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static (string JsonPath, string ImagesCsvPath, string CandidatesCsvPath) Export(
        string destinationFolder,
        PvDatasetResult dataset)
    {
        Directory.CreateDirectory(destinationFolder);

        var manifest = new PvAnalysisManifest(
            PvAnalysisManifest.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            dataset.SourceFolder,
            dataset.FlightPlanProjectPath,
            dataset.Settings,
            dataset.Summary,
            dataset.Images);

        var jsonPath = Path.Combine(
            destinationFolder,
            "pv-analysis.json");
        var imagesCsvPath = Path.Combine(
            destinationFolder,
            "pv-images.csv");
        var candidatesCsvPath = Path.Combine(
            destinationFolder,
            "pv-anomaly-candidates.csv");

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(false));

        using (var writer = new StreamWriter(
                   imagesCsvPath,
                   false,
                   new UTF8Encoding(true)))
        {
            writer.WriteLine(
                "file;relativePath;latitude;longitude;rtkFlag;waylineId;passName;" +
                "segmentIndex;distanceToRouteM;thermalWidth;thermalHeight;minimumC;" +
                "maximumC;averageC;emissivity;measurementDistanceM;severity;" +
                "candidateCount;highestDeltaC;processingError");

            foreach (var image in dataset.Images)
            {
                writer.WriteLine(string.Join(";",
                    Csv(image.FileName),
                    Csv(image.RelativePath),
                    Num(image.Latitude),
                    Num(image.Longitude),
                    Csv(image.RtkFlag),
                    image.AssignedWaylineId?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    Csv(image.AssignedPassName),
                    image.AssignedSegmentIndex?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    Num(image.DistanceToRouteMeters),
                    image.ThermalWidth?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    image.ThermalHeight?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    Num(image.MinimumC),
                    Num(image.MaximumC),
                    Num(image.AverageC),
                    Num(image.Emissivity),
                    Num(image.MeasurementDistanceMeters),
                    image.Severity.ToString(),
                    image.Candidates.Count.ToString(
                        CultureInfo.InvariantCulture),
                    Num(image.HighestDeltaC),
                    Csv(image.ProcessingError)));
            }
        }

        using (var writer = new StreamWriter(
                   candidatesCsvPath,
                   false,
                   new UTF8Encoding(true)))
        {
            writer.WriteLine(
                "file;candidate;severity;pixelCount;areaPixels;boundingBoxAreaPixels;fillRatio;" +
                "equivalentDiameterPixels;peakX;peakY;peakTemperatureC;localMedianC;" +
                "localUpperQuartileC;adaptiveSeedThresholdC;deltaC;centroidX;centroidY;" +
                "minX;minY;maxX;maxY");

            foreach (var image in dataset.Images)
            {
                foreach (var candidate in image.Candidates)
                {
                    writer.WriteLine(string.Join(";",
                        Csv(image.FileName),
                        candidate.Index.ToString(
                            CultureInfo.InvariantCulture),
                        candidate.Severity.ToString(),
                        candidate.PixelCount.ToString(
                            CultureInfo.InvariantCulture),
                        candidate.AreaPixels.ToString(
                            CultureInfo.InvariantCulture),
                        candidate.BoundingBoxAreaPixels.ToString(
                            CultureInfo.InvariantCulture),
                        Num(candidate.FillRatio),
                        Num(candidate.EquivalentDiameterPixels),
                        candidate.PeakX.ToString(
                            CultureInfo.InvariantCulture),
                        candidate.PeakY.ToString(
                            CultureInfo.InvariantCulture),
                        Num(candidate.PeakTemperatureC),
                        Num(candidate.LocalBaselineC),
                        Num(candidate.LocalUpperQuartileC),
                        Num(candidate.AdaptiveSeedThresholdC),
                        Num(candidate.DeltaC),
                        Num(candidate.CentroidX),
                        Num(candidate.CentroidY),
                        candidate.MinX.ToString(
                            CultureInfo.InvariantCulture),
                        candidate.MinY.ToString(
                            CultureInfo.InvariantCulture),
                        candidate.MaxX.ToString(
                            CultureInfo.InvariantCulture),
                        candidate.MaxY.ToString(
                            CultureInfo.InvariantCulture)));
                }
            }
        }

        return (jsonPath, imagesCsvPath, candidatesCsvPath);
    }

    private static string Num(double? value) =>
        value?.ToString("0.########", CultureInfo.InvariantCulture) ?? "";

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
