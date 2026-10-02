using System.IO;
using System.Security.Cryptography;

namespace DroneDash_x64.Desktop.Project;

public static class ProjectArtifactService
{
    private const long MaxAutomaticHashBytes =
        256L * 1024L * 1024L;

    public static async Task<DroneDashProjectArtifact> CreateFileArtifactAsync(
        string projectPath,
        string filePath,
        ProjectArtifactKind? explicitKind = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath =
            Path.GetFullPath(filePath);

        var info =
            new FileInfo(fullPath);

        if (!info.Exists)
            throw new FileNotFoundException(
                "Projektartefakt wurde nicht gefunden.",
                fullPath);

        var stored =
            DroneDashProjectStore.ToStoredPath(
                projectPath,
                fullPath);

        var kind =
            explicitKind ??
            ClassifyFile(fullPath);

        var sha =
            info.Length <= MaxAutomaticHashBytes
                ? await ComputeSha256Async(
                    fullPath,
                    cancellationToken)
                : null;

        var notes =
            sha is null &&
            info.Length > MaxAutomaticHashBytes
                ? "SHA-256 beim Hinzufügen wegen Dateigröße > 256 MiB ausgelassen; Integritätsprüfung verwendet Größe und Änderungszeit."
                : null;

        return new DroneDashProjectArtifact(
            Guid.NewGuid(),
            kind,
            ProjectReferenceKind.File,
            stored.StoredPath,
            stored.IsRelative,
            BuildLabel(
                fullPath,
                kind),
            DateTimeOffset.UtcNow,
            info.Length,
            new DateTimeOffset(
                info.LastWriteTimeUtc,
                TimeSpan.Zero),
            sha,
            notes);
    }

    public static DroneDashProjectArtifact CreateDirectoryArtifact(
        string projectPath,
        string directoryPath,
        ProjectArtifactKind kind =
            ProjectArtifactKind.SourceDataFolder)
    {
        var fullPath =
            Path.GetFullPath(
                directoryPath);

        var info =
            new DirectoryInfo(
                fullPath);

        if (!info.Exists)
            throw new DirectoryNotFoundException(
                fullPath);

        var stored =
            DroneDashProjectStore.ToStoredPath(
                projectPath,
                fullPath);

        return new DroneDashProjectArtifact(
            Guid.NewGuid(),
            kind,
            ProjectReferenceKind.Directory,
            stored.StoredPath,
            stored.IsRelative,
            info.Name,
            DateTimeOffset.UtcNow,
            null,
            new DateTimeOffset(
                info.LastWriteTimeUtc,
                TimeSpan.Zero),
            null,
            "Verzeichnisreferenz; Inhalt wird nicht in die .ddproj-Datei kopiert.");
    }

    public static ProjectArtifactKind ClassifyFile(
        string filePath)
    {
        var name =
            Path.GetFileName(filePath);

        var extension =
            Path.GetExtension(name)
                .ToLowerInvariant();

        if (extension == ".ddplan")
            return ProjectArtifactKind.FlightPlan;

        if (extension == ".kmz")
            return ProjectArtifactKind.DjiWaylineKmz;

        if (name.Equals(
                "photogrammetry-manifest.json",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProjectArtifactKind.PhotogrammetryManifest;
        }

        if (name.Equals(
                "pv-analysis.json",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProjectArtifactKind.PvAnalysis;
        }

        if (name.Equals(
                "smart-farming-dataset.json",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProjectArtifactKind.SmartFarmingDataset;
        }

        if (name.Equals(
                "field-products.json",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProjectArtifactKind.SmartFarmingFieldProducts;
        }

        if (name.Equals(
                "local-processing-plan.json",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProjectArtifactKind.LocalProcessingPlan;
        }

        if (extension == ".zip" &&
            (name.Contains(
                 "nodeodm",
                 StringComparison.OrdinalIgnoreCase) ||
             name.Equals(
                 "all.zip",
                 StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectArtifactKind.NodeOdmResultArchive;
        }

        if (extension is ".tif" or ".tiff")
        {
            if (name.StartsWith(
                    "ndvi",
                    StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(
                    "ndre",
                    StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(
                    "gndvi",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "scouting",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ProjectArtifactKind.VegetationRaster;
            }

            if (name.Contains(
                    "_MS_",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ProjectArtifactKind.MultispectralImage;
            }
        }

        if (extension is ".jpg" or ".jpeg" &&
            name.Contains(
                "_T.",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProjectArtifactKind.ThermalImage;
        }

        return ProjectArtifactKind.Other;
    }

    public static async Task<ProjectArtifactVerification> VerifyAsync(
        string projectPath,
        DroneDashProjectArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        string resolved;

        try
        {
            resolved =
                DroneDashProjectStore.ResolveArtifactPath(
                    projectPath,
                    artifact);
        }
        catch (Exception ex)
        {
            return new(
                artifact,
                artifact.StoredPath,
                ProjectArtifactIntegrity.Error,
                ex.Message);
        }

        try
        {
            if (artifact.ReferenceKind ==
                ProjectReferenceKind.Directory)
            {
                if (!Directory.Exists(resolved))
                {
                    return new(
                        artifact,
                        resolved,
                        ProjectArtifactIntegrity.Missing,
                        "Verzeichnis nicht gefunden.");
                }

                return new(
                    artifact,
                    resolved,
                    ProjectArtifactIntegrity.Ok,
                    "Verzeichnis vorhanden.");
            }

            var info =
                new FileInfo(
                    resolved);

            if (!info.Exists)
            {
                return new(
                    artifact,
                    resolved,
                    ProjectArtifactIntegrity.Missing,
                    "Datei nicht gefunden.");
            }

            if (artifact.SizeBytes is long expectedSize &&
                info.Length != expectedSize)
            {
                return new(
                    artifact,
                    resolved,
                    ProjectArtifactIntegrity.Modified,
                    $"Dateigröße geändert: erwartet {expectedSize}, aktuell {info.Length} Bytes.");
            }

            if (!string.IsNullOrWhiteSpace(
                    artifact.Sha256))
            {
                var currentHash =
                    await ComputeSha256Async(
                        resolved,
                        cancellationToken);

                if (!currentHash.Equals(
                        artifact.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new(
                        artifact,
                        resolved,
                        ProjectArtifactIntegrity.Modified,
                        "SHA-256 stimmt nicht mehr mit dem gespeicherten Snapshot überein.");
                }

                return new(
                    artifact,
                    resolved,
                    ProjectArtifactIntegrity.Ok,
                    "SHA-256 und Dateigröße stimmen.");
            }

            if (artifact.LastWriteUtc is DateTimeOffset expectedWrite)
            {
                var currentWrite =
                    new DateTimeOffset(
                        info.LastWriteTimeUtc,
                        TimeSpan.Zero);

                if (currentWrite != expectedWrite)
                {
                    return new(
                        artifact,
                        resolved,
                        ProjectArtifactIntegrity.Modified,
                        "Änderungszeit weicht vom gespeicherten Snapshot ab.");
                }
            }

            return new(
                artifact,
                resolved,
                ProjectArtifactIntegrity.Ok,
                "Datei vorhanden; Metadaten-Snapshot stimmt.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(
                artifact,
                resolved,
                ProjectArtifactIntegrity.Error,
                ex.Message);
        }
    }

    public static IReadOnlyList<string> DiscoverKnownArtifacts(
        string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
            return [];

        var candidates =
            Directory.EnumerateFiles(
                    projectDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path =>
                {
                    var kind =
                        ClassifyFile(path);

                    return kind is
                        ProjectArtifactKind.FlightPlan or
                        ProjectArtifactKind.DjiWaylineKmz or
                        ProjectArtifactKind.PhotogrammetryManifest or
                        ProjectArtifactKind.PvAnalysis or
                        ProjectArtifactKind.SmartFarmingDataset or
                        ProjectArtifactKind.SmartFarmingFieldProducts or
                        ProjectArtifactKind.LocalProcessingPlan or
                        ProjectArtifactKind.NodeOdmResultArchive or
                        ProjectArtifactKind.VegetationRaster;
                })
                .OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return candidates;
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream =
            new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

        using var algorithm =
            SHA256.Create();

        var hash =
            await algorithm.ComputeHashAsync(
                stream,
                cancellationToken);

        return Convert.ToHexString(
            hash);
    }

    private static string BuildLabel(
        string path,
        ProjectArtifactKind kind)
    {
        var name =
            Path.GetFileName(path);

        return kind switch
        {
            ProjectArtifactKind.FlightPlan =>
                $"Flugplan · {name}",
            ProjectArtifactKind.DjiWaylineKmz =>
                $"DJI Wayline · {name}",
            ProjectArtifactKind.PhotogrammetryManifest =>
                "Photogrammetrie-Manifest",
            ProjectArtifactKind.PvAnalysis =>
                "PV-Analyse",
            ProjectArtifactKind.SmartFarmingDataset =>
                "Smart-Farming-Datensatz",
            ProjectArtifactKind.SmartFarmingFieldProducts =>
                "Smart-Farming-Feldprodukte",
            ProjectArtifactKind.LocalProcessingPlan =>
                $"Processing-Plan · {name}",
            ProjectArtifactKind.NodeOdmResultArchive =>
                $"NodeODM-Ergebnis · {name}",
            ProjectArtifactKind.VegetationRaster =>
                $"Vegetationsraster · {name}",
            ProjectArtifactKind.MultispectralImage =>
                $"M3M-Band · {name}",
            ProjectArtifactKind.ThermalImage =>
                $"Thermalbild · {name}",
            _ => name
        };
    }
}
