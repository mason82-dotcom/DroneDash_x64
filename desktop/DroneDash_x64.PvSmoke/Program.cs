using System.IO;
using DroneDash_x64.Desktop.PvAnalysis;

const int width = 64;
const int height = 48;
var temperatures = Enumerable.Repeat(35f, width * height).ToArray();

for (var y = 20; y <= 23; y++)
{
    for (var x = 30; x <= 33; x++)
        temperatures[y * width + x] = 54f;
}

var settings = new PvAnalysisSettings(
    WarningDeltaC: 5,
    CriticalDeltaC: 15,
    LocalWindowRadiusPixels: 8,
    MinimumClusterPixels: 6);

var candidates = PvThermalAnomalyDetector.Detect(
    temperatures,
    width,
    height,
    settings);

if (candidates.Count != 1)
    throw new InvalidDataException(
        $"Expected one PV anomaly cluster, got {candidates.Count}.");

var hotspot = candidates[0];
if (hotspot.Severity != PvAnomalySeverity.Critical)
    throw new InvalidDataException(
        $"Expected Critical severity, got {hotspot.Severity}.");

if (hotspot.PixelCount != 16)
    throw new InvalidDataException(
        $"Expected 16 hotspot pixels, got {hotspot.PixelCount}.");

if (hotspot.DeltaC < 15)
    throw new InvalidDataException(
        $"Expected ΔT >= 15 °C, got {hotspot.DeltaC:F2}.");

var image = new PvImageAnalysisResult(
    "DJI_TEST_T.JPG",
    "DJI_TEST_T.JPG",
    "InfraredCamera",
    49.0,
    8.0,
    "50",
    0.01,
    0.01,
    0.02,
    0,
    "PV Survey",
    1,
    1.5,
    width,
    height,
    35,
    54,
    35.1,
    0.95,
    10,
    candidates,
    null);

var dataset = new PvDatasetResult(
    Path.GetTempPath(),
    null,
    settings,
    [image],
    new PvDatasetSummary(
        1,
        1,
        0,
        1,
        0,
        1,
        candidates.Count,
        hotspot.DeltaC));

var exportRoot = Path.Combine(
    Path.GetTempPath(),
    "DroneDash_PvSmoke_" + Guid.NewGuid().ToString("N"));

try
{
    var output = PvAnalysisExporter.Export(exportRoot, dataset);
    foreach (var path in new[]
    {
        output.JsonPath,
        output.ImagesCsvPath,
        output.CandidatesCsvPath
    })
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException($"Missing PV export: {path}");
    }

    Console.WriteLine(
        $"PASS PV analysis · candidates={candidates.Count} · " +
        $"severity={hotspot.Severity} · delta={hotspot.DeltaC:F2} °C");
}
finally
{
    try
    {
        Directory.Delete(exportRoot, recursive: true);
    }
    catch
    {
        // Best-effort CI temp cleanup.
    }
}
