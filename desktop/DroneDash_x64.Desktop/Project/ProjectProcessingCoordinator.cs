using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.Project;

public enum ProjectProcessingWorkerKind
{
    PhotogrammetryDatasetAnalysis,
    PvThermalBatchAnalysis,
    SmartFarmingDatasetQa,
    SmartFarmingNodeOdm,
    SmartFarmingFieldProducts
}

public enum ProjectProcessingJobStatus
{
    Queued,
    Running,
    AwaitingOutput,
    Succeeded,
    Failed,
    Canceled
}

public sealed record ProjectProcessingOutput(
    ProjectArtifactKind Kind,
    ProjectPipelinePath Path,
    DateTimeOffset RegisteredAtUtc);

public sealed record ProjectProcessingJob(
    Guid Id,
    Guid PipelineJobId,
    ProjectPipelineModule Module,
    ProjectProcessingWorkerKind Worker,
    ProjectProcessingJobStatus Status,
    double Percent,
    string? CurrentStep,
    string Message,
    ProjectPipelinePath LogPath,
    IReadOnlyList<ProjectProcessingOutput> Outputs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Error)
{
    public string StatusText => Status switch
    {
        ProjectProcessingJobStatus.Queued => "Wartend",
        ProjectProcessingJobStatus.Running => "Läuft",
        ProjectProcessingJobStatus.AwaitingOutput => "Output ausstehend",
        ProjectProcessingJobStatus.Succeeded => "Erfolgreich",
        ProjectProcessingJobStatus.Failed => "Fehlgeschlagen",
        ProjectProcessingJobStatus.Canceled => "Abgebrochen",
        _ => Status.ToString()
    };

    public bool IsTerminal =>
        Status is
            ProjectProcessingJobStatus.Succeeded or
            ProjectProcessingJobStatus.Failed or
            ProjectProcessingJobStatus.Canceled;
}

public sealed record ProjectProcessingLedger(
    int SchemaVersion,
    Guid? CurrentJobId,
    IReadOnlyList<ProjectProcessingJob> Jobs)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class ProjectProcessingChangedEventArgs : EventArgs
{
    public ProjectProcessingChangedEventArgs(
        string projectPath,
        ProjectProcessingJob? job)
    {
        ProjectPath = projectPath;
        Job = job;
    }

    public string ProjectPath { get; }
    public ProjectProcessingJob? Job { get; }
}

public static class ProjectProcessingCoordinator
{
    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static event EventHandler<ProjectProcessingChangedEventArgs>? Changed;

    public static string GetLedgerPath(
        string projectPath) =>
        Path.Combine(
            ProjectWorkspaceLayout.GetProjectDirectory(
                projectPath),
            "project-processing-jobs.json");

    public static string GetJobsDirectory(
        string projectPath)
    {
        var path =
            Path.Combine(
                ProjectWorkspaceLayout.GetProjectDirectory(
                    projectPath),
                "03_Processing",
                "_ProjectJobs");

        Directory.CreateDirectory(path);
        return path;
    }

    public static ProjectProcessingLedger Load(
        string projectPath)
    {
        var path =
            GetLedgerPath(
                projectPath);

        if (!File.Exists(path))
        {
            return new(
                ProjectProcessingLedger.CurrentSchemaVersion,
                null,
                []);
        }

        var ledger =
            JsonSerializer.Deserialize<ProjectProcessingLedger>(
                File.ReadAllText(path),
                JsonOptions)
            ?? throw new InvalidDataException(
                "Processing-Job-Ledger konnte nicht gelesen werden.");

        if (ledger.SchemaVersion !=
            ProjectProcessingLedger.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Nicht unterstützte Processing-Job-Version {ledger.SchemaVersion}; erwartet {ProjectProcessingLedger.CurrentSchemaVersion}.");
        }

        return ledger;
    }

    public static void CopyRebased(
        string oldProjectPath,
        string newProjectPath)
    {
        if (!File.Exists(
                GetLedgerPath(
                    oldProjectPath)))
        {
            return;
        }

        lock (Sync)
        {
            var ledger =
                Load(
                    oldProjectPath);

            var jobs =
                ledger.Jobs
                    .Select(job =>
                    {
                        var oldLogPath =
                            ResolvePath(
                                oldProjectPath,
                                job.LogPath);

                        var rebasedLog =
                            ToStoredPath(
                                newProjectPath,
                                oldLogPath);

                        var outputs =
                            job.Outputs
                                .Select(output =>
                                {
                                    var oldOutput =
                                        ResolvePath(
                                            oldProjectPath,
                                            output.Path);

                                    return output with
                                    {
                                        Path =
                                            ToStoredPath(
                                                newProjectPath,
                                                oldOutput)
                                    };
                                })
                                .ToArray();

                        return job with
                        {
                            LogPath =
                                rebasedLog,
                            Outputs =
                                outputs,
                            UpdatedAtUtc =
                                DateTimeOffset.UtcNow
                        };
                    })
                    .ToArray();

            Save(
                newProjectPath,
                ledger with
                {
                    Jobs = jobs
                });
        }
    }

    public static ProjectProcessingJob? GetCurrent(
        string projectPath)
    {
        var ledger =
            Load(projectPath);

        if (ledger.CurrentJobId is not Guid id)
            return ledger.Jobs.LastOrDefault();

        return ledger.Jobs
            .FirstOrDefault(job =>
                job.Id == id)
            ?? ledger.Jobs.LastOrDefault();
    }

    public static ProjectProcessingJob? EnsureForPipeline(
        string projectPath,
        DroneDashProject project,
        ProjectPipelineState pipeline)
    {
        lock (Sync)
        {
            var ledger =
                Reconcile(
                    projectPath,
                    project,
                    pipeline,
                    Load(projectPath));

            var expected =
                ResolveExpectedWorker(
                    project,
                    pipeline);

            var current =
                ResolveCurrent(
                    ledger);

            if (expected is null)
            {
                Save(
                    projectPath,
                    ledger);

                RaiseChanged(
                    projectPath,
                    current);

                return current;
            }

            if (current is not null &&
                current.PipelineJobId ==
                    pipeline.JobId &&
                current.Worker ==
                    expected.Value &&
                !current.IsTerminal)
            {
                Save(
                    projectPath,
                    ledger);

                RaiseChanged(
                    projectPath,
                    current);

                return current;
            }

            var reusable =
                ledger.Jobs
                    .Where(job =>
                        job.PipelineJobId ==
                            pipeline.JobId &&
                        job.Worker ==
                            expected.Value &&
                        !job.IsTerminal)
                    .OrderByDescending(job =>
                        job.UpdatedAtUtc)
                    .FirstOrDefault();

            if (reusable is not null)
            {
                ledger =
                    ledger with
                    {
                        CurrentJobId =
                            reusable.Id
                    };

                Save(
                    projectPath,
                    ledger);

                RaiseChanged(
                    projectPath,
                    reusable);

                return reusable;
            }

            var created =
                CreateJob(
                    projectPath,
                    pipeline,
                    expected.Value);

            ledger =
                ledger with
                {
                    CurrentJobId =
                        created.Id,
                    Jobs =
                        ledger.Jobs
                            .Append(created)
                            .ToArray()
                };

            Save(
                projectPath,
                ledger);

            AppendLog(
                projectPath,
                created,
                $"QUEUED {created.Worker} · Pipeline {pipeline.JobId}");

            RaiseChanged(
                projectPath,
                created);

            return created;
        }
    }

    public static ProjectProcessingJob? BeginActive(
        ProjectProcessingWorkerKind worker,
        string message,
        string? step = null)
    {
        return UpdateActive(
            worker,
            job =>
                job with
                {
                    Status =
                        ProjectProcessingJobStatus.Running,
                    Percent =
                        Math.Max(
                            1d,
                            job.Percent),
                    CurrentStep =
                        step,
                    Message =
                        message,
                    StartedAtUtc =
                        job.StartedAtUtc ??
                        DateTimeOffset.UtcNow,
                    FinishedAtUtc =
                        null,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,
                    Error =
                        null
                },
            $"START {worker} · {message}");
    }

    public static ProjectProcessingJob? ReportActive(
        ProjectProcessingWorkerKind worker,
        double percent,
        string message,
        string? step = null,
        bool appendLog = false)
    {
        var normalized =
            Math.Clamp(
                percent,
                0d,
                100d);

        return UpdateActive(
            worker,
            job =>
                job with
                {
                    Status =
                        ProjectProcessingJobStatus.Running,
                    Percent =
                        normalized,
                    CurrentStep =
                        step,
                    Message =
                        message,
                    StartedAtUtc =
                        job.StartedAtUtc ??
                        DateTimeOffset.UtcNow,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,
                    Error =
                        null
                },
            appendLog
                ? $"PROGRESS {normalized:F1}% · {message}"
                : null);
    }

    public static ProjectProcessingJob? AwaitOutputActive(
        ProjectProcessingWorkerKind worker,
        string message)
    {
        return UpdateActive(
            worker,
            job =>
                job with
                {
                    Status =
                        ProjectProcessingJobStatus.AwaitingOutput,
                    Percent =
                        Math.Max(
                            95d,
                            job.Percent),
                    CurrentStep =
                        "output",
                    Message =
                        message,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,
                    Error =
                        null
                },
            $"AWAITING OUTPUT · {message}");
    }

    public static ProjectProcessingJob? CompleteActive(
        ProjectProcessingWorkerKind worker,
        string message)
    {
        return UpdateActive(
            worker,
            job =>
                job with
                {
                    Status =
                        ProjectProcessingJobStatus.Succeeded,
                    Percent =
                        100d,
                    CurrentStep =
                        null,
                    Message =
                        message,
                    FinishedAtUtc =
                        DateTimeOffset.UtcNow,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,
                    Error =
                        null
                },
            $"SUCCESS · {message}");
    }

    public static ProjectProcessingJob? FailActive(
        ProjectProcessingWorkerKind worker,
        string error)
    {
        return UpdateActive(
            worker,
            job =>
                job with
                {
                    Status =
                        ProjectProcessingJobStatus.Failed,
                    Message =
                        error,
                    FinishedAtUtc =
                        DateTimeOffset.UtcNow,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,
                    Error =
                        error
                },
            $"FAILED · {error}");
    }

    public static ProjectProcessingJob? CancelActive(
        ProjectProcessingWorkerKind worker,
        string message)
    {
        return UpdateActive(
            worker,
            job =>
                job with
                {
                    Status =
                        ProjectProcessingJobStatus.Canceled,
                    Message =
                        message,
                    FinishedAtUtc =
                        DateTimeOffset.UtcNow,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,
                    Error =
                        null
                },
            $"CANCELED · {message}");
    }

    public static void LogActive(
        ProjectProcessingWorkerKind worker,
        string line)
    {
        if (!TryGetActiveContext(
                out var projectPath,
                out var pipeline))
        {
            return;
        }

        lock (Sync)
        {
            var ledger =
                Load(projectPath);

            var index =
                FindLatestJobIndex(
                    ledger,
                    pipeline.JobId,
                    worker);

            if (index < 0)
                return;

            AppendLog(
                projectPath,
                ledger.Jobs[index],
                line);
        }
    }

    public static ProjectProcessingJob? RecordOutputActive(
        ProjectProcessingWorkerKind worker,
        string outputPath,
        ProjectArtifactKind kind)
    {
        if (!TryGetActiveContext(
                out var projectPath,
                out var pipeline))
        {
            return null;
        }

        var fullPath =
            Path.GetFullPath(
                outputPath);

        lock (Sync)
        {
            var ledger =
                Load(projectPath);

            var index =
                FindLatestJobIndex(
                    ledger,
                    pipeline.JobId,
                    worker);

            if (index < 0)
                return null;

            var stored =
                DroneDashProjectStore.ToStoredPath(
                    projectPath,
                    fullPath);

            var job =
                ledger.Jobs[index];

            var outputs =
                job.Outputs
                    .Where(output =>
                    {
                        var existing =
                            ResolvePath(
                                projectPath,
                                output.Path);

                        return !Path.GetFullPath(existing)
                            .Equals(
                                fullPath,
                                StringComparison.OrdinalIgnoreCase);
                    })
                    .Append(
                        new ProjectProcessingOutput(
                            kind,
                            new ProjectPipelinePath(
                                stored.StoredPath,
                                stored.IsRelative),
                            DateTimeOffset.UtcNow))
                    .ToArray();

            job =
                job with
                {
                    Outputs =
                        outputs,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                };

            ledger =
                ReplaceJob(
                    ledger,
                    index,
                    job);

            Save(
                projectPath,
                ledger);

            AppendLog(
                projectPath,
                job,
                $"OUTPUT {kind} · {fullPath}");

            RaiseChanged(
                projectPath,
                job);

            return job;
        }
    }

    public static string? ResolveLogPath(
        string projectPath,
        ProjectProcessingJob? job)
    {
        if (job is null)
            return null;

        return ResolvePath(
            projectPath,
            job.LogPath);
    }

    private static ProjectProcessingJob? UpdateActive(
        ProjectProcessingWorkerKind worker,
        Func<ProjectProcessingJob, ProjectProcessingJob> update,
        string? logLine)
    {
        if (!TryGetActiveContext(
                out var projectPath,
                out var pipeline))
        {
            return null;
        }

        lock (Sync)
        {
            var project =
                DroneDashProjectSession.CurrentProject;

            var ledger =
                Load(projectPath);

            if (project is not null)
            {
                ledger =
                    Reconcile(
                        projectPath,
                        project,
                        pipeline,
                        ledger);
            }

            var index =
                FindLatestJobIndex(
                    ledger,
                    pipeline.JobId,
                    worker);

            if (index < 0)
            {
                var created =
                    CreateJob(
                        projectPath,
                        pipeline,
                        worker);

                ledger =
                    ledger with
                    {
                        CurrentJobId =
                            created.Id,
                        Jobs =
                            ledger.Jobs
                                .Append(created)
                                .ToArray()
                    };

                index =
                    ledger.Jobs.Count - 1;
            }

            var previousCurrent =
                ResolveCurrent(
                    ledger);

            var updated =
                update(
                    ledger.Jobs[index]);

            var keepNewerCurrent =
                previousCurrent is not null &&
                previousCurrent.Id !=
                    updated.Id &&
                previousCurrent.PipelineJobId ==
                    pipeline.JobId &&
                !previousCurrent.IsTerminal;

            ledger =
                ReplaceJob(
                    ledger,
                    index,
                    updated) with
                {
                    CurrentJobId =
                        keepNewerCurrent
                            ? previousCurrent!.Id
                            : updated.Id
                };

            Save(
                projectPath,
                ledger);

            if (!string.IsNullOrWhiteSpace(
                    logLine))
            {
                AppendLog(
                    projectPath,
                    updated,
                    logLine);
            }

            RaiseChanged(
                projectPath,
                updated);

            return updated;
        }
    }

    private static ProjectProcessingLedger Reconcile(
        string projectPath,
        DroneDashProject project,
        ProjectPipelineState pipeline,
        ProjectProcessingLedger ledger)
    {
        var jobs =
            ledger.Jobs.ToArray();

        var changed =
            false;

        for (var index = 0;
             index < jobs.Length;
             index++)
        {
            var job =
                jobs[index];

            if (job.PipelineJobId !=
                    pipeline.JobId ||
                job.Status ==
                    ProjectProcessingJobStatus.Succeeded)
            {
                continue;
            }

            if (!HasEvidenceForWorker(
                    project,
                    job.Worker))
            {
                continue;
            }

            jobs[index] =
                job with
                {
                    Status =
                        ProjectProcessingJobStatus.Succeeded,
                    Percent =
                        100d,
                    CurrentStep =
                        null,
                    Message =
                        "Erwartetes Projektartefakt ist registriert.",
                    FinishedAtUtc =
                        job.FinishedAtUtc ??
                        DateTimeOffset.UtcNow,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,
                    Error =
                        null
                };

            AppendLog(
                projectPath,
                jobs[index],
                "SUCCESS · erwartetes Projektartefakt registriert.");

            changed = true;
        }

        return changed
            ? ledger with
              {
                  Jobs = jobs
              }
            : ledger;
    }

    private static ProjectProcessingWorkerKind? ResolveExpectedWorker(
        DroneDashProject project,
        ProjectPipelineState pipeline)
    {
        return pipeline.Module switch
        {
            ProjectPipelineModule.Photogrammetry =>
                Has(
                    project,
                    ProjectArtifactKind.PhotogrammetryManifest)
                    ? null
                    : ProjectProcessingWorkerKind.PhotogrammetryDatasetAnalysis,

            ProjectPipelineModule.PvAnalysis =>
                Has(
                    project,
                    ProjectArtifactKind.PvAnalysis)
                    ? null
                    : ProjectProcessingWorkerKind.PvThermalBatchAnalysis,

            ProjectPipelineModule.SmartFarming =>
                !Has(
                    project,
                    ProjectArtifactKind.SmartFarmingDataset)
                    ? ProjectProcessingWorkerKind.SmartFarmingDatasetQa
                    : !Has(
                        project,
                        ProjectArtifactKind.NodeOdmResultArchive)
                        ? ProjectProcessingWorkerKind.SmartFarmingNodeOdm
                        : !Has(
                            project,
                            ProjectArtifactKind.SmartFarmingFieldProducts) &&
                          !Has(
                            project,
                            ProjectArtifactKind.VegetationRaster)
                            ? ProjectProcessingWorkerKind.SmartFarmingFieldProducts
                            : null,

            _ => null
        };
    }

    private static bool HasEvidenceForWorker(
        DroneDashProject project,
        ProjectProcessingWorkerKind worker) =>
        worker switch
        {
            ProjectProcessingWorkerKind.PhotogrammetryDatasetAnalysis =>
                Has(
                    project,
                    ProjectArtifactKind.PhotogrammetryManifest),
            ProjectProcessingWorkerKind.PvThermalBatchAnalysis =>
                Has(
                    project,
                    ProjectArtifactKind.PvAnalysis),
            ProjectProcessingWorkerKind.SmartFarmingDatasetQa =>
                Has(
                    project,
                    ProjectArtifactKind.SmartFarmingDataset),
            ProjectProcessingWorkerKind.SmartFarmingNodeOdm =>
                Has(
                    project,
                    ProjectArtifactKind.NodeOdmResultArchive),
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts =>
                Has(
                    project,
                    ProjectArtifactKind.SmartFarmingFieldProducts) ||
                Has(
                    project,
                    ProjectArtifactKind.VegetationRaster),
            _ => false
        };

    private static ProjectProcessingJob CreateJob(
        string projectPath,
        ProjectPipelineState pipeline,
        ProjectProcessingWorkerKind worker)
    {
        var id =
            Guid.NewGuid();

        var jobsDirectory =
            GetJobsDirectory(
                projectPath);

        var logPath =
            Path.Combine(
                jobsDirectory,
                $"{pipeline.JobId:N}_{id:N}_{worker}.log");

        var stored =
            DroneDashProjectStore.ToStoredPath(
                projectPath,
                logPath);

        var now =
            DateTimeOffset.UtcNow;

        return new(
            id,
            pipeline.JobId,
            pipeline.Module,
            worker,
            ProjectProcessingJobStatus.Queued,
            0d,
            null,
            InitialMessage(worker),
            new ProjectPipelinePath(
                stored.StoredPath,
                stored.IsRelative),
            [],
            now,
            null,
            null,
            now,
            null);
    }

    private static string InitialMessage(
        ProjectProcessingWorkerKind worker) =>
        worker switch
        {
            ProjectProcessingWorkerKind.PhotogrammetryDatasetAnalysis =>
                "Photogrammetrie-Datensatzanalyse und Manifest warten auf Ausführung.",
            ProjectProcessingWorkerKind.PvThermalBatchAnalysis =>
                "PV-Thermal-Batchanalyse wartet auf Ausführung.",
            ProjectProcessingWorkerKind.SmartFarmingDatasetQa =>
                "M3M-Datensatz-QA wartet auf Ausführung.",
            ProjectProcessingWorkerKind.SmartFarmingNodeOdm =>
                "NodeODM-M3M-Processing wartet auf Ausführung.",
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts =>
                "Georeferenzierte Smart-Farming-Feldprodukte warten auf Ausführung.",
            _ =>
                "Worker wartet auf Ausführung."
        };

    private static int FindLatestJobIndex(
        ProjectProcessingLedger ledger,
        Guid pipelineJobId,
        ProjectProcessingWorkerKind worker)
    {
        for (var index =
                 ledger.Jobs.Count - 1;
             index >= 0;
             index--)
        {
            var job =
                ledger.Jobs[index];

            if (job.PipelineJobId ==
                    pipelineJobId &&
                job.Worker ==
                    worker)
            {
                return index;
            }
        }

        return -1;
    }

    private static ProjectProcessingLedger ReplaceJob(
        ProjectProcessingLedger ledger,
        int index,
        ProjectProcessingJob job)
    {
        var jobs =
            ledger.Jobs.ToArray();

        jobs[index] =
            job;

        return ledger with
        {
            Jobs = jobs
        };
    }

    private static ProjectProcessingJob? ResolveCurrent(
        ProjectProcessingLedger ledger)
    {
        if (ledger.CurrentJobId is Guid id)
        {
            var selected =
                ledger.Jobs.FirstOrDefault(job =>
                    job.Id == id);

            if (selected is not null)
                return selected;
        }

        return ledger.Jobs.LastOrDefault();
    }

    private static void Save(
        string projectPath,
        ProjectProcessingLedger ledger)
    {
        var path =
            GetLedgerPath(
                projectPath);

        var tempPath =
            path + ".tmp";

        File.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(
                ledger,
                JsonOptions),
            new UTF8Encoding(false));

        File.Move(
            tempPath,
            path,
            overwrite: true);
    }

    private static void AppendLog(
        string projectPath,
        ProjectProcessingJob job,
        string line)
    {
        try
        {
            var path =
                ResolvePath(
                    projectPath,
                    job.LogPath);

            Directory.CreateDirectory(
                Path.GetDirectoryName(
                    path)!);

            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Job state is authoritative. Logging remains best-effort.
        }
    }

    private static bool TryGetActiveContext(
        out string projectPath,
        out ProjectPipelineState pipeline)
    {
        projectPath =
            DroneDashProjectSession.CurrentProjectPath ??
            "";

        pipeline = null!;

        if (!DroneDashProjectSession.IsOpen ||
            string.IsNullOrWhiteSpace(
                projectPath))
        {
            return false;
        }

        try
        {
            pipeline =
                ProjectPipelineStore.Load(
                    projectPath)!;

            return pipeline is not null;
        }
        catch
        {
            return false;
        }
    }

    private static ProjectPipelinePath ToStoredPath(
        string projectPath,
        string targetPath)
    {
        var stored =
            DroneDashProjectStore.ToStoredPath(
                projectPath,
                targetPath);

        return new(
            stored.StoredPath,
            stored.IsRelative);
    }

    private static string ResolvePath(
        string projectPath,
        ProjectPipelinePath path)
    {
        if (!path.IsRelative)
        {
            return Path.GetFullPath(
                path.StoredPath);
        }

        return Path.GetFullPath(
            Path.Combine(
                ProjectWorkspaceLayout.GetProjectDirectory(
                    projectPath),
                path.StoredPath));
    }

    private static bool Has(
        DroneDashProject project,
        ProjectArtifactKind kind) =>
        project.Artifacts.Any(
            artifact =>
                artifact.Kind == kind);

    private static void RaiseChanged(
        string projectPath,
        ProjectProcessingJob? job)
    {
        Changed?.Invoke(
            null,
            new ProjectProcessingChangedEventArgs(
                projectPath,
                job));
    }
}
