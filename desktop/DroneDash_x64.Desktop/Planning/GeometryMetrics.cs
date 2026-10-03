namespace DroneDash_x64.Desktop.Planning;

/// <summary>
/// Area, length and validity metrics for planning geometry. Shared by the planner
/// and the map editor so live figures and route statistics agree.
/// </summary>
public static class GeometryMetrics
{
    private const double EarthRadiusMeters = 6_378_137d;
    private const double DegToRad = Math.PI / 180d;

    public static double PolygonAreaSquareMeters(IReadOnlyList<GeoPoint> polygon)
    {
        if (polygon.Count < 3)
            return 0d;

        var local = ToLocal(polygon);

        double sum = 0;
        for (var i = 0; i < local.Count; i++)
        {
            var a = local[i];
            var b = local[(i + 1) % local.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return Math.Abs(sum / 2d);
    }

    public static double PolylineLengthMeters(IReadOnlyList<GeoPoint> line)
    {
        var result = 0d;
        for (var i = 1; i < line.Count; i++)
            result += DistanceMeters(line[i - 1], line[i]);
        return result;
    }

    public static double PolygonPerimeterMeters(IReadOnlyList<GeoPoint> polygon) =>
        polygon.Count < 2
            ? 0d
            : PolylineLengthMeters(polygon) +
              (polygon.Count > 2 ? DistanceMeters(polygon[^1], polygon[0]) : 0d);

    public static double DistanceMeters(GeoPoint a, GeoPoint b)
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

    /// <summary>
    /// True when two non-adjacent edges of the closed polygon cross. Dragging a
    /// vertex across the opposite edge easily produces such a "bow tie", which
    /// yields a wrong area and a broken survey grid.
    /// </summary>
    public static bool IsSelfIntersecting(IReadOnlyList<GeoPoint> polygon)
    {
        if (polygon.Count < 4)
            return false;

        var local = ToLocal(polygon);
        var count = local.Count;

        for (var i = 0; i < count; i++)
        {
            var a1 = local[i];
            var a2 = local[(i + 1) % count];

            for (var j = i + 1; j < count; j++)
            {
                // Edges sharing a vertex always "touch"; skip them.
                if (j == i || (j + 1) % count == i || (i + 1) % count == j)
                    continue;

                if (SegmentsIntersect(a1, a2, local[j], local[(j + 1) % count]))
                    return true;
            }
        }

        return false;
    }

    private static bool SegmentsIntersect(
        LocalPoint p1,
        LocalPoint p2,
        LocalPoint q1,
        LocalPoint q2)
    {
        var d1 = Cross(q1, q2, p1);
        var d2 = Cross(q1, q2, p2);
        var d3 = Cross(p1, p2, q1);
        var d4 = Cross(p1, p2, q2);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        return (d1 == 0 && OnSegment(q1, q2, p1)) ||
               (d2 == 0 && OnSegment(q1, q2, p2)) ||
               (d3 == 0 && OnSegment(p1, p2, q1)) ||
               (d4 == 0 && OnSegment(p1, p2, q2));
    }

    private static double Cross(LocalPoint a, LocalPoint b, LocalPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool OnSegment(LocalPoint a, LocalPoint b, LocalPoint p) =>
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) &&
        p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);

    private static List<LocalPoint> ToLocal(IReadOnlyList<GeoPoint> points)
    {
        var originLat = points.Average(p => p.Latitude);
        var originLon = points.Average(p => p.Longitude);
        var cosLat = Math.Cos(originLat * DegToRad);

        return points
            .Select(p => new LocalPoint(
                (p.Longitude - originLon) * DegToRad * EarthRadiusMeters * cosLat,
                (p.Latitude - originLat) * DegToRad * EarthRadiusMeters))
            .ToList();
    }

    private readonly record struct LocalPoint(double X, double Y);
}
