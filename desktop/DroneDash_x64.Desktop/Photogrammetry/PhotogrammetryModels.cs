using DroneDash_x64.Desktop.Planning;

namespace DroneDash_x64.Desktop.Photogrammetry;

public sealed record PhotogrammetryImageRecord(
    string FileName,
    string RelativePath,
    long SizeBytes,
    string? ImageSource,
    double? Latitude,
    double? Longitude,
    double? AbsoluteAltitudeMeters,
    double? RelativeAltitudeMeters,
    string? RtkFlag,
    double? RtkStdLongitudeMeters,
    double? RtkStdLatitudeMeters,
    double? RtkStdHeightMeters,
    double? FlightYawDegrees,
    double? GimbalYawDegrees,
    double? GimbalPitchDegrees,
    double? CalibratedFocalLength,
    bool HasOpticalCenter,
    bool HasDewarpData,
    int? AssignedWaylineId,
    string? AssignedPassName,
    int? AssignedSegmentIndex,
    double? DistanceToRouteMeters,
    IReadOnlyList<string> Issues)
{
    public bool HasGps => Latitude.HasValue && Longitude.HasValue;
    public bool HasRtkMetadata => !string.IsNullOrWhiteSpace(RtkFlag);
    public bool HasRtkPrecision =>
        RtkStdLongitudeMeters.HasValue &&
        RtkStdLatitudeMeters.HasValue &&
        RtkStdHeightMeters.HasValue;

    public string GpsText => HasGps
        ? $"{Latitude:F7}, {Longitude:F7}"
        : "—";

    public string RtkText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RtkFlag))
                return "—";

            if (!HasRtkPrecision)
                return RtkFlag;

            return $"{RtkFlag} · σ {RtkStdLongitudeMeters:F3}/{RtkStdLatitudeMeters:F3}/{RtkStdHeightMeters:F3} m";
        }
    }

    public string RouteText => AssignedWaylineId.HasValue
        ? $"WL {AssignedWaylineId} · {AssignedPassName} · S{AssignedSegmentIndex}"
        : "—";

    public string DistanceText => DistanceToRouteMeters.HasValue
        ? $"{DistanceToRouteMeters:F1} m"
        : "—";

    public string IssuesText => Issues.Count == 0
        ? "OK"
        : string.Join(" · ", Issues);
}

public sealed record PhotogrammetryDatasetSummary(
    int ImageCount,
    int GeotaggedCount,
    int RtkMetadataCount,
    int RtkPrecisionCount,
    int CalibrationCount,
    int DewarpCount,
    int AssignedCount,
    int IssueCount,
    int? PlannedPhotoCount,
    double? PhotoCountRatio)
{
    public string ToDisplayText()
    {
        var planned = PlannedPhotoCount.HasValue
            ? $" · geplant {PlannedPhotoCount:N0} · Verhältnis Ist/Plan {PhotoCountRatio:P1}"
            : "";

        return
            $"Bilder {ImageCount:N0} · GPS {GeotaggedCount:N0} · RTK {RtkMetadataCount:N0} · " +
            $"RTK-σ {RtkPrecisionCount:N0} · Kalibrierung {CalibrationCount:N0} · " +
            $"Dewarp {DewarpCount:N0} · Route zugeordnet {AssignedCount:N0} · " +
            $"mit Hinweisen {IssueCount:N0}{planned}";
    }
}

public sealed record PhotogrammetryDatasetResult(
    string SourceFolder,
    string? FlightPlanProjectPath,
    FlightPlanResult? FlightPlan,
    IReadOnlyList<PhotogrammetryImageRecord> Images,
    PhotogrammetryDatasetSummary Summary);

public sealed record PhotogrammetryManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string SourceFolder,
    string? FlightPlanProjectPath,
    FlightPlanSettings? FlightPlanSettings,
    PhotogrammetryDatasetSummary Summary,
    IReadOnlyList<PhotogrammetryImageRecord> Images)
{
    public const int CurrentSchemaVersion = 1;
}
