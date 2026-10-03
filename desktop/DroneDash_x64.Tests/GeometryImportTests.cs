using System.IO;
using System.IO.Compression;
using System.Text;
using DroneDash_x64.Desktop.Planning;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class GeometryImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_GeometryImportTests_" + Guid.NewGuid().ToString("N"));

    public GeometryImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ~100 m x ~100 m square near Karlsruhe (0.0009° lat ≈ 100 m, 0.00137° lon ≈ 100 m at 49°).
    private static readonly GeoPoint[] Square =
    [
        new(49.0000, 8.0000),
        new(49.0000, 8.00137),
        new(49.0009, 8.00137),
        new(49.0009, 8.0000)
    ];

    private const string KmlSquare = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2">
          <Document>
            <Placemark>
              <name>Schlag 7</name>
              <Polygon>
                <outerBoundaryIs><LinearRing><coordinates>
                  8.0,49.0,0 8.00137,49.0,0 8.00137,49.0009,0 8.0,49.0009,0 8.0,49.0,0
                </coordinates></LinearRing></outerBoundaryIs>
              </Polygon>
            </Placemark>
          </Document>
        </kml>
        """;

    [Fact]
    public void Metrics_SquareAreaAndPerimeterAreRealistic()
    {
        var area = GeometryMetrics.PolygonAreaSquareMeters(Square);
        var perimeter = GeometryMetrics.PolygonPerimeterMeters(Square);

        Assert.InRange(area, 9_800, 10_400);
        Assert.InRange(perimeter, 395, 410);
    }

    [Fact]
    public void Metrics_DegenerateGeometryHasNoArea()
    {
        Assert.Equal(0d, GeometryMetrics.PolygonAreaSquareMeters(Square[..2]));
        Assert.Equal(0d, GeometryMetrics.PolygonPerimeterMeters(Square[..1]));
    }

    [Fact]
    public void Metrics_DetectsBowTie()
    {
        GeoPoint[] bowTie = [Square[0], Square[2], Square[1], Square[3]];

        Assert.False(GeometryMetrics.IsSelfIntersecting(Square));
        Assert.True(GeometryMetrics.IsSelfIntersecting(bowTie));
    }

    [Fact]
    public void Metrics_ConcavePolygonIsNotSelfIntersecting()
    {
        GeoPoint[] lShape =
        [
            new(49.0000, 8.0000),
            new(49.0000, 8.0020),
            new(49.0005, 8.0020),
            new(49.0005, 8.0010),
            new(49.0010, 8.0010),
            new(49.0010, 8.0000)
        ];

        Assert.False(GeometryMetrics.IsSelfIntersecting(lShape));
    }

    [Fact]
    public void Kml_ImportsPolygonWithNameAndOpenRing()
    {
        var result = GeometryImporter.ParseKml(Encoding.UTF8.GetBytes(KmlSquare.Trim()));

        Assert.Equal(ImportedGeometryKind.Polygon, result.Kind);
        Assert.Equal("Schlag 7", result.Name);
        Assert.Equal(Square, result.Points);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Kml_ImportsLineStringAsCorridor()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><LineString>
              <coordinates>8.0,49.0 8.01,49.0 8.02,49.01</coordinates>
            </LineString></Placemark></kml>
            """;

        var result = GeometryImporter.ParseKml(Encoding.UTF8.GetBytes(kml));

        Assert.Equal(ImportedGeometryKind.Line, result.Kind);
        Assert.Equal(3, result.Points.Count);
        Assert.Equal(new GeoPoint(49.01, 8.02), result.Points[^1]);
    }

    [Fact]
    public void Kml_RejectsDtdToPreventEntityExpansion()
    {
        const string kml = """
            <?xml version="1.0"?>
            <!DOCTYPE kml [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <kml><Placemark><LineString><coordinates>&x;</coordinates></LineString></Placemark></kml>
            """;

        Assert.ThrowsAny<System.Xml.XmlException>(() =>
            GeometryImporter.ParseKml(Encoding.UTF8.GetBytes(kml)));
    }

    [Fact]
    public void Kmz_ReadsDocKml()
    {
        var path = Path.Combine(_root, "field.kmz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("doc.kml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(KmlSquare.Trim());
        }

        var result = GeometryImporter.Import(path);

        Assert.Equal(ImportedGeometryKind.Polygon, result.Kind);
        Assert.Equal(4, result.Points.Count);
    }

    [Fact]
    public void GeoJson_ChoosesLargestPolygonAndReportsSkippedAndHoles()
    {
        const string json = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature", "properties": { "name": "klein" },
                  "geometry": { "type": "Polygon", "coordinates": [[[8,49],[8.0001,49],[8.0001,49.0001],[8,49]]] } },
                { "type": "Feature", "properties": { "name": "Hauptschlag" },
                  "geometry": { "type": "Polygon", "coordinates": [
                    [[8,49],[8.01,49],[8.01,49.01],[8,49.01],[8,49]],
                    [[8.004,49.004],[8.005,49.004],[8.005,49.005],[8.004,49.004]]
                  ] } },
                { "type": "Feature", "properties": {},
                  "geometry": { "type": "Point", "coordinates": [8, 49] } }
              ]
            }
            """;

        var result = GeometryImporter.ParseGeoJson(json);

        Assert.Equal(ImportedGeometryKind.Polygon, result.Kind);
        Assert.Equal("Hauptschlag", result.Name);
        Assert.Equal(4, result.Points.Count);
        // GeoJSON order is [lon, lat].
        Assert.Equal(new GeoPoint(49, 8.01), result.Points[1]);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void GeoJson_MultiPolygonIsSupported()
    {
        const string json = """
            { "type": "MultiPolygon", "coordinates": [
              [[[8,49],[8.001,49],[8.001,49.001],[8,49]]],
              [[[9,50],[9.01,50],[9.01,50.01],[9,50.01],[9,50]]]
            ] }
            """;

        var result = GeometryImporter.ParseGeoJson(json);

        Assert.Equal(4, result.Points.Count);
        Assert.Equal(9d, result.Points[0].Longitude);
    }

    [Fact]
    public void GeoJson_RejectsProjectedCrs()
    {
        const string json = """
            { "type": "FeatureCollection",
              "crs": { "type": "name", "properties": { "name": "urn:ogc:def:crs:EPSG::25832" } },
              "features": [] }
            """;

        var error = Assert.Throws<InvalidDataException>(() => GeometryImporter.ParseGeoJson(json));
        Assert.Contains("25832", error.Message);
    }

    [Fact]
    public void GeoJson_RejectsUtmLikeCoordinatesWithoutCrs()
    {
        const string json = """
            { "type": "Polygon", "coordinates": [[[456000,5430000],[456100,5430000],[456100,5430100],[456000,5430000]]] }
            """;

        var error = Assert.Throws<InvalidDataException>(() => GeometryImporter.ParseGeoJson(json));
        Assert.Contains("WGS84", error.Message);
    }

    [Fact]
    public void GeoJson_WithoutUsableGeometryFails()
    {
        const string json = """{ "type": "Point", "coordinates": [8, 49] }""";

        Assert.Throws<InvalidDataException>(() => GeometryImporter.ParseGeoJson(json));
    }

    [Fact]
    public void Import_RejectsUnknownExtension()
    {
        var path = Path.Combine(_root, "field.shp");
        File.WriteAllText(path, "x");

        Assert.Throws<NotSupportedException>(() => GeometryImporter.Import(path));
    }
}
