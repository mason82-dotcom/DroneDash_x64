using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DroneDash_x64.Desktop.PvAnalysis;

/// <summary>Located anomaly candidates as GeoJSON and KML for field crews (phones, Google Earth, QGIS).</summary>
public static class PvAnomalyGeoExporter
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    public static (string GeoJsonPath, string KmlPath) Export(
        string destinationFolder,
        IReadOnlyList<PvAnomalyLocation> locations)
    {
        Directory.CreateDirectory(destinationFolder);
        var geoJson = Path.Combine(destinationFolder, "pv-anomalies.geojson");
        var kml = Path.Combine(destinationFolder, "pv-anomalies.kml");
        var utf8 = new UTF8Encoding(false);

        File.WriteAllText(geoJson, BuildGeoJson(locations), utf8);
        using (var stream = File.Create(kml))
            BuildKml(locations).Save(stream);

        return (geoJson, kml);
    }

    public static string BuildGeoJson(IReadOnlyList<PvAnomalyLocation> locations) =>
        JsonSerializer.Serialize(
            new
            {
                type = "FeatureCollection",
                features = locations.Select(location => new
                {
                    type = "Feature",
                    geometry = new
                    {
                        type = "Point",
                        coordinates = new[] { Math.Round(location.Longitude, 8), Math.Round(location.Latitude, 8) }
                    },
                    properties = new
                    {
                        file = location.FileName,
                        candidate = location.CandidateIndex,
                        severity = location.Severity.ToString(),
                        deltaC = Math.Round(location.DeltaC, 2),
                        peakC = Math.Round(location.PeakTemperatureC, 2),
                        groundSampleCm = Math.Round(location.GroundSampleMeters * 100, 1)
                    }
                })
            },
            new JsonSerializerOptions { WriteIndented = true });

    public static XDocument BuildKml(IReadOnlyList<PvAnomalyLocation> locations)
    {
        static XElement Style(string id, string color) =>
            new(Kml + "Style",
                new XAttribute("id", id),
                new XElement(Kml + "IconStyle",
                    new XElement(Kml + "color", color),
                    new XElement(Kml + "Icon",
                        new XElement(Kml + "href", "http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png"))));

        var document = new XElement(Kml + "Document",
            new XElement(Kml + "name", "DroneDash PV-Anomalie-Kandidaten"),
            // KML colours are aabbggrr.
            Style("Critical", "ff2f2fd3"),
            Style("Warning", "ff008cef"),
            Style("None", "ff327d2e"));

        foreach (var location in locations)
        {
            document.Add(new XElement(Kml + "Placemark",
                new XElement(Kml + "name",
                    string.Create(CultureInfo.InvariantCulture, $"ΔT {location.DeltaC:F1} °C · {location.FileName} #{location.CandidateIndex}")),
                new XElement(Kml + "description",
                    FormattableString.Invariant(
                        $"Stufe {location.Severity} · Spitze {location.PeakTemperatureC:F1} °C · Bodenpixel {location.GroundSampleMeters * 100:F1} cm. ") +
                    "Kandidat, keine Fehlerdiagnose; Lage auf wenige Meter genau."),
                new XElement(Kml + "styleUrl", "#" + location.Severity),
                new XElement(Kml + "Point",
                    new XElement(Kml + "coordinates",
                        string.Create(CultureInfo.InvariantCulture, $"{location.Longitude:F8},{location.Latitude:F8},0")))));
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Kml + "kml", document));
    }
}
