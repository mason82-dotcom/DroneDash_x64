using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;

namespace DroneDash_x64.Desktop.Planning;

public sealed record TerrainCheckOptions(
    GeoPoint? TakeOff,
    double? TakeOffElevation,
    double MinimumClearanceMeters,
    double BufferMeters);

public sealed record TerrainMinimum(
    double Clearance,
    double Obstacle,
    double Distance,
    double Lon,
    double Lat,
    string? Pass);

public sealed record TerrainViolation(
    string? Pass,
    double FromDistance,
    double ToDistance,
    double MinimumClearance,
    double Obstacle,
    double Lon,
    double Lat,
    bool Collision);

public sealed record TerrainPassResult(
    int? Id,
    string? Name,
    double LengthMeters,
    double? MinimumClearance);

public sealed record TerrainChunk(
    string Status,
    string? Pass,
    IReadOnlyList<double[]> Points);

public sealed record TerrainCheckResult(
    string Mode,
    double Altitude,
    double MinClearance,
    double BufferMeters,
    double CellMeters,
    double StartElevation,
    string StartSource,
    double? FlightElevation,
    double LengthMeters,
    int Samples,
    double Coverage,
    double[]? TerrainRange,
    double? ObstacleMax,
    TerrainMinimum? Minimum,
    IReadOnlyList<TerrainPassResult> Passes,
    IReadOnlyList<TerrainViolation> Violations,
    bool ViolationsTruncated,
    IReadOnlyList<TerrainChunk> Chunks)
{
    public bool HasCollision => Violations.Any(violation => violation.Collision);

    public bool IsClear => Violations.Count == 0 && Coverage >= 0.999;

    public string SummaryText
    {
        get
        {
            var culture = CultureInfo.GetCultureInfo("de-DE");
            var lines = new List<string>();

            lines.Add(Mode == "follow"
                ? string.Format(culture, "Terrain Follow: {0:F0} m über dem Gelände (Näherung aus dem Höhenmodell).", Altitude)
                : string.Format(
                    culture,
                    "Startpunkt {0:F1} m ({1}) + {2:F0} m = Flughöhe {3:F1} m im Höhenmodell-Bezug.",
                    StartElevation,
                    StartSource == "manual" ? "manuell" : "aus Modell",
                    Altitude,
                    FlightElevation));

            if (Minimum is { } minimum)
            {
                lines.Add(string.Format(
                    culture,
                    "Geringster Abstand {0:F1} m ({1}, {2:F0} m entlang der Linie, Hindernis {3:F1} m) · Puffer {4:F0} m · Raster {5:F1} m.",
                    minimum.Clearance,
                    minimum.Pass ?? "Route",
                    minimum.Distance,
                    minimum.Obstacle,
                    BufferMeters,
                    CellMeters));
            }

            if (TerrainRange is [var low, var high])
                lines.Add(string.Format(culture, "Gelände entlang der Route {0:F1} – {1:F1} m.", low, high));

            if (Coverage < 0.999)
            {
                lines.Add(string.Format(
                    culture,
                    "⚠ Nur {0:P0} der Route liegen im Höhenmodell; der Rest ist ungeprüft (grau).",
                    Coverage));
            }

            if (Violations.Count == 0)
            {
                lines.Add(string.Format(culture, "✓ Mindestabstand {0:F0} m überall eingehalten.", MinClearance));
            }
            else
            {
                var collisions = Violations.Count(violation => violation.Collision);
                lines.Add(string.Format(
                    culture,
                    "⚠ {0} Abschnitt(e) unter {1:F0} m Abstand{2}{3}.",
                    Violations.Count,
                    MinClearance,
                    collisions > 0 ? $", davon {collisions} mit Kollision (Abstand ≤ 0)" : "",
                    ViolationsTruncated ? " (Liste gekürzt)" : ""));

                foreach (var violation in Violations.OrderBy(v => v.MinimumClearance).Take(5))
                {
                    lines.Add(string.Format(
                        culture,
                        "   {0} {1:F0}–{2:F0} m: {3:F1} m Abstand (Hindernis {4:F1} m)",
                        violation.Pass ?? "Route",
                        violation.FromDistance,
                        violation.ToDistance,
                        violation.MinimumClearance,
                        violation.Obstacle));
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>Checks a planned route against a surface model with the DroneDash worker (--terrain-check).</summary>
public static class TerrainCheckService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The worker request: each pass as one polyline through its segments in flight order, so
    /// the connecting legs between flight lines are checked as well.
    /// </summary>
    public static string BuildRequestJson(FlightPlanResult plan, TerrainCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        if (!double.IsFinite(options.MinimumClearanceMeters) || options.MinimumClearanceMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Der Mindestabstand muss ≥ 0 m sein.");
        if (!double.IsFinite(options.BufferMeters) || options.BufferMeters is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(options), "Der Puffer muss zwischen 0 und 200 m liegen.");
        if (options.TakeOffElevation is { } elevation && !double.IsFinite(elevation))
            throw new ArgumentOutOfRangeException(nameof(options), "Ungültige Starthöhe.");

        var passes = plan.Passes
            .Select(pass => new
            {
                id = pass.WaylineId,
                name = pass.Name,
                points = Chain(pass.Segments)
            })
            .Where(pass => pass.points.Count >= 2)
            .ToArray();

        if (passes.Length == 0)
            throw new InvalidOperationException("Die Route enthält keine Fluglinien.");

        var request = new
        {
            altitude = plan.Settings.AltitudeMeters,
            mode = plan.Settings.TerrainFollowEnabled ? "follow" : "relative",
            minClearance = options.MinimumClearanceMeters,
            buffer = options.BufferMeters,
            start = options.TakeOff is { } takeOff ? new[] { takeOff.Longitude, takeOff.Latitude } : null,
            startElevation = options.TakeOffElevation,
            passes
        };

        return JsonSerializer.Serialize(request);
    }

    public static async Task<TerrainCheckResult> RunAsync(
        string pythonExecutable,
        string workerPath,
        string demPath,
        FlightPlanResult plan,
        TerrainCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        var requestPath = Path.Combine(Path.GetTempPath(), $"dronedash-terrain-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, BuildRequestJson(plan, options), new UTF8Encoding(false), cancellationToken);

        try
        {
            var json = await WorkerJsonRunner.RunAsync(
                pythonExecutable,
                workerPath,
                ["--terrain-check", "--dem", demPath, "--route", requestPath],
                "Geländeprüfung fehlgeschlagen",
                cancellationToken);

            return Parse(json);
        }
        finally
        {
            try
            {
                File.Delete(requestPath);
            }
            catch (IOException)
            {
            }
        }
    }

    public static TerrainCheckResult Parse(string json) =>
        JsonSerializer.Deserialize<TerrainCheckResult>(json, JsonOptions)
        ?? throw new InvalidDataException("Leeres Ergebnis der Geländeprüfung.");

    private static List<double[]> Chain(IReadOnlyList<RouteSegment> segments)
    {
        var points = new List<double[]>();
        foreach (var segment in segments)
        {
            foreach (var point in new[] { segment.Start, segment.End })
            {
                var last = points.Count > 0 ? points[^1] : null;
                if (last is null || last[0] != point.Longitude || last[1] != point.Latitude)
                    points.Add([point.Longitude, point.Latitude]);
            }
        }

        return points;
    }
}
