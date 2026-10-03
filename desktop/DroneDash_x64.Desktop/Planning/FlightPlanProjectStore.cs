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

    public static void Save(
        string path,
        FlightPlanSettings settings,
        IReadOnlyList<GeoPoint> geometry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FlightPlanValidation.ValidateProjectData(
            geometry,
            settings);

        var project =
            new FlightPlanProject(
                FlightPlanProject.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                settings,
                geometry.ToArray());

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
