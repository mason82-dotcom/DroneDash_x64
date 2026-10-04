using System.Text.Json;
using DroneDash_x64.Desktop.PvAnalysis;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class PvAnomalyGeolocatorTests
{
    private const int Width = 640;
    private const int Height = 512;
    private const double MetersPerDegreeLatitude = 6_378_137d * Math.PI / 180d;

    // Focal length in pixels of the M3T thermal camera (61° diagonal field of view).
    private static readonly double Focal =
        Math.Sqrt(Width * Width + Height * Height) / 2d / Math.Tan(61d * Math.PI / 360d);

    private static PvHotspotCandidate Candidate(double centroidX, double centroidY, PvAnomalySeverity severity = PvAnomalySeverity.Critical) =>
        new(1, severity, 20, (int)centroidX, (int)centroidY, 68.5, 41.0, 43.0, 47.0, 27.5,
            centroidX, centroidY, (int)centroidX - 2, (int)centroidY - 2, (int)centroidX + 2, (int)centroidY + 2);

    private static PvImageAnalysisResult Image(
        double? altitude = 40,
        double? yaw = 0,
        double? pitch = -90,
        params PvHotspotCandidate[] candidates) =>
        new("DJI_0001_T.JPG", "DJI_0001_T.JPG", "InfraredCamera", 49.0, 8.4, "50", null, null, null,
            null, null, null, null, Width, Height, 20, 70, 35, 0.95, 5, candidates, null)
        {
            RelativeAltitudeMeters = altitude,
            GimbalYawDegrees = yaw,
            GimbalPitchDegrees = pitch
        };

    // Pixel centre of the image (centroids are pixel indices, +0.5 is the pixel centre).
    private const double CenterX = Width / 2d - 0.5;
    private const double CenterY = Height / 2d - 0.5;

    [Fact]
    public void Ray_PointsDownAtTheCentreAndFollowsTheHeading()
    {
        var (e, n, d) = PvAnomalyGeolocator.Ray(0, 0, 37, -90);
        Assert.Equal(0, e, 9);
        Assert.Equal(0, n, 9);
        Assert.Equal(1, d, 9);

        // Image right is east when flying north, south when flying east.
        var right = PvAnomalyGeolocator.Ray(0.5, 0, 0, -90);
        Assert.True(right.East > 0.4 && Math.Abs(right.North) < 1e-9);
        var rightFacingEast = PvAnomalyGeolocator.Ray(0.5, 0, 90, -90);
        Assert.True(rightFacingEast.North < -0.4 && Math.Abs(rightFacingEast.East) < 1e-9);

        // Image top is ahead (north for yaw 0).
        var top = PvAnomalyGeolocator.Ray(0, -0.5, 0, -90);
        Assert.True(top.North > 0.4);
    }

    [Fact]
    public void Locate_NadirCentreIsTheImagePosition()
    {
        var location = PvAnomalyGeolocator.Locate(Image(), Candidate(CenterX, CenterY))!;

        Assert.Equal(49.0, location.Latitude, 9);
        Assert.Equal(8.4, location.Longitude, 9);
        Assert.Equal(40 / Focal, location.GroundSampleMeters, 9);
        Assert.Equal(0, location.OffsetMeters, 6);
    }

    [Fact]
    public void Locate_OffsetsByHeightTimesNormalisedPixelOffset()
    {
        // 200 px right of centre, yaw 90° (flying east): image right points south.
        var location = PvAnomalyGeolocator.Locate(Image(yaw: 90), Candidate(CenterX + 200, CenterY))!;

        var expected = 40 * 200 / Focal;
        Assert.Equal(-expected, (location.Latitude - 49.0) * MetersPerDegreeLatitude, 3);
        Assert.Equal(8.4, location.Longitude, 9);
        Assert.Equal(expected, location.OffsetMeters, 3);
    }

    [Fact]
    public void Locate_ObliqueCentreLiesAheadAtHeightOverTanOfPitch()
    {
        var location = PvAnomalyGeolocator.Locate(Image(pitch: -45), Candidate(CenterX, CenterY))!;

        Assert.Equal(40.0, (location.Latitude - 49.0) * MetersPerDegreeLatitude, 3);
        Assert.Equal(8.4, location.Longitude, 9);
    }

    [Theory]
    [InlineData(null, 0.0, -90.0)]     // no height
    [InlineData(40.0, null, -90.0)]    // no yaw
    [InlineData(40.0, 0.0, -5.0)]      // ray almost horizontal
    public void Locate_ReturnsNullWithoutGeometry(double? altitude, double? yaw, double pitch) =>
        Assert.Null(PvAnomalyGeolocator.Locate(Image(altitude, yaw, pitch), Candidate(CenterX, CenterY)));

    [Fact]
    public void LocateAll_AndExportsCarrySeverityAndDelta()
    {
        var images = new[]
        {
            Image(candidates: [Candidate(CenterX, CenterY), Candidate(10, 10, PvAnomalySeverity.Warning)]),
            Image(altitude: null, candidates: [Candidate(CenterX, CenterY)])
        };

        var located = PvAnomalyGeolocator.LocateAll(images);
        Assert.Equal(2, located.Count);

        using var geoJson = JsonDocument.Parse(PvAnomalyGeoExporter.BuildGeoJson(located));
        var feature = geoJson.RootElement.GetProperty("features")[0];
        Assert.Equal(8.4, feature.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble(), 8);
        Assert.Equal("Critical", feature.GetProperty("properties").GetProperty("severity").GetString());
        Assert.Equal(27.5, feature.GetProperty("properties").GetProperty("deltaC").GetDouble());

        var kml = PvAnomalyGeoExporter.BuildKml(located).ToString();
        Assert.Contains("<styleUrl>#Warning</styleUrl>", kml);
        Assert.Contains("8.40000000,49.00000000,0", kml);
        Assert.Contains("ΔT 27.5 °C", kml);
    }
}
