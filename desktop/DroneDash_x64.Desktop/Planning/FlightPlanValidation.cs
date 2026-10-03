namespace DroneDash_x64.Desktop.Planning;

internal static class FlightPlanValidation
{
    public static void ValidateInput(
        IReadOnlyList<GeoPoint> geometry,
        FlightPlanSettings settings) =>
        Validate(
            geometry,
            settings,
            requireExecutableGeometry: true);

    public static void ValidateProjectData(
        IReadOnlyList<GeoPoint> geometry,
        FlightPlanSettings settings) =>
        Validate(
            geometry,
            settings,
            requireExecutableGeometry: false);

    private static void Validate(
        IReadOnlyList<GeoPoint> geometry,
        FlightPlanSettings settings,
        bool requireExecutableGeometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(settings);

        if (!Enum.IsDefined(settings.Aircraft))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.Aircraft),
                "Nicht unterstütztes DJI-Luftfahrzeugprofil.");
        }

        if (!Enum.IsDefined(settings.Mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.Mode),
                "Nicht unterstützter Flugplanmodus.");
        }

        if (geometry.Count == 0)
        {
            throw new InvalidOperationException(
                "Die Planung enthält keine Geometrie.");
        }

        if (requireExecutableGeometry)
        {
            var minimumPoints =
                settings.Mode ==
                    FlightPlanMode.MappingStrip
                    ? 2
                    : 3;

            if (geometry.Count <
                minimumPoints)
            {
                throw new InvalidOperationException(
                    settings.Mode ==
                        FlightPlanMode.MappingStrip
                        ? "Für Strip-Mapping sind mindestens zwei Trassenpunkte erforderlich."
                        : "Mindestens drei Polygonpunkte sind erforderlich.");
            }
        }

        if (geometry.Any(point =>
                !double.IsFinite(
                    point.Latitude) ||
                !double.IsFinite(
                    point.Longitude) ||
                point.Latitude is
                    < -90 or > 90 ||
                point.Longitude is
                    < -180 or > 180))
        {
            throw new ArgumentOutOfRangeException(
                nameof(geometry),
                "Geometrie enthält ungültige oder nicht-endliche WGS84-Koordinaten.");
        }

        if (!double.IsFinite(
                settings.AltitudeMeters) ||
            settings.AltitudeMeters is
                < 10 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.AltitudeMeters),
                "Planungshöhe muss endlich und zwischen 10 und 500 m liegen.");
        }

        if (!double.IsFinite(
                settings.SpeedMetersPerSecond) ||
            settings.SpeedMetersPerSecond is
                <= 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.SpeedMetersPerSecond),
                "Fluggeschwindigkeit muss endlich, > 0 und <= 15 m/s sein.");
        }

        if (settings.FrontOverlapPercent is
                < 10 or > 95 ||
            settings.SideOverlapPercent is
                < 10 or > 95)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "Überlappungen müssen zwischen 10 und 95 % liegen.");
        }

        if (!double.IsFinite(
                settings.GridAngleDegrees) ||
            settings.GridAngleDegrees is
                < 0 or >= 360)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.GridAngleDegrees),
                "Rasterwinkel muss endlich und zwischen 0 und < 360° liegen.");
        }

        if (!double.IsFinite(
                settings.GimbalPitchDegrees) ||
            settings.GimbalPitchDegrees is
                < -90 or > -30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.GimbalPitchDegrees),
                "Gimbal-Pitch muss endlich und für Mapping zwischen -90° und -30° liegen.");
        }

        if (!double.IsFinite(
                settings.ObliqueGimbalPitchDegrees) ||
            settings.ObliqueGimbalPitchDegrees is
                < -90 or > -30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.ObliqueGimbalPitchDegrees),
                "Oblique-Pitch muss endlich und zwischen -90° und -30° liegen.");
        }

        if (!double.IsFinite(
                settings.StripHalfWidthMeters) ||
            settings.StripHalfWidthMeters is
                <= 0 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.StripHalfWidthMeters),
                "Strip-Halbbreite muss endlich, > 0 und <= 500 m sein.");
        }

        if (settings.SmartObliqueEnabled &&
            settings.Mode !=
                FlightPlanMode.Mapping2D)
        {
            throw new InvalidOperationException(
                "Smart Oblique ist in DroneDash derzeit nur für Mapping 2D als DJI-Templateoption verfügbar.");
        }
    }
}
