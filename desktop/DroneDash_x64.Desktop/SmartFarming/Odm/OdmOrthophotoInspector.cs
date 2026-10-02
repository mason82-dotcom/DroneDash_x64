using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public static class OdmOrthophotoInspector
{
    public static async Task<OdmOrthophotoInfo> InspectAsync(
        string orthophotoPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(orthophotoPath))
            throw new FileNotFoundException(
                "ODM-Orthophoto nicht gefunden.",
                orthophotoPath);

        var gdalInfo =
            LocalImageToolchain.ResolveExecutable(
                "gdalinfo",
                "DRONEDASH_GDAL_BIN",
                "GDAL_BIN");

        if (gdalInfo is null)
        {
            throw new InvalidOperationException(
                "gdalinfo wurde nicht gefunden. GDAL installieren oder DRONEDASH_GDAL_BIN setzen.");
        }

        var json =
            await RunGdalInfoAsync(
                gdalInfo,
                orthophotoPath,
                cancellationToken);

        return ParseGdalInfoJson(
            orthophotoPath,
            json,
            allowM3mFallback: true);
    }

    public static OdmOrthophotoInfo ParseGdalInfoJson(
        string orthophotoPath,
        string json,
        bool allowM3mFallback)
    {
        using var document =
            JsonDocument.Parse(json);

        var root =
            document.RootElement;

        var size =
            root.TryGetProperty(
                "size",
                out var sizeElement) &&
            sizeElement.ValueKind == JsonValueKind.Array
                ? sizeElement
                    .EnumerateArray()
                    .Select(value => value.GetInt32())
                    .ToArray()
                : [];

        if (size.Length < 2)
        {
            throw new InvalidDataException(
                "gdalinfo JSON enthält keine gültige Rastergröße.");
        }

        var descriptions =
            new List<string>();

        var bandNumbers =
            new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

        var bandCount = 0;

        if (root.TryGetProperty(
                "bands",
                out var bandsElement) &&
            bandsElement.ValueKind ==
            JsonValueKind.Array)
        {
            foreach (var band in
                     bandsElement.EnumerateArray())
            {
                bandCount++;

                var bandNumber =
                    band.TryGetProperty(
                        "band",
                        out var numberElement)
                        ? numberElement.GetInt32()
                        : bandCount;

                var description =
                    band.TryGetProperty(
                        "description",
                        out var descriptionElement) &&
                    descriptionElement.ValueKind ==
                    JsonValueKind.String
                        ? descriptionElement.GetString() ?? ""
                        : "";

                descriptions.Add(
                    string.IsNullOrWhiteSpace(description)
                        ? $"Band {bandNumber}"
                        : description);

                var normalized =
                    NormalizeBandName(description);

                if (normalized is not null)
                    bandNumbers.TryAdd(
                        normalized,
                        bandNumber);
            }
        }

        var warnings =
            new List<string>();

        OdmBandMap bandMap;

        if (bandNumbers.TryGetValue(
                "RED",
                out var red) &&
            bandNumbers.TryGetValue(
                "GREEN",
                out var green) &&
            bandNumbers.TryGetValue(
                "NIR",
                out var nir) &&
            bandNumbers.TryGetValue(
                "REDEDGE",
                out var redEdge))
        {
            bandMap = new(
                red,
                green,
                nir,
                redEdge,
                "GDAL-Bandbeschreibungen");
        }
        else if (
            allowM3mFallback &&
            bandCount == 4)
        {
            // ODM normalizes identifiable multispectral bands as:
            // RED, GREEN, NIR, RED EDGE (RGB/BLUE are absent in the
            // DroneDash M3M-only NodeODM task).
            bandMap = new(
                1,
                2,
                3,
                4,
                "ODM-M3M-Fallbackreihenfolge");

            warnings.Add(
                "Bandnamen fehlen oder sind unvollständig. Für diesen ausschließlich aus M3M-G/R/RE/NIR-TIFFs erzeugten ODM-Task wird ODMs normalisierte Bandreihenfolge Red, Green, NIR, Red Edge verwendet.");
        }
        else
        {
            throw new InvalidDataException(
                "Multispektrale Bänder konnten nicht eindeutig als Red, Green, NIR und Red Edge identifiziert werden.");
        }

        var driver =
            root.TryGetProperty(
                "driverShortName",
                out var driverElement) &&
            driverElement.ValueKind ==
            JsonValueKind.String
                ? driverElement.GetString()
                : null;

        string? coordinateSystemName = null;

        if (root.TryGetProperty(
                "coordinateSystem",
                out var coordinateSystem) &&
            coordinateSystem.ValueKind ==
            JsonValueKind.Object)
        {
            if (coordinateSystem.TryGetProperty(
                    "wkt",
                    out var wkt) &&
                wkt.ValueKind ==
                JsonValueKind.String)
            {
                coordinateSystemName =
                    ExtractProjectionName(
                        wkt.GetString());
            }
        }

        return new(
            Path.GetFullPath(orthophotoPath),
            size[0],
            size[1],
            bandCount,
            driver,
            coordinateSystemName,
            bandMap,
            descriptions,
            warnings);
    }

    private static async Task<string> RunGdalInfoAsync(
        string program,
        string orthophotoPath,
        CancellationToken cancellationToken)
    {
        var info =
            new ProcessStartInfo
            {
                FileName = program,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

        info.ArgumentList.Add("-json");
        info.ArgumentList.Add(orthophotoPath);

        using var process =
            new Process
            {
                StartInfo = info
            };

        process.Start();

        var stdoutTask =
            process.StandardOutput.ReadToEndAsync();

        var stderrTask =
            process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(
            cancellationToken);

        var stdout =
            await stdoutTask;

        var stderr =
            await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gdalinfo fehlgeschlagen (ExitCode {process.ExitCode}): {stderr.Trim()}");
        }

        return stdout;
    }

    private static string? NormalizeBandName(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized =
            new string(
                value
                    .ToUpperInvariant()
                    .Where(character =>
                        char.IsLetterOrDigit(character))
                    .ToArray());

        if (normalized is
            "RED" or "R")
            return "RED";

        if (normalized is
            "GREEN" or "G")
            return "GREEN";

        if (normalized.Contains(
                "REDEDGE",
                StringComparison.Ordinal) ||
            normalized is "RE")
        {
            return "REDEDGE";
        }

        if (normalized.Contains(
                "NIR",
                StringComparison.Ordinal) ||
            normalized is "N")
        {
            return "NIR";
        }

        return null;
    }

    private static string? ExtractProjectionName(
        string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
            return null;

        var marker =
            "PROJCRS[\"";

        var start =
            wkt.IndexOf(
                marker,
                StringComparison.Ordinal);

        if (start < 0)
            return null;

        start +=
            marker.Length;

        var end =
            wkt.IndexOf(
                '"',
                start);

        return end > start
            ? wkt[start..end]
            : null;
    }
}
