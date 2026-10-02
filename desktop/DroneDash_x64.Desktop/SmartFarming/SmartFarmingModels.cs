namespace DroneDash_x64.Desktop.SmartFarming;

public enum M3mBand { Green, Red, RedEdge, Nir }
public enum VegetationIndexType { Ndvi, Ndre, Gndvi }

public sealed record M3mBandMetadata(
    string FilePath, M3mBand Band, int Width, int Height, int BitsPerSample,
    double? BlackLevel, double? SensorGain, double? ExposureTimeMicroseconds,
    double? SensorGainAdjustment, double? Irradiance, string? ImageSource,
    double? Latitude, double? Longitude, string? RtkStatus,
    IReadOnlyList<string> Issues)
{
    public bool HasRadiometricInputs =>
        BlackLevel.HasValue && SensorGain is > 0 && ExposureTimeMicroseconds is > 0 &&
        SensorGainAdjustment is > 0 && Irradiance is > 0;
    public string DimensionsText => $"{Width} × {Height}";
    public string CalibrationText => HasRadiometricInputs
        ? "Exposure/Gain/Irradiance vorhanden"
        : "Metadaten unvollständig";
}

public sealed record M3mCaptureGroup(
    string CaptureKey, string? RgbPath, M3mBandMetadata? Green,
    M3mBandMetadata? Red, M3mBandMetadata? RedEdge, M3mBandMetadata? Nir,
    IReadOnlyList<string> Issues)
{
    public bool IsComplete => Green is not null && Red is not null && RedEdge is not null && Nir is not null;

    public bool IsQuicklookReady =>
        IsComplete &&
        Green!.HasRadiometricInputs && Red!.HasRadiometricInputs &&
        RedEdge!.HasRadiometricInputs && Nir!.HasRadiometricInputs &&
        Green.Width == Red.Width && Green.Width == RedEdge.Width && Green.Width == Nir.Width &&
        Green.Height == Red.Height && Green.Height == RedEdge.Height && Green.Height == Nir.Height;

    public string StatusText => Issues.Count == 0 ? "Komplett" : string.Join(" · ", Issues);

    public string GpsText
    {
        get
        {
            var band = Nir ?? Red ?? RedEdge ?? Green;
            return band?.Latitude.HasValue == true && band.Longitude.HasValue
                ? $"{band.Latitude:F7}, {band.Longitude:F7}"
                : "—";
        }
    }

    public string RtkText => (Nir ?? Red ?? RedEdge ?? Green)?.RtkStatus ?? "—";
}

public sealed record M3mDatasetSummary(
    int CaptureCount, int CompleteCaptureCount, int QuicklookReadyCount,
    int RgbCount, int GreenCount, int RedCount, int RedEdgeCount, int NirCount,
    int IssueCount)
{
    public string ToDisplayText() =>
        $"Aufnahmen {CaptureCount:N0} · komplett {CompleteCaptureCount:N0} · " +
        $"Quicklook-bereit {QuicklookReadyCount:N0} · RGB {RgbCount:N0} · " +
        $"G {GreenCount:N0} · R {RedCount:N0} · RE {RedEdgeCount:N0} · " +
        $"NIR {NirCount:N0} · mit Hinweisen {IssueCount:N0}";
}

public sealed record M3mDatasetResult(
    string SourceFolder,
    IReadOnlyList<M3mCaptureGroup> Captures,
    M3mDatasetSummary Summary);

public sealed record VegetationIndexResult(
    VegetationIndexType Type, string CaptureKey, int Width, int Height,
    float[] Values, int ValidPixelCount, double Minimum, double Maximum,
    double Average, bool IsOrthorectified, bool IsBandCoregistered,
    bool UsesReflectancePanelCalibration)
{
    public string QualityLabel =>
        IsOrthorectified && IsBandCoregistered ? "Processing-Produkt" : "Einzelaufnahme-Quicklook";

    public string StatisticsText =>
        $"{Type} · {Width} × {Height} · gültig {ValidPixelCount:N0} · " +
        $"Min {Minimum:F3} · Ø {Average:F3} · Max {Maximum:F3}";
}

public sealed record SmartFarmingManifest(
    int SchemaVersion, DateTimeOffset CreatedAtUtc, string SourceFolder,
    M3mDatasetSummary Summary, IReadOnlyList<M3mCaptureGroup> Captures)
{
    public const int CurrentSchemaVersion = 1;
}
