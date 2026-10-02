using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace DroneDash_x64.Desktop.Planning;

public static class DjiWpmlExporter
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly XNamespace Wpml = "http://www.dji.com/wpmz/1.0.2";

    public static void ExportKmz(string path, FlightPlanResult plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteXml(archive, "wpmz/template.kml", BuildTemplate(plan));
        WriteXml(archive, "wpmz/waylines.wpml", BuildWaylines(plan));
        archive.CreateEntry("wpmz/res/");
    }

    public static XDocument BuildTemplate(FlightPlanResult plan)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var settings = plan.Settings;
        var model = DjiModel(settings.Aircraft);
        var coordinates = string.Join(" ",
            plan.Polygon
                .Concat([plan.Polygon[0]])
                .Select(p => FormattableString.Invariant($"{p.Longitude:F8},{p.Latitude:F8},0")));

        var folder = new XElement(Kml + "Folder",
            E("templateType", "mapping2d"),
            E("templateId", 0),
            new XElement(Wpml + "waylineCoordinateSysParam",
                E("coordinateMode", "WGS84"),
                E("heightMode", "relativeToStartPoint"),
                E("globalShootHeight", F(settings.AltitudeMeters)),
                E("positioningType", "GPS"),
                E("surfaceFollowModeEnable", 0)),
            E("autoFlightSpeed", F(settings.SpeedMetersPerSecond)),
            new XElement(Wpml + "payloadParam",
                E("payloadPositionIndex", 0),
                E("imageFormat", model.ImageFormat)),
            new XElement(Kml + "Placemark",
                E("elevationOptimizeEnable", 0),
                E("shootType", "distance"),
                E("direction", Math.Round(settings.GridAngleDegrees)),
                E("margin", 0),
                new XElement(Wpml + "overlap",
                    E("orthoCameraOverlapH", settings.FrontOverlapPercent),
                    E("orthoCameraOverlapW", settings.SideOverlapPercent)),
                E("ellipsoidHeight", F(settings.AltitudeMeters)),
                E("height", F(settings.AltitudeMeters)),
                E("facadeWaylineEnable", 0),
                new XElement(Wpml + "mappingHeadingParam",
                    E("mappingHeadingMode", "followWayline")),
                E("gimbalPitchMode", "fixed"),
                E("gimbalPitchAngle", F(settings.GimbalPitchDegrees)),
                new XElement(Kml + "Polygon",
                    new XElement(Kml + "outerBoundaryIs",
                        new XElement(Kml + "LinearRing",
                            new XElement(Kml + "coordinates", coordinates))))));

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
        var folder = new XElement(Kml + "Folder",
            E("templateId", 0),
            E("executeHeightMode", "relativeToStartPoint"),
            E("waylineId", 0),
            E("autoFlightSpeed", F(settings.SpeedMetersPerSecond)));

        var waypointIndex = 0;
        var actionGroupId = 1;

        foreach (var segment in plan.Segments)
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
                        E("fileSuffix", $"map_{startIndex:D4}"),
                        E("payloadPositionIndex", 0),
                        E("useGlobalPayloadLensIndex", 1)))));

            folder.Add(endPlacemark);
        }

        if (folder.Elements(Kml + "Placemark").FirstOrDefault() is XElement first)
        {
            first.Add(new XElement(Wpml + "actionGroup",
                E("actionGroupId", 0),
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
                        E("gimbalPitchRotateAngle", F(settings.GimbalPitchDegrees)),
                        E("gimbalRollRotateEnable", 0),
                        E("gimbalRollRotateAngle", 0),
                        E("gimbalYawRotateEnable", 0),
                        E("gimbalYawRotateAngle", 0),
                        E("gimbalRotateTimeEnable", 0),
                        E("gimbalRotateTime", 0),
                        E("payloadPositionIndex", 0)))));
        }

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Kml + "kml",
                new XAttribute(XNamespace.Xmlns + "wpml", Wpml),
                new XElement(Kml + "Document",
                    MissionConfig(settings, model, includeRthHeight: true),
                    folder)));
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
