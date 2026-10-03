using System.Globalization;
using System.IO;
using DroneDash_x64.Desktop.Photogrammetry;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class ElevationAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_ElevationAnalysisTests_" + Guid.NewGuid().ToString("N"));

    public ElevationAnalysisTests() => Directory.CreateDirectory(_root);

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

    private static readonly (double Lon, double Lat)[] Triangle =
        [(8.4, 49.0), (8.401, 49.0), (8.4005, 49.001)];

    private ElevationAnalysisContext Context(string? baseModel = null) =>
        new("python", "worker.py", Path.Combine(_root, "dsm.tif"), baseModel);

    [Fact]
    public void FormatCoordinates_UsesInvariantCultureUnderGermanLocale()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(
                "8.4,49;8.401,49;8.4005,49.001",
                ElevationAnalysisService.FormatCoordinates(Triangle, 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FormatCoordinates_RejectsTooFewOrInvalidPoints()
    {
        Assert.Throws<ArgumentException>(() => ElevationAnalysisService.FormatCoordinates(Triangle[..2], 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ElevationAnalysisService.FormatCoordinates([(8.4, 91.0), (8.5, 49.0)], 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ElevationAnalysisService.FormatCoordinates([(double.NaN, 49.0), (8.5, 49.0)], 2));
    }

    [Fact]
    public void VolumeArguments_PlaneBase()
    {
        var args = ElevationAnalysisService.VolumeArguments(Context(), Triangle, VolumeBase.Plane, null);

        Assert.Equal(["--dem-volume", "--source", Path.Combine(_root, "dsm.tif")], args.Take(3));
        Assert.Equal(["--base", "plane"], args.TakeLast(2));
    }

    [Fact]
    public void VolumeArguments_FixedBaseNeedsHeight()
    {
        Assert.Throws<ArgumentException>(() =>
            ElevationAnalysisService.VolumeArguments(Context(), Triangle, VolumeBase.Fixed, null));

        var args = ElevationAnalysisService.VolumeArguments(Context(), Triangle, VolumeBase.Fixed, 115.25);
        Assert.Equal(["--base", "fixed", "--base-height", "115.25"], args.TakeLast(4));
    }

    [Fact]
    public void VolumeArguments_DtmBaseNeedsModel()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ElevationAnalysisService.VolumeArguments(Context(), Triangle, VolumeBase.Dtm, null));

        var args = ElevationAnalysisService.VolumeArguments(Context("dtm.tif"), Triangle, VolumeBase.Dtm, null);
        Assert.Equal(["--base", "dtm", "--base-dem", "dtm.tif"], args.TakeLast(4));
    }

    [Fact]
    public void CanopyHeightPath_SitsNextToDsm()
    {
        Assert.Equal(
            Path.Combine(_root, "odm_dem", "chm.tif"),
            ElevationAnalysisService.CanopyHeightPathFor(Path.Combine(_root, "odm_dem", "dsm.tif")));
    }

    /// <summary>
    /// Full worker round trip on a synthetic UTM model: sloped plane + 8 m cone (r = 20 m).
    /// Needs DRONEDASH_TEST_PYTHON with GDAL; skipped otherwise.
    /// </summary>
    [Fact]
    public async Task Worker_ProfileVolumeAndCanopyHeightMatchAnalyticAnswers()
    {
        var python = await TestPython.RequireModuleAsync("osgeo.gdal");
        var dsm = Path.Combine(_root, "dsm.tif");
        var dtm = Path.Combine(_root, "dtm.tif");
        var coords = Path.Combine(_root, "coords.txt");
        var script = Path.Combine(_root, "make.py");
        File.WriteAllText(script, $$"""
            from osgeo import gdal, osr
            import numpy as np
            gdal.UseExceptions()
            yy, xx = np.mgrid[0:200, 0:200]
            plane = 115.0 + xx * 0.5 * 0.02 + yy * 0.5 * 0.01
            cone = np.clip(8.0 * (1 - np.hypot(xx - 100, yy - 100) / 40.0), 0, None)
            srs = osr.SpatialReference(); srs.ImportFromEPSG(25832)
            for path, z in ((r"{{dsm}}", plane + cone), (r"{{dtm}}", plane)):
                ds = gdal.GetDriverByName("GTiff").Create(path, 200, 200, 1, gdal.GDT_Float32)
                ds.SetGeoTransform((456000.0, 0.5, 0, 5430100.0, 0, -0.5)); ds.SetProjection(srs.ExportToWkt())
                ds.GetRasterBand(1).WriteArray(z.astype(np.float32)); ds = None
            wgs = osr.SpatialReference(); wgs.ImportFromEPSG(4326)
            for s in (srs, wgs): s.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
            ct = osr.CoordinateTransformation(srs, wgs)
            cx, cy = 456050.25, 5430049.75
            square = [(cx - 25, cy - 25), (cx + 25, cy - 25), (cx + 25, cy + 25), (cx - 25, cy + 25)]
            line = [(cx - 30, cy), (cx + 30, cy)]
            with open(r"{{coords}}", "w") as f:
                for pts in (square, line):
                    f.write(";".join("%.10f,%.10f" % ct.TransformPoint(x, y)[:2] for x, y in pts) + "\n")
            """);
        await TestPython.RunScriptAsync(python, script);

        var lines = File.ReadAllLines(coords);
        static (double, double)[] Parse(string text) =>
            text.Split(';').Select(p => p.Split(','))
                .Select(p => (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)))
                .ToArray();

        var context = new ElevationAnalysisContext(python, TestPython.WorkerPath(), dsm, dtm);
        var expectedCone = Math.PI * 20 * 20 * 8 / 3;   // 3351.03 m³

        var plane = await ElevationAnalysisService.VolumeAsync(context, Parse(lines[0]), VolumeBase.Plane, null, TestContext.Current.CancellationToken);
        Assert.Equal(expectedCone, plane.CutCubicMeters, expectedCone * 0.01);
        Assert.Equal(0, plane.FillCubicMeters, 1.0);
        Assert.Equal(Math.Sqrt(0.02 * 0.02 + 0.01 * 0.01) * 100, plane.PlaneSlopePercent!.Value, 0.01);

        var onDtm = await ElevationAnalysisService.VolumeAsync(context, Parse(lines[0]), VolumeBase.Dtm, null, TestContext.Current.CancellationToken);
        Assert.Equal(expectedCone, onDtm.CutCubicMeters, expectedCone * 0.01);

        var profile = await ElevationAnalysisService.ProfileAsync(context, Parse(lines[1]), TestContext.Current.CancellationToken);
        Assert.Equal(60, profile.LengthMeters, 0.1);
        Assert.True(profile.Maximum > 123.5, $"max {profile.Maximum}");

        var chm = await ElevationAnalysisService.CanopyHeightAsync(python, TestPython.WorkerPath(), dsm, dtm, TestContext.Current.CancellationToken);
        Assert.Equal(8.0, chm.Maximum!.Value, 0.01);
        Assert.Equal(expectedCone / (100 * 100), chm.Mean, 0.01);
        Assert.True(File.Exists(chm.Output));
    }
}
