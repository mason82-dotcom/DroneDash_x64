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

public sealed record DtmFromDsmResult(
    string Output,
    double CellMeters,
    double MaxObjectMeters,
    long ValidCells,
    long GroundCells,
    double GroundFraction);

public sealed record DemDifferenceResult(
    string Output,
    double PixelSizeMeters,
    long ValidPixels,
    double Threshold,
    double Mean,
    double? Minimum,
    double? Maximum,
    double? P05,
    double? P95,
    double? RaisedAreaSquareMeters,
    double? LoweredAreaSquareMeters,
    double? RaisedCubicMeters,
    double? LoweredCubicMeters);

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

    /// <summary>Derived ground model next to the DSM, e.g. odm_dem/dsm-ground.tif.</summary>
    public static string DerivedDtmPathFor(string dsmPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(dsmPath)) ?? ".",
            Path.GetFileNameWithoutExtension(dsmPath) + "-ground.tif");

    /// <summary>Difference raster next to the newer model, named after both surveys.</summary>
    public static string DifferencePathFor(string newerPath, string olderPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(newerPath)) ?? ".",
            $"{Path.GetFileNameWithoutExtension(newerPath)}-minus-{SafeStem(olderPath)}.tif");

    public static async Task<DtmFromDsmResult> DtmFromDsmAsync(
        string pythonExecutable,
        string workerPath,
        string dsmPath,
        double cellMeters = 0.5,
        double maxObjectMeters = 20,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(cellMeters) || cellMeters <= 0 || !double.IsFinite(maxObjectMeters) || maxObjectMeters < 1)
            throw new ArgumentOutOfRangeException(nameof(cellMeters), "Zellgröße > 0 und Objektgröße ≥ 1 m erforderlich.");

        var json = await WorkerJsonRunner.RunAsync(
            pythonExecutable,
            workerPath,
            [
                "--dtm-from-dsm",
                "--source", Path.GetFullPath(dsmPath),
                "--output", DerivedDtmPathFor(dsmPath),
                "--cell", cellMeters.ToString("R", CultureInfo.InvariantCulture),
                "--max-object", maxObjectMeters.ToString("R", CultureInfo.InvariantCulture)
            ],
            "Geländemodell (DTM) aus DSM fehlgeschlagen",
            cancellationToken);

        return JsonSerializer.Deserialize<DtmFromDsmResult>(json, JsonOptions)
            ?? throw new InvalidDataException("Leeres DTM-Ergebnis.");
    }

    public static async Task<DemDifferenceResult> DifferenceAsync(
        string pythonExecutable,
        string workerPath,
        string newerPath,
        string olderPath,
        double thresholdMeters = 0.1,
        CancellationToken cancellationToken = default)
    {
        if (WorkerJsonRunner.SameFile(newerPath, olderPath))
            throw new ArgumentException("Für den Vergleich werden zwei verschiedene Höhenmodelle benötigt.", nameof(olderPath));
        if (!double.IsFinite(thresholdMeters) || thresholdMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdMeters), "Die Schwelle muss ≥ 0 m sein.");

        var json = await WorkerJsonRunner.RunAsync(
            pythonExecutable,
            workerPath,
            [
                "--dem-diff",
                "--source", Path.GetFullPath(newerPath),
                "--reference", Path.GetFullPath(olderPath),
                "--output", DifferencePathFor(newerPath, olderPath),
                "--threshold", thresholdMeters.ToString("R", CultureInfo.InvariantCulture)
            ],
            "DSM-Vergleich fehlgeschlagen",
            cancellationToken);

        return JsonSerializer.Deserialize<DemDifferenceResult>(json, JsonOptions)
            ?? throw new InvalidDataException("Leeres Vergleichsergebnis.");
    }

    private static string SafeStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
        // "dsm" alone says little; include the folder (e.g. the survey date) when the name is generic.
        var name = stem.Length <= 4 && parent.Length > 0 ? $"{parent}-{stem}" : stem;
        return new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    }
}
