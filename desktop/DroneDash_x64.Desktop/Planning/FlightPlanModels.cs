namespace DroneDash_x64.Desktop.Planning;

public enum DjiAircraftProfile
{
    M3E,
    M3T,
    M3M
}

public sealed record GeoPoint(double Latitude, double Longitude);

public sealed record RouteSegment(
    GeoPoint Start,
    GeoPoint End,
    double LengthMeters);

public sealed record FlightPlanSettings(
    string Name,
    DjiAircraftProfile Aircraft,
    double AltitudeMeters,
    double SpeedMetersPerSecond,
    int FrontOverlapPercent,
    int SideOverlapPercent,
    double GridAngleDegrees,
    double GimbalPitchDegrees);

public sealed record FlightPlanResult(
    FlightPlanSettings Settings,
    IReadOnlyList<GeoPoint> Polygon,
    IReadOnlyList<RouteSegment> Segments,
    double AreaSquareMeters,
    double FootprintWidthMeters,
    double FootprintHeightMeters,
    double LineSpacingMeters,
    double PhotoSpacingMeters,
    double GsdCentimeters,
    double? SecondaryGsdCentimeters,
    double FlightDistanceMeters,
    int EstimatedPhotos,
    TimeSpan EstimatedFlightTime,
    string CameraProfile,
    string SurveyNote);
