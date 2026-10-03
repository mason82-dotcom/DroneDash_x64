using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace DroneDash_x64.Desktop.Planning;

public sealed record DjiKmzValidationReport(
    bool IsValid,
    string? TemplateType,
    int WaylineCount,
    int WaypointCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public string Summary
    {
        get
        {
            var state = IsValid ? "VALID" : "INVALID";
            var type = TemplateType ?? "unbekannt";
            return $"{state} · {type} · {WaylineCount} Wayline(s) · {WaypointCount} Waypoint(s) · " +
                   $"{Errors.Count} Fehler · {Warnings.Count} Warnung(en)";
        }
    }
}

public static class DjiKmzValidator
{
    private const int MaxArchiveEntries = 4_096;
    private const long MaxDeclaredUncompressedBytes =
        512L * 1024 * 1024;
    private const long MaxXmlEntryBytes =
        16L * 1024 * 1024;

    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly XNamespace Wpml = "http://www.dji.com/wpmz/1.0.2";

    public static DjiKmzValidationReport Validate(string path)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(path))
            return new(false, null, 0, 0, ["KMZ-Datei nicht gefunden."], []);

        try
        {
            using var archive = ZipFile.OpenRead(path);

            if (archive.Entries.Count >
                MaxArchiveEntries)
            {
                errors.Add(
                    $"KMZ enthält zu viele Einträge: {archive.Entries.Count:N0} > {MaxArchiveEntries:N0}.");

                return new(
                    false,
                    null,
                    0,
                    0,
                    errors,
                    warnings);
            }

            long declaredBytes = 0;

            foreach (var entry in archive.Entries)
            {
                try
                {
                    declaredBytes =
                        checked(
                            declaredBytes +
                            entry.Length);
                }
                catch (OverflowException)
                {
                    errors.Add(
                        "KMZ meldet eine ungültige unkomprimierte Gesamtgröße.");

                    return new(
                        false,
                        null,
                        0,
                        0,
                        errors,
                        warnings);
                }

                if (declaredBytes >
                    MaxDeclaredUncompressedBytes)
                {
                    errors.Add(
                        $"KMZ ist unkomprimiert größer als erlaubt: {declaredBytes:N0} Bytes > {MaxDeclaredUncompressedBytes:N0} Bytes.");

                    return new(
                        false,
                        null,
                        0,
                        0,
                        errors,
                        warnings);
                }
            }

            var duplicateEntries =
                archive.Entries
                    .GroupBy(
                        entry => entry.FullName,
                        StringComparer.Ordinal)
                    .Where(group =>
                        group.Count() > 1)
                    .Select(group =>
                        group.Key)
                    .ToArray();

            if (duplicateEntries.Length > 0)
            {
                errors.Add(
                    "Doppelte KMZ-Einträge sind nicht zulässig: " +
                    string.Join(
                        ", ",
                        duplicateEntries));

                return new(
                    false,
                    null,
                    0,
                    0,
                    errors,
                    warnings);
            }

            var templateEntry = archive.GetEntry("wpmz/template.kml");
            var waylinesEntry = archive.GetEntry("wpmz/waylines.wpml");
            var hasResources = archive.Entries.Any(e =>
                e.FullName == "wpmz/res/" ||
                e.FullName.StartsWith("wpmz/res/", StringComparison.Ordinal));

            if (templateEntry is null)
                errors.Add("wpmz/template.kml fehlt.");
            if (waylinesEntry is null)
                errors.Add("wpmz/waylines.wpml fehlt.");
            if (!hasResources)
                warnings.Add("wpmz/res/ fehlt.");

            if (templateEntry is null || waylinesEntry is null)
                return new(false, null, 0, 0, errors, warnings);

            var template = LoadXml(templateEntry, errors, "template.kml");
            var waylines = LoadXml(waylinesEntry, errors, "waylines.wpml");
            if (template is null || waylines is null)
                return new(false, null, 0, 0, errors, warnings);

            ValidateNamespace(template, errors, "template.kml");
            ValidateNamespace(waylines, errors, "waylines.wpml");

            var templateType = Value(template, "templateType");
            if (templateType is not ("mapping2d" or "mapping3d" or "mappingStrip" or "waypoint"))
                errors.Add($"Unbekannter oder fehlender templateType: {templateType ?? "<leer>"}.");

            var coordinateMode = Value(template, "coordinateMode");
            if (!string.Equals(coordinateMode, "WGS84", StringComparison.Ordinal))
                errors.Add($"coordinateMode muss WGS84 sein, ist aber {coordinateMode ?? "<leer>"}.");

            ValidateAircraftAndPayload(template, waylines, errors);

            var folders = waylines.Descendants(Kml + "Folder").ToArray();
            var waylineIds = new HashSet<int>();
            var totalWaypoints = 0;

            foreach (var folder in folders)
            {
                if (!TryInt(folder.Element(Wpml + "waylineId")?.Value, out var waylineId))
                {
                    errors.Add("Wayline-Folder ohne gültige waylineId.");
                    continue;
                }

                if (!waylineIds.Add(waylineId))
                    errors.Add($"Doppelte waylineId {waylineId}.");

                var placemarks = folder.Elements(Kml + "Placemark").ToArray();
                totalWaypoints += placemarks.Length;

                for (var i = 0; i < placemarks.Length; i++)
                {
                    var indexValue = placemarks[i].Element(Wpml + "index")?.Value;
                    if (!TryInt(indexValue, out var index) || index != i)
                        errors.Add(
                            $"Wayline {waylineId}: Waypoint-Index muss fortlaufend bei 0 beginnen; " +
                            $"Position {i} enthält {indexValue ?? "<leer>"}.");

                    var point = placemarks[i]
                        .Element(Kml + "Point")?
                        .Element(Kml + "coordinates")?
                        .Value;

                    if (!ValidLonLat(point))
                        errors.Add($"Wayline {waylineId}, Waypoint {i}: ungültige WGS84-Koordinate.");
                }

                foreach (var trigger in folder.Descendants(Wpml + "actionTrigger"))
                {
                    if (trigger.Element(Wpml + "actionTriggerType")?.Value == "multipleDistance")
                    {
                        var parameter = trigger.Element(Wpml + "actionTriggerParam")?.Value;
                        if (!TryDouble(parameter, out var distance) || distance <= 0)
                            errors.Add(
                                $"Wayline {waylineId}: multipleDistance benötigt einen positiven actionTriggerParam.");
                    }
                }
            }

            if (folders.Length == 0)
                errors.Add("waylines.wpml enthält keinen Wayline-Folder.");

            if (templateType == "mapping3d" && folders.Length != 5)
                errors.Add(
                    $"mapping3d erwartet für den DroneDash-Workflow 5 Waylines, gefunden: {folders.Length}.");

            if (templateType is "mapping2d" or "mappingStrip" && folders.Length < 1)
                errors.Add($"{templateType} enthält keine ausführbare Wayline.");

            var heightMode = Value(template, "heightMode");
            var followEnabled = Value(template, "surfaceFollowModeEnable");
            if (heightMode == "realTimeFollowSurface")
            {
                if (followEnabled != "1")
                    errors.Add("realTimeFollowSurface benötigt surfaceFollowModeEnable=1.");

                if (!TryDouble(Value(template, "surfaceRelativeHeight"), out var surfaceHeight) ||
                    surfaceHeight <= 0)
                {
                    errors.Add("realTimeFollowSurface benötigt eine positive surfaceRelativeHeight.");
                }

                warnings.Add(
                    "Terrain Follow ist templatebasiert. Vor Feldbetrieb in DJI Pilot 2 neu generieren und validieren.");
            }

            if (Value(template, "smartObliqueEnable") == "1")
            {
                warnings.Add(
                    "Smart Oblique ist templatebasiert. DJI Pilot 2 sollte die ausführbaren Waylines aus der Vorlage generieren/validieren.");
            }

            if (totalWaypoints > 65535)
                errors.Add($"Zu viele Waypoints: {totalWaypoints}; WPML-Indexbereich endet bei 65535.");

            return new(errors.Count == 0, templateType, folders.Length, totalWaypoints, errors, warnings);
        }
        catch (InvalidDataException ex)
        {
            errors.Add($"Ungültiges KMZ/ZIP: {ex.Message}");
            return new(false, null, 0, 0, errors, warnings);
        }
        catch (Exception ex)
        {
            errors.Add($"KMZ-Prüfung fehlgeschlagen: {ex.Message}");
            return new(false, null, 0, 0, errors, warnings);
        }
    }

    private static XDocument? LoadXml(
        ZipArchiveEntry entry,
        List<string> errors,
        string label)
    {
        try
        {
            if (entry.Length >
                MaxXmlEntryBytes)
            {
                errors.Add(
                    $"{label} ist größer als das zulässige XML-Limit von {MaxXmlEntryBytes:N0} Bytes.");

                return null;
            }

            using var stream =
                entry.Open();

            using var reader =
                XmlReader.Create(
                    stream,
                    new XmlReaderSettings
                    {
                        DtdProcessing =
                            DtdProcessing.Prohibit,
                        XmlResolver =
                            null,
                        MaxCharactersInDocument =
                            MaxXmlEntryBytes
                    });

            return XDocument.Load(
                reader,
                LoadOptions.None);
        }
        catch (Exception ex)
        {
            errors.Add($"{label} ist kein gültiges XML: {ex.Message}");
            return null;
        }
    }

    private static void ValidateNamespace(
        XDocument document,
        List<string> errors,
        string label)
    {
        var root = document.Root;
        if (root is null || root.Name != Kml + "kml")
            errors.Add($"{label}: KML-Root fehlt oder hat einen unerwarteten Namespace.");

        var wpmlNamespace = root?.GetNamespaceOfPrefix("wpml")?.NamespaceName;
        if (!string.Equals(wpmlNamespace, Wpml.NamespaceName, StringComparison.Ordinal))
            errors.Add($"{label}: WPML-Namespace muss {Wpml.NamespaceName} sein.");
    }

    private static void ValidateAircraftAndPayload(
        XDocument template,
        XDocument waylines,
        List<string> errors)
    {
        var tDrone = Value(template, "droneEnumValue");
        var tSub = Value(template, "droneSubEnumValue");
        var tPayload = Value(template, "payloadEnumValue");
        var wDrone = Value(waylines, "droneEnumValue");
        var wSub = Value(waylines, "droneSubEnumValue");
        var wPayload = Value(waylines, "payloadEnumValue");

        if (tDrone != "77")
            errors.Add(
                $"DroneDash M3E/M3T/M3M-Export erwartet droneEnumValue 77, gefunden {tDrone ?? "<leer>"}.");

        var expectedPayload = tSub switch
        {
            "0" => "66",
            "1" => "67",
            "2" => "68",
            _ => null
        };

        if (expectedPayload is null)
            errors.Add($"Ungültiger Mavic-3-Enterprise droneSubEnumValue: {tSub ?? "<leer>"}.");
        else if (tPayload != expectedPayload)
            errors.Add(
                $"Payload {tPayload ?? "<leer>"} passt nicht zu droneSubEnumValue {tSub}; erwartet {expectedPayload}.");

        if (tDrone != wDrone || tSub != wSub || tPayload != wPayload)
            errors.Add(
                "Aircraft-/Payload-Kennung unterscheidet sich zwischen template.kml und waylines.wpml.");
    }

    private static string? Value(XDocument document, string localName) =>
        document.Descendants(Wpml + localName).FirstOrDefault()?.Value;

    private static bool TryInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryDouble(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
        double.IsFinite(result);

    private static bool ValidLonLat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split(',');
        return parts.Length >= 2 &&
               TryDouble(parts[0], out var lon) &&
               TryDouble(parts[1], out var lat) &&
               lon is >= -180 and <= 180 &&
               lat is >= -90 and <= 90;
    }
}
