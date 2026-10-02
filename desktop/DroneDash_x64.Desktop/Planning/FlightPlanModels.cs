namespace DroneDash_x64.Desktop.Planning;

public enum DjiAircraftProfile
{
    M3E,
    M3T,
    M3M
}

public enum FlightPlanMode
{
    Mapping2D,
    Mapping3D,
    MappingStrip
}

public sealed record GeoPoint(double Latitude, double Longitude);

public sealed record RouteSegment(
    GeoPoint Start,
    GeoPoint End,
    double LengthMeters);

public sealed record FlightPass(
    int WaylineId,
    string Name,
    double GridAngleDegrees,
    double GimbalPitchDegrees,
    bool IsOblique,
    IReadOnlyList<RouteSegment> Segments);

public sealed record FlightPlanSettings(
    string Name,
    DjiAircraftProfile Aircraft,
    FlightPlanMode Mode,
    double AltitudeMeters,
    double SpeedMetersPerSecond,
    int FrontOverlapPercent,
    int SideOverlapPercent,
    double GridAngleDegrees,
    double GimbalPitchDegrees,
    double ObliqueGimbalPitchDegrees,
    bool SmartObliqueEnabled,
    bool TerrainFollowEnabled,
    double StripHalfWidthMeters);

public sealed record FlightPlanResult(
    FlightPlanSettings Settings,
    IReadOnlyList<GeoPoint> Geometry,
    IReadOnlyList<FlightPass> Passes,
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
