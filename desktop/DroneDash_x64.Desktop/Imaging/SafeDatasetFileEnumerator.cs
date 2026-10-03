using System.IO;

namespace DroneDash_x64.Desktop.Imaging;

public sealed record DatasetScanProgress(
    int TotalFiles,
    int ProcessedFiles,
    string? CurrentFile)
{
    public double Percent =>
        TotalFiles <= 0
            ? 0d
            : Math.Clamp(
                ProcessedFiles * 100d / TotalFiles,
                0d,
                100d);
}

public static class SafeDatasetFileEnumerator
{
    public static IEnumerable<string> EnumerateFiles(
        string root,
        Func<string, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(predicate);

        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException(fullRoot);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.PlatformDefault
        };

        foreach (var path in Directory.EnumerateFiles(
                     fullRoot,
                     "*",
                     options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (predicate(path))
                yield return path;
        }
    }
}
