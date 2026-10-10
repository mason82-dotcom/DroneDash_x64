using System.Globalization;
using DroneDash_x64.Desktop.Models;
using DroneDash_x64.Desktop.Photogrammetry;

namespace DroneDash_x64.Desktop.Planning;

/// <summary>Read-only aircraft position for the planning map, taken from the bridge status.</summary>
public sealed record LiveAircraftState(
    GeoPoint Position,
    double? RelativeAltitudeMeters,
    double? HeadingDegrees,
    double? GroundSpeedMs,
    bool? IsFlying,
    GeoPoint? Home,
    DateTimeOffset ReceivedAt)
{
    /// <summary>
    /// The state from a bridge status, or null when it has no usable fix. DJI reports 0/0 or
    /// NaN without GPS; those must not draw a marker in the Gulf of Guinea.
    /// </summary>
    public static LiveAircraftState? FromStatus(StatusDto? status, DateTimeOffset receivedAt)
    {
        if (status is null || !status.ProductConnected || !IsFix(status.Latitude, status.Longitude))
            return null;

        GeoPoint? home = status.HomeLocationSet == true && IsFix(status.HomeLatitude, status.HomeLongitude)
            ? new GeoPoint(status.HomeLatitude!.Value, status.HomeLongitude!.Value)
            : null;

        return new LiveAircraftState(
            new GeoPoint(status.Latitude!.Value, status.Longitude!.Value),
            Finite(status.AltitudeMeters),
            Finite(status.CompassHeadingDegrees) ?? Finite(status.YawDegrees),
            Finite(status.GroundSpeedMs),
            status.IsFlying,
            home,
            receivedAt);
    }

    private static bool IsFix(double? latitude, double? longitude) =>
        latitude is { } lat && longitude is { } lon &&
        double.IsFinite(lat) && double.IsFinite(lon) &&
        lat is >= -90 and <= 90 && lon is >= -180 and <= 180 &&
        !(Math.Abs(lat) < 1e-9 && Math.Abs(lon) < 1e-9);

    private static double? Finite(double? value) =>
        value is { } v && double.IsFinite(v) ? v : null;
}

/// <summary>Flown track of the current connection, thinned and bounded.</summary>
public sealed class LiveAircraftTrack
{
    public const int MaxPoints = 5000;

    /// <summary>Positions closer than this to the last kept point are not added (hover jitter).</summary>
    public const double MinimumStepMeters = 1.0;

    private readonly List<GeoPoint> _points = [];

    public IReadOnlyList<GeoPoint> Points => _points;

    /// <summary>Adds the position; returns true when it became a new track point.</summary>
    public bool Add(GeoPoint position)
    {
        if (_points.Count > 0 &&
            RouteImageMatcher.DistanceToSegmentMeters(position, _points[^1], _points[^1]) < MinimumStepMeters)
        {
            return false;
        }

        _points.Add(position);
        if (_points.Count > MaxPoints)
        {
            // Keep the shape of the whole flight: drop every second point of the older half.
            var half = _points.Count / 2;
            var thinned = _points.Take(half).Where((_, i) => i % 2 == 0).Concat(_points.Skip(half)).ToList();
            _points.Clear();
            _points.AddRange(thinned);
        }

        return true;
    }

    public void Clear() => _points.Clear();
}

public static class LiveAircraftDescription
{
    /// <summary>Side-panel line: height, speed and how far the aircraft is from the planned route.</summary>
    public static string Describe(LiveAircraftState? state, FlightPlanResult? plan, DateTimeOffset now)
    {
        if (state is null)
            return "Keine Live-Position (Bridge nicht verbunden oder kein GPS).";

        var culture = CultureInfo.GetCultureInfo("de-DE");
        var parts = new List<string>
        {
            string.Create(culture, $"{state.Position.Latitude:F6}, {state.Position.Longitude:F6}")
        };

        if (state.RelativeAltitudeMeters is { } altitude)
            parts.Add(string.Create(culture, $"{altitude:F1} m über Start"));
        if (state.GroundSpeedMs is { } speed)
            parts.Add(string.Create(culture, $"{speed:F1} m/s"));
        if (state.IsFlying == false)
            parts.Add("am Boden");

        if (plan is not null && RouteImageMatcher.FindNearest(state.Position, plan) is { } nearest)
        {
            parts.Add(string.Create(
                culture,
                $"{nearest.DistanceMeters:F0} m neben {nearest.PassName} (WL {nearest.WaylineId})"));
        }

        var age = now - state.ReceivedAt;
        if (age > TimeSpan.FromSeconds(5))
            parts.Add(string.Create(culture, $"⚠ {age.TotalSeconds:F0} s alt"));

        return string.Join(" · ", parts);
    }
}
