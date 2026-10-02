using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using DroneDash_x64.Desktop.Planning;

var polygon = new[]
{
    new GeoPoint(49.0000, 8.0000),
    new GeoPoint(49.0000, 8.0060),
    new GeoPoint(49.0040, 8.0060),
    new GeoPoint(49.0040, 8.0000)
};

var mapping2D = new FlightPlanSettings(
    "CI Mapping 2D",
    DjiAircraftProfile.M3E,
    FlightPlanMode.Mapping2D,
    80, 8, 80, 70, 15, -90, -60,
    true, false, 40);

var plan2D = PhotogrammetryPlanner.Generate(polygon, mapping2D);
ValidatePlan(plan2D, expectedPasses: 1);
ValidateKmz(plan2D, "mapping2d", expectedFolders: 1, expectedTemplateToken: "smartObliqueEnable");

var mapping3D = mapping2D with
{
    Name = "CI Mapping 3D",
    Mode = FlightPlanMode.Mapping3D,
    SmartObliqueEnabled = false,
    TerrainFollowEnabled = true
};

var plan3D = PhotogrammetryPlanner.Generate(polygon, mapping3D);
ValidatePlan(plan3D, expectedPasses: 5);
ValidateKmz(plan3D, "mapping3d", expectedFolders: 5, expectedTemplateToken: "realTimeFollowSurface");

var stripGeometry = new[]
{
    new GeoPoint(49.0000, 8.0000),
    new GeoPoint(49.0020, 8.0030),
    new GeoPoint(49.0040, 8.0060)
};

var strip = mapping2D with
{
    Name = "CI Mapping Strip",
    Mode = FlightPlanMode.MappingStrip,
    SmartObliqueEnabled = false,
    TerrainFollowEnabled = false
};

var stripPlan = PhotogrammetryPlanner.Generate(stripGeometry, strip);
ValidatePlan(stripPlan, expectedPasses: 1);
ValidateKmz(stripPlan, "mappingStrip", expectedFolders: 1, expectedTemplateToken: "LineString");

Console.WriteLine(
    $"PASS planning modes · 2D segments={plan2D.Segments.Count} · 3D passes={plan3D.Passes.Count} · strip segments={stripPlan.Segments.Count}");

static void ValidatePlan(FlightPlanResult plan, int expectedPasses)
{
    if (plan.Passes.Count != expectedPasses ||
        plan.Segments.Count == 0 ||
        plan.AreaSquareMeters <= 0 ||
        plan.EstimatedPhotos <= 0)
    {
        throw new InvalidOperationException($"Planner returned invalid result for {plan.Settings.Mode}.");
    }
}

static void ValidateKmz(
    FlightPlanResult plan,
    string expectedTemplateType,
    int expectedFolders,
    string expectedTemplateToken)
{
    var path = Path.Combine(Path.GetTempPath(), $"DroneDash_{plan.Settings.Mode}_Smoke.kmz");
    DjiWpmlExporter.ExportKmz(path, plan);

    using var archive = ZipFile.OpenRead(path);
    var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
    foreach (var required in new[] { "wpmz/template.kml", "wpmz/waylines.wpml", "wpmz/res/" })
    {
        if (!names.Contains(required))
            throw new InvalidDataException($"KMZ entry missing: {required}");
    }

    var template = ReadEntry(archive, "wpmz/template.kml");
    var waylines = ReadEntry(archive, "wpmz/waylines.wpml");

    if (!template.Contains($">{expectedTemplateType}<", StringComparison.Ordinal))
        throw new InvalidDataException($"templateType missing: {expectedTemplateType}");
    if (!template.Contains(expectedTemplateToken, StringComparison.Ordinal))
        throw new InvalidDataException($"Template token missing: {expectedTemplateToken}");

    var waylinesXml = XDocument.Parse(waylines);
    XNamespace kml = "http://www.opengis.net/kml/2.2";
    var folderCount = waylinesXml.Descendants(kml + "Folder").Count();
    if (folderCount != expectedFolders)
        throw new InvalidDataException($"Expected {expectedFolders} wayline folders, got {folderCount}.");

    if (plan.Settings.TerrainFollowEnabled &&
        !template.Contains(">realTimeFollowSurface<", StringComparison.Ordinal))
    {
        throw new InvalidDataException("Terrain follow height mode missing.");
    }
}

static string ReadEntry(ZipArchive archive, string name)
{
    var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Missing entry: {name}");
    using var stream = entry.Open();
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
