namespace DroneDash_x64.Desktop.PvAnalysis;

public sealed record PvAnomalyLocation(
    string FileName,
    int CandidateIndex,
    PvAnomalySeverity Severity,
    double DeltaC,
    double PeakTemperatureC,
    double Latitude,
    double Longitude,
    double GroundSampleMeters,
    double OffsetMeters);

/// <summary>
/// Places hotspot candidates on the ground: the ray through the cluster centroid is intersected
/// with a flat ground plane at the take-off height (RelativeAltitude), using the gimbal yaw and
/// pitch of the image. Accuracy is a few metres (GPS, yaw, terrain not flat); good enough to find
/// the module row in the field, not to identify a single cell.
/// </summary>
public static class PvAnomalyGeolocator
{
    /// <summary>Diagonal field of view of the M3T thermal camera (DJI specification).</summary>
    public const double M3tThermalDiagonalFovDegrees = 61.0;

    // Rays flatter than this hit the ground too far away to be meaningful.
    private const double MinimumDownwardComponent = 0.25;

    private const double EarthRadiusMeters = 6_378_137d;

    public static PvAnomalyLocation? Locate(
        PvImageAnalysisResult image,
        PvHotspotCandidate candidate,
        double diagonalFovDegrees = M3tThermalDiagonalFovDegrees)
    {
        if (image.Latitude is not { } latitude || image.Longitude is not { } longitude ||
            image.RelativeAltitudeMeters is not { } height || height <= 1 ||
            image.GimbalYawDegrees is not { } yaw ||
            image.ThermalWidth is not { } width || image.ThermalHeight is not { } imageHeight ||
            width <= 0 || imageHeight <= 0)
        {
            return null;
        }

        var pitch = image.GimbalPitchDegrees ?? -90d;
        var focal = Math.Sqrt((double)width * width + (double)imageHeight * imageHeight) / 2d /
                    Math.Tan(diagonalFovDegrees * Math.PI / 360d);

        var (east, north, down) = Ray(
            (candidate.CentroidX + 0.5 - width / 2d) / focal,
            (candidate.CentroidY + 0.5 - imageHeight / 2d) / focal,
            yaw,
            pitch);

        if (down < MinimumDownwardComponent)
            return null;

        var distance = height / down;
        var offsetEast = east * distance;
        var offsetNorth = north * distance;

        var lat = latitude + offsetNorth / EarthRadiusMeters * 180d / Math.PI;
        var lon = longitude + offsetEast / (EarthRadiusMeters * Math.Cos(latitude * Math.PI / 180d)) * 180d / Math.PI;

        return new PvAnomalyLocation(
            image.FileName,
            candidate.Index,
            candidate.Severity,
            candidate.DeltaC,
            candidate.PeakTemperatureC,
            lat,
            lon,
            distance / focal,
            Math.Sqrt(offsetEast * offsetEast + offsetNorth * offsetNorth));
    }

    public static IReadOnlyList<PvAnomalyLocation> LocateAll(
        IEnumerable<PvImageAnalysisResult> images,
        double diagonalFovDegrees = M3tThermalDiagonalFovDegrees) =>
        images
            .SelectMany(image => image.Candidates.Select(candidate => Locate(image, candidate, diagonalFovDegrees)))
            .OfType<PvAnomalyLocation>()
            .ToArray();

    /// <summary>
    /// Unit ray (east, north, down) through normalised image coordinates (x right, y down) for a
    /// camera with the given yaw (clockwise from north) and pitch (−90 = straight down).
    /// </summary>
    internal static (double East, double North, double Down) Ray(double x, double y, double yawDegrees, double pitchDegrees)
    {
        var yaw = yawDegrees * Math.PI / 180d;
        var pitch = pitchDegrees * Math.PI / 180d;

        // Horizontal heading and right vectors in (east, north, up).
        var (fE, fN) = (Math.Sin(yaw), Math.Cos(yaw));
        var (rE, rN) = (Math.Cos(yaw), -Math.Sin(yaw));

        // Optical axis and image "up" after tilting the camera by the pitch.
        var (aE, aN, aU) = (Math.Cos(pitch) * fE, Math.Cos(pitch) * fN, Math.Sin(pitch));
        var (uE, uN, uU) = (-Math.Sin(pitch) * fE, -Math.Sin(pitch) * fN, Math.Cos(pitch));

        var e = aE + x * rE - y * uE;
        var n = aN + x * rN - y * uN;
        var u = aU - y * uU;
        var length = Math.Sqrt(e * e + n * n + u * u);
        return (e / length, n / length, -u / length);
    }
}
