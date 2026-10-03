using System.Globalization;
using System.IO;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public static class OdmFieldProductPlanBuilder
{
    public static LocalProcessingPlan Build(
        OdmOrthophotoInfo orthophoto,
        string workspace,
        LocalImageToolchainStatus toolchain,
        NdviScoutingZoneSettings zoneSettings)
    {
        zoneSettings.Validate();

        var otbAvailable =
            toolchain.Otb.Available &&
            !string.IsNullOrWhiteSpace(
                toolchain.OtbBandMath) &&
            !string.IsNullOrWhiteSpace(
                toolchain.OtbBandMathX);

        if (!toolchain.GdalTileEngineAvailable &&
            !otbAvailable)
        {
            throw new InvalidOperationException(
                "Für georeferenzierte Feldprodukte wird entweder GDAL-Python (Tile Engine) oder Orfeo ToolBox BandMath/BandMathX benötigt.");
        }

        var root =
            Path.GetFullPath(
                workspace);

        var products =
            Path.Combine(
                root,
                "field-products");

        var ndvi =
            Path.Combine(
                products,
                "ndvi.tif");

        var ndre =
            Path.Combine(
                products,
                "ndre.tif");

        var gndvi =
            Path.Combine(
                products,
                "gndvi.tif");

        var zones =
            Path.Combine(
                products,
                "ndvi_scouting_zones.tif");

        var bands =
            orthophoto.BandMap;

        var steps =
            new List<LocalProcessingCommand>();

        if (toolchain.GdalTileEngineAvailable)
        {
            AddGdalTileIndex(
                steps,
                "odm-ndvi",
                "ndvi",
                orthophoto.OrthophotoPath,
                bands.NirBand,
                bands.RedBand,
                ndvi,
                Path.Combine(
                    products,
                    "ndvi.json"),
                toolchain);

            AddGdalTileIndex(
                steps,
                "odm-ndre",
                "ndre",
                orthophoto.OrthophotoPath,
                bands.NirBand,
                bands.RedEdgeBand,
                ndre,
                Path.Combine(
                    products,
                    "ndre.json"),
                toolchain);

            AddGdalTileIndex(
                steps,
                "odm-gndvi",
                "gndvi",
                orthophoto.OrthophotoPath,
                bands.NirBand,
                bands.GreenBand,
                gndvi,
                Path.Combine(
                    products,
                    "gndvi.json"),
                toolchain);

            steps.Add(new(
                "odm-ndvi-scouting-zones",
                "GDAL/CUDA Tiles",
                toolchain.PythonExecutable!,
                [
                    toolchain.OpenCvWorkerPath,
                    "--geo-zones",
                    "--source", ndvi,
                    "--threshold1",
                    zoneSettings.Threshold1.ToString(
                        "R",
                        CultureInfo.InvariantCulture),
                    "--threshold2",
                    zoneSettings.Threshold2.ToString(
                        "R",
                        CultureInfo.InvariantCulture),
                    "--threshold3",
                    zoneSettings.Threshold3.ToString(
                        "R",
                        CultureInfo.InvariantCulture),
                    "--threshold4",
                    zoneSettings.Threshold4.ToString(
                        "R",
                        CultureInfo.InvariantCulture),
                    "--output", zones,
                    "--metadata",
                    Path.Combine(
                        products,
                        "ndvi_scouting_zones.json"),
                    "--backend", "auto",
                    "--tile-size", "2048"
                ],
                zones,
                $"NDVI tileweise in fünf Scouting-Zonen klassifizieren · Backend: {toolchain.GeoRasterBackend}."));
        }
        else
        {
            AddOtbIndex(
                steps,
                "odm-ndvi",
                orthophoto.OrthophotoPath,
                ndvi,
                $"(im1b{bands.NirBand}-im1b{bands.RedBand})/(im1b{bands.NirBand}+im1b{bands.RedBand}+1e-12)",
                toolchain);

            AddOtbIndex(
                steps,
                "odm-ndre",
                orthophoto.OrthophotoPath,
                ndre,
                $"(im1b{bands.NirBand}-im1b{bands.RedEdgeBand})/(im1b{bands.NirBand}+im1b{bands.RedEdgeBand}+1e-12)",
                toolchain);

            AddOtbIndex(
                steps,
                "odm-gndvi",
                orthophoto.OrthophotoPath,
                gndvi,
                $"(im1b{bands.NirBand}-im1b{bands.GreenBand})/(im1b{bands.NirBand}+im1b{bands.GreenBand}+1e-12)",
                toolchain);

            var zoneExpression =
                BuildZoneExpression(
                    zoneSettings);

            steps.Add(new(
                "odm-ndvi-scouting-zones",
                "Orfeo ToolBox",
                toolchain.OtbBandMath!,
                [
                    "-il",
                    ndvi,
                    "-exp",
                    zoneExpression,
                    "-out",
                    zones,
                    "uint8"
                ],
                zones,
                "NDVI in fünf transparente Scouting-Zonen klassifizieren. Diese Klassen sind keine Dünge- oder Pflanzenschutzempfehlung."));
        }

        return new(
            LocalProcessingPlan.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "ODM-M3M-FIELD-PRODUCTS",
            root,
            steps,
            [
                "Die Index-Raster übernehmen Georeferenzierung und Rastergeometrie des ODM-Multiband-Orthomosaiks.",
                $"Raster-Backend: {toolchain.GeoRasterBackend}. GDAL-Tile-Engine verarbeitet große Orthomosaike fensterweise und nutzt CUDA/CuPy nur für die Pixelarithmetik.",
                "Scouting-Zonen basieren ausschließlich auf den eingestellten NDVI-Schwellen und sind keine agronomische Diagnose oder Dosierempfehlung.",
                $"Bandzuordnung: {orthophoto.BandMap.ToDisplayText()}"
            ]);
    }

    public static string BuildZoneExpression(
        NdviScoutingZoneSettings settings)
    {
        settings.Validate();

        return string.Format(
            CultureInfo.InvariantCulture,
            "im1b1<{0:R}?1:(im1b1<{1:R}?2:(im1b1<{2:R}?3:(im1b1<{3:R}?4:5)))",
            settings.Threshold1,
            settings.Threshold2,
            settings.Threshold3,
            settings.Threshold4);
    }

    private static void AddGdalTileIndex(
        ICollection<LocalProcessingCommand> steps,
        string id,
        string indexType,
        string orthophoto,
        int positiveBand,
        int comparisonBand,
        string output,
        string metadata,
        LocalImageToolchainStatus toolchain)
    {
        steps.Add(new(
            id,
            "GDAL/CUDA Tiles",
            toolchain.PythonExecutable!,
            [
                toolchain.OpenCvWorkerPath,
                "--geo-index",
                "--source", orthophoto,
                "--positive-band-index",
                positiveBand.ToString(
                    CultureInfo.InvariantCulture),
                "--comparison-band-index",
                comparisonBand.ToString(
                    CultureInfo.InvariantCulture),
                "--index-type", indexType,
                "--output", output,
                "--metadata", metadata,
                "--backend", "auto",
                "--tile-size", "2048"
            ],
            output,
            $"{indexType.ToUpperInvariant()} tileweise als georeferenziertes GeoTIFF erzeugen · Backend: {toolchain.GeoRasterBackend}."));
    }

    private static void AddOtbIndex(
        ICollection<LocalProcessingCommand> steps,
        string id,
        string orthophoto,
        string output,
        string expression,
        LocalImageToolchainStatus toolchain)
    {
        steps.Add(new(
            id,
            "Orfeo ToolBox",
            toolchain.OtbBandMathX!,
            [
                "-il",
                orthophoto,
                "-exp",
                expression,
                "-out",
                output,
                "float"
            ],
            output,
            $"{id.Replace("odm-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant()} als georeferenziertes GeoTIFF erzeugen."));
    }
}
