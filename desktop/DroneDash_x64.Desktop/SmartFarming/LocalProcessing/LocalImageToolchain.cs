using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

public static partial class LocalImageToolchain
{
    [GeneratedRegex(@"GDAL\s+([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex GdalVersionRegex();

    [GeneratedRegex(@"(?:OTB|Orfeo ToolBox)[^0-9]*([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex OtbVersionRegex();

    [GeneratedRegex(@"release\s+([0-9]+(?:\.[0-9]+){1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex CudaVersionRegex();

    public static async Task<LocalImageToolchainStatus> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var gdalInfo = ResolveExecutable(
            "gdalinfo",
            "DRONEDASH_GDAL_BIN",
            "GDAL_BIN");
        var gdalBuildVrt = ResolveExecutable(
            "gdalbuildvrt",
            "DRONEDASH_GDAL_BIN",
            "GDAL_BIN");

        var otbBandMath = ResolveExecutable(
            "otbcli_BandMath",
            "DRONEDASH_OTB_BIN",
            "OTB_BIN");
        var otbBandMathX = ResolveExecutable(
            "otbcli_BandMathX",
            "DRONEDASH_OTB_BIN",
            "OTB_BIN");

        var python = ResolvePython();
        var worker = Path.Combine(
            AppContext.BaseDirectory,
            "smart-farming",
            "opencv_m3m.py");

        var nvidiaSmi =
            ResolveCudaExecutable(
                "nvidia-smi");

        var nvcc =
            ResolveCudaExecutable(
                "nvcc");

        var gdal = await ProbeGdalAsync(
            gdalInfo,
            gdalBuildVrt,
            cancellationToken);

        var otb = await ProbeOtbAsync(
            otbBandMath,
            otbBandMathX,
            cancellationToken);

        var openCvProbe = await ProbeOpenCvAsync(
            python,
            worker,
            cancellationToken);

        var cuda = await ProbeCudaAsync(
            openCvProbe,
            nvidiaSmi,
            nvcc,
            cancellationToken);

        return new LocalImageToolchainStatus(
            gdal,
            otb,
            openCvProbe.Status,
            cuda,
            gdalBuildVrt,
            otbBandMath,
            otbBandMathX,
            python,
            worker)
        {
            OpenCvCudaAvailable =
                openCvProbe.CudaAvailable,
            CupyCudaAvailable =
                openCvProbe.CupyAvailable,
            GdalPythonAvailable =
                openCvProbe.GdalPythonAvailable,
            GdalPythonVersion =
                openCvProbe.GdalPythonVersion
        };
    }

    private static async Task<LocalImageToolStatus> ProbeGdalAsync(
        string? gdalInfo,
        string? gdalBuildVrt,
        CancellationToken cancellationToken)
    {
        if (gdalInfo is null || gdalBuildVrt is null)
        {
            return new(
                LocalImageToolKind.Gdal,
                "GDAL",
                false,
                null,
                gdalInfo ?? gdalBuildVrt,
                "gdalinfo und/oder gdalbuildvrt nicht gefunden. Optional DRONEDASH_GDAL_BIN setzen.");
        }

        var result = await RunProbeAsync(
            gdalInfo,
            ["--version"],
            cancellationToken);

        var version = result.Success
            ? GdalVersionRegex().Match(result.Output).Groups[1].Value
            : null;

        return new(
            LocalImageToolKind.Gdal,
            "GDAL",
            result.Success,
            string.IsNullOrWhiteSpace(version) ? null : version,
            gdalBuildVrt,
            result.Success
                ? "Raster/VRT-Toolchain verfügbar."
                : $"GDAL-Probe fehlgeschlagen: {result.Output}");
    }

    private static async Task<LocalImageToolStatus> ProbeOtbAsync(
        string? bandMath,
        string? bandMathX,
        CancellationToken cancellationToken)
    {
        if (bandMath is null || bandMathX is null)
        {
            return new(
                LocalImageToolKind.OrfeoToolBox,
                "Orfeo ToolBox",
                false,
                null,
                bandMath ?? bandMathX,
                "otbcli_BandMath und/oder otbcli_BandMathX nicht gefunden. Optional DRONEDASH_OTB_BIN setzen.");
        }

        var result = await RunProbeAsync(
            bandMath,
            ["-help"],
            cancellationToken);

        var match = OtbVersionRegex().Match(result.Output);
        var version = match.Success ? match.Groups[1].Value : null;

        return new(
            LocalImageToolKind.OrfeoToolBox,
            "Orfeo ToolBox",
            result.Success,
            version,
            bandMath,
            result.Success
                ? "BandMath/BandMathX verfügbar."
                : $"OTB-Probe fehlgeschlagen: {result.Output}");
    }

    private sealed record OpenCvProbeResult(
        LocalImageToolStatus Status,
        bool CudaAvailable,
        int CudaDeviceCount,
        string? CudaDeviceName,
        string? CudaBuild,
        bool CupyAvailable,
        string? CupyVersion,
        int CupyDeviceCount,
        string? CupyDeviceName,
        bool GdalPythonAvailable,
        string? GdalPythonVersion);

    private static async Task<OpenCvProbeResult> ProbeOpenCvAsync(
        string? python,
        string worker,
        CancellationToken cancellationToken)
    {
        if (python is null)
        {
            return new(
                new(
                    LocalImageToolKind.PythonOpenCv,
                    "Python + OpenCV",
                    false,
                    null,
                    null,
                    "Python nicht gefunden. Optional DRONEDASH_PYTHON setzen."),
                false,
                0,
                null,
                null,
                false,
                null,
                0,
                null,
                false,
                null);
        }

        if (!File.Exists(worker))
        {
            return new(
                new(
                    LocalImageToolKind.PythonOpenCv,
                    "Python + OpenCV",
                    false,
                    null,
                    python,
                    $"DroneDash OpenCV-Worker fehlt: {worker}"),
                false,
                0,
                null,
                null,
                false,
                null,
                0,
                null,
                false,
                null);
        }

        var result = await RunProbeAsync(
            python,
            [worker, "--probe"],
            cancellationToken);

        string? version = null;
        var openCvAvailable = false;
        var numpyAvailable = false;
        var cudaAvailable = false;
        var cudaDeviceCount = 0;
        string? cudaDeviceName = null;
        string? cudaBuild = null;
        var cupyAvailable = false;
        string? cupyVersion = null;
        var cupyDeviceCount = 0;
        string? cupyDeviceName = null;
        var gdalPythonAvailable = false;
        string? gdalPythonVersion = null;

        if (result.Success)
        {
            try
            {
                using var document =
                    JsonDocument.Parse(
                        result.Output);

                var root =
                    document.RootElement;

                if (root.TryGetProperty(
                        "opencvAvailable",
                        out var openCvAvailableElement) &&
                    openCvAvailableElement.ValueKind is
                        JsonValueKind.True or
                        JsonValueKind.False)
                {
                    openCvAvailable =
                        openCvAvailableElement.GetBoolean();
                }

                if (root.TryGetProperty(
                        "numpyAvailable",
                        out var numpyAvailableElement) &&
                    numpyAvailableElement.ValueKind is
                        JsonValueKind.True or
                        JsonValueKind.False)
                {
                    numpyAvailable =
                        numpyAvailableElement.GetBoolean();
                }

                if (root.TryGetProperty(
                        "opencv",
                        out var opencv) &&
                    opencv.ValueKind ==
                        JsonValueKind.String)
                {
                    version =
                        opencv.GetString();
                }

                if (root.TryGetProperty(
                        "cudaAvailable",
                        out var available) &&
                    available.ValueKind is
                        JsonValueKind.True or
                        JsonValueKind.False)
                {
                    cudaAvailable =
                        available.GetBoolean();
                }

                if (root.TryGetProperty(
                        "cudaDeviceCount",
                        out var deviceCount) &&
                    deviceCount.TryGetInt32(
                        out var parsedCount))
                {
                    cudaDeviceCount =
                        Math.Max(
                            0,
                            parsedCount);
                }

                if (root.TryGetProperty(
                        "cudaDeviceName",
                        out var deviceName) &&
                    deviceName.ValueKind ==
                        JsonValueKind.String)
                {
                    cudaDeviceName =
                        deviceName.GetString();
                }

                if (root.TryGetProperty(
                        "cudaBuild",
                        out var build) &&
                    build.ValueKind ==
                        JsonValueKind.String)
                {
                    cudaBuild =
                        build.GetString();
                }

                if (root.TryGetProperty(
                        "cupyAvailable",
                        out var cupy) &&
                    cupy.ValueKind is
                        JsonValueKind.True or
                        JsonValueKind.False)
                {
                    cupyAvailable =
                        cupy.GetBoolean();
                }

                if (root.TryGetProperty(
                        "cupyVersion",
                        out var cupyVersionElement) &&
                    cupyVersionElement.ValueKind ==
                        JsonValueKind.String)
                {
                    cupyVersion =
                        cupyVersionElement.GetString();
                }

                if (root.TryGetProperty(
                        "cupyDeviceCount",
                        out var cupyDeviceCountElement) &&
                    cupyDeviceCountElement.TryGetInt32(
                        out var parsedCupyDeviceCount))
                {
                    cupyDeviceCount =
                        Math.Max(
                            0,
                            parsedCupyDeviceCount);
                }

                if (root.TryGetProperty(
                        "cupyDeviceName",
                        out var cupyDeviceNameElement) &&
                    cupyDeviceNameElement.ValueKind ==
                        JsonValueKind.String)
                {
                    cupyDeviceName =
                        cupyDeviceNameElement.GetString();
                }

                if (root.TryGetProperty(
                        "gdalPythonAvailable",
                        out var gdalPython) &&
                    gdalPython.ValueKind is
                        JsonValueKind.True or
                        JsonValueKind.False)
                {
                    gdalPythonAvailable =
                        gdalPython.GetBoolean();
                }

                if (root.TryGetProperty(
                        "gdalPythonVersion",
                        out var gdalPythonVersionElement) &&
                    gdalPythonVersionElement.ValueKind ==
                        JsonValueKind.String)
                {
                    gdalPythonVersion =
                        gdalPythonVersionElement.GetString();
                }
            }
            catch
            {
                // OpenCV itself is still usable even if optional probe fields
                // from an older worker cannot be parsed.
            }
        }

        var status =
            new LocalImageToolStatus(
                LocalImageToolKind.PythonOpenCv,
                "Python + OpenCV",
                result.Success &&
                openCvAvailable,
                version,
                python,
                result.Success &&
                openCvAvailable
                    ? "OpenCV-ECC-Worker verfügbar."
                    : result.Success
                        ? "Python-Worker verfügbar, OpenCV jedoch nicht. GDAL-Tile-Engine kann unabhängig davon verfügbar sein."
                        : $"Python-Worker-Probe fehlgeschlagen: {result.Output}");

        return new(
            status,
            cudaAvailable &&
            cudaDeviceCount > 0,
            cudaDeviceCount,
            cudaDeviceName,
            cudaBuild,
            cupyAvailable &&
            cupyDeviceCount > 0,
            cupyVersion,
            cupyDeviceCount,
            cupyDeviceName,
            gdalPythonAvailable &&
            numpyAvailable,
            gdalPythonVersion);
    }

    private static async Task<LocalImageToolStatus> ProbeCudaAsync(
        OpenCvProbeResult openCv,
        string? nvidiaSmi,
        string? nvcc,
        CancellationToken cancellationToken)
    {
        string? driverInfo = null;
        string? toolkitVersion = null;

        if (nvidiaSmi is not null)
        {
            var driverProbe =
                await RunProbeAsync(
                    nvidiaSmi,
                    [
                        "--query-gpu=name,driver_version",
                        "--format=csv,noheader"
                    ],
                    cancellationToken);

            if (driverProbe.Success &&
                !string.IsNullOrWhiteSpace(
                    driverProbe.Output))
            {
                driverInfo =
                    driverProbe.Output
                        .Split(
                            ['\r', '\n'],
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .FirstOrDefault();
            }
        }

        if (nvcc is not null)
        {
            var toolkitProbe =
                await RunProbeAsync(
                    nvcc,
                    ["--version"],
                    cancellationToken);

            if (toolkitProbe.Success)
            {
                var match =
                    CudaVersionRegex()
                        .Match(
                            toolkitProbe.Output);

                if (match.Success)
                {
                    toolkitVersion =
                        match.Groups[1]
                            .Value;
                }
            }
        }

        var available =
            openCv.Status.Available &&
            (openCv.CudaAvailable ||
             openCv.CupyAvailable);

        var version =
            toolkitVersion ??
            openCv.CudaBuild;

        string detail;

        if (!openCv.Status.Available)
        {
            detail =
                "CUDA optional: Python/OpenCV-Worker ist nicht verfügbar.";
        }
        else if (available)
        {
            var device =
                openCv.CudaDeviceName ??
                openCv.CupyDeviceName ??
                $"{Math.Max(openCv.CudaDeviceCount, openCv.CupyDeviceCount)} CUDA-Gerät(e)";

            var registration =
                openCv.CudaAvailable
                    ? "OpenCV-Registrierung: CUDA Resize/Warp"
                    : "OpenCV-Registrierung: CPU";

            var indices =
                openCv.CupyAvailable
                    ? $"Vegetationsindizes: CUDA/CuPy {openCv.CupyVersion ?? ""}".TrimEnd()
                    : "Vegetationsindizes: CPU/NumPy";

            detail =
                $"{device} · {registration} · {indices}" +
                (string.IsNullOrWhiteSpace(driverInfo)
                    ? ""
                    : $" · NVIDIA {driverInfo}") +
                (string.IsNullOrWhiteSpace(toolkitVersion)
                    ? ""
                    : $" · Toolkit {toolkitVersion}") +
                ".";
        }
        else if (!string.IsNullOrWhiteSpace(driverInfo))
        {
            detail =
                $"NVIDIA-GPU erkannt ({driverInfo}), aber weder OpenCV-CUDA noch CuPy stellen ein CUDA-Gerät bereit. CPU-Fallback bleibt aktiv.";
        }
        else
        {
            detail =
                "Kein CUDA-Backend verfügbar. OpenCV und Vegetationsindizes verwenden CPU-Fallback.";
        }

        return new(
            LocalImageToolKind.NvidiaCuda,
            "NVIDIA CUDA",
            available,
            version,
            nvcc ??
            nvidiaSmi,
            detail);
    }

    internal static string? ResolveExecutable(
        string baseName,
        params string[] directoryEnvironmentVariables)
    {
        foreach (var variable in directoryEnvironmentVariables)
        {
            var root = Environment.GetEnvironmentVariable(variable);
            var resolved = ResolveFromRoot(root, baseName);
            if (resolved is not null)
                return resolved;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var resolved = ResolveFromRoot(directory, baseName);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private static string? ResolveCudaExecutable(
        string baseName)
    {
        var explicitBin =
            Environment.GetEnvironmentVariable(
                "DRONEDASH_CUDA_BIN");

        var resolved =
            ResolveFromRoot(
                explicitBin,
                baseName);

        if (resolved is not null)
            return resolved;

        var cudaPath =
            Environment.GetEnvironmentVariable(
                "CUDA_PATH");

        if (!string.IsNullOrWhiteSpace(
                cudaPath))
        {
            resolved =
                ResolveFromRoot(
                    Path.Combine(
                        cudaPath,
                        "bin"),
                    baseName);

            if (resolved is not null)
                return resolved;
        }

        if (OperatingSystem.IsWindows() &&
            baseName.Equals(
                "nvidia-smi",
                StringComparison.OrdinalIgnoreCase))
        {
            var systemRoot =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows);

            resolved =
                ResolveFromRoot(
                    Path.Combine(
                        systemRoot,
                        "System32"),
                    baseName);

            if (resolved is not null)
                return resolved;
        }

        return ResolveExecutable(
            baseName);
    }

    private static string? ResolvePython()
    {
        var explicitPython =
            Environment.GetEnvironmentVariable("DRONEDASH_PYTHON");

        if (!string.IsNullOrWhiteSpace(explicitPython) &&
            File.Exists(explicitPython))
        {
            return Path.GetFullPath(explicitPython);
        }

        return ResolveExecutable("python")
            ?? ResolveExecutable("python3");
    }

    private static string? ResolveFromRoot(
        string? root,
        string baseName)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        if (File.Exists(root))
            return Path.GetFullPath(root);

        if (!Directory.Exists(root))
            return null;

        var names = OperatingSystem.IsWindows()
            ? new[]
            {
                baseName + ".exe",
                baseName + ".bat",
                baseName + ".cmd",
                baseName
            }
            : new[] { baseName };

        foreach (var name in names)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static async Task<(bool Success, string Output)> RunProbeAsync(
        string program,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = CreateProcess(program, arguments);
            process.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(timeout.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = (stdout + Environment.NewLine + stderr).Trim();

            if (output.Length > 1200)
                output = output[..1200];

            return (process.ExitCode == 0, output);
        }
        catch (OperationCanceledException)
        {
            return (false, "Zeitüberschreitung bei Tool-Probe.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static Process CreateProcess(
        string program,
        IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(program);
        var isBatch = OperatingSystem.IsWindows() &&
                      (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase));

        if (!isBatch)
        {
            var info = new ProcessStartInfo
            {
                FileName = program,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            return new Process { StartInfo = info };
        }

        var command = QuoteForCmd(program) + " " +
                      string.Join(" ", arguments.Select(QuoteForCmd));

        var batchInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")
                       ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        batchInfo.ArgumentList.Add("/d");
        batchInfo.ArgumentList.Add("/s");
        batchInfo.ArgumentList.Add("/c");
        batchInfo.ArgumentList.Add(command);

        return new Process { StartInfo = batchInfo };
    }

    private static string QuoteForCmd(string value)
    {
        if (value.IndexOfAny(['&', '|', '<', '>', '^', '\r', '\n']) >= 0)
            throw new InvalidOperationException(
                "Tool-Pfad enthält nicht unterstützte CMD-Metazeichen.");

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
