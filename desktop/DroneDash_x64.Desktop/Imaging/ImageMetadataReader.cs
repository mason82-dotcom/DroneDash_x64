using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DroneDash_x64.Desktop.Models;

namespace DroneDash_x64.Desktop.Imaging;

public sealed record ImageInspectionResult(
    BitmapSource? Preview,
    IReadOnlyList<ImageMetadataEntryDto> Metadata,
    string PreviewStatus,
    string PhotogrammetrySummary,
    int? PixelWidth,
    int? PixelHeight);

public static class ImageMetadataReader
{
    private const int MaxXmpScanBytes = 32 * 1024 * 1024;

    private static readonly string[] PhotogrammetryFields =
    [
        "GPSLatitude",
        "GPSLongitude",
        "AbsoluteAltitude",
        "RelativeAltitude",
        "FlightYawDegree",
        "FlightPitchDegree",
        "FlightRollDegree",
        "GimbalYawDegree",
        "GimbalPitchDegree",
        "GimbalRollDegree",
        "RtkFlag",
        "RtkStdLon",
        "RtkStdLat",
        "RtkStdHgt",
        "CalibratedFocalLength",
        "CalibratedOpticalCenterX",
        "CalibratedOpticalCenterY",
        "DewarpData"
    ];

    public static ImageInspectionResult Load(string path)
    {
        var metadata = new List<ImageMetadataEntryDto>();
        var fileInfo = new FileInfo(path);

        Add(metadata, "Datei", "Dateiname", fileInfo.Name);
        Add(metadata, "Datei", "Pfad", fileInfo.FullName);
        Add(metadata, "Datei", "Größe", FormatBytes(fileInfo.Length));
        Add(metadata, "Datei", "Letzte Änderung", fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
        Add(metadata, "Datei", "SHA-256", ComputeSha256(path));

        BitmapSource? frame = null;
        string previewStatus;
        int? pixelWidth = null;
        int? pixelHeight = null;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
                throw new InvalidDataException("Die Datei enthält keinen dekodierbaren Bild-Frame.");

            frame = decoder.Frames[0];
            frame.Freeze();
            pixelWidth = frame.PixelWidth;
            pixelHeight = frame.PixelHeight;
            previewStatus = "Vorschau dekodiert";

            Add(metadata, "Bild", "Codec", decoder.CodecInfo?.FriendlyName);
            Add(metadata, "Bild", "Pixel", $"{frame.PixelWidth} × {frame.PixelHeight}");
            Add(metadata, "Bild", "DPI", $"{frame.DpiX:F1} × {frame.DpiY:F1}");
            Add(metadata, "Bild", "Pixelformat", frame.Format.ToString());

            if (frame.Metadata is BitmapMetadata bitmapMetadata)
            {
                AddStandardMetadata(metadata, bitmapMetadata);
                AddExifQueries(metadata, bitmapMetadata);
            }
        }
        catch (Exception ex) when (IsRawFile(path))
        {
            previewStatus = $"RAW/DNG Metadatenmodus · keine WIC-Vorschau ({ex.GetType().Name})";
            Add(metadata, "RAW/DNG", "Vorschau", "Kein geeigneter Windows-WIC-Decoder verfügbar; Metadaten werden trotzdem gelesen.");
        }

        var xmpFields = AddXmpMetadata(metadata, path);
        var summary = AddPhotogrammetryHighlights(metadata, xmpFields);

        if (frame is null && !IsRawFile(path))
            throw new InvalidDataException("Die Datei konnte nicht als Bild dekodiert werden.");

        return new ImageInspectionResult(
            frame,
            metadata,
            previewStatus,
            summary,
            pixelWidth,
            pixelHeight);
    }

    private static void AddStandardMetadata(
        ICollection<ImageMetadataEntryDto> target,
        BitmapMetadata metadata)
    {
        Add(target, "EXIF", "Kamera-Hersteller", metadata.CameraManufacturer);
        Add(target, "EXIF", "Kamera-Modell", metadata.CameraModel);
        Add(target, "EXIF", "Aufnahmedatum", metadata.DateTaken);
        Add(target, "EXIF", "Anwendung", metadata.ApplicationName);
        Add(target, "EXIF", "Titel", metadata.Title);
        Add(target, "EXIF", "Betreff", metadata.Subject);
        Add(target, "EXIF", "Kommentar", metadata.Comment);
        Add(target, "EXIF", "Copyright", metadata.Copyright);
        Add(target, "EXIF", "Format", metadata.Format);
        Add(target, "EXIF", "Ort", metadata.Location);
        Add(target, "EXIF", "Bewertung", metadata.Rating > 0 ? metadata.Rating.ToString(CultureInfo.InvariantCulture) : null);

        if (metadata.Author is { Count: > 0 })
            Add(target, "EXIF", "Autor", string.Join(", ", metadata.Author));
        if (metadata.Keywords is { Count: > 0 })
            Add(target, "EXIF", "Schlagwörter", string.Join(", ", metadata.Keywords));
    }

    private static void AddExifQueries(
        ICollection<ImageMetadataEntryDto> target,
        BitmapMetadata metadata)
    {
        AddQuery(target, metadata, "EXIF", "Orientierung", "/app1/ifd/{ushort=274}");
        AddQuery(target, metadata, "EXIF", "Software", "/app1/ifd/{ushort=305}");
        AddQuery(target, metadata, "EXIF", "Originaldatum", "/app1/ifd/exif/{ushort=36867}");
        AddQuery(target, metadata, "EXIF", "Belichtungszeit", "/app1/ifd/exif/{ushort=33434}");
        AddQuery(target, metadata, "EXIF", "Blende", "/app1/ifd/exif/{ushort=33437}");
        AddQuery(target, metadata, "EXIF", "ISO", "/app1/ifd/exif/{ushort=34855}");
        AddQuery(target, metadata, "EXIF", "Brennweite", "/app1/ifd/exif/{ushort=37386}");
        AddQuery(target, metadata, "EXIF", "35-mm-Brennweite", "/app1/ifd/exif/{ushort=41989}");
        AddQuery(target, metadata, "EXIF", "Objektiv", "/app1/ifd/exif/{ushort=42036}");
        AddQuery(target, metadata, "EXIF", "Seriennummer", "/app1/ifd/exif/{ushort=42033}");

        AddQuery(target, metadata, "GPS", "Latitude Ref", "/app1/ifd/gps/{ushort=1}");
        AddQuery(target, metadata, "GPS", "Latitude", "/app1/ifd/gps/{ushort=2}");
        AddQuery(target, metadata, "GPS", "Longitude Ref", "/app1/ifd/gps/{ushort=3}");
        AddQuery(target, metadata, "GPS", "Longitude", "/app1/ifd/gps/{ushort=4}");
        AddQuery(target, metadata, "GPS", "Altitude Ref", "/app1/ifd/gps/{ushort=5}");
        AddQuery(target, metadata, "GPS", "Altitude", "/app1/ifd/gps/{ushort=6}");
    }

    private static void AddQuery(
        ICollection<ImageMetadataEntryDto> target,
        BitmapMetadata metadata,
        string group,
        string name,
        string query)
    {
        try
        {
            if (!metadata.ContainsQuery(query))
                return;

            Add(target, group, name, FormatMetadataValue(metadata.GetQuery(query)));
        }
        catch
        {
            // Some codecs expose only a subset of EXIF queries.
        }
    }

    private static Dictionary<string, string> AddXmpMetadata(
        ICollection<ImageMetadataEntryDto> target,
        string path)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var xmp = TryReadXmp(path);
        if (string.IsNullOrWhiteSpace(xmp))
            return fields;

        XDocument document;
        try
        {
            document = XDocument.Parse(xmp, LoadOptions.None);
        }
        catch
        {
            Add(target, "XMP", "Status", "XMP vorhanden, aber XML konnte nicht geparst werden.");
            return fields;
        }

        var namespacePrefixes = (document.Root?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>())
            .SelectMany(element => element.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            .GroupBy(attribute => attribute.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Name.LocalName == "xmlns" ? "" : group.First().Name.LocalName,
                StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                AddXmpValue(target, fields, seen, attribute.Name, attribute.Value, namespacePrefixes);
            }

            if (!element.HasElements)
                AddXmpValue(target, fields, seen, element.Name, element.Value, namespacePrefixes);
        }

        return fields;
    }

    private static void AddXmpValue(
        ICollection<ImageMetadataEntryDto> target,
        IDictionary<string, string> fields,
        ISet<string> seen,
        XName name,
        string rawValue,
        IReadOnlyDictionary<string, string> namespacePrefixes)
    {
        var value = rawValue.Trim();
        if (value.Length == 0)
            return;

        var key = QualifiedName(name, namespacePrefixes);
        if (!seen.Add(key + "\0" + value))
            return;

        Add(target, IsDji(name.NamespaceName) ? "DJI XMP" : "XMP", key, value);
        fields.TryAdd(name.LocalName, value);
    }

    private static string AddPhotogrammetryHighlights(
        ICollection<ImageMetadataEntryDto> target,
        IReadOnlyDictionary<string, string> fields)
    {
        var highlights = new List<string>();

        foreach (var field in PhotogrammetryFields)
        {
            if (!fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            Add(target, "Photogrammetrie", FriendlyPhotogrammetryName(field), value);

            if (field is "RtkFlag" or "AbsoluteAltitude" or "RelativeAltitude" or
                "GimbalPitchDegree" or "GimbalYawDegree" or "CalibratedFocalLength")
            {
                highlights.Add($"{FriendlyPhotogrammetryName(field)} {value}");
            }
        }

        fields.TryGetValue("RtkStdLon", out var stdLon);
        fields.TryGetValue("RtkStdLat", out var stdLat);
        fields.TryGetValue("RtkStdHgt", out var stdHgt);
        if (!string.IsNullOrWhiteSpace(stdLon) ||
            !string.IsNullOrWhiteSpace(stdLat) ||
            !string.IsNullOrWhiteSpace(stdHgt))
        {
            highlights.Add(
                $"RTK Std Längs/Quer/Höhe {DisplayValue(stdLon)} / {DisplayValue(stdLat)} / {DisplayValue(stdHgt)}");
        }

        return highlights.Count > 0
            ? string.Join(" · ", highlights)
            : "Keine DJI-Photogrammetrie-XMP-Felder erkannt.";
    }

    private static string FriendlyPhotogrammetryName(string field) =>
        field switch
        {
            "GPSLatitude" => "GPS Latitude",
            "GPSLongitude" => "GPS Longitude",
            "AbsoluteAltitude" => "Absolute Höhe",
            "RelativeAltitude" => "Relative Höhe",
            "FlightYawDegree" => "Flight Yaw",
            "FlightPitchDegree" => "Flight Pitch",
            "FlightRollDegree" => "Flight Roll",
            "GimbalYawDegree" => "Gimbal Yaw",
            "GimbalPitchDegree" => "Gimbal Pitch",
            "GimbalRollDegree" => "Gimbal Roll",
            "RtkFlag" => "RTK Flag",
            "RtkStdLon" => "RTK Std Longitude",
            "RtkStdLat" => "RTK Std Latitude",
            "RtkStdHgt" => "RTK Std Höhe",
            "CalibratedFocalLength" => "Kalibrierte Brennweite",
            "CalibratedOpticalCenterX" => "Optisches Zentrum X",
            "CalibratedOpticalCenterY" => "Optisches Zentrum Y",
            "DewarpData" => "Dewarp / Kalibrierdaten",
            _ => field
        };

    private static string DisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string? TryReadXmp(string path)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, MaxXmpScanBytes);
        var bytes = new byte[length];

        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, offset);
        var start = text.IndexOf("<x:xmpmeta", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            start = text.IndexOf("<xmpmeta", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var endTag = text.IndexOf("</x:xmpmeta>", start, StringComparison.OrdinalIgnoreCase) >= 0
            ? "</x:xmpmeta>"
            : "</xmpmeta>";

        var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return null;

        end += endTag.Length;
        return text[start..end];
    }

    private static bool IsRawFile(string path) =>
        Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase);

    private static bool IsDji(string namespaceName) =>
        namespaceName.Contains("dji", StringComparison.OrdinalIgnoreCase);

    private static string QualifiedName(
        XName name,
        IReadOnlyDictionary<string, string> namespacePrefixes)
    {
        if (name.NamespaceName.Length == 0)
            return name.LocalName;

        return namespacePrefixes.TryGetValue(name.NamespaceName, out var prefix) &&
               !string.IsNullOrWhiteSpace(prefix)
            ? $"{prefix}:{name.LocalName}"
            : name.LocalName;
    }

    private static string FormatMetadataValue(object? value)
    {
        return value switch
        {
            null => "",
            string text => text.TrimEnd('\0'),
            byte[] bytes => Convert.ToHexString(bytes),
            Array array => string.Join(", ", array.Cast<object?>().Select(FormatMetadataValue)),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? ""
        };
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1_073_741_824)
            return $"{value / 1_073_741_824d:F2} GB";
        if (value >= 1_048_576)
            return $"{value / 1_048_576d:F1} MB";
        if (value >= 1024)
            return $"{value / 1024d:F1} KB";
        return $"{value} B";
    }

    private static void Add(
        ICollection<ImageMetadataEntryDto> target,
        string group,
        string name,
        object? value)
    {
        var text = FormatMetadataValue(value);
        if (string.IsNullOrWhiteSpace(text))
            return;

        target.Add(new ImageMetadataEntryDto(group, name, text));
    }
}
