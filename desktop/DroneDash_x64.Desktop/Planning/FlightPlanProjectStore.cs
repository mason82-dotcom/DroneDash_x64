using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.Planning;

public sealed record FlightPlanProject(
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    FlightPlanSettings Settings,
    IReadOnlyList<GeoPoint> Geometry)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Terrain check inputs; optional, absent in plans saved before the terrain check existed.</summary>
    public FlightPlanTerrainSettings? Terrain { get; init; }
}

public sealed record FlightPlanTerrainSettings(
    string? ModelPath,
    GeoPoint? TakeOff,
    double? TakeOffElevation,
    double MinimumClearanceMeters,
    double BufferMeters)
{
    public const double DefaultMinimumClearanceMeters = 30;
    public const double DefaultBufferMeters = 10;

    public void Validate()
    {
        if (TakeOff is { } takeOff &&
            (!double.IsFinite(takeOff.Latitude) || !double.IsFinite(takeOff.Longitude) ||
             takeOff.Latitude is < -90 or > 90 || takeOff.Longitude is < -180 or > 180))
        {
            throw new ArgumentOutOfRangeException(nameof(TakeOff), "Startpunkt liegt außerhalb von WGS84.");
        }

        if (TakeOffElevation is { } elevation && !double.IsFinite(elevation))
            throw new ArgumentOutOfRangeException(nameof(TakeOffElevation), "Ungültige Starthöhe.");

        if (!double.IsFinite(MinimumClearanceMeters) || MinimumClearanceMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumClearanceMeters), "Der Mindestabstand muss ≥ 0 m sein.");

        if (!double.IsFinite(BufferMeters) || BufferMeters is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(BufferMeters), "Der Puffer muss zwischen 0 und 200 m liegen.");
    }
}

public static class FlightPlanProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(
        string path,
        FlightPlanSettings settings,
        IReadOnlyList<GeoPoint> geometry,
        FlightPlanTerrainSettings? terrain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FlightPlanValidation.ValidateProjectData(
            geometry,
            settings);
        terrain?.Validate();

        var project =
            new FlightPlanProject(
                FlightPlanProject.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                settings,
                geometry.ToArray())
            {
                Terrain = terrain
            };

        var json =
            JsonSerializer.Serialize(
                project,
                JsonOptions);

        var fullPath =
            Path.GetFullPath(path);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)!);

        var tempPath =
            fullPath +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (var writer = new StreamWriter(
                stream,
                new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                tempPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Cleanup must not hide the save result.
            }
        }
    }

    public static FlightPlanProject Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<FlightPlanProject>(json, JsonOptions)
            ?? throw new InvalidDataException("DroneDash-Projekt konnte nicht gelesen werden.");

        if (project.SchemaVersion != FlightPlanProject.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Nicht unterstützte DroneDash-Projektversion {project.SchemaVersion}; erwartet {FlightPlanProject.CurrentSchemaVersion}.");

        if (project.Geometry is null || project.Geometry.Count == 0)
            throw new InvalidDataException("DroneDash-Projekt enthält keine Geometrie.");

        if (project.Settings is null)
            throw new InvalidDataException("DroneDash-Projekt enthält keine Planungseinstellungen.");

        try
        {
            FlightPlanValidation.ValidateProjectData(
                project.Geometry,
                project.Settings);
            project.Terrain?.Validate();
        }
        catch (Exception ex)
            when (ex is
                ArgumentException or
                InvalidOperationException)
        {
            throw new InvalidDataException(
                $"DroneDash-Projekt enthält ungültige Planungsdaten: {ex.Message}",
                ex);
        }

        return project;
    }
}
