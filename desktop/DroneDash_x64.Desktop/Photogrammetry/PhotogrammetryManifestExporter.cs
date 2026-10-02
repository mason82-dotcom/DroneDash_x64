using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.Photogrammetry;

public static class PhotogrammetryManifestExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static (string JsonPath, string CsvPath, string ImageListPath) Export(
        string destinationFolder,
        PhotogrammetryDatasetResult dataset)
    {
        Directory.CreateDirectory(destinationFolder);

        var manifest = new PhotogrammetryManifest(
            PhotogrammetryManifest.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            dataset.SourceFolder,
            dataset.FlightPlanProjectPath,
            dataset.FlightPlan?.Settings,
            dataset.Summary,
            dataset.Images);

        var jsonPath = Path.Combine(
            destinationFolder,
            "photogrammetry-manifest.json");
        var csvPath = Path.Combine(
            destinationFolder,
            "photogrammetry-images.csv");
        var imageListPath = Path.Combine(
            destinationFolder,
            "image-list.txt");

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(false));

        using (var writer = new StreamWriter(
                   csvPath,
                   false,
                   new UTF8Encoding(true)))
        {
            writer.WriteLine(
                "file;relativePath;imageSource;latitude;longitude;absoluteAltitudeM;" +
                "relativeAltitudeM;rtkFlag;rtkStdLonM;rtkStdLatM;rtkStdHgtM;" +
                "flightYawDeg;gimbalYawDeg;gimbalPitchDeg;calibratedFocalLength;" +
                "hasOpticalCenter;hasDewarpData;waylineId;passName;segmentIndex;" +
                "distanceToRouteM;issues");

            foreach (var image in dataset.Images)
            {
                writer.WriteLine(string.Join(";",
                    Csv(image.FileName),
                    Csv(image.RelativePath),
                    Csv(image.ImageSource),
                    Num(image.Latitude),
                    Num(image.Longitude),
                    Num(image.AbsoluteAltitudeMeters),
                    Num(image.RelativeAltitudeMeters),
                    Csv(image.RtkFlag),
                    Num(image.RtkStdLongitudeMeters),
                    Num(image.RtkStdLatitudeMeters),
                    Num(image.RtkStdHeightMeters),
                    Num(image.FlightYawDegrees),
                    Num(image.GimbalYawDegrees),
                    Num(image.GimbalPitchDegrees),
                    Num(image.CalibratedFocalLength),
                    image.HasOpticalCenter ? "true" : "false",
                    image.HasDewarpData ? "true" : "false",
                    image.AssignedWaylineId?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    Csv(image.AssignedPassName),
                    image.AssignedSegmentIndex?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    Num(image.DistanceToRouteMeters),
                    Csv(string.Join(" | ", image.Issues))));
            }
        }

        File.WriteAllLines(
            imageListPath,
            dataset.Images.Select(image =>
                Path.Combine(dataset.SourceFolder, image.RelativePath)),
            new UTF8Encoding(false));

        return (jsonPath, csvPath, imageListPath);
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
