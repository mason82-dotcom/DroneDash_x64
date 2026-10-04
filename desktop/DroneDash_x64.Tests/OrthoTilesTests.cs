using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;
using DroneDash_x64.Desktop.Planning;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class OrthoTilesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_OrthoTilesTests_" + Guid.NewGuid().ToString("N"));

    public OrthoTilesTests() => Directory.CreateDirectory(_root);

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

    private static string Metadata(
        string source,
        string template = "{z}/{x}/{y}.png",
        int minZoom = 13,
        int maxZoom = 21,
        double south = 49.02,
        double north = 49.03) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            source,
            bounds = new { south, west = 8.39, north, east = 8.40 },
            minZoom,
            maxZoom,
            tileSize = 256,
            pixelSizeMeters = 0.05,
            tiles = 103,
            template
        });

    [Fact]
    public void Parse_AcceptsWorkerMetadata()
    {
        var tiles = OrthoTileService.Parse(Metadata("ortho.tif"));

        Assert.Equal(21, tiles.MaxZoom);
        Assert.Equal(49.03, tiles.Bounds.North);
        Assert.Equal(103, tiles.Tiles);
    }

    [Theory]
    [InlineData("../{z}/{x}/{y}.png", 13, 21, 49.02, 49.03)]
    [InlineData("{z}/{x}/{y}.png", 22, 21, 49.02, 49.03)]
    [InlineData("{z}/{x}/{y}.png", 13, 30, 49.02, 49.03)]
    [InlineData("{z}/{x}/{y}.png", 13, 21, 49.03, 49.02)]
    public void Parse_RejectsInvalidMetadata(string template, int minZoom, int maxZoom, double south, double north) =>
        Assert.Throws<InvalidDataException>(() =>
            OrthoTileService.Parse(Metadata("ortho.tif", template, minZoom, maxZoom, south, north)));

    [Fact]
    public void TryLoadCached_RequiresFreshMetadataForTheSameImage()
    {
        var ortho = Path.Combine(_root, "odm_orthophoto.tif");
        File.WriteAllText(ortho, "image");
        var folder = OrthoTileService.TileFolderFor(ortho);
        Assert.Equal(Path.Combine(_root, "odm_orthophoto.tiles"), folder);

        Assert.Null(OrthoTileService.TryLoadCached(ortho));

        Directory.CreateDirectory(folder);
        var metadata = Path.Combine(folder, OrthoTileService.MetadataFileName);
        File.WriteAllText(metadata, Metadata(ortho));
        Assert.NotNull(OrthoTileService.TryLoadCached(ortho));

        // Tiles from another image do not count.
        File.WriteAllText(metadata, Metadata(Path.Combine(_root, "other.tif")));
        Assert.Null(OrthoTileService.TryLoadCached(ortho));

        // An image changed after tiling invalidates the cache.
        File.WriteAllText(metadata, Metadata(ortho));
        File.SetLastWriteTimeUtc(ortho, File.GetLastWriteTimeUtc(metadata).AddMinutes(1));
        Assert.Null(OrthoTileService.TryLoadCached(ortho));
    }

    [Fact]
    public void MapLayerMessage_UsesHostTemplateBoundsAndZooms()
    {
        var ortho = Path.Combine(_root, "ortho.tif");
        var tiles = OrthoTileService.Parse(Metadata(ortho));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            OrthoTileService.MapLayerMessage(tiles, ortho, "ortho.dronedash.local", "ortho.tif")));
        var root = json.RootElement;

        Assert.StartsWith("https://ortho.dronedash.local/{z}/{x}/{y}.png?v=", root.GetProperty("url").GetString());
        Assert.Equal(49.02, root.GetProperty("bounds")[0][0].GetDouble());
        Assert.Equal(8.40, root.GetProperty("bounds")[1][1].GetDouble());
        Assert.Equal(13, root.GetProperty("minZoom").GetInt32());
        Assert.Equal(21, root.GetProperty("maxZoom").GetInt32());
    }

    [Fact]
    public void ProjectStore_RoundTripsOrthophotoPath()
    {
        var path = Path.Combine(_root, "plan.ddplan");
        IReadOnlyList<GeoPoint> field = [new(49.0, 8.4), new(49.0, 8.404), new(49.0025, 8.404)];
        var settings = new FlightPlanSettings(
            "Ortho", DjiAircraftProfile.M3E, FlightPlanMode.Mapping2D, 80, 8, 80, 70, 0, -90, -60, false, false, 40);

        FlightPlanProjectStore.Save(path, settings, field, null, @"D:\Befliegung\odm_orthophoto.tif");
        Assert.Equal(@"D:\Befliegung\odm_orthophoto.tif", FlightPlanProjectStore.Load(path).OrthophotoPath);

        FlightPlanProjectStore.Save(path, settings, field, null, "  ");
        Assert.Null(FlightPlanProjectStore.Load(path).OrthophotoPath);
    }

    /// <summary>Worker round trip with gdal2tiles. Needs DRONEDASH_TEST_PYTHON with GDAL; skipped otherwise.</summary>
    [Fact]
    public async Task Worker_TilesOrthomosaicAndCachesTheResult()
    {
        var python = await TestPython.RequireModuleAsync("osgeo.gdal, osgeo_utils.gdal2tiles");
        var ortho = Path.Combine(_root, "ortho.tif");
        var script = Path.Combine(_root, "make.py");
        File.WriteAllText(script, $$"""
            from osgeo import gdal, osr
            import numpy as np
            gdal.UseExceptions()
            ds = gdal.GetDriverByName("GTiff").Create(r"{{ortho}}", 400, 300, 3, gdal.GDT_Byte)
            ds.SetGeoTransform((456000.0, 0.1, 0, 5430075.0, 0, -0.1))
            srs = osr.SpatialReference(); srs.ImportFromEPSG(32632); ds.SetProjection(srs.ExportToWkt())
            for b in range(3): ds.GetRasterBand(b + 1).WriteArray(np.full((300, 400), 60 * (b + 1), np.uint8))
            ds = None
            """);
        await TestPython.RunScriptAsync(python, script);

        var tiles = await OrthoTileService.LoadOrRenderAsync(
            python, TestPython.WorkerPath(), ortho, TestContext.Current.CancellationToken);

        // 10 cm at 49° N: zoom 20 is the first level at least as fine (0.098 m per tile pixel).
        Assert.Equal(20, tiles.MaxZoom);
        Assert.True(tiles.Tiles > 0);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(OrthoTileService.TileFolderFor(ortho), "20"), "*.png", SearchOption.AllDirectories));
        Assert.Equal(tiles, OrthoTileService.TryLoadCached(ortho));
    }
}
