namespace DroneDash_x64.Desktop.Models;

public sealed record HealthDto(
    bool Ok,
    string SdkPhase,
    string SdkVersion,
    bool Registered,
    bool ProductConnected,
    string? Error);

public sealed record StatusDto(
    string SdkPhase,
    string SdkVersion,
    bool ProductConnected,
    string ProductType,
    string AircraftFirmware,
    string RemoteControllerType,
    string RemoteControllerFirmware,
    int? AircraftBatteryPercent,
    int? RemoteControllerBatteryPercent,
    int? SatelliteCount,
    string FlightMode,
    bool? IsFlying,
    double? Latitude,
    double? Longitude,
    double? AltitudeMeters,
    double? PitchDegrees,
    double? RollDegrees,
    double? YawDegrees,
    DateTimeOffset Timestamp);

public sealed record ConfigurationDto(
    int? HeightLimitMeters,
    int? GoHomeHeightMeters);

public sealed record ConfigurationUpdateDto(
    int? HeightLimitMeters,
    int? GoHomeHeightMeters);

public sealed record MediaItemDto(
    int Index,
    string Name,
    string Type,
    long SizeBytes,
    string Date)
{
    public string SizeText => SizeBytes switch
    {
        >= 1_073_741_824 => $"{SizeBytes / 1_073_741_824d:F2} GB",
        >= 1_048_576 => $"{SizeBytes / 1_048_576d:F1} MB",
        >= 1024 => $"{SizeBytes / 1024d:F1} KB",
        _ => $"{SizeBytes} B"
    };
}
