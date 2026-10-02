using System.IO;
using System.IO.Compression;
using DroneDash_x64.Desktop.SmartFarming;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using DroneDash_x64.Desktop.SmartFarming.Odm;

if (!M3mCaptureKeyParser.TryParse(
        "DJI_20261003120000_0042_MS_NIR.TIF",
        out var captureKey,
        out var kind) ||
    captureKey != "DJI_20261003120000_0042" ||
    kind != "MS_NIR")
{
    throw new InvalidDataException("M3M filename grouping failed.");
}

var corrected = M3mVegetationIndexEngine.CorrectDn(
    rawDn: 40000,
    bitsPerSample: 16,
    blackLevel: 512,
    sensorGain: 2,
    exposureTimeMicroseconds: 2000,
    sensorGainAdjustment: 0.8,
    irradiance: 10000);

if (!float.IsFinite(corrected) || corrected <= 0)
    throw new InvalidDataException("M3M radiometric compensation failed.");

const int width = 4;
const int height = 2;
var nir = Enumerable.Repeat(0.8f, width * height).ToArray();
var red = Enumerable.Repeat(0.2f, width * height).ToArray();
var redEdge = Enumerable.Repeat(0.4f, width * height).ToArray();
var green = Enumerable.Repeat(0.5f, width * height).ToArray();

var ndvi = M3mVegetationIndexEngine.ComputeFromSignals(
    "TEST", VegetationIndexType.Ndvi, nir, red, width, height);
var ndre = M3mVegetationIndexEngine.ComputeFromSignals(
    "TEST", VegetationIndexType.Ndre, nir, redEdge, width, height);
var gndvi = M3mVegetationIndexEngine.ComputeFromSignals(
    "TEST", VegetationIndexType.Gndvi, nir, green, width, height);

AssertNear(ndvi.Average, 0.6, 1e-5, "NDVI");
AssertNear(ndre.Average, 1d / 3d, 1e-5, "NDRE");
AssertNear(gndvi.Average, 3d / 13d, 1e-5, "GNDVI");

if (ndvi.IsOrthorectified || ndvi.IsBandCoregistered ||
    ndvi.UsesReflectancePanelCalibration)
{
    throw new InvalidDataException("Quicklook quality flags must remain conservative.");
}

Console.WriteLine(
    $"PASS Smart Farming · corrected={corrected:F6} · " +
    $"NDVI={ndvi.Average:F3} · NDRE={ndre.Average:F3} · GNDVI={gndvi.Average:F3}");

static void AssertNear(double actual, double expected, double tolerance, string label)
{
    if (Math.Abs(actual - expected) > tolerance)
        throw new InvalidDataException(
            $"{label} expected {expected:F6}, got {actual:F6}.");
}


var noIssues = Array.Empty<string>();
M3mBandMetadata MakeBand(string path, M3mBand band) =>
    new(
        path,
        band,
        2592,
        1944,
        16,
        512,
        2,
        2000,
        0.8,
        10000,
        band.ToString(),
        49.0,
        8.0,
        "RTK",
        noIssues);

var fakeCapture = new M3mCaptureGroup(
    "DJI_20261003120000_0042",
    @"C:\m3m\DJI_20261003120000_0042_D.JPG",
    MakeBand(@"C:\m3m\DJI_20261003120000_0042_MS_G.TIF", M3mBand.Green),
    MakeBand(@"C:\m3m\DJI_20261003120000_0042_MS_R.TIF", M3mBand.Red),
    MakeBand(@"C:\m3m\DJI_20261003120000_0042_MS_RE.TIF", M3mBand.RedEdge),
    MakeBand(@"C:\m3m\DJI_20261003120000_0042_MS_NIR.TIF", M3mBand.Nir),
    noIssues);

var fakeToolchain = new LocalImageToolchainStatus(
    new(LocalImageToolKind.Gdal, "GDAL", true, "3.x", @"C:\tools\gdalbuildvrt.exe", "test"),
    new(LocalImageToolKind.OrfeoToolBox, "Orfeo ToolBox", true, "10.0", @"C:\tools\otbcli_BandMath.exe", "test"),
    new(LocalImageToolKind.PythonOpenCv, "Python + OpenCV", true, "4.x", @"C:\Python\python.exe", "test"),
    @"C:\tools\gdalbuildvrt.exe",
    @"C:\tools\otbcli_BandMath.exe",
    @"C:\tools\otbcli_BandMathX.exe",
    @"C:\Python\python.exe",
    @"C:\DroneDash\smart-farming\opencv_m3m.py");

var localPlan = LocalProcessingPlanBuilder.Build(
    fakeCapture,
    Path.Combine(Path.GetTempPath(), "DroneDash_SmartFarming_LocalProcessing"),
    fakeToolchain);

if (localPlan.Steps.Count != 11)
    throw new InvalidDataException(
        $"Expected 11 local processing steps, got {localPlan.Steps.Count}.");

if (localPlan.Steps.Count(step => step.Tool == "Python/OpenCV") != 3 ||
    localPlan.Steps.Count(step => step.Tool == "GDAL") != 1 ||
    localPlan.Steps.Count(step => step.Tool == "Orfeo ToolBox") != 7)
{
    throw new InvalidDataException("Unexpected local processing tool distribution.");
}

if (!localPlan.Steps.Any(step =>
        step.Id == "index-ndvi" &&
        step.Arguments.Contains("(im1b4-im1b2)/(im1b4+im1b2+1e-12)")))
{
    throw new InvalidDataException("NDVI local-processing expression missing.");
}

Console.WriteLine(
    $"PASS local processing plan · steps={localPlan.Steps.Count} · " +
    $"warnings={localPlan.Warnings.Count}");


var odmDataset = new M3mDatasetResult(
    @"C:\m3m",
    [fakeCapture],
    new M3mDatasetSummary(1, 1, 1, 1, 1, 1, 1, 1, 0));

var odmInputs = NodeOdmClient.GetM3mInputFiles(odmDataset);
if (odmInputs.Count != 4 ||
    odmInputs.Any(path => path.EndsWith("_D.JPG", StringComparison.OrdinalIgnoreCase)))
{
    throw new InvalidDataException(
        "NodeODM M3M input selection must contain exactly four multispectral TIFFs.");
}

var odmOptions = NodeOdmClient.BuildM3mOptions("camera+sun");
if (!odmOptions.Any(option =>
        option.Name == "radiometric-calibration" &&
        Equals(option.Value, "camera+sun")) ||
    !odmOptions.Any(option =>
        option.Name == "primary-band" &&
        Equals(option.Value, "NIR")))
{
    throw new InvalidDataException(
        "NodeODM M3M options are incomplete.");
}

var supportedNode = new NodeOdmServerInfo(
    true,
    "http://127.0.0.1:3000/",
    "2.2.1",
    "odm",
    "3.5.3",
    0,
    null,
    8,
    null,
    "test");

if (!supportedNode.SupportsMavic3M)
    throw new InvalidDataException(
        "ODM 3.5.3 must be marked M3M-capable.");

var unsupportedNode =
    supportedNode with { EngineVersion = "3.5.2" };

if (unsupportedNode.SupportsMavic3M)
    throw new InvalidDataException(
        "ODM 3.5.2 must not be marked M3M-capable.");

var runnerRoot = Path.Combine(
    Path.GetTempPath(),
    "DroneDash_LocalRunner_" +
    Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(runnerRoot);

try
{
    var runnerPlan = new LocalProcessingPlan(
        LocalProcessingPlan.CurrentSchemaVersion,
        DateTimeOffset.UtcNow,
        "RUNNER-SMOKE",
        runnerRoot,
        [
            new LocalProcessingCommand(
                "dotnet-version",
                ".NET",
                "dotnet",
                ["--version"],
                null,
                "Verify direct process execution")
        ],
        []);

    var runnerResult =
        await LocalProcessingRunner.RunAsync(
            runnerPlan);

    if (!runnerResult.Success ||
        runnerResult.CompletedSteps != 1 ||
        !File.Exists(runnerResult.LogPath))
    {
        throw new InvalidDataException(
            "LocalProcessingRunner smoke test failed.");
    }

    Console.WriteLine(
        $"PASS local runner · log={Path.GetFileName(runnerResult.LogPath)}");
}
finally
{
    try
    {
        Directory.Delete(
            runnerRoot,
            recursive: true);
    }
    catch
    {
    }
}


var describedGdalInfo = """
{
  "driverShortName": "GTiff",
  "size": [1200, 900],
  "coordinateSystem": {
    "wkt": "PROJCRS[\"WGS 84 / UTM zone 32N\",BASEGEOGCRS[\"WGS 84\"]]"
  },
  "bands": [
    { "band": 1, "description": "Green" },
    { "band": 2, "description": "NIR" },
    { "band": 3, "description": "Red Edge" },
    { "band": 4, "description": "Red" }
  ]
}
""";

var describedOrthophoto =
    OdmOrthophotoInspector.ParseGdalInfoJson(
        @"C:\odm\odm_orthophoto.tif",
        describedGdalInfo,
        allowM3mFallback: false);

if (describedOrthophoto.BandMap.RedBand != 4 ||
    describedOrthophoto.BandMap.GreenBand != 1 ||
    describedOrthophoto.BandMap.NirBand != 2 ||
    describedOrthophoto.BandMap.RedEdgeBand != 3)
{
    throw new InvalidDataException(
        "GDAL band descriptions were not mapped correctly.");
}

var fallbackGdalInfo = """
{
  "driverShortName": "GTiff",
  "size": [1200, 900],
  "bands": [
    { "band": 1 },
    { "band": 2 },
    { "band": 3 },
    { "band": 4 }
  ]
}
""";

var fallbackOrthophoto =
    OdmOrthophotoInspector.ParseGdalInfoJson(
        @"C:\odm\odm_orthophoto.tif",
        fallbackGdalInfo,
        allowM3mFallback: true);

if (fallbackOrthophoto.BandMap.RedBand != 1 ||
    fallbackOrthophoto.BandMap.GreenBand != 2 ||
    fallbackOrthophoto.BandMap.NirBand != 3 ||
    fallbackOrthophoto.BandMap.RedEdgeBand != 4 ||
    fallbackOrthophoto.Warnings.Count == 0)
{
    throw new InvalidDataException(
        "ODM M3M normalized-band fallback is incorrect.");
}

var zones = NdviScoutingZoneSettings.Default;
var zoneExpression =
    OdmFieldProductPlanBuilder.BuildZoneExpression(zones);

if (!zoneExpression.Contains(
        "im1b1<0.2?1",
        StringComparison.Ordinal) ||
    !zoneExpression.EndsWith(
        "?4:5)))",
        StringComparison.Ordinal))
{
    throw new InvalidDataException(
        "NDVI scouting-zone expression is incorrect.");
}

var fieldPlan =
    OdmFieldProductPlanBuilder.Build(
        describedOrthophoto,
        Path.Combine(
            Path.GetTempPath(),
            "DroneDash_FieldProducts_Smoke"),
        fakeToolchain,
        zones);

if (fieldPlan.Steps.Count != 4 ||
    !fieldPlan.Steps.Any(step =>
        step.Id == "odm-ndvi" &&
        step.Arguments.Contains(
            "(im1b2-im1b4)/(im1b2+im1b4+1e-12)")) ||
    !fieldPlan.Steps.Any(step =>
        step.Id == "odm-ndre" &&
        step.Arguments.Contains(
            "(im1b2-im1b3)/(im1b2+im1b3+1e-12)")) ||
    !fieldPlan.Steps.Any(step =>
        step.Id == "odm-gndvi" &&
        step.Arguments.Contains(
            "(im1b2-im1b1)/(im1b2+im1b1+1e-12)")))
{
    throw new InvalidDataException(
        "ODM field-product plan does not use the mapped bands.");
}

var zipSmokeRoot =
    Path.Combine(
        Path.GetTempPath(),
        "DroneDash_OdmZip_" +
        Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(zipSmokeRoot);

try
{
    var safeZip =
        Path.Combine(
            zipSmokeRoot,
            "all.zip");

    using (var archive =
           ZipFile.Open(
               safeZip,
               ZipArchiveMode.Create))
    {
        var entry =
            archive.CreateEntry(
                "odm_orthophoto/odm_orthophoto.tif");

        using var writer =
            new StreamWriter(entry.Open());

        writer.Write("synthetic");
    }

    var extracted =
        OdmResultImporter.ExtractSafely(
            safeZip,
            Path.Combine(
                zipSmokeRoot,
                "safe"));

    var foundOrthophoto =
        OdmResultImporter.FindOrthophoto(
            extracted);

    if (!File.Exists(foundOrthophoto))
        throw new InvalidDataException(
            "Safe ODM ZIP extraction did not find the orthophoto.");

    var maliciousZip =
        Path.Combine(
            zipSmokeRoot,
            "malicious.zip");

    using (var archive =
           ZipFile.Open(
               maliciousZip,
               ZipArchiveMode.Create))
    {
        var entry =
            archive.CreateEntry(
                "../escape.txt");

        using var writer =
            new StreamWriter(entry.Open());

        writer.Write("blocked");
    }

    var rejected = false;

    try
    {
        OdmResultImporter.ExtractSafely(
            maliciousZip,
            Path.Combine(
                zipSmokeRoot,
                "malicious"));
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    if (!rejected)
        throw new InvalidDataException(
            "ZIP path traversal was not rejected.");

    Console.WriteLine(
        $"PASS ODM field products · {describedOrthophoto.BandMap.ToDisplayText()} · {zones.LegendText}");
}
finally
{
    try
    {
        Directory.Delete(
            zipSmokeRoot,
            recursive: true);
    }
    catch
    {
    }
}
