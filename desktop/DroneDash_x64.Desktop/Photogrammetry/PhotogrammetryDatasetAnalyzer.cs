using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DroneDash_x64.Desktop.Planning;

namespace DroneDash_x64.Desktop.Photogrammetry;

public static class PhotogrammetryDatasetAnalyzer
{
    private const int MaxXmpScanBytes = 32 * 1024 * 1024;

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".dng", ".tif", ".tiff"
        };

    public static PhotogrammetryDatasetResult Analyze(
        string sourceFolder,
        string? flightPlanProjectPath = null)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException(sourceFolder);

        FlightPlanResult? plan = null;
        if (!string.IsNullOrWhiteSpace(flightPlanProjectPath))
        {
            var project = FlightPlanProjectStore.Load(flightPlanProjectPath);
            plan = PhotogrammetryPlanner.Generate(project.Geometry, project.Settings);
        }

        var files = Directory
            .EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var images = new List<PhotogrammetryImageRecord>(files.Length);
        foreach (var path in files)
            images.Add(AnalyzeImage(path, sourceFolder, plan));

        var plannedCount = plan?.EstimatedPhotos;
        double? ratio = plannedCount is > 0
            ? (double)images.Count / plannedCount.Value
            : null;

        var summary = new PhotogrammetryDatasetSummary(
            images.Count,
            images.Count(x => x.HasGps),
            images.Count(x => x.HasRtkMetadata),
            images.Count(x => x.HasRtkPrecision),
            images.Count(x => x.CalibratedFocalLength.HasValue && x.HasOpticalCenter),
            images.Count(x => x.HasDewarpData),
            images.Count(x => x.AssignedWaylineId.HasValue),
            images.Count(x => x.Issues.Count > 0),
            plannedCount,
            ratio);

        return new PhotogrammetryDatasetResult(
            Path.GetFullPath(sourceFolder),
            string.IsNullOrWhiteSpace(flightPlanProjectPath)
                ? null
                : Path.GetFullPath(flightPlanProjectPath),
            plan,
            images,
            summary);
    }

    private static PhotogrammetryImageRecord AnalyzeImage(
        string path,
        string root,
        FlightPlanResult? plan)
    {
        var fields = ReadXmpFields(path);
        var issues = new List<string>();

        var latitude = Number(fields, "GPSLatitude");
        var longitude = Number(fields, "GPSLongitude");
        var absoluteAltitude = Number(fields, "AbsoluteAltitude");
        var relativeAltitude = Number(fields, "RelativeAltitude");
        var stdLon = Number(fields, "RtkStdLon");
        var stdLat = Number(fields, "RtkStdLat");
        var stdHgt = Number(fields, "RtkStdHgt");
        var flightYaw = Number(fields, "FlightYawDegree");
        var gimbalYaw = Number(fields, "GimbalYawDegree");
        var gimbalPitch = Number(fields, "GimbalPitchDegree");
        var calibratedFocalLength = Number(fields, "CalibratedFocalLength");

        fields.TryGetValue("RtkFlag", out var rtkFlag);
        fields.TryGetValue("ImageSource", out var imageSource);
        fields.TryGetValue("CalibratedOpticalCenterX", out var opticalX);
        fields.TryGetValue("CalibratedOpticalCenterY", out var opticalY);
        fields.TryGetValue("DewarpData", out var dewarp);

        var hasOpticalCenter =
            !string.IsNullOrWhiteSpace(opticalX) &&
            !string.IsNullOrWhiteSpace(opticalY);

        if (!latitude.HasValue || !longitude.HasValue)
            issues.Add("GPS fehlt");

        if (string.IsNullOrWhiteSpace(rtkFlag))
            issues.Add("RTK-Metadaten fehlen");

        var precisionCount =
            (stdLon.HasValue ? 1 : 0) +
            (stdLat.HasValue ? 1 : 0) +
            (stdHgt.HasValue ? 1 : 0);
        if (precisionCount is > 0 and < 3)
            issues.Add("RTK-σ unvollständig");

        if (!calibratedFocalLength.HasValue || !hasOpticalCenter)
            issues.Add("Kamerakalibrierung unvollständig");

        if (string.IsNullOrWhiteSpace(dewarp))
            issues.Add("DewarpData fehlt");

        int? waylineId = null;
        string? passName = null;
        int? segmentIndex = null;
        double? routeDistance = null;

        if (plan is not null && latitude.HasValue && longitude.HasValue)
        {
            var assignment = RouteImageMatcher.FindNearest(
                new GeoPoint(latitude.Value, longitude.Value),
                plan);

            if (assignment is not null)
            {
                waylineId = assignment.WaylineId;
                passName = assignment.PassName;
                segmentIndex = assignment.SegmentIndex;
                routeDistance = assignment.DistanceMeters;
            }
        }

        return new PhotogrammetryImageRecord(
            Path.GetFileName(path),
            Path.GetRelativePath(root, path),
            new FileInfo(path).Length,
            imageSource,
            latitude,
            longitude,
            absoluteAltitude,
            relativeAltitude,
            string.IsNullOrWhiteSpace(rtkFlag) ? null : rtkFlag,
            stdLon,
            stdLat,
            stdHgt,
            flightYaw,
            gimbalYaw,
            gimbalPitch,
            calibratedFocalLength,
            hasOpticalCenter,
            !string.IsNullOrWhiteSpace(dewarp),
            waylineId,
            passName,
            segmentIndex,
            routeDistance,
            issues);
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
                foreach (var attribute in element.Attributes()
                             .Where(attribute => !attribute.IsNamespaceDeclaration))
                {
                    AddField(result, attribute.Name.LocalName, attribute.Value);
                }

                if (!element.HasElements)
                    AddField(result, element.Name.LocalName, element.Value);
            }
        }
        catch
        {
            // Broken XMP is represented by missing fields and surfaced through QA issues.
        }

        return result;
    }

    private static void AddField(
        IDictionary<string, string> fields,
        string key,
        string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length > 0)
            fields.TryAdd(key, value);
    }

    private static double? Number(
        IReadOnlyDictionary<string, string> fields,
        string key)
    {
        if (!fields.TryGetValue(key, out var value))
            return null;

        var normalized = value.Trim().TrimStart('+');
        return double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result) &&
            double.IsFinite(result)
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

        var endTag = text.IndexOf(
            "</x:xmpmeta>",
            start,
            StringComparison.OrdinalIgnoreCase) >= 0
                ? "</x:xmpmeta>"
                : "</xmpmeta>";

        var end = text.IndexOf(
            endTag,
            start,
            StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return null;

        end += endTag.Length;
        return text[start..end];
    }
}
