using System.IO;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

namespace DroneDash_x64.Desktop.Photogrammetry;

/// <summary>Runs a one-shot worker mode and returns the JSON line it prints last.</summary>
internal static class WorkerJsonRunner
{
    public static async Task<string> RunAsync(
        string pythonExecutable,
        string workerPath,
        IReadOnlyList<string> arguments,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        using var process = LocalImageToolchain.CreateProcess(
            pythonExecutable,
            [workerPath, .. arguments]);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"{failureMessage}: {LastLines(detail, 6)}");
        }

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'))
            ?? throw new InvalidDataException($"{failureMessage}: der Worker hat keine Metadaten geliefert.");
    }

    /// <summary>
    /// A plain file name inside the preview folder: no separators of any platform and no
    /// relative segments, so metadata can never point the viewer outside that folder.
    /// </summary>
    public static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.IndexOfAny(['/', '\\', ':']) < 0 &&
        name is not "." and not "..";

    public static bool SameFile(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string LastLines(string text, int count) =>
        string.Join(
            " · ",
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(count));
}
