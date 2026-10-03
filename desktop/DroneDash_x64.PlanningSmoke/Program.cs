using System.Globalization;
using DroneDash_x64.Desktop.Photogrammetry;
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

var projectPath = Path.Combine(Path.GetTempPath(), "DroneDash_PlanningSmoke.ddplan");
FlightPlanProjectStore.Save(projectPath, mapping2D, polygon);
var loadedProject = FlightPlanProjectStore.Load(projectPath);

if (loadedProject.SchemaVersion != FlightPlanProject.CurrentSchemaVersion ||
    loadedProject.Settings != mapping2D ||
    loadedProject.Geometry.Count != polygon.Length)
{
    throw new InvalidDataException("DroneDash project round-trip failed.");
}

for (var i = 0; i < polygon.Length; i++)
{
    if (loadedProject.Geometry[i] != polygon[i])
        throw new InvalidDataException($"DroneDash project geometry mismatch at {i}.");
}

var rejectedNonFiniteSettings = false;

try
{
    PhotogrammetryPlanner.Generate(
        polygon,
        mapping2D with
        {
            AltitudeMeters = double.NaN
        });
}
catch (ArgumentOutOfRangeException)
{
    rejectedNonFiniteSettings = true;
}

if (!rejectedNonFiniteSettings)
    throw new InvalidDataException(
        "Planner accepted a non-finite altitude.");

var rejectedNonFiniteGeometry = false;

try
{
    PhotogrammetryPlanner.Generate(
        [
            polygon[0],
            polygon[1],
            new GeoPoint(
                double.PositiveInfinity,
                polygon[2].Longitude)
        ],
        mapping2D);
}
catch (ArgumentOutOfRangeException)
{
    rejectedNonFiniteGeometry = true;
}

if (!rejectedNonFiniteGeometry)
    throw new InvalidDataException(
        "Planner accepted non-finite WGS84 geometry.");

var guardedProjectPath =
    Path.Combine(
        Path.GetTempPath(),
        "DroneDash_InvalidPlanningSave_Smoke.ddplan");

File.WriteAllText(
    guardedProjectPath,
    "preserve-existing-project");

var rejectedInvalidProjectSave = false;

try
{
    FlightPlanProjectStore.Save(
        guardedProjectPath,
        mapping2D,
        [
            polygon[0],
            polygon[1],
            new GeoPoint(
                999d,
                polygon[2].Longitude)
        ]);
}
catch (ArgumentOutOfRangeException)
{
    rejectedInvalidProjectSave = true;
}

if (!rejectedInvalidProjectSave ||
    File.ReadAllText(
        guardedProjectPath) !=
        "preserve-existing-project")
{
    throw new InvalidDataException(
        "Invalid flight-plan save replaced an existing project file.");
}

var plan2D = PhotogrammetryPlanner.Generate(
    loadedProject.Geometry,
    loadedProject.Settings);
ValidatePlan(plan2D, expectedPasses: 1);
ValidateKmz(
    plan2D,
    "mapping2d",
    expectedFolders: 1,
    expectedTemplateToken: "smartObliqueEnable");

ValidateAtomicKmzExport(plan2D);
ValidateDuplicateKmzEntryRejected(plan2D);

var mapping3D = mapping2D with
{
    Name = "CI Mapping 3D",
    Mode = FlightPlanMode.Mapping3D,
    SmartObliqueEnabled = false,
    TerrainFollowEnabled = true
};

var plan3D = PhotogrammetryPlanner.Generate(polygon, mapping3D);
ValidatePlan(plan3D, expectedPasses: 5);
ValidateKmz(
    plan3D,
    "mapping3d",
    expectedFolders: 5,
    expectedTemplateToken: "realTimeFollowSurface");

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
ValidateKmz(
    stripPlan,
    "mappingStrip",
    expectedFolders: 1,
    expectedTemplateToken: "LineString");

Console.WriteLine(
    $"PASS planning persistence + validation · " +
    $"2D segments={plan2D.Segments.Count} · " +
    $"3D passes={plan3D.Passes.Count} · " +
    $"strip segments={stripPlan.Segments.Count}");

static void ValidatePlan(FlightPlanResult plan, int expectedPasses)
{
    if (plan.Passes.Count != expectedPasses ||
        plan.Segments.Count == 0 ||
        plan.AreaSquareMeters <= 0 ||
        plan.EstimatedPhotos <= 0)
    {
        throw new InvalidOperationException(
            $"Planner returned invalid result for {plan.Settings.Mode}.");
    }
}

static void ValidateKmz(
    FlightPlanResult plan,
    string expectedTemplateType,
    int expectedFolders,
    string expectedTemplateToken)
{
    var path = Path.Combine(
        Path.GetTempPath(),
        $"DroneDash_{plan.Settings.Mode}_Smoke.kmz");

    DjiWpmlExporter.ExportKmz(path, plan);

    var report = DjiKmzValidator.Validate(path);
    if (!report.IsValid)
    {
        throw new InvalidDataException(
            $"KMZ validation failed: {string.Join(" | ", report.Errors)}");
    }

    if (report.TemplateType != expectedTemplateType)
        throw new InvalidDataException(
            $"Expected template {expectedTemplateType}, got {report.TemplateType}.");

    if (report.WaylineCount != expectedFolders)
        throw new InvalidDataException(
            $"Expected {expectedFolders} waylines, got {report.WaylineCount}.");

    using var archive = ZipFile.OpenRead(path);
    var names = archive.Entries
        .Select(e => e.FullName)
        .ToHashSet(StringComparer.Ordinal);

    foreach (var required in new[]
    {
        "wpmz/template.kml",
        "wpmz/waylines.wpml",
        "wpmz/res/"
    })
    {
        if (!names.Contains(required))
            throw new InvalidDataException($"KMZ entry missing: {required}");
    }

    var template = ReadEntry(archive, "wpmz/template.kml");
    if (!template.Contains(expectedTemplateToken, StringComparison.Ordinal))
        throw new InvalidDataException(
            $"Template token missing: {expectedTemplateToken}");

    var waylines = ReadEntry(archive, "wpmz/waylines.wpml");
    var waylinesXml = XDocument.Parse(waylines);
    XNamespace kml = "http://www.opengis.net/kml/2.2";
    var folderCount = waylinesXml.Descendants(kml + "Folder").Count();
    if (folderCount != expectedFolders)
        throw new InvalidDataException(
            $"Expected {expectedFolders} wayline folders, got {folderCount}.");
}

static string ReadEntry(ZipArchive archive, string name)
{
    var entry = archive.GetEntry(name)
        ?? throw new InvalidDataException($"Missing entry: {name}");

    using var stream = entry.Open();
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static void ValidateAtomicKmzExport(
    FlightPlanResult validPlan)
{
    var path = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_AtomicExport_Smoke.kmz");

    var sentinel =
        new byte[] { 0x44, 0x52, 0x4F, 0x4E, 0x45 };

    File.WriteAllBytes(
        path,
        sentinel);

    var invalidPlan =
        validPlan with
        {
            Geometry = []
        };

    var rejected = false;

    try
    {
        DjiWpmlExporter.ExportKmz(
            path,
            invalidPlan);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    if (!rejected)
        throw new InvalidDataException(
            "Invalid KMZ export input was not rejected.");

    var preserved =
        File.ReadAllBytes(path);

    if (!preserved.SequenceEqual(sentinel))
        throw new InvalidDataException(
            "Failed KMZ export replaced or truncated the existing target file.");
}

static void ValidateDuplicateKmzEntryRejected(
    FlightPlanResult plan)
{
    var source = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_DuplicateEntry_Source.kmz");

    var duplicate = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_DuplicateEntry_Smoke.kmz");

    DjiWpmlExporter.ExportKmz(
        source,
        plan);

    using (var input =
           ZipFile.OpenRead(source))
    using (var output =
           ZipFile.Open(
               duplicate,
               ZipArchiveMode.Create))
    {
        foreach (var entry in input.Entries)
        {
            var copy =
                output.CreateEntry(
                    entry.FullName);

            using var inputStream =
                entry.Open();

            using var outputStream =
                copy.Open();

            inputStream.CopyTo(
                outputStream);
        }

        var duplicateEntry =
            output.CreateEntry(
                "wpmz/template.kml");

        using var duplicateStream =
            duplicateEntry.Open();

        using var writer =
            new StreamWriter(
                duplicateStream);

        writer.Write(
            "<duplicate/>");
    }

    var report =
        DjiKmzValidator.Validate(
            duplicate);

    if (report.IsValid ||
        !report.Errors.Any(error =>
            error.Contains(
                "Doppelte KMZ-Einträge",
                StringComparison.Ordinal)))
    {
        throw new InvalidDataException(
            "Duplicate KMZ entries were not rejected.");
    }
}


var datasetRoot = Path.Combine(
    Path.GetTempPath(),
    "DroneDash_PhotogrammetrySmoke_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(datasetRoot);

try
{
    WriteSyntheticDjiXmp(
        Path.Combine(datasetRoot, "DJI_0001.JPG"),
        49.0010,
        8.0010,
        includeRtk: true,
        includeCalibration: true);

    WriteSyntheticDjiXmp(
        Path.Combine(datasetRoot, "DJI_0002.JPG"),
        49.0020,
        8.0030,
        includeRtk: false,
        includeCalibration: true);

    var dataset = PhotogrammetryDatasetAnalyzer.Analyze(
        datasetRoot,
        projectPath);

    if (dataset.Images.Count != 2 ||
        dataset.Summary.GeotaggedCount != 2 ||
        dataset.Summary.RtkMetadataCount != 1 ||
        dataset.Summary.AssignedCount != 2)
    {
        throw new InvalidDataException(
            "Photogrammetry dataset analysis smoke test failed.");
    }

    if (!dataset.Images.Any(image => image.Issues.Contains("RTK-Metadaten fehlen")))
        throw new InvalidDataException("Expected RTK QA issue was not produced.");

    var manifestRoot = Path.Combine(datasetRoot, "manifest");
    var exported = PhotogrammetryManifestExporter.Export(
        manifestRoot,
        dataset);

    foreach (var file in new[]
    {
        exported.JsonPath,
        exported.CsvPath,
        exported.ImageListPath
    })
    {
        if (!File.Exists(file) || new FileInfo(file).Length == 0)
            throw new InvalidDataException($"Photogrammetry manifest output missing: {file}");
    }

    Console.WriteLine(
        $"PASS photogrammetry dataset · images={dataset.Images.Count} · " +
        $"gps={dataset.Summary.GeotaggedCount} · rtk={dataset.Summary.RtkMetadataCount} · " +
        $"assigned={dataset.Summary.AssignedCount}");
}
finally
{
    try
    {
        Directory.Delete(datasetRoot, recursive: true);
    }
    catch
    {
        // CI temp cleanup is best-effort.
    }
}

static void WriteSyntheticDjiXmp(
    string path,
    double latitude,
    double longitude,
    bool includeRtk,
    bool includeCalibration)
{
    var rtk = includeRtk
        ? """
          drone-dji:RtkFlag="50"
          drone-dji:RtkStdLon="0.012"
          drone-dji:RtkStdLat="0.014"
          drone-dji:RtkStdHgt="0.025"
          """
        : "";

    var calibration = includeCalibration
        ? """
          drone-dji:CalibratedFocalLength="3666.666"
          drone-dji:CalibratedOpticalCenterX="2640.0"
          drone-dji:CalibratedOpticalCenterY="1978.0"
          drone-dji:DewarpData="test-calibration"
          """
        : "";

    var xmp = $"""
    fake-jpeg-prefix
    <x:xmpmeta xmlns:x="adobe:ns:meta/">
      <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description
          xmlns:drone-dji="http://www.dji.com/drone-dji/1.0/"
          drone-dji:GPSLatitude="{latitude.ToString(CultureInfo.InvariantCulture)}"
          drone-dji:GPSLongitude="{longitude.ToString(CultureInfo.InvariantCulture)}"
          drone-dji:AbsoluteAltitude="145.2"
          drone-dji:RelativeAltitude="80.1"
          drone-dji:FlightYawDegree="15.0"
          drone-dji:GimbalYawDegree="15.0"
          drone-dji:GimbalPitchDegree="-90.0"
          drone-dji:ImageSource="WideCamera"
          {rtk}
          {calibration}/>
      </rdf:RDF>
    </x:xmpmeta>
    fake-jpeg-suffix
    """;

    File.WriteAllText(path, xmp);
}
