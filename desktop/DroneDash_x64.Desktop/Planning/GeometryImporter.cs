using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DroneDash_x64.Desktop.Planning;

public enum ImportedGeometryKind
{
    Polygon,
    Line
}

public sealed record ImportedGeometry(
    ImportedGeometryKind Kind,
    IReadOnlyList<GeoPoint> Points,
    string? Name,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Imports a field boundary (polygon) or corridor centerline (line) from KML, KMZ or
/// GeoJSON. When a file holds several geometries the largest polygon wins, otherwise
/// the longest line; everything skipped is reported as a warning.
/// </summary>
public static class GeometryImporter
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const long MaxKmlEntryBytes = 50L * 1024 * 1024;

    private sealed record Candidate(
        ImportedGeometryKind Kind,
        List<GeoPoint> Points,
        string? Name,
        bool HadHoles);

    public static ImportedGeometry Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Geometriedatei nicht gefunden.", path);

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException(
                $"Geometriedatei ist größer als {MaxFileBytes / (1024 * 1024)} MiB.");
        }

        var extension = info.Extension.ToLowerInvariant();
        return extension switch
        {
            ".kml" => ParseKml(File.ReadAllBytes(path)),
            ".kmz" => ParseKmz(path),
            ".geojson" or ".json" => ParseGeoJson(File.ReadAllText(path)),
            _ => throw new NotSupportedException(
                "Unterstützt werden KML, KMZ und GeoJSON (.geojson/.json).")
        };
    }

    public static ImportedGeometry ParseKmz(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        var entry =
            archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("doc.kml", StringComparison.OrdinalIgnoreCase)) ??
            archive.Entries
                .Where(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c == '/'))
                .ThenBy(e => e.FullName, StringComparer.Ordinal)
                .FirstOrDefault() ??
            throw new InvalidDataException("KMZ enthält keine KML-Datei.");

        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        // Entry.Length is only the declared size, so the limit is enforced while reading.
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxKmlEntryBytes)
            {
                throw new InvalidDataException(
                    $"KML in der KMZ ist größer als {MaxKmlEntryBytes / (1024 * 1024)} MiB.");
            }
        }

        return ParseKml(buffer.ToArray());
    }

    public static ImportedGeometry ParseKml(byte[] content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true
        };

        XDocument document;
        using (var stream = new MemoryStream(content))
        using (var reader = XmlReader.Create(stream, settings))
        {
            document = XDocument.Load(reader);
        }

        var candidates = new List<Candidate>();

        foreach (var polygon in document.Descendants().Where(e => e.Name.LocalName == "Polygon"))
        {
            var outer = polygon.Elements()
                .Where(e => e.Name.LocalName == "outerBoundaryIs")
                .SelectMany(e => e.Descendants())
                .FirstOrDefault(e => e.Name.LocalName == "coordinates");

            if (outer is null)
                continue;

            var hasHoles = polygon.Elements().Any(e => e.Name.LocalName == "innerBoundaryIs");
            candidates.Add(new Candidate(
                ImportedGeometryKind.Polygon,
                ParseKmlCoordinates(outer.Value),
                PlacemarkName(polygon),
                hasHoles));
        }

        foreach (var line in document.Descendants().Where(e => e.Name.LocalName == "LineString"))
        {
            var coordinates = line.Elements().FirstOrDefault(e => e.Name.LocalName == "coordinates");
            if (coordinates is null)
                continue;

            candidates.Add(new Candidate(
                ImportedGeometryKind.Line,
                ParseKmlCoordinates(coordinates.Value),
                PlacemarkName(line),
                false));
        }

        return Select(candidates, "KML");
    }

    public static ImportedGeometry ParseGeoJson(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        });

        var root = document.RootElement;
        EnsureWgs84(root);

        var candidates = new List<Candidate>();
        CollectGeoJson(root, null, candidates);
        return Select(candidates, "GeoJSON");
    }

    private static void CollectGeoJson(JsonElement element, string? name, List<Candidate> candidates)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var type = typeElement.GetString();

        switch (type)
        {
            case "FeatureCollection":
                if (element.TryGetProperty("features", out var features) &&
                    features.ValueKind == JsonValueKind.Array)
                {
                    foreach (var feature in features.EnumerateArray())
                        CollectGeoJson(feature, null, candidates);
                }
                break;

            case "Feature":
                if (element.TryGetProperty("geometry", out var geometry))
                    CollectGeoJson(geometry, FeatureName(element), candidates);
                break;

            case "GeometryCollection":
                if (element.TryGetProperty("geometries", out var geometries) &&
                    geometries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in geometries.EnumerateArray())
                        CollectGeoJson(child, name, candidates);
                }
                break;

            case "Polygon":
                AddGeoJsonPolygon(Coordinates(element), name, candidates);
                break;

            case "MultiPolygon":
                foreach (var polygon in Coordinates(element).EnumerateArray())
                    AddGeoJsonPolygon(polygon, name, candidates);
                break;

            case "LineString":
                candidates.Add(new Candidate(
                    ImportedGeometryKind.Line,
                    ParseGeoJsonPositions(Coordinates(element)),
                    name,
                    false));
                break;

            case "MultiLineString":
                foreach (var line in Coordinates(element).EnumerateArray())
                {
                    candidates.Add(new Candidate(
                        ImportedGeometryKind.Line,
                        ParseGeoJsonPositions(line),
                        name,
                        false));
                }
                break;
        }
    }

    private static void AddGeoJsonPolygon(JsonElement rings, string? name, List<Candidate> candidates)
    {
        if (rings.ValueKind != JsonValueKind.Array || rings.GetArrayLength() == 0)
            return;

        candidates.Add(new Candidate(
            ImportedGeometryKind.Polygon,
            ParseGeoJsonPositions(rings[0]),
            name,
            rings.GetArrayLength() > 1));
    }

    private static JsonElement Coordinates(JsonElement geometry) =>
        geometry.TryGetProperty("coordinates", out var coordinates) &&
        coordinates.ValueKind == JsonValueKind.Array
            ? coordinates
            : throw new InvalidDataException("GeoJSON-Geometrie ohne gültige Koordinaten.");

    private static List<GeoPoint> ParseGeoJsonPositions(JsonElement positions)
    {
        if (positions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GeoJSON-Koordinatenliste ist ungültig.");

        var points = new List<GeoPoint>();
        foreach (var position in positions.EnumerateArray())
        {
            if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
                throw new InvalidDataException("GeoJSON-Position braucht Länge und Breite.");

            // GeoJSON order is longitude, latitude.
            points.Add(new GeoPoint(position[1].GetDouble(), position[0].GetDouble()));
        }

        return points;
    }

    private static void EnsureWgs84(JsonElement root)
    {
        // RFC 7946 GeoJSON is always WGS84; the legacy "crs" member may say otherwise.
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("crs", out var crs) ||
            !crs.TryGetProperty("properties", out var properties) ||
            !properties.TryGetProperty("name", out var nameElement))
        {
            return;
        }

        var name = nameElement.GetString() ?? "";
        if (name.Contains("CRS84", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("4326", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException(
            $"GeoJSON verwendet das Koordinatensystem '{name}'. Bitte als WGS84 (EPSG:4326) exportieren.");
    }

    private static string? FeatureName(JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "name", "Name", "NAME", "title" })
        {
            if (properties.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }

        return null;
    }

    private static List<GeoPoint> ParseKmlCoordinates(string text)
    {
        var points = new List<GeoPoint>();
        var tuples = text.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var tuple in tuples)
        {
            var parts = tuple.Split(',');
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                throw new InvalidDataException($"Ungültige KML-Koordinate '{tuple}'.");
            }

            points.Add(new GeoPoint(lat, lon));
        }

        return points;
    }

    private static string? PlacemarkName(XElement geometry)
    {
        var placemark = geometry.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Placemark");
        var name = placemark?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static ImportedGeometry Select(List<Candidate> candidates, string format)
    {
        var usable = candidates
            .Select(Normalize)
            .Where(c => c.Points.Count >= (c.Kind == ImportedGeometryKind.Polygon ? 3 : 2))
            .ToList();

        if (usable.Count == 0)
        {
            throw new InvalidDataException(
                $"{format} enthält keine Fläche (Polygon) und keine Linie mit genügend Punkten.");
        }

        foreach (var candidate in usable)
            ValidateWgs84(candidate.Points);

        var polygons = usable.Where(c => c.Kind == ImportedGeometryKind.Polygon).ToList();
        var chosen = polygons.Count > 0
            ? polygons.MaxBy(c => GeometryMetrics.PolygonAreaSquareMeters(c.Points))!
            : usable.MaxBy(c => GeometryMetrics.PolylineLengthMeters(c.Points))!;

        var warnings = new List<string>();
        var skipped = usable.Count - 1;
        if (skipped > 0)
        {
            warnings.Add(chosen.Kind == ImportedGeometryKind.Polygon
                ? $"{skipped} weitere Geometrie(n) ignoriert; die größte Fläche wurde übernommen."
                : $"{skipped} weitere Linie(n) ignoriert; die längste Linie wurde übernommen.");
        }

        if (chosen.HadHoles)
        {
            warnings.Add(
                "Aussparungen (Innenringe) wurden ignoriert; die Route deckt die gesamte Außenfläche ab.");
        }

        if (chosen.Kind == ImportedGeometryKind.Polygon &&
            GeometryMetrics.IsSelfIntersecting(chosen.Points))
        {
            warnings.Add("Die importierte Fläche überschneidet sich selbst. Bitte Eckpunkte prüfen.");
        }

        return new ImportedGeometry(chosen.Kind, chosen.Points, chosen.Name, warnings);
    }

    private static Candidate Normalize(Candidate candidate)
    {
        var points = new List<GeoPoint>(candidate.Points.Count);
        foreach (var point in candidate.Points)
        {
            if (points.Count == 0 || points[^1] != point)
                points.Add(point);
        }

        // Closed rings repeat the first point at the end; the planner expects open rings.
        if (candidate.Kind == ImportedGeometryKind.Polygon &&
            points.Count > 1 &&
            points[0] == points[^1])
        {
            points.RemoveAt(points.Count - 1);
        }

        return candidate with { Points = points };
    }

    private static void ValidateWgs84(IReadOnlyList<GeoPoint> points)
    {
        foreach (var point in points)
        {
            if (!double.IsFinite(point.Latitude) ||
                !double.IsFinite(point.Longitude) ||
                point.Latitude is < -90 or > 90 ||
                point.Longitude is < -180 or > 180)
            {
                throw new InvalidDataException(
                    "Koordinaten liegen außerhalb von WGS84 (Länge ±180°, Breite ±90°). " +
                    "Vermutlich ist die Datei projiziert (z. B. UTM/EPSG:25832); bitte als WGS84 exportieren.");
            }
        }
    }
}
