using System.IO;
using DroneDash_x64.Desktop.SmartFarming;

namespace DroneDash_x64.Desktop.Project;

internal static class ProjectDatasetProbeDetector
{
    public static ProjectDatasetProbe Probe(
        string sourceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sourceFolder);

        var source =
            Path.GetFullPath(
                sourceFolder);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                source);
        }

        var total = 0;
        var supported = 0;
        var thermal = 0;
        var m3m = 0;
        var m3mBands = 0;

        var options =
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip =
                    FileAttributes.ReparsePoint
            };

        foreach (var path in
                 Directory.EnumerateFiles(
                     source,
                     "*",
                     options))
        {
            total =
                checked(
                    total + 1);

            var extension =
                Path.GetExtension(path);

            if (!IsSupportedImageExtension(
                    extension))
            {
                continue;
            }

            supported =
                checked(
                    supported + 1);

            var name =
                Path.GetFileName(path);

            if (name.EndsWith(
                    "_T.JPG",
                    StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(
                    "_T.JPEG",
                    StringComparison.OrdinalIgnoreCase))
            {
                thermal =
                    checked(
                        thermal + 1);
            }

            if (M3mCaptureKeyParser.TryParse(
                    name,
                    out _,
                    out var kind))
            {
                m3m =
                    checked(
                        m3m + 1);

                if (!kind.Equals(
                        "D",
                        StringComparison.OrdinalIgnoreCase))
                {
                    m3mBands =
                        checked(
                            m3mBands + 1);
                }
            }
        }

        var ambiguous =
            thermal > 0 &&
            m3mBands > 0;

        var module =
            ambiguous
                ? ProjectPipelineModule.Unknown
                : m3mBands >= 4
                    ? ProjectPipelineModule.SmartFarming
                    : thermal > 0
                        ? ProjectPipelineModule.PvAnalysis
                        : supported > 0
                            ? ProjectPipelineModule.Photogrammetry
                            : ProjectPipelineModule.Unknown;

        var detail =
            ambiguous
                ? $"Gemischter Datensatz: {thermal:N0} Thermaldateien und {m3mBands:N0} M3M-Banddateien. Automatische Modulwahl aus Sicherheitsgründen ausgesetzt."
                : module switch
                {
                    ProjectPipelineModule.SmartFarming =>
                        $"M3M erkannt · {m3m:N0} M3M-Dateien, davon {m3mBands:N0} Multispektralbänder.",
                    ProjectPipelineModule.PvAnalysis =>
                        $"PV/Thermal erkannt · {thermal:N0} _T.JPG/_T.JPEG-Dateien.",
                    ProjectPipelineModule.Photogrammetry =>
                        $"Photogrammetrie erkannt · {supported:N0} unterstützte Bilddateien.",
                    _ =>
                        "Kein unterstützter DJI-Bilddatensatz erkannt."
                };

        return new(
            module,
            total,
            supported,
            thermal,
            m3m,
            m3mBands,
            ambiguous,
            detail);
    }

    private static bool IsSupportedImageExtension(
        string extension) =>
        extension.Equals(
            ".jpg",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".jpeg",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".dng",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".tif",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".tiff",
            StringComparison.OrdinalIgnoreCase);
}
