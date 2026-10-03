using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.PvAnalysis;
using DroneDash_x64.Desktop.Thermal;

if (args.Length > 0 &&
    string.Equals(
        args[0],
        "--fixture",
        StringComparison.OrdinalIgnoreCase))
{
    return RunRealFixture(args);
}


const int width = 96;
const int height = 64;

var settings = new PvAnalysisSettings(
    WarningDeltaC: 5,
    CriticalDeltaC: 15,
    LocalWindowRadiusPixels: 8,
    MinimumClusterPixels: 6);

var temperatures = new float[width * height];

for (var y = 0; y < height; y++)
{
    for (var x = 0; x < width; x++)
    {
        temperatures[y * width + x] =
            33f +
            x * 0.035f +
            y * 0.02f;
    }
}

// One isolated hot pixel must be rejected by the minimum cluster size.
temperatures[10 * width + 10] = 70f;

// A compact real hotspot on top of a smooth thermal gradient.
for (var y = 28; y <= 31; y++)
{
    for (var x = 46; x <= 49; x++)
    {
        temperatures[y * width + x] += 19f;
    }
}

var candidates = PvThermalAnomalyDetector.Detect(
    temperatures,
    width,
    height,
    settings);

if (candidates.Count != 1)
{
    throw new InvalidDataException(
        $"Expected one robust PV anomaly cluster, got {candidates.Count}.");
}

var hotspot = candidates[0];

if (hotspot.Severity != PvAnomalySeverity.Critical)
{
    throw new InvalidDataException(
        $"Expected Critical severity, got {hotspot.Severity}.");
}

if (hotspot.PixelCount != 16)
{
    throw new InvalidDataException(
        $"Expected 16 hotspot pixels, got {hotspot.PixelCount}.");
}

if (hotspot.DeltaC < 15)
{
    throw new InvalidDataException(
        $"Expected robust ΔT >= 15 °C, got {hotspot.DeltaC:F2}.");
}

if (hotspot.LocalUpperQuartileC <
    hotspot.LocalBaselineC)
{
    throw new InvalidDataException(
        "Expected local P75 to be >= local median.");
}

if (hotspot.AdaptiveSeedThresholdC <
    settings.WarningDeltaC)
{
    throw new InvalidDataException(
        "Adaptive seed threshold fell below configured warning threshold.");
}

if (hotspot.AreaPixels != 16 ||
    hotspot.BoundingBoxAreaPixels != 16 ||
    Math.Abs(hotspot.FillRatio - 1d) > 0.0001d)
{
    throw new InvalidDataException(
        $"Unexpected hotspot geometry: area={hotspot.AreaPixels}, " +
        $"bbox={hotspot.BoundingBoxAreaPixels}, fill={hotspot.FillRatio:F3}.");
}

if (hotspot.PeakX is < 46 or > 49 ||
    hotspot.PeakY is < 28 or > 31)
{
    throw new InvalidDataException(
        $"Peak coordinate outside expected hotspot: {hotspot.PixelText}.");
}

// A pure gradient with no real hotspot must stay clean.
var gradientOnly =
    new float[width * height];

for (var y = 0; y < height; y++)
{
    for (var x = 0; x < width; x++)
    {
        gradientOnly[y * width + x] =
            30f +
            x * 0.07f +
            y * 0.04f;
    }
}

var gradientCandidates =
    PvThermalAnomalyDetector.Detect(
        gradientOnly,
        width,
        height,
        settings);

if (gradientCandidates.Count != 0)
{
    throw new InvalidDataException(
        $"Expected no anomalies in a smooth thermal gradient, got {gradientCandidates.Count}.");
}

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
    temperatures.Min(),
    temperatures.Max(),
    temperatures.Average(value => (double)value),
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
    "DroneDash_PvSmoke_" +
    Guid.NewGuid().ToString("N"));

try
{
    var output =
        PvAnalysisExporter.Export(
            exportRoot,
            dataset);

    foreach (var path in new[]
    {
        output.JsonPath,
        output.ImagesCsvPath,
        output.CandidatesCsvPath
    })
    {
        if (!File.Exists(path) ||
            new FileInfo(path).Length == 0)
        {
            throw new InvalidDataException(
                $"Missing PV export: {path}");
        }
    }

    var candidatesCsv =
        File.ReadAllText(
            output.CandidatesCsvPath);

    if (!candidatesCsv.Contains(
            "localMedianC",
            StringComparison.Ordinal) ||
        !candidatesCsv.Contains(
            "localUpperQuartileC",
            StringComparison.Ordinal) ||
        !candidatesCsv.Contains(
            "fillRatio",
            StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "PV candidates CSV is missing robust baseline or geometry fields.");
    }

    var reportPath =
        PvInspectionReportExporter.ExportHtml(
            exportRoot,
            dataset);

    if (!File.Exists(reportPath) ||
        new FileInfo(reportPath).Length == 0)
    {
        throw new InvalidDataException(
            "Missing PV HTML inspection report.");
    }

    var reportHtml =
        File.ReadAllText(
            reportPath);

    if (!reportHtml.Contains(
            "Critical",
            StringComparison.Ordinal) ||
        !reportHtml.Contains(
            "DJI_TEST_T.JPG",
            StringComparison.Ordinal) ||
        !reportHtml.Contains(
            "Anomalie-Kandidaten",
            StringComparison.Ordinal) ||
        !reportHtml.Contains(
            "lokaler Median",
            StringComparison.OrdinalIgnoreCase) ||
        !reportHtml.Contains(
            "P75",
            StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "PV HTML report content is incomplete.");
    }

    Console.WriteLine(
        $"PASS robust PV analysis · candidates={candidates.Count} · " +
        $"severity={hotspot.Severity} · delta={hotspot.DeltaC:F2} °C · " +
        $"baseline={hotspot.LocalBaselineC:F2} °C · p75={hotspot.LocalUpperQuartileC:F2} °C · " +
        $"area={hotspot.AreaPixels}px² · fill={hotspot.FillRatio:P0}");
}
finally
{
    try
    {
        Directory.Delete(
            exportRoot,
            recursive: true);
    }
    catch
    {
        // Best-effort CI temp cleanup.
    }
}


static int RunRealFixture(
    string[] arguments)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine(
            "Usage: DroneDash_x64.PvSmoke --fixture <DJI_RJPEG>");
        return 64;
    }

    var fixture =
        Path.GetFullPath(
            arguments[1]);

    if (!File.Exists(fixture))
    {
        Console.Error.WriteLine(
            $"M3T fixture not found: {fixture}");
        return 3;
    }

    using var sdk =
        new DjiThermalSdk();

    if (!sdk.IsAvailable)
    {
        Console.Error.WriteLine(
            sdk.Status);
        return 2;
    }

    var thermal =
        sdk.Analyze(
            fixture,
            ThermalPalette.IronRed);

    var settings =
        PvAnalysisSettings.Default;

    var candidates =
        PvThermalAnomalyDetector.Detect(
            thermal.Temperatures,
            thermal.Width,
            thermal.Height,
            settings);

    foreach (var candidate in candidates)
    {
        if (candidate.PixelCount <= 0 ||
            candidate.PeakX < 0 ||
            candidate.PeakY < 0 ||
            candidate.PeakX >= thermal.Width ||
            candidate.PeakY >= thermal.Height ||
            candidate.MinX < 0 ||
            candidate.MinY < 0 ||
            candidate.MaxX >= thermal.Width ||
            candidate.MaxY >= thermal.Height ||
            candidate.MinX > candidate.MaxX ||
            candidate.MinY > candidate.MaxY ||
            !double.IsFinite(candidate.PeakTemperatureC) ||
            !double.IsFinite(candidate.LocalBaselineC) ||
            !double.IsFinite(candidate.LocalUpperQuartileC) ||
            !double.IsFinite(candidate.AdaptiveSeedThresholdC) ||
            !double.IsFinite(candidate.DeltaC) ||
            candidate.LocalUpperQuartileC <
                candidate.LocalBaselineC ||
            candidate.AdaptiveSeedThresholdC <
                settings.WarningDeltaC ||
            candidate.DeltaC <
                settings.WarningDeltaC ||
            candidate.FillRatio <= 0d ||
            candidate.FillRatio > 1d)
        {
            Console.Error.WriteLine(
                $"Invalid PV candidate #{candidate.Index}.");
            return 5;
        }
    }

    var output = new
    {
        file =
            Path.GetFileName(
                fixture),
        sdk =
            DjiThermalSdk.SupportedSdkVersion,
        api =
            thermal.ApiVersion,
        rjpeg =
            thermal.RjpegVersion,
        width =
            thermal.Width,
        height =
            thermal.Height,
        pixels =
            thermal.Temperatures.Length,
        minimumC =
            thermal.MinimumC,
        maximumC =
            thermal.MaximumC,
        averageC =
            thermal.AverageC,
        distribution = new
        {
            standardDeviationC =
                thermal.StandardDeviationC,
            p05C =
                thermal.P05C,
            medianC =
                thermal.MedianC,
            p95C =
                thermal.P95C
        },
        settings = new
        {
            warningDeltaC =
                settings.WarningDeltaC,
            criticalDeltaC =
                settings.CriticalDeltaC,
            localWindowRadiusPixels =
                settings.LocalWindowRadiusPixels,
            minimumClusterPixels =
                settings.MinimumClusterPixels
        },
        candidateCount =
            candidates.Count,
        candidates =
            candidates.Select(candidate => new
            {
                candidate.Index,
                severity =
                    candidate.Severity.ToString(),
                areaPixels =
                    candidate.AreaPixels,
                boundingBoxAreaPixels =
                    candidate.BoundingBoxAreaPixels,
                fillRatio =
                    candidate.FillRatio,
                equivalentDiameterPixels =
                    candidate.EquivalentDiameterPixels,
                peak = new
                {
                    x =
                        candidate.PeakX,
                    y =
                        candidate.PeakY,
                    temperatureC =
                        candidate.PeakTemperatureC
                },
                localMedianC =
                    candidate.LocalBaselineC,
                localP75C =
                    candidate.LocalUpperQuartileC,
                adaptiveSeedThresholdC =
                    candidate.AdaptiveSeedThresholdC,
                deltaC =
                    candidate.DeltaC,
                centroid = new
                {
                    x =
                        candidate.CentroidX,
                    y =
                        candidate.CentroidY
                },
                boundingBox = new
                {
                    minX =
                        candidate.MinX,
                    minY =
                        candidate.MinY,
                    maxX =
                        candidate.MaxX,
                    maxY =
                        candidate.MaxY
                }
            })
    };

    Console.WriteLine(
        "PASS real M3T PV detector");

    Console.WriteLine(
        JsonSerializer.Serialize(
            output,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }));

    return 0;
}
