using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class DemPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_DemPreviewTests_" + Guid.NewGuid().ToString("N"));

    public DemPreviewTests() => Directory.CreateDirectory(_root);

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

    private static string Metadata(string source, int width = 2, int height = 3, string image = "dem-preview.png") =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            source,
            sourceCrs = "ETRS89 / UTM zone 32N",
            sourceWidth = 400,
            sourceHeight = 300,
            width,
            height,
            bounds = new { south = 49.0, west = 8.0, north = 49.1, east = 8.1 },
            mercatorBounds = new { minX = 1.0, minY = 2.0, maxX = 3.0, maxY = 4.0 },
            groundPixelSizeMeters = 0.5,
            minimum = 100.0,
            maximum = 120.0,
            mean = 110.0,
            displayMinimum = 101.0,
            displayMaximum = 119.0,
            validPixels = 6,
            image,
            grid = "dem-grid.f32",
            gridType = "float32-le"
        });

    private (string Dem, string Folder) CreateCachedPreview(int gridFloats = 6)
    {
        var dem = Path.Combine(_root, "odm_dem", "dsm.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(dem)!);
        File.WriteAllText(dem, "tif");
        File.SetLastWriteTimeUtc(dem, DateTime.UtcNow.AddMinutes(-10));

        var folder = DemPreviewService.PreviewFolderFor(dem);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "dem-preview.png"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "dem-grid.f32"), new byte[gridFloats * sizeof(float)]);
        File.WriteAllText(Path.Combine(folder, DemPreviewService.MetadataFileName), Metadata(dem));
        return (dem, folder);
    }

    [Fact]
    public void PreviewFolder_SitsNextToModel()
    {
        var dem = Path.Combine(_root, "odm_dem", "dtm.tif");

        Assert.Equal(Path.Combine(_root, "odm_dem", "dtm.preview"), DemPreviewService.PreviewFolderFor(dem));
    }

    [Fact]
    public void Parse_ReadsWorkerMetadata()
    {
        var preview = DemPreviewService.Parse(Metadata("dsm.tif"));

        Assert.Equal(2, preview.Width);
        Assert.Equal(49.1, preview.Bounds.North);
        Assert.Equal(3.0, preview.MercatorBounds.MaxX);
        Assert.Equal("ETRS89 / UTM zone 32N", preview.SourceCrs);
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("sub/dem-preview.png")]
    [InlineData("..\\evil.png")]
    [InlineData("C:evil.png")]
    [InlineData("")]
    public void Parse_RejectsFileNamesOutsidePreviewFolder(string image)
    {
        Assert.Throws<InvalidDataException>(() => DemPreviewService.Parse(Metadata("dsm.tif", image: image)));
    }

    [Fact]
    public void Parse_RejectsUnknownSchema()
    {
        var json = Metadata("dsm.tif").Replace("\"schemaVersion\":1", "\"schemaVersion\":2");

        Assert.Throws<InvalidDataException>(() => DemPreviewService.Parse(json));
    }

    [Fact]
    public void TryLoadCached_UsesCompleteFreshPreview()
    {
        var (dem, _) = CreateCachedPreview();

        Assert.NotNull(DemPreviewService.TryLoadCached(dem));
    }

    [Fact]
    public void TryLoadCached_IgnoresPreviewOlderThanModel()
    {
        var (dem, _) = CreateCachedPreview();
        File.SetLastWriteTimeUtc(dem, DateTime.UtcNow.AddMinutes(10));

        Assert.Null(DemPreviewService.TryLoadCached(dem));
    }

    [Fact]
    public void TryLoadCached_IgnoresTruncatedGrid()
    {
        var (dem, _) = CreateCachedPreview(gridFloats: 5);

        Assert.Null(DemPreviewService.TryLoadCached(dem));
    }

    [Fact]
    public void TryLoadCached_IgnoresPreviewOfOtherModel()
    {
        var (dem, folder) = CreateCachedPreview();
        File.WriteAllText(
            Path.Combine(folder, DemPreviewService.MetadataFileName),
            Metadata(Path.Combine(_root, "other.tif")));

        Assert.Null(DemPreviewService.TryLoadCached(dem));
    }

    /// <summary>
    /// End-to-end through the real worker; needs Python with GDAL, so it only runs when
    /// DRONEDASH_TEST_PYTHON points at such an interpreter.
    /// </summary>
    [Fact]
    public async Task RenderAsync_ProducesPreviewFromGeoTiff()
    {
        var python = await TestPython.RequireModuleAsync("osgeo.gdal");

        var worker = TestPython.WorkerPath();
        var dem = Path.Combine(_root, "dsm.tif");

        // Build a 40 x 30 UTM 32N test model with GDAL itself.
        var script = Path.Combine(_root, "make.py");
        File.WriteAllText(script, $$"""
            from osgeo import gdal, osr
            import numpy as np
            gdal.UseExceptions()
            ds = gdal.GetDriverByName("GTiff").Create(r"{{dem}}", 40, 30, 1, gdal.GDT_Float32)
            ds.SetGeoTransform((456000.0, 1.0, 0, 5430030.0, 0, -1.0))
            srs = osr.SpatialReference(); srs.ImportFromEPSG(25832); ds.SetProjection(srs.ExportToWkt())
            band = ds.GetRasterBand(1); band.SetNoDataValue(-9999)
            band.WriteArray((100 + np.arange(40)[None, :] * 0.5 + np.zeros((30, 1))).astype(np.float32))
            ds = None
            """);
        await TestPython.RunScriptAsync(python, script);

        var preview = await DemPreviewService.RenderAsync(
            python, worker, dem, 256, TestContext.Current.CancellationToken);

        Assert.Equal(100d, preview.Minimum, 0.5);
        Assert.Equal(119.5d, preview.Maximum, 0.5);
        Assert.InRange(preview.Bounds.West, 8.39, 8.41);
        Assert.InRange(preview.Bounds.North, 49.02, 49.03);
        Assert.Equal(1d, preview.GroundPixelSizeMeters, 0.05);
        Assert.NotNull(DemPreviewService.TryLoadCached(dem));
    }
}
