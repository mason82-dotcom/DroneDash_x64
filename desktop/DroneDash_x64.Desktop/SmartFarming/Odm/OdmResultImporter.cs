using System.IO;
using System.IO.Compression;

namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public static class OdmResultImporter
{
    public const int DefaultMaxEntries = 100_000;
    public const long DefaultMaxExtractedBytes =
        100L * 1024 * 1024 * 1024;

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
        CancellationToken cancellationToken = default,
        int maxEntries = DefaultMaxEntries,
        long maxExtractedBytes = DefaultMaxExtractedBytes)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException(
                "NodeODM ZIP nicht gefunden.",
                zipPath);

        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries));

        if (maxExtractedBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxExtractedBytes));

        var root =
            Path.GetFullPath(
                destinationRoot);

        var parent =
            Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException(
                "Ungültiger NodeODM-Zielpfad.");

        Directory.CreateDirectory(parent);

        if (Directory.Exists(root) &&
            Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException(
                $"NodeODM-Zielordner ist nicht leer: {root}");
        }

        var stagingRoot =
            root +
            ".partial-" +
            Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(
            stagingRoot);

        try
        {
            using var archive =
                ZipFile.OpenRead(zipPath);

            if (archive.Entries.Count > maxEntries)
            {
                throw new InvalidDataException(
                    $"NodeODM-Archiv enthält zu viele Einträge: {archive.Entries.Count:N0} > {maxEntries:N0}.");
            }

            long declaredBytes = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsDirectory(entry))
                    continue;

                try
                {
                    declaredBytes =
                        checked(
                            declaredBytes +
                            entry.Length);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidDataException(
                        "NodeODM-Archiv meldet eine ungültige Gesamtgröße.",
                        ex);
                }

                if (declaredBytes > maxExtractedBytes)
                {
                    throw new InvalidDataException(
                        $"NodeODM-Archiv ist entpackt größer als erlaubt: {declaredBytes:N0} Bytes > {maxExtractedBytes:N0} Bytes.");
                }
            }

            long extractedBytes = 0;

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

                if (string.IsNullOrWhiteSpace(
                        normalizedName))
                {
                    continue;
                }

                var destination =
                    Path.GetFullPath(
                        Path.Combine(
                            stagingRoot,
                            normalizedName.Replace(
                                '/',
                                Path.DirectorySeparatorChar)));

                if (!IsInsideRoot(
                        stagingRoot,
                        destination))
                {
                    throw new InvalidDataException(
                        $"Unsicherer ZIP-Pfad erkannt: {entry.FullName}");
                }

                if (IsDirectory(entry))
                {
                    Directory.CreateDirectory(
                        destination);
                    continue;
                }

                var directory =
                    Path.GetDirectoryName(
                        destination);

                if (!string.IsNullOrWhiteSpace(
                        directory))
                {
                    Directory.CreateDirectory(
                        directory);
                }

                using var source =
                    entry.Open();

                using var target =
                    new FileStream(
                        destination,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.SequentialScan);

                CopyEntryWithLimit(
                    source,
                    target,
                    ref extractedBytes,
                    maxExtractedBytes,
                    cancellationToken);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: false);
            }

            Directory.Move(
                stagingRoot,
                root);

            stagingRoot = "";

            return root;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(
                    stagingRoot) &&
                Directory.Exists(stagingRoot))
            {
                try
                {
                    Directory.Delete(
                        stagingRoot,
                        recursive: true);
                }
                catch
                {
                    // Cleanup failure must not hide the original import result.
                }
            }
        }
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

    private static void CopyEntryWithLimit(
        Stream source,
        Stream target,
        ref long extractedBytes,
        long maxExtractedBytes,
        CancellationToken cancellationToken)
    {
        var buffer =
            new byte[1024 * 1024];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read =
                source.Read(
                    buffer,
                    0,
                    buffer.Length);

            if (read == 0)
                break;

            if (extractedBytes >
                maxExtractedBytes - read)
            {
                throw new InvalidDataException(
                    $"NodeODM-Archiv überschreitet beim Entpacken das Limit von {maxExtractedBytes:N0} Bytes.");
            }

            target.Write(
                buffer,
                0,
                read);

            extractedBytes +=
                read;
        }
    }

    private static bool IsDirectory(
        ZipArchiveEntry entry) =>
        string.IsNullOrEmpty(
            entry.Name) ||
        entry.FullName.EndsWith(
            "/",
            StringComparison.Ordinal) ||
        entry.FullName.EndsWith(
            "\\",
            StringComparison.Ordinal);

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
