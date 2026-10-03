using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using DroneDash_x64.Desktop.SmartFarming.Odm;
using DroneDash_x64.Desktop.Thermal;

namespace DroneDash_x64.Desktop.Diagnostics;

public static class WindowsRuntimeDiagnostics
{
    private static readonly TimeSpan NodeOdmProbeTimeout =
        TimeSpan.FromSeconds(5);

    public static async Task<RuntimeDiagnosticsSnapshot> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var items =
            new List<RuntimeDiagnosticItem>();

        AddPlatform(items);
        AddDotNet(items);
        AddWebView2(items);

        cancellationToken.ThrowIfCancellationRequested();

        var toolchain =
            await LocalImageToolchain.ProbeAsync(
                cancellationToken);

        await AddProcessingToolchainAsync(
            items,
            toolchain,
            cancellationToken);

        AddThermalSdk(items);

        await AddNodeOdmAsync(
            items,
            cancellationToken);

        return new RuntimeDiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            items);
    }

    private static void AddPlatform(
        ICollection<RuntimeDiagnosticItem> items)
    {
        var osX64 =
            OperatingSystem.IsWindows() &&
            RuntimeInformation.OSArchitecture ==
            Architecture.X64;

        items.Add(
            new RuntimeDiagnosticItem(
                "Windows x64",
                "Betriebssystem",
                osX64
                    ? RuntimeDiagnosticState.Ready
                    : RuntimeDiagnosticState.Error,
                Environment.OSVersion.VersionString,
                null,
                osX64
                    ? "Windows x64 erkannt."
                    : $"DroneDash_x64 benötigt Windows x64; erkannt: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}."));

        var processX64 =
            RuntimeInformation.ProcessArchitecture ==
            Architecture.X64 &&
            Environment.Is64BitProcess;

        items.Add(
            new RuntimeDiagnosticItem(
                "Windows x64",
                "DroneDash Prozess",
                processX64
                    ? RuntimeDiagnosticState.Ready
                    : RuntimeDiagnosticState.Error,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessPath,
                processX64
                    ? "Der Desktopprozess läuft nativ als x64/AMD64."
                    : "Der Desktopprozess läuft nicht als x64. Native x64-Runtimes können so nicht zuverlässig geladen werden."));
    }

    private static void AddDotNet(
        ICollection<RuntimeDiagnosticItem> items)
    {
        var dotNet10 =
            Environment.Version.Major >= 10;

        items.Add(
            new RuntimeDiagnosticItem(
                ".NET",
                "Windows Desktop Runtime",
                dotNet10
                    ? RuntimeDiagnosticState.Ready
                    : RuntimeDiagnosticState.Error,
                RuntimeInformation.FrameworkDescription,
                null,
                dotNet10
                    ? ".NET 10 Windows Desktop Runtime aktiv."
                    : $"DroneDash_x64 erwartet .NET 10; aktiv ist {RuntimeInformation.FrameworkDescription}."));
    }

    private static void AddWebView2(
        ICollection<RuntimeDiagnosticItem> items)
    {
        try
        {
            var version =
                CoreWebView2Environment
                    .GetAvailableBrowserVersionString();

            items.Add(
                new RuntimeDiagnosticItem(
                    "UI Runtime",
                    "Microsoft Edge WebView2",
                    RuntimeDiagnosticState.Ready,
                    version,
                    null,
                    "WebView2 Runtime ist für Flugplanung und Kartenansichten verfügbar."));
        }
        catch (Exception ex)
        {
            items.Add(
                new RuntimeDiagnosticItem(
                    "UI Runtime",
                    "Microsoft Edge WebView2",
                    RuntimeDiagnosticState.Error,
                    null,
                    null,
                    $"WebView2 Runtime nicht verfügbar: {ex.Message}"));
        }
    }

    private static async Task AddProcessingToolchainAsync(
        ICollection<RuntimeDiagnosticItem> items,
        LocalImageToolchainStatus toolchain,
        CancellationToken cancellationToken)
    {
        items.Add(
            ToolItem(
                toolchain.Gdal,
                "Geospatial",
                optional: true));

        items.Add(
            new RuntimeDiagnosticItem(
                "Geospatial",
                "GDAL Python Tile Engine",
                toolchain.GdalPythonAvailable
                    ? RuntimeDiagnosticState.Ready
                    : RuntimeDiagnosticState.OptionalMissing,
                toolchain.GdalPythonVersion,
                toolchain.PythonExecutable,
                toolchain.GdalPythonAvailable
                    ? $"Python-GDAL bereit · Rasterbackend: {toolchain.GeoRasterBackend}."
                    : "Python-Bindings osgeo.gdal fehlen; georeferenzierte Feldprodukte können auf OTB zurückfallen."));

        items.Add(
            ToolItem(
                toolchain.Otb,
                "Geospatial",
                optional: true));

        items.Add(
            ToolItem(
                toolchain.OpenCv,
                "Python / CV",
                optional: true));

        if (string.IsNullOrWhiteSpace(
                toolchain.PythonExecutable))
        {
            items.Add(
                new RuntimeDiagnosticItem(
                    "Python / CV",
                    "Python x64",
                    RuntimeDiagnosticState.OptionalMissing,
                    null,
                    null,
                    "Python ist optional für Smart-Farming-Local-Processing, wurde aber nicht gefunden."));
        }
        else
        {
            items.Add(
                await ProbePythonAsync(
                    toolchain.PythonExecutable,
                    cancellationToken));
        }

        items.Add(
            ToolItem(
                toolchain.Cuda,
                "GPU",
                optional: true));

        items.Add(
            new RuntimeDiagnosticItem(
                "GPU",
                "OpenCV CUDA",
                toolchain.OpenCvCudaAvailable
                    ? RuntimeDiagnosticState.Ready
                    : RuntimeDiagnosticState.OptionalMissing,
                toolchain.OpenCv.Version,
                toolchain.PythonExecutable,
                toolchain.OpenCvCudaAvailable
                    ? $"CUDA-Registrierung verfügbar · {toolchain.OpenCvBackend}."
                    : "OpenCV-CUDA ist optional; Registrierung verwendet CPU-Fallback."));

        items.Add(
            new RuntimeDiagnosticItem(
                "GPU",
                "CuPy CUDA",
                toolchain.CupyCudaAvailable
                    ? RuntimeDiagnosticState.Ready
                    : RuntimeDiagnosticState.OptionalMissing,
                null,
                toolchain.PythonExecutable,
                toolchain.CupyCudaAvailable
                    ? $"CUDA-Rasterarithmetik verfügbar · {toolchain.VegetationIndexBackend}."
                    : "CuPy/CUDA ist optional; Vegetationsindizes verwenden NumPy auf der CPU."));
    }

    private static RuntimeDiagnosticItem ToolItem(
        LocalImageToolStatus tool,
        string category,
        bool optional)
    {
        return new RuntimeDiagnosticItem(
            category,
            tool.Name,
            tool.Available
                ? RuntimeDiagnosticState.Ready
                : optional
                    ? RuntimeDiagnosticState.OptionalMissing
                    : RuntimeDiagnosticState.Error,
            tool.Version,
            tool.PrimaryExecutable,
            tool.Detail);
    }

    private static async Task<RuntimeDiagnosticItem> ProbePythonAsync(
        string python,
        CancellationToken cancellationToken)
    {
        try
        {
            var info =
                new ProcessStartInfo
                {
                    FileName = python,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(
                "import platform,struct;print(str(struct.calcsize('P')*8)+'|'+platform.python_version())");

            using var process =
                Process.Start(info) ??
                throw new InvalidOperationException(
                    "Python-Prozess konnte nicht gestartet werden.");

            var stdoutTask =
                process.StandardOutput.ReadToEndAsync(
                    cancellationToken);
            var stderrTask =
                process.StandardError.ReadToEndAsync(
                    cancellationToken);

            await process.WaitForExitAsync(
                cancellationToken);

            var stdout =
                (await stdoutTask).Trim();
            var stderr =
                (await stderrTask).Trim();

            var fields =
                stdout.Split(
                    '|',
                    2,
                    StringSplitOptions.TrimEntries);

            var bits =
                fields.Length > 0
                    ? fields[0]
                    : "";

            var version =
                fields.Length > 1
                    ? fields[1]
                    : null;

            var x64 =
                process.ExitCode == 0 &&
                bits == "64";

            return new RuntimeDiagnosticItem(
                "Python / CV",
                "Python x64",
                x64
                    ? RuntimeDiagnosticState.Ready
                    : RuntimeDiagnosticState.Warning,
                version,
                python,
                x64
                    ? "64-Bit-Python ist mit nativen x64-Paketen kompatibel."
                    : $"Python ist nicht als 64-Bit-Runtime bestätigt. Ausgabe: {stdout} {stderr}".Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RuntimeDiagnosticItem(
                "Python / CV",
                "Python x64",
                RuntimeDiagnosticState.Warning,
                null,
                python,
                $"Python-Architektur konnte nicht geprüft werden: {ex.Message}");
        }
    }

    private static void AddThermalSdk(
        ICollection<RuntimeDiagnosticItem> items)
    {
        try
        {
            using var sdk =
                new DjiThermalSdk();

            items.Add(
                new RuntimeDiagnosticItem(
                    "DJI Thermal",
                    "DJI Thermal SDK",
                    sdk.IsAvailable
                        ? RuntimeDiagnosticState.Ready
                        : RuntimeDiagnosticState.OptionalMissing,
                    DjiThermalSdk.SupportedSdkVersion,
                    sdk.ReleaseDirectory,
                    sdk.Status));
        }
        catch (Exception ex)
        {
            items.Add(
                new RuntimeDiagnosticItem(
                    "DJI Thermal",
                    "DJI Thermal SDK",
                    RuntimeDiagnosticState.Warning,
                    DjiThermalSdk.SupportedSdkVersion,
                    null,
                    $"Thermal-SDK-Prüfung fehlgeschlagen: {ex.Message}"));
        }
    }

    private static async Task AddNodeOdmAsync(
        ICollection<RuntimeDiagnosticItem> items,
        CancellationToken cancellationToken)
    {
        var endpoint =
            NodeOdmClient.EndpointFromEnvironment();

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            NodeOdmProbeTimeout);

        try
        {
            using var client =
                new NodeOdmClient(
                    endpoint,
                    NodeOdmClient.TokenFromEnvironment());

            var server =
                await client.ProbeAsync(
                    timeout.Token);

            cancellationToken.ThrowIfCancellationRequested();

            var version =
                string.Join(
                    " · ",
                    new[]
                    {
                        server.ApiVersion,
                        server.Engine,
                        server.EngineVersion
                    }
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value)));

            items.Add(
                new RuntimeDiagnosticItem(
                    "Photogrammetrie",
                    "NodeODM",
                    server.Available
                        ? RuntimeDiagnosticState.Ready
                        : RuntimeDiagnosticState.OptionalMissing,
                    string.IsNullOrWhiteSpace(version)
                        ? null
                        : version,
                    server.Endpoint,
                    server.Available
                        ? $"{server.Detail} {server.M3mSupportText} · CPU {server.CpuCores?.ToString() ?? "—"} · Queue {server.TaskQueueCount?.ToString() ?? "—"}."
                        : $"Optionaler NodeODM-Dienst nicht erreichbar: {server.Detail}"));
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            items.Add(
                new RuntimeDiagnosticItem(
                    "Photogrammetrie",
                    "NodeODM",
                    RuntimeDiagnosticState.OptionalMissing,
                    null,
                    endpoint,
                    $"Optionaler NodeODM-Dienst antwortete nicht innerhalb von {NodeOdmProbeTimeout.TotalSeconds:F0} s."));
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            items.Add(
                new RuntimeDiagnosticItem(
                    "Photogrammetrie",
                    "NodeODM",
                    RuntimeDiagnosticState.OptionalMissing,
                    null,
                    endpoint,
                    $"Optionaler NodeODM-Dienst konnte nicht geprüft werden: {ex.Message}"));
        }
    }
}
