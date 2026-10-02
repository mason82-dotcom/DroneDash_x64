using System.Collections.Generic;

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
    int? AircraftBatteryVoltageMv,
    int? AircraftBatteryCurrentMa,
    double? AircraftBatteryTemperatureC,
    int? AircraftBatteryRemainingMah,
    int? AircraftBatteryFullChargeMah,
    int? RemoteControllerBatteryPercent,
    int? SatelliteCount,
    string GpsSignalLevel,
    string FlightMode,
    bool? IsFlying,
    bool? AreMotorsOn,
    double? Latitude,
    double? Longitude,
    double? AltitudeMeters,
    double? TakeoffAltitudeMeters,
    double? VelocityNorthMs,
    double? VelocityEastMs,
    double? VelocityDownMs,
    double? GroundSpeedMs,
    double? VerticalSpeedMs,
    double? PitchDegrees,
    double? RollDegrees,
    double? YawDegrees,
    double? CompassHeadingDegrees,
    bool? CompassHasError,
    bool? HomeLocationSet,
    double? HomeLatitude,
    double? HomeLongitude,
    double? WindSpeedMs,
    string WindWarning,
    string WindDirection,
    bool? RtkEnabled,
    bool? RtkHealthy,
    bool? RtkMaintainAccuracyEnabled,
    string? RtkReferenceStationSource,
    string? RtkPositioningSolution,
    double? RtkMobileLatitude,
    double? RtkMobileLongitude,
    double? RtkMobileAltitudeMeters,
    double? RtkBaseLatitude,
    double? RtkBaseLongitude,
    double? RtkBaseAltitudeMeters,
    double? RtkStdLongitudeMeters,
    double? RtkStdLatitudeMeters,
    double? RtkStdAltitudeMeters,
    string? RtkHeading,
    string? RtkRealHeading,
    Dictionary<string, int>? RtkSatelliteCounts,
    string? RtkError,
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
