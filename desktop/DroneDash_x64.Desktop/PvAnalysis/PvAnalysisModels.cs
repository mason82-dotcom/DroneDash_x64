namespace DroneDash_x64.Desktop.PvAnalysis;

public enum PvAnomalySeverity
{
    None = 0,
    Warning = 1,
    Critical = 2
}

public sealed record PvAnalysisSettings(
    double WarningDeltaC,
    double CriticalDeltaC,
    int LocalWindowRadiusPixels,
    int MinimumClusterPixels)
{
    public static PvAnalysisSettings Default =>
        new(5.0, 15.0, 10, 6);

    public void Validate()
    {
        if (!double.IsFinite(WarningDeltaC) || WarningDeltaC <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(WarningDeltaC),
                "Warnschwelle ΔT muss > 0 °C sein.");

        if (!double.IsFinite(CriticalDeltaC) ||
            CriticalDeltaC <= WarningDeltaC)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CriticalDeltaC),
                "Kritische ΔT-Schwelle muss größer als die Warnschwelle sein.");
        }

        if (LocalWindowRadiusPixels is < 2 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(LocalWindowRadiusPixels),
                "Lokaler Radius muss zwischen 2 und 100 Pixel liegen.");

        if (MinimumClusterPixels is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(
                nameof(MinimumClusterPixels),
                "Minimale Clustergröße muss zwischen 1 und 10000 Pixel liegen.");
    }
}

public sealed record PvHotspotCandidate(
    int Index,
    PvAnomalySeverity Severity,
    int PixelCount,
    int PeakX,
    int PeakY,
    double PeakTemperatureC,
    double LocalBaselineC,
    double DeltaC,
    double CentroidX,
    double CentroidY,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY)
{
    public string PixelText => $"{PeakX},{PeakY}";
    public string BoundingBoxText => $"{MinX},{MinY} – {MaxX},{MaxY}";
}

public sealed record PvImageAnalysisResult(
    string FileName,
    string RelativePath,
    string? ImageSource,
    double? Latitude,
    double? Longitude,
    string? RtkFlag,
    double? RtkStdLongitudeMeters,
    double? RtkStdLatitudeMeters,
    double? RtkStdHeightMeters,
    int? AssignedWaylineId,
    string? AssignedPassName,
    int? AssignedSegmentIndex,
    double? DistanceToRouteMeters,
    int? ThermalWidth,
    int? ThermalHeight,
    double? MinimumC,
    double? MaximumC,
    double? AverageC,
    double? Emissivity,
    double? MeasurementDistanceMeters,
    IReadOnlyList<PvHotspotCandidate> Candidates,
    string? ProcessingError)
{
    public PvAnomalySeverity Severity =>
        Candidates.Count == 0
            ? PvAnomalySeverity.None
            : Candidates.Max(candidate => candidate.Severity);

    public double? HighestDeltaC =>
        Candidates.Count == 0
            ? null
            : Candidates.Max(candidate => candidate.DeltaC);

    public string GpsText =>
        Latitude.HasValue && Longitude.HasValue
            ? $"{Latitude:F7}, {Longitude:F7}"
            : "—";

    public string RtkText =>
        string.IsNullOrWhiteSpace(RtkFlag)
            ? "—"
            : RtkStdLongitudeMeters.HasValue &&
              RtkStdLatitudeMeters.HasValue &&
              RtkStdHeightMeters.HasValue
                ? $"{RtkFlag} · σ {RtkStdLongitudeMeters:F3}/{RtkStdLatitudeMeters:F3}/{RtkStdHeightMeters:F3} m"
                : RtkFlag;

    public string MaxTemperatureText =>
        MaximumC.HasValue
            ? $"{MaximumC:F2} °C"
            : "—";

    public string HighestDeltaText =>
        HighestDeltaC.HasValue
            ? $"{HighestDeltaC:F2} °C"
            : "—";

    public string RouteText =>
        AssignedWaylineId.HasValue
            ? $"WL {AssignedWaylineId} · {AssignedPassName} · S{AssignedSegmentIndex}"
            : "—";

    public string StatusText =>
        !string.IsNullOrWhiteSpace(ProcessingError)
            ? ProcessingError
            : Candidates.Count == 0
                ? "Keine ΔT-Anomalie über Schwelle"
                : $"{Candidates.Count} Kandidat(en)";
}

public sealed record PvDatasetSummary(
    int ThermalImageCount,
    int SuccessfullyProcessedCount,
    int FailedCount,
    int ImagesWithCandidates,
    int WarningImageCount,
    int CriticalImageCount,
    int CandidateCount,
    double? HighestDeltaC)
{
    public string ToDisplayText() =>
        $"Thermalbilder {ThermalImageCount:N0} · verarbeitet {SuccessfullyProcessedCount:N0} · " +
        $"Fehler {FailedCount:N0} · mit Kandidaten {ImagesWithCandidates:N0} · " +
        $"Warnung {WarningImageCount:N0} · kritisch {CriticalImageCount:N0} · " +
        $"Cluster {CandidateCount:N0} · max ΔT {(HighestDeltaC.HasValue ? $"{HighestDeltaC:F2} °C" : "—")}";
}

public sealed record PvDatasetResult(
    string SourceFolder,
    string? FlightPlanProjectPath,
    PvAnalysisSettings Settings,
    IReadOnlyList<PvImageAnalysisResult> Images,
    PvDatasetSummary Summary);

public sealed record PvAnalysisManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string SourceFolder,
    string? FlightPlanProjectPath,
    PvAnalysisSettings Settings,
    PvDatasetSummary Summary,
    IReadOnlyList<PvImageAnalysisResult> Images)
{
    public const int CurrentSchemaVersion = 1;
}
