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

        var logGate = new SemaphoreSlim(1, 1);

        async Task WriteLogAsync(string line)
        {
            var formatted =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {line}";

            await logGate.WaitAsync();
            try
            {
                await log.WriteLineAsync(formatted);
            }
            finally
            {
                logGate.Release();
            }

            logSink?.Invoke(formatted);
        }

        await WriteLogAsync(
            $"Start Smart Farming local processing · capture={plan.CaptureKey} · steps={plan.Steps.Count}");

        var completed = 0;
        PersistentPythonWorkerSession? pythonWorker = null;

        try
        {
            foreach (var step in plan.Steps)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await WriteLogAsync(
                        "Abbruch vor dem nächsten Processing-Schritt.");

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

                StepExecutionResult execution;

                if (PersistentPythonWorkerSession.CanHandle(step))
                {
                    if (pythonWorker is null ||
                        !pythonWorker.Matches(step))
                    {
                        if (pythonWorker is not null)
                            await pythonWorker.DisposeAsync();

                        pythonWorker =
                            PersistentPythonWorkerSession.Start(
                                step,
                                plan.Workspace,
                                WriteLogAsync);

                        await WriteLogAsync(
                            $"Python-Worker persistent gestartet · {Path.GetFileName(step.Arguments[0])}");
                    }

                    try
                    {
                        execution =
                            await pythonWorker.ExecuteAsync(
                                step,
                                cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        execution =
                            new StepExecutionResult(
                                Success: false,
                                Canceled: true,
                                ExitCode: null,
                                Error: "Abbruch angefordert.");
                    }
                    catch (Exception ex)
                    {
                        execution =
                            new StepExecutionResult(
                                Success: false,
                                Canceled: cancellationToken.IsCancellationRequested,
                                ExitCode: pythonWorker.ExitCode,
                                Error: ex.Message);
                    }
                }
                else
                {
                    execution =
                        await RunOneShotProcessAsync(
                            step,
                            plan.Workspace,
                            WriteLogAsync,
                            cancellationToken);
                }

                if (execution.Canceled ||
                    cancellationToken.IsCancellationRequested)
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
                        execution.ExitCode);
                }

                if (!execution.Success)
                {
                    await WriteLogAsync(
                        $"FEHLER {step.Id}: " +
                        (string.IsNullOrWhiteSpace(execution.Error)
                            ? $"ExitCode={execution.ExitCode}"
                            : execution.Error));

                    return new(
                        Success: false,
                        Canceled: false,
                        completed,
                        plan.Steps.Count,
                        logPath,
                        step.Id,
                        execution.ExitCode);
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
                        execution.ExitCode);
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
        finally
        {
            if (pythonWorker is not null)
                await pythonWorker.DisposeAsync();

            logGate.Dispose();
        }
    }

    private static async Task<StepExecutionResult> RunOneShotProcessAsync(
        LocalProcessingCommand step,
        string workingDirectory,
        Func<string, Task> writeLogAsync,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(
            step.Program,
            step.Arguments,
            workingDirectory);

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

            _ = writeLogAsync(
                $"{step.Id} stdout: {e.Data}");
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorClosed.TrySetResult();
                return;
            }

            _ = writeLogAsync(
                $"{step.Id} stderr: {e.Data}");
        };

        var started = false;

        try
        {
            if (!process.Start())
            {
                return new(
                    Success: false,
                    Canceled: false,
                    ExitCode: null,
                    Error:
                        $"Prozess konnte nicht gestartet werden: {step.Program}");
            }

            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cancellationRegistration =
                cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(
                                entireProcessTree: true);
                    }
                    catch
                    {
                    }
                });

            await process.WaitForExitAsync(
                CancellationToken.None);

            await Task.WhenAll(
                outputClosed.Task,
                errorClosed.Task);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            return new(
                Success: false,
                Canceled: cancellationToken.IsCancellationRequested,
                ExitCode:
                    started && process.HasExited
                        ? process.ExitCode
                        : null,
                Error: ex.Message);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new(
                Success: false,
                Canceled: true,
                ExitCode:
                    process.HasExited
                        ? process.ExitCode
                        : null,
                Error: "Abbruch angefordert.");
        }

        return new(
            Success: process.ExitCode == 0,
            Canceled: false,
            ExitCode: process.ExitCode,
            Error:
                process.ExitCode == 0
                    ? null
                    : $"ExitCode={process.ExitCode}");
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

            return new Process
            {
                StartInfo = info
            };
        }

        var command =
            QuoteForCmd(program) +
            " " +
            string.Join(
                " ",
                arguments.Select(
                    QuoteForCmd));

        var batchInfo = new ProcessStartInfo
        {
            FileName =
                Environment.GetEnvironmentVariable(
                    "ComSpec") ??
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

        return new Process
        {
            StartInfo = batchInfo
        };
    }

    private static string QuoteForCmd(
        string value)
    {
        if (value.IndexOfAny(
                ['&', '|', '<', '>', '^', '\r', '\n']) >= 0)
        {
            throw new InvalidOperationException(
                "Processing-Pfad/Argument enthält nicht unterstützte CMD-Metazeichen.");
        }

        return "\"" +
               value.Replace(
                   "\"",
                   "\"\"") +
               "\"";
    }

    internal sealed record StepExecutionResult(
        bool Success,
        bool Canceled,
        int? ExitCode,
        string? Error);
}
