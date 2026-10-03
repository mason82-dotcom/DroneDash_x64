using System.Globalization;
using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Planning;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class TerrainCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_TerrainCheckTests_" + Guid.NewGuid().ToString("N"));

    public TerrainCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly IReadOnlyList<GeoPoint> Field =
    [
        new(49.0000, 8.4000),
        new(49.0000, 8.4040),
        new(49.0025, 8.4040),
        new(49.0025, 8.4000)
    ];

    private static FlightPlanSettings Settings(double altitude = 80, bool follow = false) =>
        new("Terrain", DjiAircraftProfile.M3E, FlightPlanMode.Mapping2D, altitude, 8, 80, 70, 0, -90, -60, false, follow, 40);

    private static TerrainCheckOptions Options(GeoPoint? takeOff = null, double? elevation = null) =>
        new(takeOff, elevation, 30, 10);

    [Fact]
    public void BuildRequestJson_ChainsSegmentsIncludingConnectingLegs()
    {
        var segment1 = new RouteSegment(new GeoPoint(49.0, 8.40), new GeoPoint(49.0, 8.41), 700);
        var segment2 = new RouteSegment(new GeoPoint(49.001, 8.41), new GeoPoint(49.001, 8.40), 700);
        var segment3 = new RouteSegment(new GeoPoint(49.001, 8.40), new GeoPoint(49.002, 8.40), 110);
        var plan = PhotogrammetryPlanner.Generate(Field, Settings()) with
        {
            Passes = [new FlightPass(1, "Nadir", 0, -90, false, [segment1, segment2, segment3])]
        };

        using var json = JsonDocument.Parse(TerrainCheckService.BuildRequestJson(plan, Options(new GeoPoint(48.9995, 8.3995), 101.5)));
        var root = json.RootElement;

        Assert.Equal(80, root.GetProperty("altitude").GetDouble());
        Assert.Equal("relative", root.GetProperty("mode").GetString());
        Assert.Equal(30, root.GetProperty("minClearance").GetDouble());
        Assert.Equal(10, root.GetProperty("buffer").GetDouble());
        Assert.Equal(101.5, root.GetProperty("startElevation").GetDouble());
        Assert.Equal(8.3995, root.GetProperty("start")[0].GetDouble());
        Assert.Equal(48.9995, root.GetProperty("start")[1].GetDouble());

        var points = root.GetProperty("passes")[0].GetProperty("points")
            .EnumerateArray()
            .Select(p => (p[0].GetDouble(), p[1].GetDouble()))
            .ToArray();
        // segment2 starts where segment1 ends only after the turn leg; segment3 starts at segment2's end.
        Assert.Equal(
            [(8.40, 49.0), (8.41, 49.0), (8.41, 49.001), (8.40, 49.001), (8.40, 49.002)],
            points);
    }

    [Fact]
    public void BuildRequestJson_UsesFollowModeAndOmitsUnsetStart()
    {
        var plan = PhotogrammetryPlanner.Generate(Field, Settings(follow: true));

        using var json = JsonDocument.Parse(TerrainCheckService.BuildRequestJson(plan, Options()));

        Assert.Equal("follow", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("start").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("startElevation").ValueKind);
        Assert.Equal(plan.Passes.Count, json.RootElement.GetProperty("passes").GetArrayLength());
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(30, -1)]
    [InlineData(30, 500)]
    [InlineData(double.NaN, 10)]
    public void BuildRequestJson_RejectsInvalidOptions(double clearance, double buffer)
    {
        var plan = PhotogrammetryPlanner.Generate(Field, Settings());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TerrainCheckService.BuildRequestJson(plan, new TerrainCheckOptions(null, null, clearance, buffer)));
    }

    [Fact]
    public void Parse_ReadsWorkerResultAndSummarizes()
    {
        const string json = """
            {"schemaVersion": 1, "dem": "dsm.tif", "mode": "relative", "altitude": 80.0, "minClearance": 30.0,
             "bufferMeters": 10.0, "cellMeters": 0.5, "stepMeters": 0.5, "startElevation": 100.0,
             "startSource": "model", "flightElevation": 180.0, "lengthMeters": 1234.5, "samples": 2470,
             "coverage": 0.95, "terrainRange": [99.5, 120.0], "obstacleMax": 165.0,
             "minimum": {"clearance": -2.5, "obstacle": 182.5, "distance": 300.0, "lon": 8.4, "lat": 49.0, "pass": "Nadir"},
             "passes": [{"id": 1, "name": "Nadir", "lengthMeters": 1234.5, "minimumClearance": -2.5}],
             "violations": [
               {"pass": "Nadir", "fromDistance": 280.0, "toDistance": 320.0, "minimumClearance": -2.5,
                "obstacle": 182.5, "lon": 8.4, "lat": 49.0, "collision": true},
               {"pass": "Nadir", "fromDistance": 900.0, "toDistance": 910.0, "minimumClearance": 25.0,
                "obstacle": 155.0, "lon": 8.41, "lat": 49.0, "collision": false}],
             "violationsTruncated": false,
             "chunks": [{"status": "ok", "pass": "Nadir", "points": [[49.0, 8.4], [49.0, 8.41]]}]}
            """;

        var result = TerrainCheckService.Parse(json);

        Assert.True(result.HasCollision);
        Assert.False(result.IsClear);
        Assert.Equal(-2.5, result.Minimum!.Clearance);
        Assert.Equal([49.0, 8.41], result.Chunks[0].Points[1]);
        var summary = result.SummaryText;
        Assert.Contains("Flughöhe 180,0 m", summary);
        Assert.Contains("2 Abschnitt(e) unter 30 m", summary);
        Assert.Contains("1 mit Kollision", summary);
        Assert.Contains("95 %", summary);
    }

    private static TerrainCheckResult Result(double altitude, double? minimumClearance, double required = 30) =>
        new("relative", altitude, required, 10, 0.5, 100, "model", 100 + altitude, 1000, 2000, 1.0, [99, 120], 160,
            minimumClearance is { } c ? new TerrainMinimum(c, 160, 10, 8.4, 49, "Nadir") : null,
            [], [], false, []);

    [Theory]
    [InlineData(80, 20, 90)]      // 10 m missing
    [InlineData(80, 29.4, 81)]    // rounded up to whole metres
    [InlineData(80, -15, 125)]    // collision
    public void RequiredAltitude_AddsMissingClearance(double altitude, double clearance, double expected) =>
        Assert.Equal(expected, TerrainCheckService.RequiredAltitude(Result(altitude, clearance)));

    [Fact]
    public void RequiredAltitude_IsNullWhenClearOrUnknown()
    {
        Assert.Null(TerrainCheckService.RequiredAltitude(Result(80, 30)));
        Assert.Null(TerrainCheckService.RequiredAltitude(Result(80, 55)));
        Assert.Null(TerrainCheckService.RequiredAltitude(Result(80, null)));
    }

    [Fact]
    public async Task RaiseAltitudeAsync_ReplansAndRechecksUntilClear()
    {
        var plan = PhotogrammetryPlanner.Generate(Field, Settings(altitude: 80));
        var altitudes = new List<double>();
        // A re-planned route shifts its lines, so the first raise falls 2 m short.
        var shortfall = 2.0;

        var adjustment = await TerrainCheckService.RaiseAltitudeAsync(
            plan,
            Result(80, 20),
            altitude =>
            {
                altitudes.Add(altitude);
                return PhotogrammetryPlanner.Generate(Field, Settings(altitude: altitude));
            },
            replanned =>
            {
                var altitude = replanned.Settings.AltitudeMeters;
                var clearance = altitude - 60 - shortfall;
                shortfall = 0;
                return Task.FromResult(Result(altitude, clearance));
            });

        Assert.Equal([90.0, 92.0], altitudes);
        Assert.True(adjustment.Satisfied);
        Assert.Equal(2, adjustment.Iterations);
        Assert.Equal(92, adjustment.Plan.Settings.AltitudeMeters);
    }

    [Fact]
    public async Task RaiseAltitudeAsync_StopsAtThePlanningLimit()
    {
        var plan = PhotogrammetryPlanner.Generate(Field, Settings(altitude: 80));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TerrainCheckService.RaiseAltitudeAsync(
                plan,
                Result(80, -500),
                _ => throw new InvalidOperationException("must not replan"),
                _ => throw new InvalidOperationException("must not check")));

        Assert.Contains("höchstens 500 m", error.Message);
    }

    [Fact]
    public void ProjectStore_RoundTripsTerrainSettings()
    {
        var path = Path.Combine(_root, "plan.ddplan");
        var terrain = new FlightPlanTerrainSettings(@"C:\Daten\dsm.tif", new GeoPoint(49.001, 8.401), 112.5, 25, 15);

        FlightPlanProjectStore.Save(path, Settings(), Field, terrain);
        var loaded = FlightPlanProjectStore.Load(path);

        Assert.Equal(terrain, loaded.Terrain);
        Assert.Equal(FlightPlanProject.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void ProjectStore_LoadsPlansWithoutTerrainAndRejectsInvalidTerrain()
    {
        var path = Path.Combine(_root, "plan.ddplan");
        FlightPlanProjectStore.Save(path, Settings(), Field);
        Assert.Null(FlightPlanProjectStore.Load(path).Terrain);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FlightPlanProjectStore.Save(path, Settings(), Field, new FlightPlanTerrainSettings(null, null, null, -5, 10)));

        FlightPlanProjectStore.Save(path, Settings(), Field, new FlightPlanTerrainSettings(null, new GeoPoint(49, 8.4), null, 30, 10));
        var json = File.ReadAllText(path);
        var takeOffIndex = json.IndexOf("\"takeOff\"", StringComparison.Ordinal);
        Assert.True(takeOffIndex > 0);
        var broken = json[..takeOffIndex] + json[takeOffIndex..].Replace("49", "95");
        File.WriteAllText(path, broken);
        Assert.Throws<InvalidDataException>(() => FlightPlanProjectStore.Load(path));
    }

    /// <summary>
    /// Worker round trip on a synthetic UTM model: flat 100 m with a 60 x 60 m block of 160 m in
    /// the middle of the field. Needs DRONEDASH_TEST_PYTHON with GDAL; skipped otherwise.
    /// </summary>
    [Fact]
    public async Task Worker_FindsBlockBelowMinimumClearanceAndCollisionWhenLow()
    {
        var python = await TestPython.RequireModuleAsync("osgeo.gdal");
        var dsm = Path.Combine(_root, "dsm.tif");
        var coords = Path.Combine(_root, "field.txt");
        var script = Path.Combine(_root, "make.py");
        File.WriteAllText(script, $$"""
            from osgeo import gdal, osr
            import numpy as np
            gdal.UseExceptions()
            z = np.full((800, 800), 100.0)
            z[340:460, 340:460] = 160.0
            srs = osr.SpatialReference(); srs.ImportFromEPSG(32632)
            ds = gdal.GetDriverByName("GTiff").Create(r"{{dsm}}", 800, 800, 1, gdal.GDT_Float32)
            ds.SetGeoTransform((456000.0, 0.5, 0, 5430400.0, 0, -0.5)); ds.SetProjection(srs.ExportToWkt())
            ds.GetRasterBand(1).WriteArray(z.astype(np.float32)); ds = None
            wgs = osr.SpatialReference(); wgs.ImportFromEPSG(4326)
            for s in (srs, wgs): s.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
            ct = osr.CoordinateTransformation(srs, wgs)
            pts = [(456050, 5430050), (456350, 5430050), (456350, 5430350), (456050, 5430350), (456020, 5430020)]
            with open(r"{{coords}}", "w") as f:
                f.write(";".join("%.10f,%.10f" % ct.TransformPoint(x, y)[:2] for x, y in pts))
            """);
        await TestPython.RunScriptAsync(python, script);

        var points = File.ReadAllText(coords).Split(';')
            .Select(p => p.Split(','))
            .Select(p => new GeoPoint(double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[0], CultureInfo.InvariantCulture)))
            .ToArray();
        var field = points[..4];
        var takeOff = points[4];

        var plan = PhotogrammetryPlanner.Generate(field, Settings(altitude: 80));
        var result = await TerrainCheckService.RunAsync(
            python, TestPython.WorkerPath(), dsm, plan, Options(takeOff), TestContext.Current.CancellationToken);

        Assert.Equal(100.0, result.StartElevation, 0.01);
        Assert.Equal(180.0, result.FlightElevation!.Value, 0.01);
        Assert.Equal(1.0, result.Coverage);
        Assert.Equal(20.0, result.Minimum!.Clearance, 0.01);
        Assert.NotEmpty(result.Violations);
        Assert.False(result.HasCollision);
        Assert.Contains(result.Chunks, chunk => chunk.Status == "low");

        var low = PhotogrammetryPlanner.Generate(field, Settings(altitude: 50));
        var lowResult = await TerrainCheckService.RunAsync(
            python, TestPython.WorkerPath(), dsm, low, Options(takeOff), TestContext.Current.CancellationToken);

        Assert.True(lowResult.HasCollision);
        Assert.Equal(-10.0, lowResult.Minimum!.Clearance, 0.01);

        // 50 m leaves -10 m; 30 m clearance needs 90 m, and the re-planned route then passes.
        var adjustment = await TerrainCheckService.RaiseAltitudeAsync(
            low,
            lowResult,
            altitude => PhotogrammetryPlanner.Generate(field, Settings(altitude: altitude)),
            plan => TerrainCheckService.RunAsync(
                python, TestPython.WorkerPath(), dsm, plan, Options(takeOff), TestContext.Current.CancellationToken));

        Assert.True(adjustment.Satisfied);
        Assert.Equal(90, adjustment.Plan.Settings.AltitudeMeters);
        Assert.Equal(30.0, adjustment.Result.Minimum!.Clearance, 0.01);
        Assert.Empty(adjustment.Result.Violations);
    }
}
