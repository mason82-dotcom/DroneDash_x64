using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class ColmapPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_ColmapPipelineTests_" + Guid.NewGuid().ToString("N"));

    public ColmapPipelineTests() => Directory.CreateDirectory(_root);

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

    private const string Header391 =
        "COLMAP 3.9.1 -- Structure-from-Motion and Multi-View Stereo\n" +
        "(Commit 0a1b2c3 on 2024-01-05 with CUDA)\n\nUsage:\n  colmap [command] [options]\n";

    // Option names of COLMAP 3.9 – 3.11.
    private static readonly IReadOnlySet<string> ClassicOptions = new HashSet<string>
    {
        "database_path", "image_path", "image_list_path", "ImageReader.camera_model",
        "ImageReader.camera_params", "SiftExtraction.use_gpu", "SiftExtraction.max_image_size",
        "SiftMatching.use_gpu", "SpatialMatching.is_gps", "SpatialMatching.max_num_neighbors",
        "Mapper.ba_refine_focal_length", "Mapper.ba_refine_principal_point",
        "Mapper.ba_refine_extra_params", "max_image_size", "PatchMatchStereo.geom_consistency",
        "PatchMatchStereo.max_image_size", "StereoFusion.max_image_size"
    };

    private static ColmapStatus Colmap(bool? cuda = true, IReadOnlySet<string>? options = null) =>
        new(true, "/opt/colmap/bin/colmap", "3.9.1", cuda, options ?? ClassicOptions, "");

    private static PhotogrammetryImageRecord Image(
        string relativePath,
        int index,
        double? focal = 3725.15,
        double? cx = 2640.0,
        double? cy = 1978.0,
        double? altitude = 155.0) =>
        new(
            Path.GetFileName(relativePath),
            relativePath,
            1024,
            null,
            49.0 + index * 1e-4,
            8.4,
            altitude, 60.0, null, null, null, null, null, null, null,
            focal,
            cx is not null && cy is not null, true, null, null, null, null,
            [])
        {
            OpticalCenterX = cx,
            OpticalCenterY = cy
        };

    private PhotogrammetryDatasetResult Dataset(params PhotogrammetryImageRecord[] images) =>
        new(_root, null, null, images, null!);

    private PhotogrammetryDatasetResult ThreeImages() =>
        Dataset(
            Image("DJI_0001_V.JPG", 0),
            Image("DJI_0002_V.JPG", 1),
            Image("DJI_0003_V.JPG", 2));

    private ColmapProcessingPlan Build(
        PhotogrammetryDatasetResult dataset,
        ColmapStatus? colmap = null,
        PhotogrammetryOdmPreset preset = PhotogrammetryOdmPreset.Standard) =>
        ColmapPlanBuilder.Build(
            dataset,
            colmap ?? Colmap(),
            "python",
            "opencv_m3m.py",
            Path.Combine(_root, "ws"),
            preset);

    private static IReadOnlyList<string> Args(ColmapProcessingPlan plan, string id) =>
        plan.Plan.Steps.Single(step => step.Id == id).Arguments;

    private static string? Value(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf("--" + option);
        return index < 0 ? null : arguments[index + 1];
    }

    [Fact]
    public void ParseHeader_ReadsVersionAndCuda()
    {
        Assert.Equal(("3.9.1", (bool?)true), ColmapToolchain.ParseHeader(Header391));
        Assert.Equal(
            ("3.11.1", (bool?)false),
            ColmapToolchain.ParseHeader("COLMAP 3.11.1 -- Structure-from-Motion\n(Commit Unknown on Unknown without CUDA)"));
        Assert.Equal(("3.12", (bool?)null), ColmapToolchain.ParseHeader("COLMAP 3.12 -- Structure-from-Motion"));
        Assert.Equal((null, (bool?)null), ColmapToolchain.ParseHeader("'colmap' is not recognized"));
    }

    [Fact]
    public void ParseOptions_ReadsOptionNames()
    {
        const string help =
            "Options can either be specified via command-line or by defining\n" +
            "  -h [ --help ]\n" +
            "  --database_path arg\n" +
            "  --SiftExtraction.use_gpu arg (=1)\n" +
            "  --ImageReader.camera_model arg (=SIMPLE_RADIAL)\n";

        var options = ColmapToolchain.ParseOptions(help);

        Assert.Equal(
            new[] { "ImageReader.camera_model", "SiftExtraction.use_gpu", "database_path" }.Order(),
            options.Order());
    }

    [Fact]
    public void Build_CreatesFullPipelineWithCalibrationAndGpu()
    {
        var plan = Build(ThreeImages());

        Assert.Equal(
            ["georef", "features", "matching", "mapper", "undistort", "stereo", "fusion", "products"],
            plan.Plan.Steps.Select(step => step.Id));

        var features = Args(plan, "features");
        Assert.Equal("RADIAL", Value(features, "ImageReader.camera_model"));
        Assert.Equal("3725.15,2640,1978,0,0", Value(features, "ImageReader.camera_params"));
        Assert.Equal("1", Value(features, "SiftExtraction.use_gpu"));
        Assert.Equal("3200", Value(features, "SiftExtraction.max_image_size"));
        Assert.Equal(Path.GetFullPath(_root), Value(features, "image_path"));

        Assert.Equal("1", Value(Args(plan, "matching"), "SiftMatching.use_gpu"));
        Assert.Equal("1", Value(Args(plan, "matching"), "SpatialMatching.is_gps"));

        // Known calibration: bundle adjustment must not rescale the focal length.
        var mapper = Args(plan, "mapper");
        Assert.Equal("0", Value(mapper, "Mapper.ba_refine_focal_length"));
        Assert.Equal("0", Value(mapper, "Mapper.ba_refine_principal_point"));
        Assert.Equal("1", Value(mapper, "Mapper.ba_refine_extra_params"));

        Assert.Equal("true", Value(Args(plan, "stereo"), "PatchMatchStereo.geom_consistency"));
        Assert.Equal("geometric", Value(Args(plan, "fusion"), "input_type"));

        var products = Args(plan, "products");
        Assert.Equal("--colmap-products", products[1]);
        Assert.Equal("0.1", Value(products, "resolution"));
        Assert.Equal("10", Value(products, "fill-distance"));
        Assert.Equal(plan.DsmPath, plan.Plan.Steps.Last().OutputPath);
        Assert.Empty(plan.Plan.Warnings);
    }

    [Fact]
    public void Build_UsesRenamedOptionsOfNewerColmap()
    {
        var options = ClassicOptions
            .Select(name => name
                .Replace("SiftExtraction.use_gpu", "FeatureExtraction.use_gpu")
                .Replace("SiftMatching.use_gpu", "FeatureMatching.use_gpu"))
            .ToHashSet();

        var plan = Build(ThreeImages(), Colmap(options: options));

        Assert.Equal("1", Value(Args(plan, "features"), "FeatureExtraction.use_gpu"));
        Assert.Null(Value(Args(plan, "features"), "SiftExtraction.use_gpu"));
        Assert.Equal("1", Value(Args(plan, "matching"), "FeatureMatching.use_gpu"));
        Assert.Empty(plan.Plan.Warnings);
    }

    [Fact]
    public void Build_UsesClassicStereoNamesWhenStereoHelpIsUnreadable()
    {
        // COLMAP prints no patch_match_stereo help without CUDA; the names then fall back.
        var options = ClassicOptions.Where(name => !name.StartsWith("PatchMatchStereo.")).ToHashSet();

        var plan = Build(ThreeImages(), Colmap(options: options));

        Assert.Equal("true", Value(Args(plan, "stereo"), "PatchMatchStereo.geom_consistency"));
        Assert.Empty(plan.Plan.Warnings);
    }

    [Fact]
    public void Build_WarnsAndSkipsUnknownOption()
    {
        var options = ClassicOptions.Where(name => name != "SpatialMatching.max_num_neighbors").ToHashSet();

        var plan = Build(ThreeImages(), Colmap(options: options));

        Assert.Null(Value(Args(plan, "matching"), "SpatialMatching.max_num_neighbors"));
        Assert.Contains(plan.Plan.Warnings, warning => warning.Contains("SpatialMatching.max_num_neighbors"));
    }

    [Fact]
    public void Build_RequiresCuda()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(ThreeImages(), Colmap(cuda: false)));
        Assert.Contains("CUDA", error.Message);
        Assert.Throws<InvalidOperationException>(() => Build(ThreeImages(), Colmap(cuda: null)));
    }

    [Fact]
    public void Build_WithoutCommonCalibrationRefinesFocalAndWarns()
    {
        var plan = Build(Dataset(
            Image("DJI_0001_V.JPG", 0),
            Image("DJI_0002_V.JPG", 1, focal: 3000),
            Image("DJI_0003_V.JPG", 2)));

        Assert.Null(Value(Args(plan, "features"), "ImageReader.camera_params"));
        Assert.Equal("1", Value(Args(plan, "mapper"), "Mapper.ba_refine_focal_length"));
        Assert.Contains(plan.Plan.Warnings, warning => warning.Contains("Kamerakalibrierung"));
    }

    [Fact]
    public void CommonCalibration_ToleratesRoundingAndRejectsMissingValues()
    {
        Assert.Equal(
            (3725.15, 2640.0, 1978.0),
            ColmapPlanBuilder.CommonCalibration([Image("a.jpg", 0), Image("b.jpg", 1, focal: 3725.4)]));
        Assert.Null(ColmapPlanBuilder.CommonCalibration([Image("a.jpg", 0), Image("b.jpg", 1, cx: null)]));
        Assert.Null(ColmapPlanBuilder.CommonCalibration([]));
    }

    [Fact]
    public void Build_UsesForwardSlashNamesAndSkipsPathsWithSpaces()
    {
        var plan = Build(Dataset(
            Image(Path.Combine("flight1", "DJI_0001_V.JPG"), 0),
            Image(Path.Combine("flight1", "DJI_0002_V.JPG"), 1),
            Image(Path.Combine("flight1", "DJI_0003_V.JPG"), 2),
            Image(Path.Combine("flight 2", "DJI_0004_V.JPG"), 3),
            Image("DJI_0005_T.JPG", 4)));

        Assert.Equal(
            ["flight1/DJI_0001_V.JPG", "flight1/DJI_0002_V.JPG", "flight1/DJI_0003_V.JPG"],
            plan.Images.Select(image => image.Name));
        Assert.Contains(plan.Plan.Warnings, warning => warning.Contains("Leerzeichen"));
        Assert.Contains(plan.Plan.Warnings, warning => warning.Contains("Thermal"));
    }

    [Fact]
    public void Build_NeedsThreeImagesWithAltitude()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(Dataset(
            Image("DJI_0001_V.JPG", 0),
            Image("DJI_0002_V.JPG", 1, altitude: null),
            Image("DJI_0003_V.JPG", 2, altitude: null))));
        Assert.Contains("Höhe", error.Message);

        Assert.Throws<InvalidOperationException>(() => Build(Dataset(Image("DJI_0001_V.JPG", 0))));
    }

    [Theory]
    [InlineData(PhotogrammetryOdmPreset.Fast, "1600", "1200", "0.2", "5")]
    [InlineData(PhotogrammetryOdmPreset.High, "4800", "3200", "0.05", "20")]
    public void Build_PresetsSetImageSizesAndResolution(
        PhotogrammetryOdmPreset preset,
        string featureSize,
        string denseSize,
        string resolution,
        string fillDistance)
    {
        var plan = Build(ThreeImages(), preset: preset);

        Assert.Equal(featureSize, Value(Args(plan, "features"), "SiftExtraction.max_image_size"));
        Assert.Equal(denseSize, Value(Args(plan, "undistort"), "max_image_size"));
        Assert.Equal(denseSize, Value(Args(plan, "stereo"), "PatchMatchStereo.max_image_size"));
        Assert.Equal(resolution, Value(Args(plan, "products"), "resolution"));
        Assert.Equal(fillDistance, Value(Args(plan, "products"), "fill-distance"));
    }

    [Fact]
    public void PrepareWorkspace_WritesInputsAndRemovesOldDatabase()
    {
        var plan = Build(ThreeImages());
        Directory.CreateDirectory(plan.Plan.Workspace);
        var database = Path.Combine(plan.Plan.Workspace, "database.db");
        File.WriteAllText(database, "old");

        ColmapPlanBuilder.PrepareWorkspace(plan);

        Assert.False(File.Exists(database));
        Assert.Equal(
            ["DJI_0001_V.JPG", "DJI_0002_V.JPG", "DJI_0003_V.JPG"],
            File.ReadAllLines(plan.ImageListPath));
        Assert.True(Directory.Exists(plan.ProductsFolder));
        Assert.True(File.Exists(Path.Combine(plan.Plan.Workspace, "colmap-plan.json")));

        using var references = JsonDocument.Parse(File.ReadAllText(plan.ReferenceImagesPath));
        var first = references.RootElement[0];
        Assert.Equal("DJI_0001_V.JPG", first.GetProperty("name").GetString());
        Assert.Equal(49.0, first.GetProperty("lat").GetDouble(), 9);
        Assert.Equal(8.4, first.GetProperty("lon").GetDouble(), 9);
        Assert.Equal(155.0, first.GetProperty("alt").GetDouble(), 9);
    }

    [Fact]
    public void ProductsReport_ParsesQualityAndWarnsOnLargeResiduals()
    {
        const string json = """
            {"schemaVersion": 1, "points": 123456, "resolution": 0.1,
             "georeferencing": {"registeredImages": 40, "imagesWithReference": 40, "usedImages": 38,
                                "scale": 2.5, "rmseMeters": 1.4, "maxResidualMeters": 2.9,
                                "rejected": ["a.jpg", "b.jpg"]}}
            """;

        var report = ColmapProductsReport.Parse(json);

        Assert.Equal(123456, report.Points);
        Assert.Equal(38, report.UsedImages);
        Assert.Contains("RMSE 1.40 m", report.SummaryText.Replace(',', '.'));
        Assert.Contains("ungenau", report.Warning);
        Assert.Contains("2 Bilder als Ausreißer", report.Warning);

        var good = ColmapProductsReport.Parse(json.Replace("1.4,", "0.05,").Replace("\"a.jpg\", \"b.jpg\"", ""));
        Assert.Null(good.Warning);
    }
}
