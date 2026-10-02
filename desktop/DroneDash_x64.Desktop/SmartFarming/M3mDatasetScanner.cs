using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace DroneDash_x64.Desktop.SmartFarming;

public static class M3mDatasetScanner
{
    private const int MaxXmpScanBytes = 32 * 1024 * 1024;

    public static M3mDatasetResult Scan(string sourceFolder)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException(sourceFolder);

        var builders = new Dictionary<string, CaptureBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            if (!M3mCaptureKeyParser.TryParse(Path.GetFileName(path), out var key, out var kind))
                continue;

            if (!builders.TryGetValue(key, out var builder))
            {
                builder = new CaptureBuilder(key);
                builders.Add(key, builder);
            }

            if (kind == "D")
            {
                builder.RgbPath ??= path;
                continue;
            }

            var band = M3mCaptureKeyParser.BandFromKind(kind);
            if (band is not null)
                builder.SetBand(ReadBandMetadata(path, band.Value));
        }

        var captures = builders.Values
            .OrderBy(builder => builder.Key, StringComparer.OrdinalIgnoreCase)
            .Select(builder => builder.Build())
            .ToArray();

        var summary = new M3mDatasetSummary(
            captures.Length,
            captures.Count(capture => capture.IsComplete),
            captures.Count(capture => capture.IsQuicklookReady),
            captures.Count(capture => capture.RgbPath is not null),
            captures.Count(capture => capture.Green is not null),
            captures.Count(capture => capture.Red is not null),
            captures.Count(capture => capture.RedEdge is not null),
            captures.Count(capture => capture.Nir is not null),
            captures.Count(capture => capture.Issues.Count > 0));

        return new M3mDatasetResult(Path.GetFullPath(sourceFolder), captures, summary);
    }

    private static M3mBandMetadata ReadBandMetadata(string path, M3mBand band)
    {
        var issues = new List<string>();
        var fields = ReadXmpFields(path);
        int width;
        int height;
        int bitsPerSample;

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            bitsPerSample = frame.Format.BitsPerPixel <= 8 ? 8 : 16;
        }
        catch (Exception ex)
        {
            width = 0;
            height = 0;
            bitsPerSample = 16;
            issues.Add($"TIFF-Header nicht lesbar: {ex.Message}");
        }

        var blackLevel = Number(fields, "BlackLevel");
        var sensorGain = Number(fields, "SensorGain");
        var exposureTime = Number(fields, "ExposureTime");
        var gainAdjustment = Number(fields, "SensorGainAdjustment");
        var irradiance = Number(fields, "Irradiance");
        var latitude = Number(fields, "GpsLatitude") ?? Number(fields, "GPSLatitude");
        var longitude = Number(fields, "GpsLongitude") ?? Number(fields, "GPSLongitude");

        fields.TryGetValue("ImageSource", out var imageSource);
        fields.TryGetValue("GpsStatus", out var rtkStatus);
        if (string.IsNullOrWhiteSpace(rtkStatus))
            fields.TryGetValue("GPSStatus", out rtkStatus);

        if (blackLevel is null) issues.Add("BlackLevel fehlt");
        if (sensorGain is not > 0) issues.Add("SensorGain fehlt");
        if (exposureTime is not > 0) issues.Add("ExposureTime fehlt");
        if (gainAdjustment is not > 0) issues.Add("SensorGainAdjustment fehlt");
        if (irradiance is not > 0) issues.Add("Irradiance fehlt");
        if (width <= 0 || height <= 0) issues.Add("Bildabmessungen ungültig");

        return new M3mBandMetadata(
            path, band, width, height, bitsPerSample, blackLevel, sensorGain,
            exposureTime, gainAdjustment, irradiance, imageSource, latitude,
            longitude, rtkStatus, issues);
    }

    private static Dictionary<string, string> ReadXmpFields(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var xmp = TryReadXmp(path);
        if (string.IsNullOrWhiteSpace(xmp))
            return result;

        try
        {
            var document = XDocument.Parse(xmp, LoadOptions.None);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    AddField(result, attribute.Name.LocalName, attribute.Value);
                if (!element.HasElements)
                    AddField(result, element.Name.LocalName, element.Value);
            }
        }
        catch
        {
        }

        return result;
    }

    private static void AddField(IDictionary<string, string> fields, string key, string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length > 0)
            fields.TryAdd(key, value);
    }

    private static double? Number(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value))
            return null;

        return double.TryParse(
            value.Trim().TrimStart('+'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result) && double.IsFinite(result)
                ? result
                : null;
    }

    private static string? TryReadXmp(string path)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, MaxXmpScanBytes);
        var bytes = new byte[length];
        var offset = 0;

        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) break;
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
        return end < 0 ? null : text[start..(end + endTag.Length)];
    }

    private sealed class CaptureBuilder
    {
        public CaptureBuilder(string key) => Key = key;
        public string Key { get; }
        public string? RgbPath { get; set; }
        public M3mBandMetadata? Green { get; private set; }
        public M3mBandMetadata? Red { get; private set; }
        public M3mBandMetadata? RedEdge { get; private set; }
        public M3mBandMetadata? Nir { get; private set; }

        public void SetBand(M3mBandMetadata metadata)
        {
            switch (metadata.Band)
            {
                case M3mBand.Green: Green ??= metadata; break;
                case M3mBand.Red: Red ??= metadata; break;
                case M3mBand.RedEdge: RedEdge ??= metadata; break;
                case M3mBand.Nir: Nir ??= metadata; break;
            }
        }

        public M3mCaptureGroup Build()
        {
            var issues = new List<string>();
            if (RgbPath is null) issues.Add("RGB fehlt");
            if (Green is null) issues.Add("Green fehlt");
            if (Red is null) issues.Add("Red fehlt");
            if (RedEdge is null) issues.Add("Red Edge fehlt");
            if (Nir is null) issues.Add("NIR fehlt");

            foreach (var band in new[] { Green, Red, RedEdge, Nir })
            {
                if (band is not null)
                    issues.AddRange(band.Issues.Select(issue => $"{band.Band}: {issue}"));
            }

            var present = new[] { Green, Red, RedEdge, Nir }
                .Where(band => band is not null)
                .Cast<M3mBandMetadata>()
                .ToArray();

            if (present.Length > 1)
            {
                var first = present[0];
                if (present.Any(band => band.Width != first.Width || band.Height != first.Height))
                    issues.Add("Bandabmessungen unterscheiden sich");
            }

            return new M3mCaptureGroup(
                Key, RgbPath, Green, Red, RedEdge, Nir,
                issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
}
