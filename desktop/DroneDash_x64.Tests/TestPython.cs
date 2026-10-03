using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Xunit;

namespace DroneDash_x64.Tests;

/// <summary>
/// Optional end-to-end tests run the real Python worker. They need DRONEDASH_TEST_PYTHON
/// pointing at an interpreter that has the required module, and are skipped otherwise.
/// With DRONEDASH_TEST_PYTHON_REQUIRED=1 (the GDAL CI job) a missing interpreter or module fails
/// the test instead, so a broken environment cannot pass as "skipped".
/// </summary>
internal static class TestPython
{
    private static bool Required =>
        Environment.GetEnvironmentVariable("DRONEDASH_TEST_PYTHON_REQUIRED") == "1";

    [DoesNotReturn]
    private static void SkipOrFail(string reason)
    {
        if (Required)
            Assert.Fail(reason + " (DRONEDASH_TEST_PYTHON_REQUIRED=1)");

        Assert.Skip(reason);
    }

    public static async Task<string> RequireModuleAsync(string module)
    {
        var python = Environment.GetEnvironmentVariable("DRONEDASH_TEST_PYTHON");
        if (string.IsNullOrWhiteSpace(python))
            SkipOrFail("DRONEDASH_TEST_PYTHON ist nicht gesetzt.");

        using var probe = Process.Start(new ProcessStartInfo(python, ["-c", $"import {module}"])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        })!;
        await probe.WaitForExitAsync(TestContext.Current.CancellationToken);

        if (probe.ExitCode != 0)
            SkipOrFail($"{python} hat das Modul {module} nicht.");

        return python!;
    }

    public static string WorkerPath()
    {
        var repoRoot = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repoRoot, "DroneDash_x64.slnx")))
            repoRoot = Path.GetDirectoryName(repoRoot) ?? throw new DirectoryNotFoundException("Repository root not found.");

        return Path.Combine(repoRoot, "desktop", "DroneDash_x64.Desktop", "SmartFarming", "Workers", "opencv_m3m.py");
    }

    public static async Task RunScriptAsync(string python, string scriptPath)
    {
        using var process = Process.Start(new ProcessStartInfo(python, [scriptPath])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        })!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await stderr);
    }
}
