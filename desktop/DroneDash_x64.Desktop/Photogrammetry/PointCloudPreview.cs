using System.IO;
using System.Text.Json;

namespace DroneDash_x64.Desktop.Photogrammetry;

public sealed record PointCloudPreviewFiles(string Positions, string? Colors, string? Classification);

/// <summary>Metadata written by the worker's --pointcloud-preview mode.</summary>
public sealed record PointCloudPreview(
    int SchemaVersion,
    string Source,
    string? SourceCrs,
    string LasVersion,
    int PointFormat,
    long TotalPoints,
    long Points,
    int Stride,
    double[] Center,
    double[] Min,
    double[] Max,
    bool HasColor,
    int[] Classes,
    PointCloudPreviewFiles Files)
{
    public const int SupportedSchemaVersion = 1;
}

public static class PointCloudPreviewService
{
    public const int DefaultMaxPoints = 3_000_000;
    public const string MetadataFileName = "pointcloud-preview.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Preview files live next to the cloud, e.g. odm_georeferencing/odm_georeferenced_model.viewer/.</summary>
    public static string PreviewFolderFor(string cloudPath)
    {
        var name = Path.GetFileName(cloudPath);
        // "model.copc.laz" -> "model.copc.viewer" keeps COPC and LAZ previews apart.
        return Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(cloudPath)) ?? ".",
            Path.GetFileNameWithoutExtension(name) + ".viewer");
    }

    public static PointCloudPreview Parse(string json)
    {
        var preview = JsonSerializer.Deserialize<PointCloudPreview>(json, JsonOptions)
            ?? throw new InvalidDataException("Leere Punktwolken-Metadaten.");

        if (preview.SchemaVersion != PointCloudPreview.SupportedSchemaVersion)
            throw new InvalidDataException($"Nicht unterstützte Punktwolken-Vorschau-Version {preview.SchemaVersion}.");

        static bool Local(string? file) => file is null || WorkerJsonRunner.IsPlainFileName(file);

        if (preview.Points <= 0 ||
            preview.Center is not { Length: 3 } ||
            preview.Min is not { Length: 3 } ||
            preview.Max is not { Length: 3 } ||
            preview.Files is null ||
            string.IsNullOrWhiteSpace(preview.Files.Positions) ||
            !Local(preview.Files.Positions) ||
            !Local(preview.Files.Colors) ||
            !Local(preview.Files.Classification) ||
            (preview.HasColor && preview.Files.Colors is null))
        {
            throw new InvalidDataException("Punktwolken-Metadaten sind ungültig.");
        }

        return preview;
    }

    public static PointCloudPreview? TryLoadCached(string cloudPath)
    {
        var folder = PreviewFolderFor(cloudPath);
        var metadata = Path.Combine(folder, MetadataFileName);

        if (!File.Exists(cloudPath) || !File.Exists(metadata) ||
            File.GetLastWriteTimeUtc(metadata) < File.GetLastWriteTimeUtc(cloudPath))
        {
            return null;
        }

        try
        {
            var preview = Parse(File.ReadAllText(metadata));

            bool HasSize(string? file, long bytes) =>
                file is null || (File.Exists(Path.Combine(folder, file)) &&
                                 new FileInfo(Path.Combine(folder, file)).Length == bytes);

            var complete =
                WorkerJsonRunner.SameFile(preview.Source, cloudPath) &&
                HasSize(preview.Files.Positions, preview.Points * 3 * sizeof(float)) &&
                HasSize(preview.Files.Colors, preview.Points * 3) &&
                HasSize(preview.Files.Classification, preview.Points);

            return complete ? preview : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static async Task<PointCloudPreview> RenderAsync(
        string pythonExecutable,
        string workerPath,
        string cloudPath,
        int maxPoints = DefaultMaxPoints,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cloudPath))
            throw new FileNotFoundException("Punktwolke nicht gefunden.", cloudPath);

        var json = await WorkerJsonRunner.RunAsync(
            pythonExecutable,
            workerPath,
            [
                "--pointcloud-preview",
                "--source", Path.GetFullPath(cloudPath),
                "--output-dir", PreviewFolderFor(cloudPath),
                "--max-points", maxPoints.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            "Punktwolken-Vorschau fehlgeschlagen",
            cancellationToken);

        return Parse(json);
    }
}
