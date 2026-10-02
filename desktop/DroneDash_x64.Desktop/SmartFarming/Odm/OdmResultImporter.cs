using System.IO;
using System.IO.Compression;

namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public static class OdmResultImporter
{
    private static readonly string[] PreferredOrthophotoPaths =
    [
        Path.Combine(
            "odm_orthophoto",
            "odm_orthophoto.tif"),
        "odm_orthophoto.tif"
    ];

    public static string ExtractSafely(
        string zipPath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException(
                "NodeODM ZIP nicht gefunden.",
                zipPath);

        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);

        using var archive =
            ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsUnixSymlink(entry))
            {
                throw new InvalidDataException(
                    $"Symlink im NodeODM-Archiv wird aus Sicherheitsgründen nicht extrahiert: {entry.FullName}");
            }

            var normalizedName =
                entry.FullName
                    .Replace('\\', '/')
                    .TrimStart('/');

            if (string.IsNullOrWhiteSpace(normalizedName))
                continue;

            var destination =
                Path.GetFullPath(
                    Path.Combine(
                        root,
                        normalizedName.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));

            if (!IsInsideRoot(
                    root,
                    destination))
            {
                throw new InvalidDataException(
                    $"Unsicherer ZIP-Pfad erkannt: {entry.FullName}");
            }

            if (normalizedName.EndsWith(
                    "/",
                    StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var directory =
                Path.GetDirectoryName(destination);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using var source = entry.Open();
            using var target = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024);

            source.CopyTo(target);
        }

        return root;
    }

    public static string FindOrthophoto(
        string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot))
        {
            throw new DirectoryNotFoundException(
                extractedRoot);
        }

        foreach (var relative in PreferredOrthophotoPaths)
        {
            var direct =
                Path.Combine(
                    extractedRoot,
                    relative);

            if (File.Exists(direct))
                return Path.GetFullPath(direct);
        }

        var candidates =
            Directory.EnumerateFiles(
                    extractedRoot,
                    "odm_orthophoto.tif",
                    SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException(
                "odm_orthophoto.tif wurde im NodeODM-Ergebnis nicht gefunden."),
            _ => throw new InvalidDataException(
                "Mehrere odm_orthophoto.tif gefunden; eindeutige Zuordnung nicht möglich.")
        };
    }

    private static bool IsInsideRoot(
        string root,
        string candidate)
    {
        var relative =
            Path.GetRelativePath(
                root,
                candidate);

        return relative != ".." &&
               !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool IsUnixSymlink(
        ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymlink = 0xA000;

        var mode =
            (entry.ExternalAttributes >> 16) &
            0xFFFF;

        return (mode & UnixFileTypeMask) ==
               UnixSymlink;
    }
}
