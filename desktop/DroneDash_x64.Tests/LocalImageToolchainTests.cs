using System.IO;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class LocalImageToolchainTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_LocalImageToolchainTests_" + Guid.NewGuid().ToString("N"));

    public LocalImageToolchainTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ResolveExecutable_FindsPowerShellLauncherInConfiguredDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string variable = "DRONEDASH_TEST_TOOL_BIN";
        var previous = Environment.GetEnvironmentVariable(variable);
        var script = Path.Combine(_root, "otbcli_BandMath.ps1");
        File.WriteAllText(script, "Write-Output 'OTB test'");

        try
        {
            Environment.SetEnvironmentVariable(variable, _root);

            var resolved = LocalImageToolchain.ResolveExecutable(
                "otbcli_BandMath",
                variable);

            Assert.Equal(Path.GetFullPath(script), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task CreateProcess_RunsPowerShellLauncherWithArguments()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var script = Path.Combine(_root, "tool.ps1");
        await File.WriteAllTextAsync(
            script,
            "param([string]$Value)\nWrite-Output \"ok:$Value\"",
            TestContext.Current.CancellationToken);

        using var process = LocalImageToolchain.CreateProcess(
            script,
            ["hello world"]);

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("ok:hello world", stdout);
        Assert.True(
            string.IsNullOrWhiteSpace(stderr),
            $"PowerShell launcher wrote to stderr: {stderr}");
    }
}
