using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace DroneDash_x64.Desktop.Planning;

public static class DjiWpmlExporter
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly XNamespace Wpml = "http://www.dji.com/wpmz/1.0.2";

    public static void ExportKmz(string path, FlightPlanResult plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);

        ValidatePlanForExport(plan);

        var fullPath =
            Path.GetFullPath(path);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)!);

        var tempPath =
            fullPath +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                using (var archive = new ZipArchive(
                    stream,
                    ZipArchiveMode.Create,
                    leaveOpen: true))
                {
                    WriteXml(
                        archive,
                        "wpmz/template.kml",
                        BuildTemplate(plan));

                    WriteXml(
                        archive,
                        "wpmz/waylines.wpml",
                        BuildWaylines(plan));

                    archive.CreateEntry(
                        "wpmz/res/");
                }

                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                tempPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Cleanup must not hide the export result.
            }
        }
    }

    public static XDocument BuildTemplate(FlightPlanResult plan)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var settings = plan.Settings;
        var model = DjiModel(settings.Aircraft);

        var folder = new XElement(Kml + "Folder",
            E("templateType", TemplateType(settings.Mode)),
            E("templateId", 0),
            CoordinateSystem(settings),
            E("autoFlightSpeed", F(settings.SpeedMetersPerSecond)),
            new XElement(Wpml + "payloadParam",
                E("payloadPositionIndex", 0),
                E("imageFormat", model.ImageFormat)),
            BuildTemplatePlacemark(plan));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Kml + "kml",
                new XAttribute(XNamespace.Xmlns + "wpml", Wpml),
                new XElement(Kml + "Document",
                    E("author", "DroneDash_x64"),
                    E("createTime", now),
                    E("updateTime", now),
                    MissionConfig(settings, model),
                    folder)));
    }

    public static XDocument BuildWaylines(FlightPlanResult plan)
    {
        var settings = plan.Settings;
        var model = DjiModel(settings.Aircraft);
        var document = new XElement(Kml + "Document",
            MissionConfig(settings, model, includeRthHeight: true));

        foreach (var pass in plan.Passes)
            document.Add(BuildWaylineFolder(plan, pass));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Kml + "kml",
                new XAttribute(XNamespace.Xmlns + "wpml", Wpml),
                document));
    }

    private static void ValidatePlanForExport(
        FlightPlanResult plan)
    {
        var minimumGeometryPoints =
            plan.Settings.Mode ==
                FlightPlanMode.MappingStrip
                ? 2
                : 3;

        if (plan.Geometry.Count <
            minimumGeometryPoints)
        {
            throw new InvalidDataException(
                $"Flugplan enthält zu wenig Geometriepunkte für {plan.Settings.Mode}.");
        }

        if (plan.Geometry.Any(point =>
                !ValidCoordinate(point)))
        {
            throw new InvalidDataException(
                "Flugplan enthält ungültige oder nicht-endliche WGS84-Koordinaten.");
        }

        if (!double.IsFinite(
                plan.PhotoSpacingMeters) ||
            plan.PhotoSpacingMeters <= 0)
        {
            throw new InvalidDataException(
                "Flugplan enthält keinen gültigen Fotoabstand.");
        }

        if (!double.IsFinite(
                plan.Settings.AltitudeMeters) ||
            plan.Settings.AltitudeMeters <= 0 ||
            !double.IsFinite(
                plan.Settings.SpeedMetersPerSecond) ||
            plan.Settings.SpeedMetersPerSecond <= 0)
        {
            throw new InvalidDataException(
                "Flugplan enthält keine gültige Höhe oder Geschwindigkeit.");
        }

        if (plan.Passes.Count == 0)
        {
            throw new InvalidDataException(
                "Flugplan enthält keine ausführbaren Waylines.");
        }

        var waylineIds =
            new HashSet<int>();

        long waypointCount = 0;

        foreach (var pass in plan.Passes)
        {
            if (pass.WaylineId < 0 ||
                !waylineIds.Add(
                    pass.WaylineId))
            {
                throw new InvalidDataException(
                    $"Ungültige oder doppelte Wayline-ID {pass.WaylineId}.");
            }

            if (pass.Segments.Count == 0)
            {
                throw new InvalidDataException(
                    $"Wayline {pass.WaylineId} enthält keine Segmente.");
            }

            foreach (var segment in pass.Segments)
            {
                if (!ValidCoordinate(
                        segment.Start) ||
                    !ValidCoordinate(
                        segment.End) ||
                    !double.IsFinite(
                        segment.LengthMeters) ||
                    segment.LengthMeters <= 0)
                {
                    throw new InvalidDataException(
                        $"Wayline {pass.WaylineId} enthält ein ungültiges Segment.");
                }
            }

            waypointCount +=
                checked(
                    (long)pass.Segments.Count *
                    2L);
        }

        if (waypointCount > 65_535)
        {
            throw new InvalidDataException(
                $"Flugplan enthält zu viele Waypoints für WPML: {waypointCount:N0}.");
        }
    }

    private static bool ValidCoordinate(
        GeoPoint point) =>
        double.IsFinite(
            point.Latitude) &&
        double.IsFinite(
            point.Longitude) &&
        point.Latitude is
            >= -90 and <= 90 &&
        point.Longitude is
            >= -180 and <= 180;

    private static XElement CoordinateSystem(FlightPlanSettings settings)
    {
        var element = new XElement(Wpml + "waylineCoordinateSysParam",
            E("coordinateMode", "WGS84"),
            E("heightMode", settings.TerrainFollowEnabled ? "realTimeFollowSurface" : "relativeToStartPoint"),
            E("globalShootHeight", F(settings.AltitudeMeters)),
            E("positioningType", "GPS"),
            E("surfaceFollowModeEnable", settings.TerrainFollowEnabled ? 1 : 0));

        if (settings.TerrainFollowEnabled)
            element.Add(E("surfaceRelativeHeight", F(settings.AltitudeMeters)));

        return element;
    }

    private static XElement BuildTemplatePlacemark(FlightPlanResult plan) =>
        plan.Settings.Mode switch
        {
            FlightPlanMode.Mapping2D => BuildMapping2DPlacemark(plan),
            FlightPlanMode.Mapping3D => BuildMapping3DPlacemark(plan),
            FlightPlanMode.MappingStrip => BuildMappingStripPlacemark(plan),
            _ => throw new ArgumentOutOfRangeException(nameof(plan.Settings.Mode))
        };

    private static XElement BuildMapping2DPlacemark(FlightPlanResult plan)
    {
        var settings = plan.Settings;
        var placemark = new XElement(Kml + "Placemark",
            E("elevationOptimizeEnable", 0),
            E("smartObliqueEnable", settings.SmartObliqueEnabled ? 1 : 0));

        if (settings.SmartObliqueEnabled)
            placemark.Add(E("smartObliqueGimbalPitch", F(settings.ObliqueGimbalPitchDegrees)));

        placemark.Add(
            E("shootType", "distance"),
            E("direction", Math.Round(settings.GridAngleDegrees)),
            E("margin", 0),
            Overlap(settings, includeInclined: settings.SmartObliqueEnabled),
            E("ellipsoidHeight", F(settings.AltitudeMeters)),
            E("height", F(settings.AltitudeMeters)),
            E("facadeWaylineEnable", 0),
            new XElement(Wpml + "mappingHeadingParam",
                E("mappingHeadingMode", "followWayline")),
            E("gimbalPitchMode", "fixed"),
            E("gimbalPitchAngle", F(settings.GimbalPitchDegrees)),
            Polygon(plan.Geometry));

        return placemark;
    }

    private static XElement BuildMapping3DPlacemark(FlightPlanResult plan)
    {
        var settings = plan.Settings;
        return new XElement(Kml + "Placemark",
            E("inclinedGimbalPitch", F(settings.ObliqueGimbalPitchDegrees)),
            E("inclinedFlightSpeed", F(settings.SpeedMetersPerSecond)),
            E("shootType", "distance"),
            E("direction", Math.Round(settings.GridAngleDegrees)),
            E("margin", 0),
            Overlap(settings, includeInclined: true),
            E("ellipsoidHeight", F(settings.AltitudeMeters)),
            E("height", F(settings.AltitudeMeters)),
            Polygon(plan.Geometry));
    }

    private static XElement BuildMappingStripPlacemark(FlightPlanResult plan)
    {
        var settings = plan.Settings;
        var coordinates = string.Join(" ",
            plan.Geometry.Select(p =>
                FormattableString.Invariant($"{p.Longitude:F8},{p.Latitude:F8},{settings.AltitudeMeters:F3}")));

        return new XElement(Kml + "Placemark",
            E("caliFlightEnable", 0),
            E("shootType", "distance"),
            E("direction", Math.Round(settings.GridAngleDegrees)),
            E("margin", 0),
            E("singleLineEnable", 0),
            E("cuttingDistance", 500),
            E("boundaryOptimEnable", 1),
            E("leftExtend", Math.Round(settings.StripHalfWidthMeters)),
            E("rightExtend", Math.Round(settings.StripHalfWidthMeters)),
            E("includeCenterEnable", 1),
            Overlap(settings, includeInclined: false),
            E("ellipsoidHeight", F(settings.AltitudeMeters)),
            E("height", F(settings.AltitudeMeters)),
            E("stripUseTemplateAltitude", 1),
            new XElement(Kml + "LineString",
                new XElement(Kml + "coordinates", coordinates)));
    }

    private static XElement BuildWaylineFolder(FlightPlanResult plan, FlightPass pass)
    {
        var settings = plan.Settings;
        var folder = new XElement(Kml + "Folder",
            E("templateId", 0),
            E("executeHeightMode", "relativeToStartPoint"),
            E("waylineId", pass.WaylineId),
            E("autoFlightSpeed", F(settings.SpeedMetersPerSecond)));

        var waypointIndex = 0;
        var actionGroupId = pass.WaylineId * 10_000 + 1;

        foreach (var segment in pass.Segments)
        {
            var startIndex = waypointIndex;
            folder.Add(Waypoint(segment.Start, waypointIndex++, settings));
            var endIndex = waypointIndex;
            var endPlacemark = Waypoint(segment.End, waypointIndex++, settings);

            endPlacemark.Add(new XElement(Wpml + "actionGroup",
                E("actionGroupId", actionGroupId++),
                E("actionGroupStartIndex", startIndex),
                E("actionGroupEndIndex", endIndex),
                E("actionGroupMode", "sequence"),
                new XElement(Wpml + "actionTrigger",
                    E("actionTriggerType", "multipleDistance"),
                    E("actionTriggerParam", F(plan.PhotoSpacingMeters))),
                new XElement(Wpml + "action",
                    E("actionId", 0),
                    E("actionActuatorFunc", "takePhoto"),
                    new XElement(Wpml + "actionActuatorFuncParam",
                        E("fileSuffix", $"map_{pass.WaylineId:D2}_{startIndex:D4}"),
                        E("payloadPositionIndex", 0),
                        E("useGlobalPayloadLensIndex", 1)))));

            folder.Add(endPlacemark);
        }

        if (folder.Elements(Kml + "Placemark").FirstOrDefault() is XElement first)
        {
            first.Add(new XElement(Wpml + "actionGroup",
                E("actionGroupId", pass.WaylineId * 10_000),
                E("actionGroupStartIndex", 0),
                E("actionGroupEndIndex", 0),
                E("actionGroupMode", "sequence"),
                new XElement(Wpml + "actionTrigger",
                    E("actionTriggerType", "reachPoint")),
                new XElement(Wpml + "action",
                    E("actionId", 0),
                    E("actionActuatorFunc", "gimbalRotate"),
                    new XElement(Wpml + "actionActuatorFuncParam",
                        E("gimbalRotateMode", "absoluteAngle"),
                        E("gimbalPitchRotateEnable", 1),
                        E("gimbalPitchRotateAngle", F(pass.GimbalPitchDegrees)),
                        E("gimbalRollRotateEnable", 0),
                        E("gimbalRollRotateAngle", 0),
                        E("gimbalYawRotateEnable", 0),
                        E("gimbalYawRotateAngle", 0),
                        E("gimbalRotateTimeEnable", 0),
                        E("gimbalRotateTime", 0),
                        E("payloadPositionIndex", 0)))));
        }

        return folder;
    }

    private static XElement Waypoint(GeoPoint point, int index, FlightPlanSettings settings) =>
        new(Kml + "Placemark",
            new XElement(Kml + "Point",
                new XElement(Kml + "coordinates",
                    FormattableString.Invariant($"{point.Longitude:F8},{point.Latitude:F8}"))),
            E("index", index),
            E("executeHeight", F(settings.AltitudeMeters)),
            E("waypointSpeed", F(settings.SpeedMetersPerSecond)),
            new XElement(Wpml + "waypointHeadingParam",
                E("waypointHeadingMode", "followWayline")),
            new XElement(Wpml + "waypointTurnParam",
                E("waypointTurnMode", "toPointAndStopWithDiscontinuityCurvature"),
                E("waypointTurnDampingDist", 0)));

    private static XElement MissionConfig(
        FlightPlanSettings settings,
        DjiModelInfo model,
        bool includeRthHeight = false)
    {
        var config = new XElement(Wpml + "missionConfig",
            E("flyToWaylineMode", "safely"),
            E("finishAction", "goHome"),
            E("exitOnRCLost", "goContinue"),
            E("executeRCLostAction", "hover"),
            E("takeOffSecurityHeight", Math.Max(20d, Math.Min(settings.AltitudeMeters, 120d))),
            E("globalTransitionalSpeed", Math.Min(settings.SpeedMetersPerSecond, 15d)));

        if (includeRthHeight)
            config.Add(E("globalRTHHeight", Math.Max(30d, Math.Min(settings.AltitudeMeters, 120d))));

        config.Add(
            new XElement(Wpml + "droneInfo",
                E("droneEnumValue", 77),
                E("droneSubEnumValue", model.DroneSubType)),
            new XElement(Wpml + "payloadInfo",
                E("payloadEnumValue", model.PayloadEnum),
                E("payloadPositionIndex", 0)));

        return config;
    }

    private static XElement Polygon(IReadOnlyList<GeoPoint> polygon)
    {
        var coordinates = string.Join(" ",
            polygon
                .Concat([polygon[0]])
                .Select(p => FormattableString.Invariant($"{p.Longitude:F8},{p.Latitude:F8},0")));

        return new XElement(Kml + "Polygon",
            new XElement(Kml + "outerBoundaryIs",
                new XElement(Kml + "LinearRing",
                    new XElement(Kml + "coordinates", coordinates))));
    }

    private static XElement Overlap(FlightPlanSettings settings, bool includeInclined)
    {
        var element = new XElement(Wpml + "overlap",
            E("orthoCameraOverlapH", settings.FrontOverlapPercent),
            E("orthoCameraOverlapW", settings.SideOverlapPercent));

        if (includeInclined)
        {
            element.Add(
                E("inclinedCameraOverlapH", settings.FrontOverlapPercent),
                E("inclinedCameraOverlapW", settings.SideOverlapPercent));
        }

        return element;
    }

    private static string TemplateType(FlightPlanMode mode) =>
        mode switch
        {
            FlightPlanMode.Mapping2D => "mapping2d",
            FlightPlanMode.Mapping3D => "mapping3d",
            FlightPlanMode.MappingStrip => "mappingStrip",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static DjiModelInfo DjiModel(DjiAircraftProfile profile) =>
        profile switch
        {
            DjiAircraftProfile.M3E => new(0, 66, "wide"),
            DjiAircraftProfile.M3T => new(1, 67, "wide"),
            DjiAircraftProfile.M3M => new(2, 68, "wide,narrow_band"),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

    private static XElement E(string name, object value) =>
        new(Wpml + name, Convert.ToString(value, CultureInfo.InvariantCulture));

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WriteXml(ZipArchive archive, string entryName, XDocument document)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        document.Save(entryStream);
    }

    private sealed record DjiModelInfo(int DroneSubType, int PayloadEnum, string ImageFormat);
}
