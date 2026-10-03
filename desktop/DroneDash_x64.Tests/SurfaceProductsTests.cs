using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class SurfaceProductsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_SurfaceProductsTests_" + Guid.NewGuid().ToString("N"));

    public SurfaceProductsTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void OutputPaths_SitNextToTheModelsAndNameBothSurveys()
    {
        var dsm = Path.Combine(_root, "2026-09", "odm_dem", "dsm.tif");
        var older = Path.Combine(_root, "2026-05", "odm_dem", "dsm.tif");

        Assert.Equal(Path.Combine(_root, "2026-09", "odm_dem", "dsm-ground.tif"), ElevationAnalysisService.DerivedDtmPathFor(dsm));
        Assert.Equal(
            Path.Combine(_root, "2026-09", "odm_dem", "dsm-minus-odm_dem-dsm.tif"),
            ElevationAnalysisService.DifferencePathFor(dsm, older));
        Assert.Equal(
            Path.Combine(_root, "2026-09", "odm_dem", "dsm-minus-Halde_Mai_2026.tif"),
            ElevationAnalysisService.DifferencePathFor(dsm, Path.Combine(_root, "Halde Mai 2026.tif")));
    }

    [Fact]
    public async Task DifferenceAsync_RejectsTheSameModelTwice()
    {
        var dsm = Path.Combine(_root, "dsm.tif");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ElevationAnalysisService.DifferenceAsync(
                "python", "worker.py", dsm, Path.Combine(_root, ".", "dsm.tif"),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string PreviewJson(string? palette) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["source"] = "dsm.tif", ["sourceCrs"] = "UTM", ["sourceWidth"] = 10,
            ["sourceHeight"] = 10, ["width"] = 10, ["height"] = 10,
            ["bounds"] = new { south = 49.0, west = 8.0, north = 49.1, east = 8.1 },
            ["mercatorBounds"] = new { minX = 0.0, minY = 0.0, maxX = 1.0, maxY = 1.0 },
            ["groundPixelSizeMeters"] = 0.5, ["minimum"] = -1.0, ["maximum"] = 2.0, ["mean"] = 0.1,
            ["displayMinimum"] = -2.0, ["displayMaximum"] = 2.0, ["validPixels"] = 100,
            ["image"] = "dem-preview.png", ["grid"] = "dem-grid.f32", ["gridType"] = "float32-le",
            ["palette"] = palette
        });

    [Fact]
    public void DemPreview_ReadsPaletteAndTreatsMissingAsTerrain()
    {
        Assert.Equal(DemPreview.DivergingPalette, DemPreviewService.Parse(PreviewJson("diverging")).EffectivePalette);
        Assert.Equal(DemPreview.TerrainPalette, DemPreviewService.Parse(PreviewJson(null)).EffectivePalette);
        Assert.Throws<InvalidDataException>(() => DemPreviewService.Parse(PreviewJson("rainbow")));
    }

    [Fact]
    public void DemPreview_CacheOnlyServesTheRequestedPalette()
    {
        var dem = Path.Combine(_root, "diff.tif");
        File.WriteAllText(dem, "x");
        var folder = DemPreviewService.PreviewFolderFor(dem);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "dem-preview.png"), "png");
        File.WriteAllBytes(Path.Combine(folder, "dem-grid.f32"), new byte[10 * 10 * 4]);
        File.WriteAllText(
            Path.Combine(folder, DemPreviewService.MetadataFileName),
            PreviewJson("diverging").Replace("\"dsm.tif\"", JsonSerializer.Serialize(dem)));

        Assert.NotNull(DemPreviewService.TryLoadCached(dem, DemPreview.DivergingPalette));
        Assert.Null(DemPreviewService.TryLoadCached(dem));
    }

    /// <summary>
    /// Worker round trip: DSM with an 8 m block → derived DTM → canopy height of the block, and
    /// the difference to a survey without it. Needs DRONEDASH_TEST_PYTHON with GDAL.
    /// </summary>
    [Fact]
    public async Task Worker_DerivesGroundAndMeasuresChange()
    {
        var python = await TestPython.RequireModuleAsync("osgeo.gdal");
        var before = Path.Combine(_root, "before", "dsm.tif");
        var after = Path.Combine(_root, "after", "dsm.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(before)!);
        Directory.CreateDirectory(Path.GetDirectoryName(after)!);
        var script = Path.Combine(_root, "make.py");
        File.WriteAllText(script, $$"""
            from osgeo import gdal, osr
            import numpy as np
            gdal.UseExceptions()
            yy, xx = np.mgrid[0:200, 0:200] * 0.5 + 0.25
            ground = 100.0 + 0.03 * xx
            block = (np.abs(xx - 50) < 10) & (np.abs(yy - 50) < 5)   # 20 x 10 m, 8 m high = 1600 m3
            srs = osr.SpatialReference(); srs.ImportFromEPSG(32632)
            for path, z in ((r"{{before}}", ground), (r"{{after}}", ground + np.where(block, 8.0, 0.0))):
                ds = gdal.GetDriverByName("GTiff").Create(path, 200, 200, 1, gdal.GDT_Float32)
                ds.SetGeoTransform((456000.0, 0.5, 0, 5430100.0, 0, -0.5)); ds.SetProjection(srs.ExportToWkt())
                ds.GetRasterBand(1).SetNoDataValue(-9999); ds.GetRasterBand(1).WriteArray(z.astype(np.float32)); ds = None
            """);
        await TestPython.RunScriptAsync(python, script);
        var worker = TestPython.WorkerPath();
        var token = TestContext.Current.CancellationToken;

        var dtm = await ElevationAnalysisService.DtmFromDsmAsync(python, worker, after, cellMeters: 0.5, cancellationToken: token);
        Assert.Equal(ElevationAnalysisService.DerivedDtmPathFor(after), dtm.Output);
        Assert.InRange(dtm.GroundFraction, 0.9, 0.99);

        var chm = await ElevationAnalysisService.CanopyHeightAsync(python, worker, after, dtm.Output, token);
        Assert.Equal(8.0, chm.Maximum!.Value, 0.3);

        var diff = await ElevationAnalysisService.DifferenceAsync(python, worker, after, before, cancellationToken: token);
        Assert.Equal(1600.0, diff.RaisedCubicMeters!.Value, 1.0);
        Assert.Equal(0.0, diff.LoweredCubicMeters!.Value, 0.01);
        Assert.Equal(200.0, diff.RaisedAreaSquareMeters!.Value, 0.01);

        var preview = await DemPreviewService.RenderAsync(python, worker, diff.Output, cancellationToken: token, palette: DemPreview.DivergingPalette);
        Assert.Equal(DemPreview.DivergingPalette, preview.EffectivePalette);
        Assert.Equal(-preview.DisplayMaximum, preview.DisplayMinimum, 6);
    }
}
