using System.Diagnostics;
using System.IO;
using System.Text;

namespace DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

public sealed record LocalProcessingProgress(
    int CompletedSteps,
    int TotalSteps,
    string? CurrentStepId,
    string Message,
    double Percent);

public sealed record LocalProcessingRunResult(
    bool Success,
    bool Canceled,
    int CompletedSteps,
    int TotalSteps,
    string LogPath,
    string? FailedStepId,
    int? ExitCode);

public static class LocalProcessingRunner
{
    public static async Task<LocalProcessingRunResult> RunAsync(
        LocalProcessingPlan plan,
        IProgress<LocalProcessingProgress>? progress = null,
        Action<string>? logSink = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(plan.Workspace);

        var logPath = Path.Combine(
            plan.Workspace,
            "local-processing.log");

        await using var log = new StreamWriter(
            logPath,
            append: true,
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        async Task WriteLogAsync(string line)
        {
            var formatted =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {line}";
            await log.WriteLineAsync(formatted);
            logSink?.Invoke(formatted);
        }

        await WriteLogAsync(
            $"Start Smart Farming local processing · capture={plan.CaptureKey} · steps={plan.Steps.Count}");

        var completed = 0;

        foreach (var step in plan.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await WriteLogAsync("Abbruch vor dem nächsten Processing-Schritt.");
                return new(
                    Success: false,
                    Canceled: true,
                    completed,
                    plan.Steps.Count,
                    logPath,
                    step.Id,
                    null);
            }

            progress?.Report(new(
                completed,
                plan.Steps.Count,
                step.Id,
                step.Description,
                plan.Steps.Count == 0
                    ? 100d
                    : completed * 100d / plan.Steps.Count));

            await WriteLogAsync(
                $"START {step.Id} [{step.Tool}] {step.Description}");

            using var process = CreateProcess(
                step.Program,
                step.Arguments,
                plan.Workspace);

            var outputClosed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var errorClosed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    outputClosed.TrySetResult();
                    return;
                }

                _ = WriteLogAsync($"{step.Id} stdout: {e.Data}");
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    errorClosed.TrySetResult();
                    return;
                }

                _ = WriteLogAsync($"{step.Id} stderr: {e.Data}");
            };

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException(
                        $"Prozess konnte nicht gestartet werden: {step.Program}");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cancellationRegistration =
                    cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!process.HasExited)
                                process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                        }
                    });

                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(outputClosed.Task, errorClosed.Task);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                await WriteLogAsync(
                    $"FEHLER {step.Id}: {ex.Message}");

                return new(
                    Success: false,
                    Canceled: cancellationToken.IsCancellationRequested,
                    completed,
                    plan.Steps.Count,
                    logPath,
                    step.Id,
                    process.HasExited ? process.ExitCode : null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await WriteLogAsync(
                    $"ABGEBROCHEN {step.Id}");

                return new(
                    Success: false,
                    Canceled: true,
                    completed,
                    plan.Steps.Count,
                    logPath,
                    step.Id,
                    process.HasExited ? process.ExitCode : null);
            }

            if (process.ExitCode != 0)
            {
                await WriteLogAsync(
                    $"FEHLER {step.Id}: ExitCode={process.ExitCode}");

                return new(
                    Success: false,
                    Canceled: false,
                    completed,
                    plan.Steps.Count,
                    logPath,
                    step.Id,
                    process.ExitCode);
            }

            if (!string.IsNullOrWhiteSpace(step.OutputPath) &&
                !File.Exists(step.OutputPath))
            {
                await WriteLogAsync(
                    $"FEHLER {step.Id}: erwarteter Output fehlt: {step.OutputPath}");

                return new(
                    Success: false,
                    Canceled: false,
                    completed,
                    plan.Steps.Count,
                    logPath,
                    step.Id,
                    process.ExitCode);
            }

            completed++;
            await WriteLogAsync(
                $"OK {step.Id}");

            progress?.Report(new(
                completed,
                plan.Steps.Count,
                step.Id,
                $"Abgeschlossen: {step.Description}",
                completed * 100d / plan.Steps.Count));
        }

        await WriteLogAsync(
            "Smart Farming local processing erfolgreich abgeschlossen.");

        return new(
            Success: true,
            Canceled: false,
            completed,
            plan.Steps.Count,
            logPath,
            null,
            0);
    }

    private static Process CreateProcess(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var extension = Path.GetExtension(program);
        var isBatch = OperatingSystem.IsWindows() &&
                      (extension.Equals(
                           ".bat",
                           StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(
                           ".cmd",
                           StringComparison.OrdinalIgnoreCase));

        if (!isBatch)
        {
            var info = new ProcessStartInfo
            {
                FileName = program,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            return new Process { StartInfo = info };
        }

        var command =
            QuoteForCmd(program) +
            " " +
            string.Join(
                " ",
                arguments.Select(QuoteForCmd));

        var batchInfo = new ProcessStartInfo
        {
            FileName =
                Environment.GetEnvironmentVariable("ComSpec") ??
                "cmd.exe",
            WorkingDirectory = workingDirectory,
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
        if (value.IndexOfAny(
                ['&', '|', '<', '>', '^', '\r', '\n']) >= 0)
        {
            throw new InvalidOperationException(
                "Processing-Pfad/Argument enthält nicht unterstützte CMD-Metazeichen.");
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
