using System.Globalization;
using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.Photogrammetry;

namespace DroneDash_x64.Desktop.SmartFarming;

public sealed record PrescriptionZone(int Zone, double Rate, double AreaHectares, double Amount);

public sealed record PrescriptionResult(
    string Source,
    double CellMeters,
    string Unit,
    string Shapefile,
    string Geojson,
    int Polygons,
    IReadOnlyList<PrescriptionZone> Zones,
    double TotalAreaHectares,
    double TotalAmount)
{
    public string SummaryText
    {
        get
        {
            var culture = CultureInfo.GetCultureInfo("de-DE");
            var lines = Zones
                .Where(zone => zone.AreaHectares > 0)
                .Select(zone => string.Format(
                    culture,
                    "Zone {0}: {1:N2} ha × {2:N1} {3} = {4:N1}",
                    zone.Zone, zone.AreaHectares, zone.Rate, Unit, zone.Amount))
                .ToList();
            lines.Add(string.Format(
                culture,
                "Gesamt: {0:N2} ha · Menge {1:N1} ({2}·ha) · Raster {3:N0} m · {4} Polygone",
                TotalAreaHectares, TotalAmount, Unit, CellMeters, Polygons));
            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>Application map from NDVI scouting zones via the worker (--prescription).</summary>
public static class PrescriptionMapService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>"1:120,2:100,..." for the five zones; rates must be finite and ≥ 0.</summary>
    public static string FormatRates(IReadOnlyList<double> rates)
    {
        if (rates.Count != 5)
            throw new ArgumentException("Für alle fünf Zonen wird eine Menge benötigt.", nameof(rates));
        if (rates.Any(rate => !double.IsFinite(rate) || rate < 0))
            throw new ArgumentOutOfRangeException(nameof(rates), "Mengen müssen endlich und ≥ 0 sein.");

        return string.Join(
            ",",
            rates.Select((rate, index) => string.Create(CultureInfo.InvariantCulture, $"{index + 1}:{rate:R}")));
    }

    /// <summary>File name stem without characters that farm terminals or shapefiles choke on.</summary>
    public static string SafeName(string name)
    {
        var cleaned = new string((name ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(cleaned) ? "prescription" : cleaned[..Math.Min(cleaned.Length, 40)];
    }

    public static async Task<PrescriptionResult> CreateAsync(
        string pythonExecutable,
        string workerPath,
        string zonesPath,
        string outputFolder,
        string name,
        IReadOnlyList<double> rates,
        double cellMeters,
        string unit,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(cellMeters) || cellMeters is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(cellMeters), "Das Maschinenraster muss zwischen 1 und 100 m liegen.");

        var json = await WorkerJsonRunner.RunAsync(
            pythonExecutable,
            workerPath,
            [
                "--prescription",
                "--source", Path.GetFullPath(zonesPath),
                "--output-dir", Path.GetFullPath(outputFolder),
                "--name", SafeName(name),
                "--rates", FormatRates(rates),
                "--cell", cellMeters.ToString("R", CultureInfo.InvariantCulture),
                "--unit", unit ?? ""
            ],
            "Applikationskarte fehlgeschlagen",
            cancellationToken);

        return JsonSerializer.Deserialize<PrescriptionResult>(json, JsonOptions)
            ?? throw new InvalidDataException("Leeres Ergebnis der Applikationskarte.");
    }
}
