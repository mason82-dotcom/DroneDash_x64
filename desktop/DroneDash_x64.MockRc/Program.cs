using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:49152");
var app = builder.Build();

const string token = "change-me-now";
var started = DateTimeOffset.UtcNow;
var config = new MutableConfig { HeightLimitMeters = 120, GoHomeHeightMeters = 60 };

var media = new[]
{
    new MediaDto(101, "DJI_20261002_190001_D.JPG", "JPEG", 18_250_112, "2026-10-02T19:00:01"),
    new MediaDto(102, "DJI_20261002_190115_T.JPG", "JPEG", 11_840_442, "2026-10-02T19:01:15"),
    new MediaDto(103, "DJI_20261002_190307_W.MP4", "MP4", 248_100_340, "2026-10-02T19:03:07")
};

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/api/v1/health")
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-Bridge-Token", out var supplied) ||
        !string.Equals(supplied.ToString(), token, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "invalid bridge token" });
        return;
    }

    await next();
});

app.MapGet("/api/v1/health", () => Results.Json(new
{
    ok = true,
    sdkPhase = "MOCK_READY",
    sdkVersion = "5.18.0-mock",
    registered = true,
    productConnected = true,
    error = (string?)null
}));

app.MapGet("/api/v1/status", () =>
{
    var seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
    var velocityNorth = Math.Sin(seconds / 5d) * 4.5;
    var velocityEast = Math.Cos(seconds / 7d) * 3.2;
    var velocityDown = Math.Sin(seconds / 9d) * 0.8;

    return Results.Json(new
    {
        sdkPhase = "READY",
        sdkVersion = "5.18.0-mock",
        productConnected = true,
        productType = "DJI_MAVIC_3_ENTERPRISE_SERIES",
        aircraftFirmware = "mock-fw",
        remoteControllerType = "DJI_RC_PRO_ENTERPRISE",
        remoteControllerFirmware = "mock-rc-fw",
        aircraftBatteryPercent = Math.Max(20, 93 - (int)(seconds / 90)),
        aircraftBatteryVoltageMv = 15420,
        aircraftBatteryCurrentMa = -3850,
        aircraftBatteryTemperatureC = 31.7,
        aircraftBatteryRemainingMah = 4020,
        aircraftBatteryFullChargeMah = 5000,
        remoteControllerBatteryPercent = 82,
        satelliteCount = 18,
        gpsSignalLevel = "LEVEL_5",
        flightMode = "GPS_NORMAL",
        isFlying = seconds % 30 > 5,
        areMotorsOn = seconds % 30 > 3,
        latitude = 49.223500 + Math.Sin(seconds / 40d) * 0.0002,
        longitude = 8.535100 + Math.Cos(seconds / 40d) * 0.0002,
        altitudeMeters = 42.0 + Math.Sin(seconds / 4d) * 2.5,
        takeoffAltitudeMeters = 102.4,
        velocityNorthMs = velocityNorth,
        velocityEastMs = velocityEast,
        velocityDownMs = velocityDown,
        groundSpeedMs = Math.Sqrt(velocityNorth * velocityNorth + velocityEast * velocityEast),
        verticalSpeedMs = -velocityDown,
        pitchDegrees = Math.Sin(seconds / 3d) * 4,
        rollDegrees = Math.Cos(seconds / 3d) * 5,
        yawDegrees = (seconds * 4) % 360 - 180,
        compassHeadingDegrees = (seconds * 4) % 360 - 180,
        compassHasError = false,
        homeLocationSet = true,
        homeLatitude = 49.223350,
        homeLongitude = 8.534950,
        windSpeedMs = 3.4,
        windWarning = "LEVEL_0",
        windDirection = "NORTH",
        rtkEnabled = true,
        rtkHealthy = true,
        rtkMaintainAccuracyEnabled = true,
        rtkReferenceStationSource = "NTRIP_NETWORK_SERVICE",
        rtkPositioningSolution = "FIXED_POINT",
        rtkMobileLatitude = 49.223501,
        rtkMobileLongitude = 8.535101,
        rtkMobileAltitudeMeters = 143.82,
        rtkBaseLatitude = 49.220000,
        rtkBaseLongitude = 8.530000,
        rtkBaseAltitudeMeters = 101.25,
        rtkStdLongitudeMeters = 0.012,
        rtkStdLatitudeMeters = 0.010,
        rtkStdAltitudeMeters = 0.018,
        rtkHeading = "RTKHeading{heading=42.1}",
        rtkRealHeading = "42.0",
        rtkSatelliteCounts = new Dictionary<string, int>
        {
            ["mobile1.GPS"] = 14,
            ["mobile1.GALILEO"] = 10,
            ["mobile1.BEIDOU"] = 16,
            ["base.GPS"] = 13,
            ["base.GALILEO"] = 9
        },
        rtkError = (string?)null,
        cameraType = "MAVIC_3_ENTERPRISE_CAMERA",
        cameraFirmware = "mock-camera-fw",
        cameraMode = "PHOTO_NORMAL",
        cameraIsShootingPhoto = false,
        cameraIsRecording = seconds % 45 > 30,
        cameraCurrentStorage = "SDCARD",
        sdStorageState = "NORMAL",
        sdStorageCapacityMb = 122880,
        sdStorageLeftMb = 86420,
        sdAvailablePhotoCount = 4210,
        sdAvailableVideoSeconds = 11240,
        internalStorageState = "NORMAL",
        internalStorageCapacityMb = 8192,
        internalStorageLeftMb = 6144,
        internalAvailablePhotoCount = 280,
        internalAvailableVideoSeconds = 920,
        gimbalMode = "YAW_FOLLOW",
        gimbalPitchDegrees = -35.0 + Math.Sin(seconds / 5d) * 8,
        gimbalRollDegrees = Math.Sin(seconds / 8d),
        gimbalYawDegrees = Math.Sin(seconds / 12d) * 12,
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapGet("/api/v1/config", () => Results.Json(config));

app.MapPut("/api/v1/config", (ConfigUpdate update) =>
{
    if (update.HeightLimitMeters is int height)
    {
        if (height is < 20 or > 500)
            return Results.BadRequest(new { error = "mock heightLimit range is 20..500" });
        config.HeightLimitMeters = height;
    }

    if (update.GoHomeHeightMeters is int rth)
    {
        if (rth is < 20 or > 500)
            return Results.BadRequest(new { error = "mock goHomeHeight range is 20..500" });
        config.GoHomeHeightMeters = rth;
    }

    return Results.Json(config);
});

app.MapGet("/api/v1/media", () => Results.Json(media));

app.MapGet("/api/v1/media/{index:int}/download", (int index) =>
{
    var item = media.SingleOrDefault(x => x.Index == index);
    if (item is null)
        return Results.NotFound(new { error = "media index not found" });

    var text = $"DroneDash_x64 Mock media\r\nIndex: {item.Index}\r\nName: {item.Name}\r\n";
    var bytes = Encoding.UTF8.GetBytes(text);
    return Results.File(bytes, "application/octet-stream", item.Name + ".mock.txt");
});

Console.WriteLine("DroneDash_x64 Mock RC");
Console.WriteLine("Endpoint: http://127.0.0.1:49152/");
Console.WriteLine("Token:    change-me-now");
await app.RunAsync();

sealed class MutableConfig
{
    public int? HeightLimitMeters { get; set; }
    public int? GoHomeHeightMeters { get; set; }
}

sealed record ConfigUpdate(int? HeightLimitMeters, int? GoHomeHeightMeters);
sealed record MediaDto(int Index, string Name, string Type, long SizeBytes, string Date);
