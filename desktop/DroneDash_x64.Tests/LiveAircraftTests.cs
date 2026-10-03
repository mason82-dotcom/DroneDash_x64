using System.Text.Json;
using DroneDash_x64.Desktop.Models;
using DroneDash_x64.Desktop.Planning;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class LiveAircraftTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A bridge status as the RC agent sends it (only the fields the map uses).</summary>
    private static StatusDto Status(
        double? lat = 49.0005,
        double? lon = 8.4010,
        bool connected = true,
        bool? flying = true,
        bool? homeSet = true,
        double? homeLat = 49.0,
        double? homeLon = 8.4) =>
        JsonSerializer.Deserialize<StatusDto>(
            JsonSerializer.Serialize(new
            {
                sdkPhase = "REGISTERED",
                productConnected = connected,
                latitude = lat,
                longitude = lon,
                altitudeMeters = 79.6,
                groundSpeedMs = 8.1,
                compassHeadingDegrees = 92.0,
                yawDegrees = 90.0,
                isFlying = flying,
                homeLocationSet = homeSet,
                homeLatitude = homeLat,
                homeLongitude = homeLon
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void FromStatus_ReadsPositionHeadingAndHome()
    {
        var state = LiveAircraftState.FromStatus(Status(), Now)!;

        Assert.Equal(new GeoPoint(49.0005, 8.4010), state.Position);
        Assert.Equal(79.6, state.RelativeAltitudeMeters);
        Assert.Equal(92.0, state.HeadingDegrees);
        Assert.Equal(new GeoPoint(49.0, 8.4), state.Home);
        Assert.True(state.IsFlying);
    }

    [Theory]
    [InlineData(0.0, 0.0, true)]          // DJI without GPS fix
    [InlineData(null, 8.4, true)]          // JSON has no NaN; the bridge sends null
    [InlineData(95.0, 8.4, true)]
    [InlineData(49.0, 8.4, false)]        // aircraft not connected
    public void FromStatus_IgnoresMissingFixOrAircraft(double? lat, double lon, bool connected) =>
        Assert.Null(LiveAircraftState.FromStatus(Status(lat, lon, connected), Now));

    [Fact]
    public void FromStatus_DropsHomeWithoutCoordinatesAndNullStatus()
    {
        Assert.Null(LiveAircraftState.FromStatus(Status(homeLat: 0, homeLon: 0), Now)!.Home);
        Assert.Null(LiveAircraftState.FromStatus(Status(homeSet: false), Now)!.Home);
        Assert.Null(LiveAircraftState.FromStatus(null, Now));
    }

    [Fact]
    public void Track_SkipsJitterAndStaysBounded()
    {
        var track = new LiveAircraftTrack();
        Assert.True(track.Add(new GeoPoint(49.0, 8.4)));
        Assert.False(track.Add(new GeoPoint(49.000003, 8.4)));   // ~0.3 m
        Assert.True(track.Add(new GeoPoint(49.00002, 8.4)));      // ~2.2 m

        for (var i = 0; i < LiveAircraftTrack.MaxPoints * 2; i++)
            track.Add(new GeoPoint(49.0 + i * 2e-5, 8.4));

        Assert.InRange(track.Points.Count, LiveAircraftTrack.MaxPoints / 2, LiveAircraftTrack.MaxPoints);
        Assert.Equal(new GeoPoint(49.0, 8.4), track.Points[0]);           // start of the flight kept
        Assert.Equal(49.0 + (LiveAircraftTrack.MaxPoints * 2 - 1) * 2e-5, track.Points[^1].Latitude, 9);

        track.Clear();
        Assert.Empty(track.Points);
    }

    [Fact]
    public void Describe_ShowsHeightSpeedRouteDistanceAndAge()
    {
        IReadOnlyList<GeoPoint> field = [new(49.0, 8.4), new(49.0, 8.404), new(49.0025, 8.404), new(49.0025, 8.4)];
        var plan = PhotogrammetryPlanner.Generate(
            field,
            new FlightPlanSettings("Live", DjiAircraftProfile.M3E, FlightPlanMode.Mapping2D, 80, 8, 80, 70, 0, -90, -60, false, false, 40));
        var state = LiveAircraftState.FromStatus(Status(), Now)!;

        var text = LiveAircraftDescription.Describe(state, plan, Now.AddSeconds(2));
        Assert.Contains("79,6 m über Start", text);
        Assert.Contains("8,1 m/s", text);
        Assert.Matches(@"\d+ m neben .+ \(WL \d+\)", text);
        Assert.DoesNotContain("alt", text);

        Assert.Contains("⚠ 12 s alt", LiveAircraftDescription.Describe(state, null, Now.AddSeconds(12)));
        Assert.Contains("am Boden", LiveAircraftDescription.Describe(
            LiveAircraftState.FromStatus(Status(flying: false), Now), null, Now));
        Assert.StartsWith("Keine Live-Position", LiveAircraftDescription.Describe(null, plan, Now));
    }
}
