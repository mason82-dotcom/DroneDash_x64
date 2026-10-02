using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public static class OdmFieldProductExporter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

    public static string ExportManifest(
        string workspace,
        OdmOrthophotoInfo orthophoto,
        NdviScoutingZoneSettings zoneSettings)
    {
        zoneSettings.Validate();

        var products =
            Path.Combine(
                Path.GetFullPath(workspace),
                "field-products");

        Directory.CreateDirectory(
            products);

        var manifest =
            new OdmFieldProductManifest(
                OdmFieldProductManifest.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                orthophoto.OrthophotoPath,
                orthophoto.BandMap,
                zoneSettings,
                Path.Combine(
                    products,
                    "ndvi.tif"),
                Path.Combine(
                    products,
                    "ndre.tif"),
                Path.Combine(
                    products,
                    "gndvi.tif"),
                Path.Combine(
                    products,
                    "ndvi_scouting_zones.tif"),
                orthophoto.Warnings.Concat(
                    [
                        "NDVI-Scouting-Zonen sind relative Analyseklassen und keine automatische Dünge-, Bewässerungs- oder Pflanzenschutzempfehlung."
                    ])
                    .ToArray());

        var path =
            Path.Combine(
                products,
                "field-products.json");

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                manifest,
                JsonOptions),
            new UTF8Encoding(false));

        return path;
    }
}
