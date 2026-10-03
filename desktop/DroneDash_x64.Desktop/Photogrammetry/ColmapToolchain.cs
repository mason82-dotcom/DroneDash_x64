using System.IO;
using System.Text.RegularExpressions;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

namespace DroneDash_x64.Desktop.Photogrammetry;

public sealed record ColmapStatus(
    bool Available,
    string? Executable,
    string? Version,
    bool? CudaEnabled,
    IReadOnlySet<string> Options,
    string Detail)
{
    public bool CanReconstructDense => Available && CudaEnabled == true;

    public string Summary => !Available
        ? "COLMAP nicht gefunden"
        : $"COLMAP {Version ?? "?"} · " + CudaEnabled switch
        {
            true => "CUDA",
            false => "ohne CUDA",
            null => "CUDA unbekannt"
        };
}

/// <summary>
/// Finds a COLMAP installation and reads version, CUDA support and the option names of the
/// commands DroneDash uses (they were renamed between COLMAP releases).
/// </summary>
public static partial class ColmapToolchain
{
    public const string EnvironmentVariable = "DRONEDASH_COLMAP";

    // Commands whose options the plan builder relies on.
    private static readonly string[] Commands =
    [
        "feature_extractor",
        "spatial_matcher",
        "mapper",
        "image_undistorter",
        "patch_match_stereo",
        "stereo_fusion"
    ];

    [GeneratedRegex(@"COLMAP\s+(?<version>\d+\.\d+(?:\.\d+)?(?:[-.\w]*)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b(?<state>with|without)\s+CUDA\b", RegexOptions.IgnoreCase)]
    private static partial Regex CudaRegex();

    [GeneratedRegex(@"^\s*--(?<name>[A-Za-z][A-Za-z0-9_.]*)", RegexOptions.Multiline)]
    private static partial Regex OptionRegex();

    public static async Task<ColmapStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable();
        if (executable is null)
        {
            return new ColmapStatus(
                false, null, null, null, new HashSet<string>(),
                $"COLMAP nicht gefunden. {EnvironmentVariable} auf den COLMAP-Ordner setzen " +
                "oder scripts\\install-colmap.ps1 ausführen.");
        }

        var help = await RunAsync(executable, ["-h"], cancellationToken);
        var (version, cuda) = ParseHeader(help);
        if (version is null)
        {
            return new ColmapStatus(
                false, executable, null, null, new HashSet<string>(),
                "COLMAP antwortet nicht wie erwartet: " + Shorten(help));
        }

        var options = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in Commands)
            options.UnionWith(ParseOptions(await RunAsync(executable, [command, "-h"], cancellationToken)));

        var detail = cuda switch
        {
            true => "COLMAP mit CUDA: Merkmale, Matching und dichte Rekonstruktion auf der GPU.",
            false => "COLMAP ohne CUDA: die dichte Rekonstruktion (patch_match_stereo) braucht die CUDA-Version.",
            null => "CUDA-Unterstützung konnte nicht aus der COLMAP-Hilfe gelesen werden."
        };

        return new ColmapStatus(true, executable, version, cuda, options, detail);
    }

    /// <summary>
    /// Checks that the DroneDash Python can write the COLMAP products: NumPy, GDAL and laspy
    /// with a LAZ backend.
    /// </summary>
    public static async Task<(bool Ready, string Detail)> ProbePythonAsync(
        string pythonExecutable,
        CancellationToken cancellationToken = default)
    {
        const string script =
            "import numpy, laspy\n" +
            "from osgeo import gdal\n" +
            "print('LAZ=' + str(len(laspy.LazBackend.detect_available())))";

        var output = await RunAsync(pythonExecutable, ["-c", script], cancellationToken);
        var match = Regex.Match(output, @"LAZ=(\d+)");
        if (!match.Success)
        {
            return (false,
                "Python braucht NumPy, GDAL und laspy für Georeferenzierung und DSM " +
                "(pip install \"laspy[lazrs]\"): " + Shorten(output));
        }

        return match.Groups[1].Value == "0"
            ? (false, "laspy ohne LAZ-Unterstützung; pip install \"laspy[lazrs]\" ausführen.")
            : (true, "Python mit NumPy, GDAL und laspy (LAZ) bereit.");
    }

    /// <summary>Version and CUDA flag from the header of <c>colmap -h</c>.</summary>
    public static (string? Version, bool? Cuda) ParseHeader(string helpText)
    {
        var version = VersionRegex().Match(helpText);
        var cuda = CudaRegex().Match(helpText);
        return (
            version.Success ? version.Groups["version"].Value : null,
            cuda.Success
                ? cuda.Groups["state"].Value.Equals("with", StringComparison.OrdinalIgnoreCase)
                : null);
    }

    /// <summary>Option names (without the leading dashes) listed by <c>colmap &lt;command&gt; -h</c>.</summary>
    public static IReadOnlySet<string> ParseOptions(string helpText) =>
        OptionRegex().Matches(helpText)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    public static string? ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return Path.GetFullPath(configured);

            var fromFolder = FromInstallFolder(configured);
            if (fromFolder is not null)
                return fromFolder;
        }

        // Default target of scripts\install-colmap.ps1.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var installed = FromInstallFolder(Path.Combine(localAppData, "DroneDash", "colmap"));
            if (installed is not null)
                return installed;
        }

        return LocalImageToolchain.ResolveExecutable("colmap")
               ?? (OperatingSystem.IsWindows() ? LocalImageToolchain.ResolveExecutable("COLMAP") : null);
    }

    /// <summary>
    /// The Windows release starts through COLMAP.bat, which puts its DLL and Qt plugin folders on
    /// the PATH; bin\colmap.exe alone may not find them.
    /// </summary>
    internal static string? FromInstallFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return null;

        string[] candidates = OperatingSystem.IsWindows()
            ? ["COLMAP.bat", Path.Combine("bin", "colmap.exe"), "colmap.exe"]
            : [Path.Combine("bin", "colmap"), "colmap"];

        foreach (var root in new[] { folder }.Concat(SafeSubdirectories(folder)))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(root, candidate);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeSubdirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = LocalImageToolchain.CreateProcess(executable, arguments);
            process.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
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

            return await stdout + Environment.NewLine + await stderr;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Zeitüberschreitung beim Aufruf von COLMAP.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private static string Shorten(string text)
    {
        text = text.Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
