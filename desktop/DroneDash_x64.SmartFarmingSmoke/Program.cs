using System.IO;
using DroneDash_x64.Desktop.SmartFarming;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

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
