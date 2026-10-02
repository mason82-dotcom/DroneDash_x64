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
}

public static class FlightPlanProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string path, FlightPlanSettings settings, IReadOnlyList<GeoPoint> geometry)
    {
        if (geometry.Count == 0)
            throw new InvalidOperationException("Die Planung enthält keine Geometrie.");

        var project = new FlightPlanProject(
            FlightPlanProject.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            settings,
            geometry.ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(project, JsonOptions));
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

        return project;
    }
}
