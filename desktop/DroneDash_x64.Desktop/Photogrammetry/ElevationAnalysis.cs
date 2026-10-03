using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DroneDash_x64.Desktop.Photogrammetry;

public enum VolumeBase
{
    Plane,
    Lowest,
    Fixed,
    Dtm
}

public sealed record ProfileSample(double Distance, double? Elevation, double Lon, double Lat);

public sealed record ElevationProfile(
    double LengthMeters,
    double StepMeters,
    double? Minimum,
    double? Maximum,
    double AscentMeters,
    double DescentMeters,
    IReadOnlyList<ProfileSample> Samples);

public sealed record VolumeResult(
    string Base,
    double PixelSizeMeters,
    double AreaSquareMeters,
    double ValidFraction,
    double CutCubicMeters,
    double FillCubicMeters,
    double NetCubicMeters,
    double? MeanHeightAboveBase,
    double? MaxHeightAboveBase,
    double? MinHeightAboveBase,
    double? BaseHeight,
    double? PlaneSlopePercent,
    double? BoundaryRmse);

public sealed record CanopyHeightResult(
    string Output,
    long ValidPixels,
    double PixelSizeMeters,
    double Mean,
    double? Median,
    double? P95,
    double? Maximum,
    double NegativeClippedFraction);

/// <summary>Where an elevation model can be analysed: interpreter, worker and optional base DTM.</summary>
public sealed record ElevationAnalysisContext(
    string PythonExecutable,
    string WorkerPath,
    string ModelPath,
    string? BaseModelPath);

/// <summary>Profile, volume and canopy-height analysis on full-resolution models via the worker.</summary>
public static class ElevationAnalysisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>"lon,lat;lon,lat" in invariant culture, validated for WGS84.</summary>
    public static string FormatCoordinates(IReadOnlyList<(double Lon, double Lat)> points, int minimum)
    {
        if (points.Count < minimum)
            throw new ArgumentException($"Mindestens {minimum} Punkte erforderlich.", nameof(points));

        foreach (var (lon, lat) in points)
        {
            if (!double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -90 or > 90)
                throw new ArgumentOutOfRangeException(nameof(points), "Koordinate außerhalb von WGS84.");
        }

        return string.Join(
            ";",
            points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Lon:R},{p.Lat:R}")));
    }

    public static async Task<ElevationProfile> ProfileAsync(
        ElevationAnalysisContext context,
        IReadOnlyList<(double Lon, double Lat)> line,
        CancellationToken cancellationToken = default)
    {
        var json = await WorkerJsonRunner.RunAsync(
            context.PythonExecutable,
            context.WorkerPath,
            ["--dem-profile", "--source", context.ModelPath, "--line", FormatCoordinates(line, 2)],
            "Höhenprofil fehlgeschlagen",
            cancellationToken);

        return JsonSerializer.Deserialize<ElevationProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Leeres Höhenprofil.");
    }

    public static IReadOnlyList<string> VolumeArguments(
        ElevationAnalysisContext context,
        IReadOnlyList<(double Lon, double Lat)> polygon,
        VolumeBase baseMode,
        double? baseHeight)
    {
        var arguments = new List<string>
        {
            "--dem-volume",
            "--source", context.ModelPath,
            "--polygon", FormatCoordinates(polygon, 3),
            "--base", baseMode.ToString().ToLowerInvariant()
        };

        switch (baseMode)
        {
            case VolumeBase.Fixed:
                if (baseHeight is not { } height || !double.IsFinite(height))
                    throw new ArgumentException("Für eine feste Basis wird eine Höhe benötigt.", nameof(baseHeight));
                arguments.AddRange(["--base-height", height.ToString("R", CultureInfo.InvariantCulture)]);
                break;

            case VolumeBase.Dtm:
                if (string.IsNullOrWhiteSpace(context.BaseModelPath))
                    throw new InvalidOperationException("Für die DTM-Basis ist kein Geländemodell verfügbar.");
                arguments.AddRange(["--base-dem", context.BaseModelPath]);
                break;
        }

        return arguments;
    }

    public static async Task<VolumeResult> VolumeAsync(
        ElevationAnalysisContext context,
        IReadOnlyList<(double Lon, double Lat)> polygon,
        VolumeBase baseMode,
        double? baseHeight,
        CancellationToken cancellationToken = default)
    {
        var json = await WorkerJsonRunner.RunAsync(
            context.PythonExecutable,
            context.WorkerPath,
            VolumeArguments(context, polygon, baseMode, baseHeight),
            "Volumenberechnung fehlgeschlagen",
            cancellationToken);

        return JsonSerializer.Deserialize<VolumeResult>(json, JsonOptions)
            ?? throw new InvalidDataException("Leeres Volumenergebnis.");
    }

    /// <summary>Canopy/object height model next to the DSM: odm_dem/chm.tif.</summary>
    public static string CanopyHeightPathFor(string dsmPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dsmPath)) ?? ".", "chm.tif");

    public static async Task<CanopyHeightResult> CanopyHeightAsync(
        string pythonExecutable,
        string workerPath,
        string dsmPath,
        string dtmPath,
        CancellationToken cancellationToken = default)
    {
        var output = CanopyHeightPathFor(dsmPath);
        var json = await WorkerJsonRunner.RunAsync(
            pythonExecutable,
            workerPath,
            ["--chm", "--dsm", Path.GetFullPath(dsmPath), "--dtm", Path.GetFullPath(dtmPath), "--output", output],
            "Bestandshöhe (DSM − DTM) fehlgeschlagen",
            cancellationToken);

        return JsonSerializer.Deserialize<CanopyHeightResult>(json, JsonOptions)
            ?? throw new InvalidDataException("Leeres CHM-Ergebnis.");
    }
}
