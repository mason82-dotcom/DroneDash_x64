using DroneDash_x64.Desktop.Photogrammetry;
using DroneDash_x64.Desktop.Planning;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class FlightPlanningTests
{
    private static readonly GeoPoint[] Field =
    [
        new(49.0000, 8.0000),
        new(49.0000, 8.0040),
        new(49.0025, 8.0040),
        new(49.0025, 8.0000)
    ];

    private static FlightPlanSettings Settings(
        FlightPlanMode mode = FlightPlanMode.Mapping2D,
        double altitude = 80,
        int frontOverlap = 80,
        int sideOverlap = 70) =>
        new(
            "Unit",
            DjiAircraftProfile.M3E,
            mode,
            altitude,
            8,
            frontOverlap,
            sideOverlap,
            15,
            -90,
            -60,
            false,
            false,
            40);

    [Fact]
    public void Planner_RejectsPolygonWithFewerThanThreePoints()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PhotogrammetryPlanner.Generate(Field[..2], Settings()));
    }

    [Fact]
    public void Planner_AcceptsTwoPointStripCenterline()
    {
        var plan = PhotogrammetryPlanner.Generate(
            Field[..2],
            Settings(FlightPlanMode.MappingStrip));

        Assert.NotEmpty(plan.Passes);
        Assert.True(plan.FlightDistanceMeters > 0);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(501)]
    [InlineData(double.NaN)]
    public void Planner_RejectsAltitudeOutsideSupportedRange(double altitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PhotogrammetryPlanner.Generate(Field, Settings(altitude: altitude)));
    }

    [Fact]
    public void Planner_RejectsNonWgs84Coordinates()
    {
        GeoPoint[] invalid = [.. Field[..2], new(91, 8.002)];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PhotogrammetryPlanner.Generate(invalid, Settings()));
    }

    [Fact]
    public void Planner_GsdScalesLinearlyWithAltitude()
    {
        var low = PhotogrammetryPlanner.Generate(Field, Settings(altitude: 60));
        var high = PhotogrammetryPlanner.Generate(Field, Settings(altitude: 120));

        Assert.Equal(2d, high.GsdCentimeters / low.GsdCentimeters, 3);
        Assert.Equal(2d, high.FootprintWidthMeters / low.FootprintWidthMeters, 3);
    }

    [Fact]
    public void Planner_HigherOverlapProducesDenserCoverage()
    {
        var sparse = PhotogrammetryPlanner.Generate(
            Field,
            Settings(frontOverlap: 70, sideOverlap: 60));
        var dense = PhotogrammetryPlanner.Generate(
            Field,
            Settings(frontOverlap: 85, sideOverlap: 80));

        Assert.True(dense.LineSpacingMeters < sparse.LineSpacingMeters);
        Assert.True(dense.PhotoSpacingMeters < sparse.PhotoSpacingMeters);
        Assert.True(dense.EstimatedPhotos > sparse.EstimatedPhotos);
    }

    [Fact]
    public void Planner_Mapping3DProducesNadirPlusFourObliquePasses()
    {
        var plan = PhotogrammetryPlanner.Generate(
            Field,
            Settings(FlightPlanMode.Mapping3D));

        Assert.Equal(5, plan.Passes.Count);
        Assert.Single(plan.Passes, pass => !pass.IsOblique);
        Assert.Equal(4, plan.Passes.Count(pass => pass.IsOblique));
        Assert.Equal(
            plan.Passes.Count,
            plan.Passes.Select(pass => pass.WaylineId).Distinct().Count());
    }

    [Fact]
    public void RouteMatcher_PointOnSegmentHasZeroDistance()
    {
        var distance = RouteImageMatcher.DistanceToSegmentMeters(
            new GeoPoint(49.0, 8.001),
            new GeoPoint(49.0, 8.000),
            new GeoPoint(49.0, 8.002));

        Assert.Equal(0d, distance, 3);
    }

    [Fact]
    public void RouteMatcher_MeasuresPerpendicularDistance()
    {
        // 0.001° latitude ≈ 111.3 m on the WGS84 equatorial-radius sphere used by the matcher.
        var distance = RouteImageMatcher.DistanceToSegmentMeters(
            new GeoPoint(49.001, 8.001),
            new GeoPoint(49.0, 8.000),
            new GeoPoint(49.0, 8.002));

        Assert.InRange(distance, 110.5, 112.0);
    }

    [Fact]
    public void RouteMatcher_ClampsToNearestEndpoint()
    {
        var beyondEnd = RouteImageMatcher.DistanceToSegmentMeters(
            new GeoPoint(49.0, 8.003),
            new GeoPoint(49.0, 8.000),
            new GeoPoint(49.0, 8.002));
        var toEndpoint = RouteImageMatcher.DistanceToSegmentMeters(
            new GeoPoint(49.0, 8.003),
            new GeoPoint(49.0, 8.002),
            new GeoPoint(49.0, 8.002));

        Assert.True(beyondEnd > 70);
        Assert.Equal(toEndpoint, beyondEnd, 6);
    }

    [Fact]
    public void RouteMatcher_FindsSegmentOfPlannedRoute()
    {
        var plan = PhotogrammetryPlanner.Generate(Field, Settings());
        var target = plan.Passes[0].Segments[0];
        var midpoint = new GeoPoint(
            (target.Start.Latitude + target.End.Latitude) / 2,
            (target.Start.Longitude + target.End.Longitude) / 2);

        var assignment = RouteImageMatcher.FindNearest(midpoint, plan);

        Assert.NotNull(assignment);
        Assert.InRange(assignment.DistanceMeters, 0d, 0.01d);
    }
}
