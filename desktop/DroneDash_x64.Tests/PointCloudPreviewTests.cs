using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class PointCloudPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_PointCloudPreviewTests_" + Guid.NewGuid().ToString("N"));

    public PointCloudPreviewTests() => Directory.CreateDirectory(_root);

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
        long points = 4,
        bool hasColor = true,
        string positions = "positions.f32",
        string? colors = "colors.u8",
        string? classification = "classification.u8") =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            source,
            sourceCrs = (string?)null,
            lasVersion = "1.2",
            pointFormat = 2,
            totalPoints = points * 5,
            points,
            stride = 5,
            center = new[] { 456100.0, 5430075.0, 120.0 },
            min = new[] { -100.0, -75.0, -5.0 },
            max = new[] { 100.0, 75.0, 5.0 },
            hasColor,
            classes = new[] { 2, 6 },
            files = new { positions, colors, classification }
        });

    private (string Cloud, string Folder) CreateCachedPreview(long points = 4, long positionFloats = 12)
    {
        var cloud = Path.Combine(_root, "odm_georeferencing", "odm_georeferenced_model.laz");
        Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
        File.WriteAllText(cloud, "laz");
        File.SetLastWriteTimeUtc(cloud, DateTime.UtcNow.AddMinutes(-10));

        var folder = PointCloudPreviewService.PreviewFolderFor(cloud);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "positions.f32"), new byte[positionFloats * sizeof(float)]);
        File.WriteAllBytes(Path.Combine(folder, "colors.u8"), new byte[points * 3]);
        File.WriteAllBytes(Path.Combine(folder, "classification.u8"), new byte[points]);
        File.WriteAllText(Path.Combine(folder, PointCloudPreviewService.MetadataFileName), Metadata(cloud, points));
        return (cloud, folder);
    }

    [Theory]
    [InlineData("odm_georeferenced_model.laz", "odm_georeferenced_model.viewer")]
    [InlineData("odm_georeferenced_model.copc.laz", "odm_georeferenced_model.copc.viewer")]
    public void PreviewFolder_KeepsLazAndCopcApart(string file, string folder)
    {
        Assert.Equal(
            Path.Combine(_root, folder),
            PointCloudPreviewService.PreviewFolderFor(Path.Combine(_root, file)));
    }

    [Fact]
    public void Parse_ReadsWorkerMetadata()
    {
        var preview = PointCloudPreviewService.Parse(Metadata("cloud.laz"));

        Assert.Equal(4, preview.Points);
        Assert.Equal(456100.0, preview.Center[0]);
        Assert.Equal([2, 6], preview.Classes);
        Assert.Equal("colors.u8", preview.Files.Colors);
    }

    [Theory]
    [InlineData("../positions.f32", "colors.u8", null)]
    [InlineData("positions.f32", "sub/colors.u8", null)]
    [InlineData("positions.f32", "colors.u8", "..\\classification.u8")]
    [InlineData("", "colors.u8", null)]
    public void Parse_RejectsFilesOutsidePreviewFolder(string positions, string? colors, string? classification)
    {
        Assert.Throws<InvalidDataException>(() =>
            PointCloudPreviewService.Parse(Metadata("cloud.laz", positions: positions, colors: colors, classification: classification)));
    }

    [Fact]
    public void Parse_RejectsColorFlagWithoutColorFile()
    {
        Assert.Throws<InvalidDataException>(() =>
            PointCloudPreviewService.Parse(Metadata("cloud.laz", hasColor: true, colors: null)));
    }

    [Fact]
    public void TryLoadCached_UsesCompletePreview()
    {
        var (cloud, _) = CreateCachedPreview();

        Assert.NotNull(PointCloudPreviewService.TryLoadCached(cloud));
    }

    [Fact]
    public void TryLoadCached_IgnoresTruncatedPositions()
    {
        var (cloud, _) = CreateCachedPreview(positionFloats: 11);

        Assert.Null(PointCloudPreviewService.TryLoadCached(cloud));
    }

    [Fact]
    public void TryLoadCached_IgnoresPreviewOlderThanCloud()
    {
        var (cloud, _) = CreateCachedPreview();
        File.SetLastWriteTimeUtc(cloud, DateTime.UtcNow.AddMinutes(10));

        Assert.Null(PointCloudPreviewService.TryLoadCached(cloud));
    }

    [Fact]
    public async Task RenderAsync_ProducesPreviewFromLaz()
    {
        var python = await TestPython.RequireModuleAsync("laspy, lazrs");
        var cloud = Path.Combine(_root, "cloud.laz");
        var script = Path.Combine(_root, "make.py");
        File.WriteAllText(script, $$"""
            import laspy, numpy as np
            n = 30000
            h = laspy.LasHeader(point_format=2, version="1.2"); h.scales = [0.001] * 3; h.offsets = [456000, 5430000, 0]
            las = laspy.LasData(h)
            las.x = 456000 + np.linspace(0, 100, n); las.y = 5430000 + np.linspace(0, 50, n); las.z = 110 + np.linspace(0, 5, n)
            las.classification = np.full(n, 2, np.uint8)
            las.red = las.green = las.blue = np.full(n, 65535, np.uint16)
            las.write(r"{{cloud}}")
            """);
        await TestPython.RunScriptAsync(python, script);

        var preview = await PointCloudPreviewService.RenderAsync(
            python, TestPython.WorkerPath(), cloud, 10_000, TestContext.Current.CancellationToken);

        Assert.Equal(30_000, preview.TotalPoints);
        Assert.Equal(3, preview.Stride);
        Assert.Equal(10_000, preview.Points);
        Assert.Equal(456050.0, preview.Center[0], 0.01);
        Assert.Equal([2], preview.Classes);
        Assert.NotNull(PointCloudPreviewService.TryLoadCached(cloud));
    }
}
