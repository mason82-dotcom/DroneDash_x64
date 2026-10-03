using System.IO;
using System.Text.Json;

namespace DroneDash_x64.Desktop.Photogrammetry;

/// <summary>Quality figures from products/colmap-products.json.</summary>
public sealed record ColmapProductsReport(
    long Points,
    int RegisteredImages,
    int UsedImages,
    double RmseMeters,
    double MaxResidualMeters,
    double Resolution,
    IReadOnlyList<string> Rejected)
{
    // Above this the camera positions disagree with the GPS more than RTK/GPS noise explains.
    public const double RmseWarningMeters = 1.0;

    public string SummaryText =>
        $"{Points:N0} Punkte · {RegisteredImages} Bilder rekonstruiert, {UsedImages} zur Georeferenzierung genutzt · " +
        $"Lage-RMSE {RmseMeters:F2} m (max {MaxResidualMeters:F2} m) · DSM {Resolution:0.###} m/px";

    public string? Warning
    {
        get
        {
            var parts = new List<string>();
            if (RmseMeters > RmseWarningMeters)
                parts.Add($"Georeferenzierung ungenau (RMSE {RmseMeters:F2} m); GPS-Höhen oder Rekonstruktion prüfen.");
            if (Rejected.Count > 0)
                parts.Add($"{Rejected.Count} Bilder als Ausreißer verworfen: {string.Join(", ", Rejected.Take(5))}" +
                          (Rejected.Count > 5 ? " …" : ""));
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    public static ColmapProductsReport? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    public static ColmapProductsReport Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var georef = root.GetProperty("georeferencing");
        return new ColmapProductsReport(
            root.GetProperty("points").GetInt64(),
            georef.GetProperty("registeredImages").GetInt32(),
            georef.GetProperty("usedImages").GetInt32(),
            georef.GetProperty("rmseMeters").GetDouble(),
            georef.GetProperty("maxResidualMeters").GetDouble(),
            root.GetProperty("resolution").GetDouble(),
            georef.TryGetProperty("rejected", out var rejected)
                ? rejected.EnumerateArray().Select(item => item.GetString() ?? "").ToArray()
                : []);
    }
}
