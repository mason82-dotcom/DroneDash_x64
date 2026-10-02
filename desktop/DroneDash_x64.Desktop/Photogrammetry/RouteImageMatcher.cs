using DroneDash_x64.Desktop.Planning;

namespace DroneDash_x64.Desktop.Photogrammetry;

public sealed record RouteImageAssignment(
    int WaylineId,
    string PassName,
    int SegmentIndex,
    double DistanceMeters);

public static class RouteImageMatcher
{
    private const double EarthRadiusMeters = 6_378_137d;
    private const double DegToRad = Math.PI / 180d;

    public static RouteImageAssignment? FindNearest(
        GeoPoint point,
        FlightPlanResult plan)
    {
        RouteImageAssignment? best = null;

        foreach (var pass in plan.Passes)
        {
            for (var index = 0; index < pass.Segments.Count; index++)
            {
                var distance = DistanceToSegmentMeters(
                    point,
                    pass.Segments[index].Start,
                    pass.Segments[index].End);

                if (best is null || distance < best.DistanceMeters)
                {
                    best = new RouteImageAssignment(
                        pass.WaylineId,
                        pass.Name,
                        index,
                        distance);
                }
            }
        }

        return best;
    }

    public static double DistanceToSegmentMeters(
        GeoPoint point,
        GeoPoint start,
        GeoPoint end)
    {
        var originLat = point.Latitude;
        var originLon = point.Longitude;

        var p = ToLocal(point, originLat, originLon);
        var a = ToLocal(start, originLat, originLon);
        var b = ToLocal(end, originLat, originLon);

        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= 1e-9)
            return Math.Sqrt(
                (p.X - a.X) * (p.X - a.X) +
                (p.Y - a.Y) * (p.Y - a.Y));

        var t =
            ((p.X - a.X) * dx + (p.Y - a.Y) * dy) /
            lengthSquared;
        t = Math.Clamp(t, 0d, 1d);

        var closestX = a.X + t * dx;
        var closestY = a.Y + t * dy;
        var ex = p.X - closestX;
        var ey = p.Y - closestY;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static LocalPoint ToLocal(
        GeoPoint point,
        double originLat,
        double originLon)
    {
        var cosLat = Math.Cos(originLat * DegToRad);
        return new LocalPoint(
            (point.Longitude - originLon) *
            DegToRad *
            EarthRadiusMeters *
            cosLat,
            (point.Latitude - originLat) *
            DegToRad *
            EarthRadiusMeters);
    }

    private readonly record struct LocalPoint(double X, double Y);
}
