using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Thermal;

var allowMissingSdk = args.Any(argument =>
    argument.Equals(
        "--allow-missing-sdk",
        StringComparison.OrdinalIgnoreCase));

var fixtureArgument = args.FirstOrDefault(argument =>
    !argument.StartsWith("--", StringComparison.Ordinal));

var fixture = fixtureArgument is not null
    ? Path.GetFullPath(fixtureArgument)
    : FindFixture("DJI_20261002154302_0001_T.JPG");

if (fixture is null || !File.Exists(fixture))
{
    Console.Error.WriteLine("M3T thermal fixture not found.");
    return 3;
}

using var sdk = new DjiThermalSdk();
if (!sdk.IsAvailable)
{
    if (allowMissingSdk)
    {
        Console.WriteLine(
            $"SKIP native DJI Thermal SDK analysis · {sdk.Status}");
        return 0;
    }

    Console.Error.WriteLine(sdk.Status);
    return 2;
}

var result = sdk.Analyze(fixture, ThermalPalette.IronRed);

if (result.Width != 640 || result.Height != 512)
{
    Console.Error.WriteLine($"Unexpected thermal dimensions: {result.Width}x{result.Height}");
    return 4;
}

if (!float.IsFinite(result.MinimumC) ||
    !float.IsFinite(result.MaximumC) ||
    !float.IsFinite(result.AverageC) ||
    !float.IsFinite(result.CenterC) ||
    !float.IsFinite(result.StandardDeviationC) ||
    !float.IsFinite(result.P05C) ||
    !float.IsFinite(result.MedianC) ||
    !float.IsFinite(result.P95C) ||
    result.Temperatures.Length != result.Width * result.Height ||
    result.ValidPixelCount <= 0 ||
    result.P05C > result.MedianC ||
    result.MedianC > result.P95C)
{
    Console.Error.WriteLine("DIRP returned an invalid temperature matrix or distribution.");
    return 5;
}

var histogram = result.BuildHistogram(64);
if (histogram.Sum(bin => bin.Count) != result.ValidPixelCount)
{
    Console.Error.WriteLine("Thermal histogram does not account for every valid pixel.");
    return 6;
}

var output = new
{
    file = Path.GetFileName(fixture),
    sdk = DjiThermalSdk.SupportedSdkVersion,
    api = result.ApiVersion,
    rjpeg = result.RjpegVersion,
    width = result.Width,
    height = result.Height,
    pixels = result.Temperatures.Length,
    minimumC = result.MinimumC,
    minimum = new { x = result.MinimumX, y = result.MinimumY },
    maximumC = result.MaximumC,
    maximum = new { x = result.MaximumX, y = result.MaximumY },
    averageC = result.AverageC,
    centerC = result.CenterC,
    validPixels = result.ValidPixelCount,
    standardDeviationC = result.StandardDeviationC,
    p05C = result.P05C,
    medianC = result.MedianC,
    p95C = result.P95C,
    histogramBins = histogram.Count,
    measurement = new
    {
        distanceM = result.DistanceM,
        humidity = result.Humidity,
        emissivity = result.Emissivity,
        reflectionC = result.ReflectionC,
        ambientC = result.AmbientTemperatureC
    }
};

Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static string? FindFixture(string fileName)
{
    foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var current = new DirectoryInfo(Path.GetFullPath(root));
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
    }

    return null;
}
