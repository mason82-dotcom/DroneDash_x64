using System.IO;
using DroneDash_x64.Desktop.SmartFarming.Odm;

namespace DroneDash_x64.Desktop.Photogrammetry;

public enum PhotogrammetryOdmPreset
{
    Fast,
    Standard,
    High
}

public sealed record PhotogrammetryOdmInputs(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Notes);

public sealed record PhotogrammetryOdmOptions(
    IReadOnlyList<NodeOdmOption> Options,
    IReadOnlyList<string> Notes);

/// <summary>
/// Builds a NodeODM task for an RGB survey that yields DSM, DTM, orthomosaic and a
/// georeferenced point cloud.
/// </summary>
public static class PhotogrammetryOdmPlan
{
    // DJI filename suffixes that must not be mixed into an RGB reconstruction.
    private static readonly (string Suffix, string Reason)[] ExcludedSuffixes =
    [
        ("_T", "Thermalbilder (_T)"),
        ("_Z", "Zoombilder (_Z)"),
        ("_MS_G", "M3M-Multispektralbänder"),
        ("_MS_R", "M3M-Multispektralbänder"),
        ("_MS_RE", "M3M-Multispektralbänder"),
        ("_MS_NIR", "M3M-Multispektralbänder")
    ];

    public static PhotogrammetryOdmInputs SelectInputFiles(PhotogrammetryDatasetResult dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
        var withoutGps = 0;
        var candidates = new List<(string Path, string Stem, string Extension)>();

        foreach (var image in dataset.Images)
        {
            var stem = Path.GetFileNameWithoutExtension(image.FileName);
            var reason = ExcludedSuffixes
                .FirstOrDefault(entry => stem.EndsWith(entry.Suffix, StringComparison.OrdinalIgnoreCase))
                .Reason;

            if (reason is not null)
            {
                excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
                continue;
            }

            if (!image.HasGps)
            {
                withoutGps++;
                continue;
            }

            candidates.Add((
                Path.Combine(dataset.SourceFolder, image.RelativePath),
                Path.Combine(Path.GetDirectoryName(image.RelativePath) ?? "", stem),
                Path.GetExtension(image.FileName).ToLowerInvariant()));
        }

        // Prefer the JPEG of a capture; ODM does not process DJI DNG reliably.
        var jpegStems = candidates
            .Where(c => c.Extension is ".jpg" or ".jpeg")
            .Select(c => c.Stem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = new List<string>();
        var dngSkipped = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Extension == ".dng")
            {
                dngSkipped++;
                continue;
            }

            if (candidate.Extension is ".tif" or ".tiff" && jpegStems.Contains(candidate.Stem))
                continue;

            files.Add(candidate.Path);
        }

        var notes = excluded
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Value} {entry.Key} ausgeschlossen.")
            .ToList();

        if (withoutGps > 0)
            notes.Add($"{withoutGps} Bilder ohne GPS ausgeschlossen.");
        if (dngSkipped > 0)
            notes.Add($"{dngSkipped} DNG-Dateien übersprungen (ODM nutzt die JPEGs).");

        return new PhotogrammetryOdmInputs(
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            notes);
    }

    /// <param name="supportedOptions">
    /// Option names reported by the server; required options must be present,
    /// optional ones are dropped with a note when missing.
    /// </param>
    public static PhotogrammetryOdmOptions BuildOptions(
        PhotogrammetryOdmPreset preset,
        IReadOnlySet<string> supportedOptions)
    {
        ArgumentNullException.ThrowIfNull(supportedOptions);

        var (pcQuality, featureQuality, demResolution, orthoResolution) = preset switch
        {
            PhotogrammetryOdmPreset.Fast => ("low", "medium", 10d, 5d),
            PhotogrammetryOdmPreset.Standard => ("medium", "high", 5d, 2d),
            PhotogrammetryOdmPreset.High => ("high", "high", 2d, 1d),
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };

        NodeOdmOption[] required =
        [
            new("dsm", true),
            new("dtm", true)
        ];

        NodeOdmOption[] optional =
        [
            new("pc-quality", pcQuality),
            new("feature-quality", featureQuality),
            new("dem-resolution", demResolution),
            new("orthophoto-resolution", orthoResolution),
            // Cloud-optimized point cloud for streaming viewers.
            new("pc-copc", true),
            new("auto-boundary", true)
        ];

        var missingRequired = required
            .Where(option => !supportedOptions.Contains(option.Name))
            .Select(option => option.Name)
            .ToArray();

        if (missingRequired.Length > 0)
        {
            throw new InvalidOperationException(
                "Der NodeODM-Server unterstützt die DSM/DTM-Optionen nicht: " +
                string.Join(", ", missingRequired));
        }

        var notes = new List<string>();
        var options = new List<NodeOdmOption>(required);

        foreach (var option in optional)
        {
            if (supportedOptions.Contains(option.Name))
                options.Add(option);
            else
                notes.Add($"Option '{option.Name}' wird vom Server nicht unterstützt und entfällt.");
        }

        return new PhotogrammetryOdmOptions(options, notes);
    }
}
