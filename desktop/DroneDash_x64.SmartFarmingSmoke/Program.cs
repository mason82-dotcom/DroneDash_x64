using System.IO;
using DroneDash_x64.Desktop.SmartFarming;

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
