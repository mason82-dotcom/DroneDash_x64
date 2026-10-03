using System.IO;
using System.Text.Json;

namespace DroneDash_x64.Desktop.Photogrammetry;

public sealed record DemGeoBounds(double South, double West, double North, double East);

public sealed record DemMercatorBounds(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>Metadata written by the worker's --dem-preview mode (dem-preview.json).</summary>
public sealed record DemPreview(
    int SchemaVersion,
    string Source,
    string? SourceCrs,
    int SourceWidth,
    int SourceHeight,
    int Width,
    int Height,
    DemGeoBounds Bounds,
    DemMercatorBounds MercatorBounds,
    double GroundPixelSizeMeters,
    double Minimum,
    double Maximum,
    double Mean,
    double DisplayMinimum,
    double DisplayMaximum,
    long ValidPixels,
    string Image,
    string Grid,
    string GridType,
    string? Palette = null)
{
    public const string TerrainPalette = "terrain";
    public const string DivergingPalette = "diverging";

    /// <summary>Colour scheme of the image; older metadata without the field used the terrain relief.</summary>
    public string EffectivePalette => Palette ?? TerrainPalette;

    public const int SupportedSchemaVersion = 1;
}

public static class DemPreviewService
{
    public const int DefaultMaxSize = 2048;
    public const string MetadataFileName = "dem-preview.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Preview files live next to the model, e.g. odm_dem/dsm.preview/.</summary>
    public static string PreviewFolderFor(string demPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(demPath)) ?? ".",
            Path.GetFileNameWithoutExtension(demPath) + ".preview");

    public static DemPreview Parse(string json)
    {
        var preview = JsonSerializer.Deserialize<DemPreview>(json, JsonOptions)
            ?? throw new InvalidDataException("Leere DSM-Vorschau-Metadaten.");

        if (preview.SchemaVersion != DemPreview.SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Nicht unterstützte DSM-Vorschau-Version {preview.SchemaVersion}.");
        }

        if (preview.Width <= 0 || preview.Height <= 0 ||
            preview.GridType != "float32-le" ||
            !WorkerJsonRunner.IsPlainFileName(preview.Image) ||
            !WorkerJsonRunner.IsPlainFileName(preview.Grid) ||
            preview.EffectivePalette is not (DemPreview.TerrainPalette or DemPreview.DivergingPalette))
        {
            throw new InvalidDataException("DSM-Vorschau-Metadaten sind ungültig.");
        }

        return preview;
    }

    /// <summary>
    /// Returns a cached preview when it was rendered from this model after the model's
    /// last change and its files are complete; otherwise null.
    /// </summary>
    public static DemPreview? TryLoadCached(string demPath, string palette = DemPreview.TerrainPalette)
    {
        var folder = PreviewFolderFor(demPath);
        var metadata = Path.Combine(folder, MetadataFileName);

        if (!File.Exists(demPath) || !File.Exists(metadata) ||
            File.GetLastWriteTimeUtc(metadata) < File.GetLastWriteTimeUtc(demPath))
        {
            return null;
        }

        try
        {
            var preview = Parse(File.ReadAllText(metadata));
            var gridBytes = (long)preview.Width * preview.Height * sizeof(float);
            var grid = Path.Combine(folder, preview.Grid);

            var complete =
                WorkerJsonRunner.SameFile(preview.Source, demPath) &&
                preview.EffectivePalette == palette &&
                File.Exists(Path.Combine(folder, preview.Image)) &&
                File.Exists(grid) &&
                new FileInfo(grid).Length == gridBytes;

            return complete ? preview : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static async Task<DemPreview> RenderAsync(
        string pythonExecutable,
        string workerPath,
        string demPath,
        int maxSize = DefaultMaxSize,
        CancellationToken cancellationToken = default,
        string palette = DemPreview.TerrainPalette)
    {
        if (!File.Exists(demPath))
            throw new FileNotFoundException("Höhenmodell nicht gefunden.", demPath);

        var json = await WorkerJsonRunner.RunAsync(
            pythonExecutable,
            workerPath,
            [
                "--dem-preview",
                "--source", Path.GetFullPath(demPath),
                "--output-dir", PreviewFolderFor(demPath),
                "--max-size", maxSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--palette", palette
            ],
            "DSM-Vorschau fehlgeschlagen",
            cancellationToken);

        return Parse(json);
    }
}
