using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

internal sealed class PersistentPythonWorkerSession :
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    private readonly Process _process;
    private readonly string _program;
    private readonly string _workerPath;
    private readonly Func<string, Task> _writeLogAsync;
    private readonly Task _stderrPump;
    private bool _disposed;

    private PersistentPythonWorkerSession(
        Process process,
        string program,
        string workerPath,
        Func<string, Task> writeLogAsync)
    {
        _process = process;
        _program = program;
        _workerPath = workerPath;
        _writeLogAsync = writeLogAsync;
        _stderrPump = PumpStderrAsync();
    }

    public int? ExitCode =>
        _process.HasExited
            ? _process.ExitCode
            : null;

    public static bool CanHandle(
        LocalProcessingCommand step)
    {
        if (step.Arguments.Count == 0)
            return false;

        var programName =
            Path.GetFileNameWithoutExtension(
                step.Program);

        return programName.StartsWith(
                   "python",
                   StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(
                       step.Arguments[0])
                   .Equals(
                       "opencv_m3m.py",
                       StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(
        LocalProcessingCommand step)
    {
        if (!CanHandle(step))
            return false;

        return string.Equals(
                   _program,
                   step.Program,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   _workerPath,
                   step.Arguments[0],
                   StringComparison.OrdinalIgnoreCase);
    }

    public static PersistentPythonWorkerSession Start(
        LocalProcessingCommand step,
        string workingDirectory,
        Func<string, Task> writeLogAsync)
    {
        if (!CanHandle(step))
        {
            throw new ArgumentException(
                "Der Processing-Schritt ist kein unterstützter DroneDash-Python-Worker.",
                nameof(step));
        }

        var info = new ProcessStartInfo
        {
            FileName = step.Program,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        info.ArgumentList.Add(
            step.Arguments[0]);
        info.ArgumentList.Add(
            "--serve-jsonl");

        var process = new Process
        {
            StartInfo = info
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Persistenter Python-Worker konnte nicht gestartet werden: {step.Program}");
        }

        return new PersistentPythonWorkerSession(
            process,
            step.Program,
            step.Arguments[0],
            writeLogAsync);
    }

    public async Task<LocalProcessingRunner.StepExecutionResult>
        ExecuteAsync(
            LocalProcessingCommand step,
            CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (!Matches(step))
        {
            throw new InvalidOperationException(
                "Processing-Schritt passt nicht zur aktiven Python-Worker-Session.");
        }

        if (_process.HasExited)
        {
            return new(
                Success: false,
                Canceled: false,
                ExitCode: _process.ExitCode,
                Error:
                    $"Persistenter Python-Worker wurde unerwartet beendet (ExitCode {_process.ExitCode}).");
        }

        var request = JsonSerializer.Serialize(
            new PythonWorkerRequest(
                step.Id,
                step.Arguments
                    .Skip(1)
                    .ToArray()),
            JsonOptions);

        using var cancellationRegistration =
            cancellationToken.Register(
                KillWorker);

        await _process.StandardInput.WriteLineAsync(
            request);

        await _process.StandardInput.FlushAsync(
            cancellationToken);

        var responseLine =
            await _process.StandardOutput.ReadLineAsync(
                cancellationToken);

        if (responseLine is null)
        {
            return new(
                Success: false,
                Canceled:
                    cancellationToken.IsCancellationRequested,
                ExitCode: ExitCode,
                Error:
                    "Persistenter Python-Worker lieferte keine Antwort.");
        }

        PythonWorkerResponse? response;

        try
        {
            response =
                JsonSerializer.Deserialize<PythonWorkerResponse>(
                    responseLine,
                    JsonOptions);
        }
        catch (JsonException ex)
        {
            return new(
                Success: false,
                Canceled: false,
                ExitCode: ExitCode,
                Error:
                    $"Ungültige Python-Worker-Antwort: {ex.Message}");
        }

        if (response is null ||
            !string.Equals(
                response.Id,
                step.Id,
                StringComparison.Ordinal))
        {
            return new(
                Success: false,
                Canceled: false,
                ExitCode: ExitCode,
                Error:
                    "Python-Worker-Antwort konnte dem Processing-Schritt nicht zugeordnet werden.");
        }

        foreach (var line in response.Stdout ?? [])
        {
            await _writeLogAsync(
                $"{step.Id} stdout: {line}");
        }

        foreach (var line in response.Stderr ?? [])
        {
            await _writeLogAsync(
                $"{step.Id} stderr: {line}");
        }

        return new(
            Success: response.Ok,
            Canceled: false,
            ExitCode:
                response.Ok
                    ? 0
                    : 2,
            Error: response.Error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
        }

        try
        {
            if (!_process.HasExited)
            {
                var wait =
                    _process.WaitForExitAsync();

                var completed =
                    await Task.WhenAny(
                        wait,
                        Task.Delay(
                            TimeSpan.FromSeconds(2)));

                if (!ReferenceEquals(
                        completed,
                        wait))
                {
                    KillWorker();
                }
            }

            if (!_process.HasExited)
            {
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            KillWorker();
        }

        try
        {
            await _stderrPump;
        }
        catch
        {
        }

        _process.Dispose();
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (true)
            {
                var line =
                    await _process.StandardError.ReadLineAsync();

                if (line is null)
                    return;

                await _writeLogAsync(
                    $"python-worker stderr: {line}");
            }
        }
        catch
        {
        }
    }

    private void KillWorker()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(
                    entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed record PythonWorkerRequest(
        string Id,
        IReadOnlyList<string> Arguments);

    private sealed record PythonWorkerResponse(
        string? Id,
        bool Ok,
        IReadOnlyList<string>? Stdout,
        IReadOnlyList<string>? Stderr,
        string? Error);
}
