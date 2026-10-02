using System.Text.Json;
using DroneDash_x64.Desktop.Thermal;

var fixture = args.Length > 0
    ? Path.GetFullPath(args[0])
    : FindFixture("DJI_20261002154302_0001_T.JPG");

if (fixture is null || !File.Exists(fixture))
{
    Console.Error.WriteLine("M3T thermal fixture not found.");
    return 3;
}

using var sdk = new DjiThermalSdk();
if (!sdk.IsAvailable)
{
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
    result.Temperatures.Length != result.Width * result.Height)
{
    Console.Error.WriteLine("DIRP returned an invalid temperature matrix.");
    return 5;
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
