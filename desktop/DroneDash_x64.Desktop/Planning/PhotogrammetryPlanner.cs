namespace DroneDash_x64.Desktop.Planning;

public static class PhotogrammetryPlanner
{
    private const double EarthRadiusMeters = 6_378_137d;
    private const double DegToRad = Math.PI / 180d;
    private const double RadToDeg = 180d / Math.PI;

    public static FlightPlanResult Generate(
        IReadOnlyList<GeoPoint> polygon,
        FlightPlanSettings settings)
    {
        Validate(polygon, settings);

        var camera = CameraFor(settings.Aircraft);
        var frame = camera.FrameAt(settings.AltitudeMeters);
        var lineSpacing = frame.WidthMeters * (1d - settings.SideOverlapPercent / 100d);
        var photoSpacing = frame.HeightMeters * (1d - settings.FrontOverlapPercent / 100d);

        if (lineSpacing <= 0.1 || photoSpacing <= 0.1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Überlappung erzeugt einen zu kleinen Linien-/Fotoabstand.");

        var originLat = polygon.Average(p => p.Latitude);
        var originLon = polygon.Average(p => p.Longitude);
        var local = polygon.Select(p => ToLocal(p, originLat, originLon)).ToList();
        var angle = settings.GridAngleDegrees * DegToRad;
        var rotated = local.Select(p => Rotate(p, -angle)).ToList();

        var minY = rotated.Min(p => p.Y);
        var maxY = rotated.Max(p => p.Y);
        var height = maxY - minY;
        var scanLines = new List<double>();

        if (height <= lineSpacing)
        {
            scanLines.Add((minY + maxY) / 2d);
        }
        else
        {
            var y = minY + lineSpacing / 2d;
            while (y <= maxY)
            {
                scanLines.Add(y);
                y += lineSpacing;
            }

            if (scanLines.Count == 0)
                scanLines.Add((minY + maxY) / 2d);
        }

        var routeSegments = new List<RouteSegment>();
        var reverse = false;

        foreach (var y in scanLines)
        {
            var intersections = HorizontalIntersections(rotated, y);
            if (intersections.Count < 2)
                continue;

            var pairs = new List<(double A, double B)>();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                if (intersections[i + 1] - intersections[i] > 0.25)
                    pairs.Add((intersections[i], intersections[i + 1]));
            }

            if (reverse)
                pairs.Reverse();

            foreach (var pair in pairs)
            {
                var a = reverse ? pair.B : pair.A;
                var b = reverse ? pair.A : pair.B;
                var startLocal = Rotate(new LocalPoint(a, y), angle);
                var endLocal = Rotate(new LocalPoint(b, y), angle);
                var start = ToGeo(startLocal, originLat, originLon);
                var end = ToGeo(endLocal, originLat, originLon);
                routeSegments.Add(new RouteSegment(start, end, Math.Abs(b - a)));
            }

            reverse = !reverse;
        }

        if (routeSegments.Count == 0)
            throw new InvalidOperationException("Aus dem Polygon konnte kein Mapping-Raster erzeugt werden.");

        var area = Math.Abs(PolygonArea(local));
        var segmentDistance = routeSegments.Sum(s => s.LengthMeters);
        var transitDistance = 0d;
        for (var i = 1; i < routeSegments.Count; i++)
            transitDistance += DistanceMeters(routeSegments[i - 1].End, routeSegments[i].Start);

        var flightDistance = segmentDistance + transitDistance;
        var photos = routeSegments.Sum(segment =>
            Math.Max(2, (int)Math.Ceiling(segment.LengthMeters / photoSpacing) + 1));
        var seconds = flightDistance / settings.SpeedMetersPerSecond;

        return new FlightPlanResult(
            settings,
            polygon.ToArray(),
            routeSegments,
            area,
            frame.WidthMeters,
            frame.HeightMeters,
            lineSpacing,
            photoSpacing,
            frame.GsdCentimeters,
            frame.SecondaryGsdCentimeters,
            flightDistance,
            photos,
            TimeSpan.FromSeconds(seconds),
            camera.Name,
            camera.Note);
    }

    private static void Validate(IReadOnlyList<GeoPoint> polygon, FlightPlanSettings settings)
    {
        if (polygon.Count < 3)
            throw new InvalidOperationException("Mindestens drei Polygonpunkte sind erforderlich.");
        if (polygon.Any(p => p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180))
            throw new ArgumentOutOfRangeException(nameof(polygon), "Polygon enthält ungültige WGS84-Koordinaten.");
        if (settings.AltitudeMeters is < 10 or > 500)
            throw new ArgumentOutOfRangeException(nameof(settings.AltitudeMeters), "Planungshöhe muss zwischen 10 und 500 m liegen.");
        if (settings.SpeedMetersPerSecond is <= 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(settings.SpeedMetersPerSecond), "Fluggeschwindigkeit muss > 0 und <= 15 m/s sein.");
        if (settings.FrontOverlapPercent is < 10 or > 95 || settings.SideOverlapPercent is < 10 or > 95)
            throw new ArgumentOutOfRangeException(nameof(settings), "Überlappungen müssen zwischen 10 und 95 % liegen.");
        if (settings.GridAngleDegrees is < 0 or >= 360)
            throw new ArgumentOutOfRangeException(nameof(settings.GridAngleDegrees), "Rasterwinkel muss zwischen 0 und < 360° liegen.");
        if (settings.GimbalPitchDegrees is < -90 or > -30)
            throw new ArgumentOutOfRangeException(nameof(settings.GimbalPitchDegrees), "Gimbal-Pitch muss für Mapping zwischen -90° und -30° liegen.");
    }

    private static CameraModel CameraFor(DjiAircraftProfile aircraft) =>
        aircraft switch
        {
            DjiAircraftProfile.M3E => CameraModel.M3E,
            DjiAircraftProfile.M3M => CameraModel.M3M,
            DjiAircraftProfile.M3T => CameraModel.M3T,
            _ => throw new ArgumentOutOfRangeException(nameof(aircraft))
        };

    private static List<double> HorizontalIntersections(IReadOnlyList<LocalPoint> polygon, double y)
    {
        var intersections = new List<double>();

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];

            if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
            {
                var x = a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                intersections.Add(x);
            }
        }

        intersections.Sort();
        return intersections;
    }

    private static double PolygonArea(IReadOnlyList<LocalPoint> polygon)
    {
        double sum = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2d;
    }

    private static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        var lat1 = a.Latitude * DegToRad;
        var lat2 = b.Latitude * DegToRad;
        var dLat = (b.Latitude - a.Latitude) * DegToRad;
        var dLon = (b.Longitude - a.Longitude) * DegToRad;
        var h = Math.Sin(dLat / 2d) * Math.Sin(dLat / 2d) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLon / 2d) * Math.Sin(dLon / 2d);
        return 2d * EarthRadiusMeters * Math.Asin(Math.Min(1d, Math.Sqrt(h)));
    }

    private static LocalPoint ToLocal(GeoPoint point, double originLat, double originLon)
    {
        var cosLat = Math.Cos(originLat * DegToRad);
        return new LocalPoint(
            (point.Longitude - originLon) * DegToRad * EarthRadiusMeters * cosLat,
            (point.Latitude - originLat) * DegToRad * EarthRadiusMeters);
    }

    private static GeoPoint ToGeo(LocalPoint point, double originLat, double originLon)
    {
        var cosLat = Math.Cos(originLat * DegToRad);
        return new GeoPoint(
            originLat + point.Y / EarthRadiusMeters * RadToDeg,
            originLon + point.X / (EarthRadiusMeters * cosLat) * RadToDeg);
    }

    private static LocalPoint Rotate(LocalPoint point, double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new LocalPoint(
            point.X * cos - point.Y * sin,
            point.X * sin + point.Y * cos);
    }

    private readonly record struct LocalPoint(double X, double Y);

    private sealed record CameraModel(
        string Name,
        int WidthPixels,
        int HeightPixels,
        double? SensorWidthMm,
        double? SensorHeightMm,
        double? FocalLengthMm,
        double? DiagonalFovDegrees,
        double? SecondaryHorizontalFovDegrees,
        int? SecondaryWidthPixels,
        string Note)
    {
        public static readonly CameraModel M3E = new(
            "Mavic 3 Enterprise · 20 MP 4/3 Wide",
            5280, 3956,
            17.424, 13.0548, 12.0,
            null, null, null,
            "Survey-Profil: mechanischer Verschluss; GSD basiert auf 3,3 µm Pixelpitch und 12 mm realer Brennweite.");

        public static readonly CameraModel M3M = new(
            "Mavic 3 Multispectral · RGB 20 MP + MS",
            5280, 3956,
            17.424, 13.0548, 12.0,
            null, 61.2, 2592,
            "RGB-GSD basiert auf der 20-MP-Kamera; zusätzlicher MS-GSD-Wert nutzt 61,2° horizontales FOV und 2592 px.");

        public static readonly CameraModel M3T = new(
            "Mavic 3 Thermal · Wide 48 MP",
            8000, 6000,
            null, null, null,
            84.0, null, null,
            "Inspektionsprofil: 48-MP-Wide ohne mechanischen Verschluss; GSD ist geometrisch aus 84° diagonalem FOV abgeleitet und nicht als Survey-Kalibrierung zu verstehen.");

        public CameraFrame FrameAt(double altitude)
        {
            double width;
            double height;

            if (SensorWidthMm is double sw &&
                SensorHeightMm is double sh &&
                FocalLengthMm is double focal)
            {
                width = altitude * sw / focal;
                height = altitude * sh / focal;
            }
            else if (DiagonalFovDegrees is double diagonal)
            {
                var aspect = (double)WidthPixels / HeightPixels;
                var tanDiagonal = Math.Tan(diagonal * DegToRad / 2d);
                var tanVertical = tanDiagonal / Math.Sqrt(aspect * aspect + 1d);
                var tanHorizontal = aspect * tanVertical;
                width = 2d * altitude * tanHorizontal;
                height = 2d * altitude * tanVertical;
            }
            else
            {
                throw new InvalidOperationException("Kamerageometrie unvollständig.");
            }

            var gsdCm = width / WidthPixels * 100d;
            double? secondary = null;
            if (SecondaryHorizontalFovDegrees is double secondaryFov && SecondaryWidthPixels is int secondaryWidth)
            {
                var secondaryWidthMeters = 2d * altitude * Math.Tan(secondaryFov * DegToRad / 2d);
                secondary = secondaryWidthMeters / secondaryWidth * 100d;
            }

            return new CameraFrame(width, height, gsdCm, secondary);
        }
    }

    private sealed record CameraFrame(
        double WidthMeters,
        double HeightMeters,
        double GsdCentimeters,
        double? SecondaryGsdCentimeters);
}
