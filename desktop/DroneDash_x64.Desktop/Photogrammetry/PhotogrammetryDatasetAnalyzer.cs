using System.IO;
using System.Globalization;
using DroneDash_x64.Desktop.Imaging;
using DroneDash_x64.Desktop.Planning;

namespace DroneDash_x64.Desktop.Photogrammetry;

public static class PhotogrammetryDatasetAnalyzer
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".dng", ".tif", ".tiff"
        };

    public static PhotogrammetryDatasetResult Analyze(
        string sourceFolder,
        string? flightPlanProjectPath = null,
        CancellationToken cancellationToken = default,
        IProgress<DatasetScanProgress>? progress = null)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException(sourceFolder);

        FlightPlanResult? plan = null;
        if (!string.IsNullOrWhiteSpace(flightPlanProjectPath))
        {
            var project = FlightPlanProjectStore.Load(flightPlanProjectPath);
            plan = PhotogrammetryPlanner.Generate(project.Geometry, project.Settings);
        }

        var files = SafeDatasetFileEnumerator
            .EnumerateFiles(
                sourceFolder,
                path => SupportedExtensions.Contains(Path.GetExtension(path)),
                cancellationToken)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        progress?.Report(new DatasetScanProgress(
            files.Length,
            0,
            null));

        var images = new List<PhotogrammetryImageRecord>(files.Length);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = files[index];
            images.Add(AnalyzeImage(path, sourceFolder, plan));

            progress?.Report(new DatasetScanProgress(
                files.Length,
                index + 1,
                Path.GetFileName(path)));
        }

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
        var xmp = DjiXmpMetadataReader.Read(path);
        var fields = xmp.Fields;
        var issues = new List<string>();

        if (!string.IsNullOrWhiteSpace(xmp.Error))
            issues.Add(xmp.Error);

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
}
