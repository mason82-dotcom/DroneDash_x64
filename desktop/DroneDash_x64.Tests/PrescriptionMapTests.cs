using System.IO;
using DroneDash_x64.Desktop.SmartFarming;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class PrescriptionMapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_PrescriptionMapTests_" + Guid.NewGuid().ToString("N"));

    public PrescriptionMapTests() => Directory.CreateDirectory(_root);

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
    public void FormatRates_IsInvariantAndComplete()
    {
        Assert.Equal("1:140,2:120.5,3:100,4:80,5:0", PrescriptionMapService.FormatRates([140, 120.5, 100, 80, 0]));
        Assert.Throws<ArgumentException>(() => PrescriptionMapService.FormatRates([1, 2, 3, 4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrescriptionMapService.FormatRates([1, 2, -3, 4, 5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrescriptionMapService.FormatRates([1, 2, double.NaN, 4, 5]));
    }

    [Theory]
    [InlineData("Weizen Schlag 7 (N2)", "Weizen_Schlag_7__N2")]
    [InlineData("../../evil", "evil")]
    [InlineData("äöü", "prescription")]
    [InlineData("", "prescription")]
    public void SafeName_KeepsTerminalFriendlyCharacters(string name, string expected) =>
        Assert.Equal(expected, PrescriptionMapService.SafeName(name));

    [Fact]
    public async Task CreateAsync_RejectsUnusableMachineGrid() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PrescriptionMapService.CreateAsync(
            "python", "worker.py", "zones.tif", _root, "x", [1, 1, 1, 1, 1], 0.5, "kg/ha",
            TestContext.Current.CancellationToken));

    [Fact]
    public void SummaryText_ListsZonesWithAreaAndTotal()
    {
        var result = new PrescriptionResult(
            "zones.tif", 12, "kg/ha", "rx.shp", "rx.geojson", 4,
            [new(1, 140, 0, 0), new(2, 120, 1.5, 180), new(3, 100, 2.0, 200), new(4, 80, 0, 0), new(5, 60, 0.5, 30)],
            4.0, 410);

        var text = result.SummaryText;
        Assert.DoesNotContain("Zone 1", text);
        Assert.Contains("Zone 2: 1,50 ha × 120,0 kg/ha = 180,0", text);
        Assert.Contains("Gesamt: 4,00 ha · Menge 410,0", text);
    }

    /// <summary>Worker round trip with GDAL/OGR. Needs DRONEDASH_TEST_PYTHON with GDAL; skipped otherwise.</summary>
    [Fact]
    public async Task Worker_WritesShapefileAndGeoJsonWithRates()
    {
        var python = await TestPython.RequireModuleAsync("osgeo.gdal, osgeo.ogr");
        var zones = Path.Combine(_root, "ndvi_scouting_zones.tif");
        var script = Path.Combine(_root, "make.py");
        File.WriteAllText(script, $$"""
            from osgeo import gdal, osr
            import numpy as np
            gdal.UseExceptions()
            z = np.zeros((200, 400), np.uint8); z[:, :200] = 1; z[:, 200:] = 3
            srs = osr.SpatialReference(); srs.ImportFromEPSG(32632)
            ds = gdal.GetDriverByName("GTiff").Create(r"{{zones}}", 400, 200, 1, gdal.GDT_Byte)
            ds.SetGeoTransform((456000.0, 0.5, 0, 5430100.0, 0, -0.5)); ds.SetProjection(srs.ExportToWkt())
            ds.GetRasterBand(1).SetNoDataValue(0); ds.GetRasterBand(1).WriteArray(z); ds = None
            """);
        await TestPython.RunScriptAsync(python, script);

        var result = await PrescriptionMapService.CreateAsync(
            python, TestPython.WorkerPath(), zones, Path.Combine(_root, "rx"), "Schlag 7",
            [150, 120, 100, 80, 60], 10, "kg/ha", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_root, "rx", "Schlag_7.shp"), result.Shapefile);
        Assert.True(File.Exists(result.Geojson));
        Assert.Equal(1.0, result.Zones.Single(z => z.Zone == 1).AreaHectares, 6);
        Assert.Equal(1.0 * 150 + 1.0 * 100, result.TotalAmount, 6);
        Assert.Contains("\"RATE\": 150", File.ReadAllText(result.Geojson).Replace("150.0", "150"));
    }
}
