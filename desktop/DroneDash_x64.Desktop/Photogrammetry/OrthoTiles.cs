using System.IO;
using System.Text.Json;

namespace DroneDash_x64.Desktop.Photogrammetry;

/// <summary>Metadata written by the worker's --ortho-tiles mode (ortho-tiles.json).</summary>
public sealed record OrthoTileSet(
    int SchemaVersion,
    string Source,
    DemGeoBounds Bounds,
    int MinZoom,
    int MaxZoom,
    int TileSize,
    double PixelSizeMeters,
    int Tiles,
    string Template)
{
    public const int SupportedSchemaVersion = 1;
}

/// <summary>An orthomosaic as an XYZ tile pyramid for the Leaflet maps, cached next to the image.</summary>
public static class OrthoTileService
{
    public const string MetadataFileName = "ortho-tiles.json";

    /// <summary>The only template the maps load; anything else is rejected as tampered metadata.</summary>
    public const string ExpectedTemplate = "{z}/{x}/{y}.png";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Tiles live next to the image, e.g. odm_orthophoto/odm_orthophoto.tiles/.</summary>
    public static string TileFolderFor(string orthoPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(orthoPath)) ?? ".",
            Path.GetFileNameWithoutExtension(orthoPath) + ".tiles");

    public static OrthoTileSet Parse(string json)
    {
        var tiles = JsonSerializer.Deserialize<OrthoTileSet>(json, JsonOptions)
            ?? throw new InvalidDataException("Leere Orthomosaik-Kachel-Metadaten.");

        if (tiles.SchemaVersion != OrthoTileSet.SupportedSchemaVersion)
            throw new InvalidDataException($"Nicht unterstützte Orthomosaik-Kachel-Version {tiles.SchemaVersion}.");

        var bounds = tiles.Bounds;
        var valid =
            bounds is not null &&
            bounds.South is >= -90 and <= 90 && bounds.North is >= -90 and <= 90 && bounds.South < bounds.North &&
            bounds.West is >= -180 and <= 180 && bounds.East is >= -180 and <= 180 && bounds.West < bounds.East &&
            tiles.MinZoom is >= 0 and <= 24 && tiles.MaxZoom is >= 0 and <= 24 && tiles.MinZoom <= tiles.MaxZoom &&
            tiles.TileSize == 256 && tiles.Tiles > 0 &&
            tiles.Template == ExpectedTemplate;

        return valid ? tiles : throw new InvalidDataException("Orthomosaik-Kachel-Metadaten sind ungültig.");
    }

    /// <summary>Cached tiles when they were rendered from this image after its last change; otherwise null.</summary>
    public static OrthoTileSet? TryLoadCached(string orthoPath)
    {
        var metadata = Path.Combine(TileFolderFor(orthoPath), MetadataFileName);
        if (!File.Exists(orthoPath) || !File.Exists(metadata) ||
            File.GetLastWriteTimeUtc(metadata) < File.GetLastWriteTimeUtc(orthoPath))
        {
            return null;
        }

        try
        {
            var tiles = Parse(File.ReadAllText(metadata));
            return WorkerJsonRunner.SameFile(tiles.Source, orthoPath) ? tiles : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static async Task<OrthoTileSet> RenderAsync(
        string pythonExecutable,
        string workerPath,
        string orthoPath,
        CancellationToken cancellationToken = default)
    {
        var json = await WorkerJsonRunner.RunAsync(
            pythonExecutable,
            workerPath,
            ["--ortho-tiles", "--source", orthoPath, "--output-dir", TileFolderFor(orthoPath)],
            "Orthomosaik-Kacheln fehlgeschlagen",
            cancellationToken);

        return Parse(json);
    }

    public static async Task<OrthoTileSet> LoadOrRenderAsync(
        string pythonExecutable,
        string workerPath,
        string orthoPath,
        CancellationToken cancellationToken = default) =>
        TryLoadCached(orthoPath) ??
        await RenderAsync(pythonExecutable, workerPath, orthoPath, cancellationToken);

    /// <summary>
    /// Message for the map pages: tile URL on the given virtual host (cache-busted with the
    /// metadata time, so re-rendered tiles are never served stale) plus extent and zooms.
    /// </summary>
    public static object MapLayerMessage(OrthoTileSet tiles, string orthoPath, string host, string name)
    {
        var version = File.GetLastWriteTimeUtc(Path.Combine(TileFolderFor(orthoPath), MetadataFileName)).Ticks;
        return new
        {
            url = $"https://{host}/{{z}}/{{x}}/{{y}}.png?v={version}",
            name,
            bounds = new[] { new[] { tiles.Bounds.South, tiles.Bounds.West }, new[] { tiles.Bounds.North, tiles.Bounds.East } },
            minZoom = tiles.MinZoom,
            maxZoom = tiles.MaxZoom
        };
    }
}
