namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public sealed record OdmBandMap(
    int RedBand,
    int GreenBand,
    int NirBand,
    int RedEdgeBand,
    string MappingSource)
{
    public IReadOnlyDictionary<string, int> AsDictionary() =>
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Red"] = RedBand,
            ["Green"] = GreenBand,
            ["NIR"] = NirBand,
            ["RedEdge"] = RedEdgeBand
        };

    public string ToDisplayText() =>
        $"Red B{RedBand} · Green B{GreenBand} · NIR B{NirBand} · " +
        $"Red Edge B{RedEdgeBand} · Quelle: {MappingSource}";
}

public sealed record OdmOrthophotoInfo(
    string OrthophotoPath,
    int Width,
    int Height,
    int BandCount,
    string? DriverShortName,
    string? CoordinateSystemName,
    OdmBandMap BandMap,
    IReadOnlyList<string> BandDescriptions,
    IReadOnlyList<string> Warnings)
{
    public string SummaryText =>
        $"{Width} × {Height} · {BandCount} Bänder · " +
        $"{DriverShortName ?? "?"} · {BandMap.ToDisplayText()}";
}

public sealed record OdmImportedResult(
    string ZipPath,
    string ExtractedRoot,
    string OrthophotoPath,
    OdmOrthophotoInfo Orthophoto,
    IReadOnlyList<string> Warnings);

public sealed record NdviScoutingZoneSettings(
    double Threshold1,
    double Threshold2,
    double Threshold3,
    double Threshold4)
{
    public static NdviScoutingZoneSettings Default =>
        new(0.20, 0.40, 0.60, 0.80);

    public void Validate()
    {
        var values = new[]
        {
            Threshold1,
            Threshold2,
            Threshold3,
            Threshold4
        };

        if (values.Any(value =>
                !double.IsFinite(value) ||
                value < -1 ||
                value > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Threshold1),
                "NDVI-Zonenschwellen müssen zwischen -1 und +1 liegen.");
        }

        if (!(Threshold1 < Threshold2 &&
              Threshold2 < Threshold3 &&
              Threshold3 < Threshold4))
        {
            throw new ArgumentException(
                "NDVI-Zonenschwellen müssen streng aufsteigend sein.");
        }
    }

    public string LegendText =>
        $"Z1 < {Threshold1:F2} · Z2 < {Threshold2:F2} · " +
        $"Z3 < {Threshold3:F2} · Z4 < {Threshold4:F2} · " +
        $"Z5 ≥ {Threshold4:F2}";
}

public sealed record OdmFieldProductManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string SourceOrthophoto,
    OdmBandMap BandMap,
    NdviScoutingZoneSettings ZoneSettings,
    string NdviPath,
    string NdrePath,
    string GndviPath,
    string NdviZonesPath,
    IReadOnlyList<string> Warnings)
{
    public const int CurrentSchemaVersion = 1;
}
