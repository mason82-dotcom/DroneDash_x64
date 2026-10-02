using System.IO.Compression;
using DroneDash_x64.Desktop.Planning;

var polygon = new[]
{
    new GeoPoint(49.0000, 8.0000),
    new GeoPoint(49.0000, 8.0060),
    new GeoPoint(49.0040, 8.0060),
    new GeoPoint(49.0040, 8.0000)
};

var settings = new FlightPlanSettings(
    "CI Mapping",
    DjiAircraftProfile.M3E,
    80,
    8,
    80,
    70,
    15,
    -90);

var plan = PhotogrammetryPlanner.Generate(polygon, settings);
if (plan.Segments.Count == 0 || plan.AreaSquareMeters <= 0 || plan.EstimatedPhotos <= 0)
    throw new InvalidOperationException("Planner returned an empty mapping result.");

var path = Path.Combine(Path.GetTempPath(), "DroneDash_PlanningSmoke.kmz");
DjiWpmlExporter.ExportKmz(path, plan);

using var archive = ZipFile.OpenRead(path);
var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
foreach (var required in new[] { "wpmz/template.kml", "wpmz/waylines.wpml", "wpmz/res/" })
{
    if (!names.Contains(required))
        throw new InvalidDataException($"KMZ entry missing: {required}");
}

Console.WriteLine($"PASS · segments={plan.Segments.Count} · photos={plan.EstimatedPhotos} · gsd={plan.GsdCentimeters:F2}cm · kmz={path}");
