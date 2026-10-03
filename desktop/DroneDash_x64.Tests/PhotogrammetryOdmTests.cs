using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.SmartFarming.Odm;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class PhotogrammetryOdmTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_PhotogrammetryOdmTests_" + Guid.NewGuid().ToString("N"));

    public PhotogrammetryOdmTests() => Directory.CreateDirectory(_root);

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

    private static PhotogrammetryImageRecord Image(string relativePath, bool gps = true) =>
        new(
            Path.GetFileName(relativePath),
            relativePath,
            1024,
            null,
            gps ? 49.0 : null,
            gps ? 8.0 : null,
            null, null, null, null, null, null, null, null, null, null,
            false, false, null, null, null, null,
            []);

    private PhotogrammetryDatasetResult Dataset(params PhotogrammetryImageRecord[] images) =>
        new(_root, null, null, images, null!);

    private static readonly IReadOnlySet<string> AllOptions = new HashSet<string>
    {
        "dsm", "dtm", "pc-quality", "feature-quality", "dem-resolution",
        "orthophoto-resolution", "pc-copc", "auto-boundary"
    };

    [Fact]
    public void SelectInputFiles_KeepsRgbAndExcludesOtherSensors()
    {
        var dataset = Dataset(
            Image("DJI_20260101120000_0001_V.JPG"),
            Image("DJI_20260101120000_0002_W.JPG"),
            Image("DJI_20260101120000_0003_T.JPG"),
            Image("DJI_20260101120000_0004_Z.JPG"),
            Image("DJI_20260101120000_0005_MS_NIR.TIF"),
            Image("DJI_20260101120000_0006_V.JPG", gps: false));

        var inputs = PhotogrammetryOdmPlan.SelectInputFiles(dataset);

        Assert.Equal(
            [
                Path.Combine(_root, "DJI_20260101120000_0001_V.JPG"),
                Path.Combine(_root, "DJI_20260101120000_0002_W.JPG")
            ],
            inputs.Files);
        Assert.Contains(inputs.Notes, note => note.Contains("Thermal"));
        Assert.Contains(inputs.Notes, note => note.Contains("Zoom"));
        Assert.Contains(inputs.Notes, note => note.Contains("Multispektral"));
        Assert.Contains(inputs.Notes, note => note.Contains("ohne GPS"));
    }

    [Fact]
    public void SelectInputFiles_PrefersJpegOverDngAndTiffOfSameCapture()
    {
        var dataset = Dataset(
            Image(Path.Combine("flight1", "DJI_0001.JPG")),
            Image(Path.Combine("flight1", "DJI_0001.DNG")),
            Image(Path.Combine("flight1", "DJI_0001.TIF")),
            Image(Path.Combine("flight2", "DJI_0001.TIF")));

        var inputs = PhotogrammetryOdmPlan.SelectInputFiles(dataset);

        Assert.Equal(
            [
                Path.Combine(_root, "flight1", "DJI_0001.JPG"),
                Path.Combine(_root, "flight2", "DJI_0001.TIF")
            ],
            inputs.Files);
        Assert.Contains(inputs.Notes, note => note.Contains("DNG"));
    }

    [Theory]
    [InlineData(PhotogrammetryOdmPreset.Fast, "low", 10d)]
    [InlineData(PhotogrammetryOdmPreset.Standard, "medium", 5d)]
    [InlineData(PhotogrammetryOdmPreset.High, "high", 2d)]
    public void BuildOptions_RequestsDemProductsPerPreset(
        PhotogrammetryOdmPreset preset,
        string pcQuality,
        double demResolution)
    {
        var result = PhotogrammetryOdmPlan.BuildOptions(preset, AllOptions);
        var options = result.Options.ToDictionary(o => o.Name, o => o.Value);

        Assert.Equal(true, options["dsm"]);
        Assert.Equal(true, options["dtm"]);
        Assert.Equal(pcQuality, options["pc-quality"]);
        Assert.Equal(demResolution, options["dem-resolution"]);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void BuildOptions_DropsUnsupportedOptionalOptions()
    {
        var result = PhotogrammetryOdmPlan.BuildOptions(
            PhotogrammetryOdmPreset.Standard,
            new HashSet<string> { "dsm", "dtm", "pc-quality" });

        Assert.Equal(["dsm", "dtm", "pc-quality"], result.Options.Select(o => o.Name));
        Assert.Contains(result.Notes, note => note.Contains("pc-copc"));
    }

    [Fact]
    public void BuildOptions_FailsWithoutDemSupport()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PhotogrammetryOdmPlan.BuildOptions(
                PhotogrammetryOdmPreset.Standard,
                new HashSet<string> { "pc-quality" }));
    }

    [Fact]
    public void Locate_FindsProductsAndPrefersCopc()
    {
        foreach (var relative in new[]
                 {
                     Path.Combine("odm_dem", "dsm.tif"),
                     Path.Combine("odm_dem", "dtm.tif"),
                     Path.Combine("odm_orthophoto", "odm_orthophoto.tif"),
                     Path.Combine("odm_georeferencing", "odm_georeferenced_model.laz"),
                     Path.Combine("odm_georeferencing", "odm_georeferenced_model.copc.laz")
                 })
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        var products = OdmElevationProducts.Locate(_root);

        Assert.EndsWith(Path.Combine("odm_dem", "dsm.tif"), products.DsmPath);
        Assert.EndsWith(Path.Combine("odm_dem", "dtm.tif"), products.DtmPath);
        Assert.EndsWith("odm_orthophoto.tif", products.OrthophotoPath);
        Assert.EndsWith("odm_georeferenced_model.copc.laz", products.PointCloudPath);
        Assert.Equal(PointCloudFormat.Copc, products.CloudFormat);
        Assert.Empty(products.Warnings);
        Assert.Equal(4, products.Files.Count());
    }

    [Fact]
    public void Locate_WarnsAboutMissingProducts()
    {
        var products = OdmElevationProducts.Locate(_root);

        Assert.False(products.HasAny);
        Assert.Equal(4, products.Warnings.Count);
    }

    [Theory]
    [InlineData("dsm.tif", ProjectArtifactKind.ElevationModel)]
    [InlineData("dtm.tif", ProjectArtifactKind.ElevationModel)]
    [InlineData("odm_orthophoto.tif", ProjectArtifactKind.Orthomosaic)]
    [InlineData("odm_georeferenced_model.copc.laz", ProjectArtifactKind.PointCloud)]
    [InlineData("cloud.las", ProjectArtifactKind.PointCloud)]
    [InlineData("ndvi.tif", ProjectArtifactKind.VegetationRaster)]
    public void ClassifyFile_RecognizesOdmProducts(string fileName, ProjectArtifactKind expected)
    {
        Assert.Equal(expected, ProjectArtifactService.ClassifyFile(Path.Combine(_root, fileName)));
    }

    [Fact]
    public async Task CreateTaskAsync_InitsUploadsEachFileAndCommits()
    {
        var files = Enumerable.Range(1, 3)
            .Select(i =>
            {
                var path = Path.Combine(_root, $"DJI_000{i}_V.JPG");
                File.WriteAllBytes(path, [1, 2, 3]);
                return path;
            })
            .ToArray();

        var handler = new FakeNodeOdm();
        using var client = new NodeOdmClient("http://nodeodm.test:3000", "secret", handler);

        var uuid = await client.CreateTaskAsync(
            files,
            "unit",
            PhotogrammetryOdmPlan.BuildOptions(PhotogrammetryOdmPreset.Fast, AllOptions).Options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("task-1", uuid);
        Assert.Equal(
            ["POST task/new/init", "POST task/new/upload/task-1", "POST task/new/upload/task-1",
             "POST task/new/upload/task-1", "POST task/new/commit/task-1"],
            handler.Calls);
        Assert.All(handler.Queries, query => Assert.Contains("token=secret", query));

        using var options = JsonDocument.Parse(handler.InitOptionsJson!);
        var names = options.RootElement.EnumerateArray().Select(o => o.GetProperty("name").GetString()).ToArray();
        Assert.Contains("dsm", names);
        Assert.Contains("dtm", names);
    }

    [Fact]
    public async Task GetSupportedOptionNamesAsync_ReadsOptionList()
    {
        var handler = new FakeNodeOdm();
        using var client = new NodeOdmClient("http://nodeodm.test:3000", null, handler);

        var names = await client.GetSupportedOptionNamesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { "dsm", "dtm", "pc-copc" }, names);
    }

    private sealed class FakeNodeOdm : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];
        public string? InitOptionsJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Calls.Add($"{request.Method} {path}");
            Queries.Add(request.RequestUri.Query);

            if (path == "options")
                return Json("""[{"name":"dsm"},{"name":"dtm"},{"name":"pc-copc"}]""");

            if (path == "task/new/init")
            {
                var form = (MultipartFormDataContent)request.Content!;
                foreach (var part in form)
                {
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "options")
                        InitOptionsJson = await part.ReadAsStringAsync(cancellationToken);
                }

                return Json("""{"uuid":"task-1"}""");
            }

            return Json("""{"success":true}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
