using System.IO;

namespace DroneDash_x64.Desktop.Project;

public static class ProjectProcessingCoordinator
{
    private static readonly object Sync = new();

    public static event EventHandler<ProjectProcessingChangedEventArgs>? Changed;

    public static string GetLedgerPath(
        string projectPath) =>
        ProjectProcessingStore.GetLedgerPath(
            projectPath);

    public static string GetJobsDirectory(
        string projectPath) =>
        ProjectProcessingStore.GetJobsDirectory(
            projectPath);

    public static ProjectProcessingLedger Load(
        string projectPath) =>
        ProjectProcessingStore.Load(
            projectPath);

    public static void CopyRebased(
        string oldProjectPath,
        string newProjectPath)
    {
        lock (Sync)
        {
            ProjectProcessingStore.CopyRebased(
                oldProjectPath,
                newProjectPath);
        }
    }

    public static ProjectProcessingJob? GetCurrent(
        string projectPath)
    {
        var ledger =
            ProjectProcessingStore.Load(projectPath);

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
                    ProjectProcessingStore.Load(projectPath));

            var expected =
                ProjectProcessingPolicy.ResolveExpectedWorker(
                    project,
                    pipeline);

            var current =
                ProjectProcessingState.ResolveCurrent(
                    ledger);

            if (expected is null)
            {
                ProjectProcessingStore.Save(
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
                ProjectProcessingStore.Save(
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

                ProjectProcessingStore.Save(
                    projectPath,
                    ledger);

                RaiseChanged(
                    projectPath,
                    reusable);

                return reusable;
            }

            var created =
                ProjectProcessingState.CreateJob(
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

            ProjectProcessingStore.Save(
                projectPath,
                ledger);

            ProjectProcessingStore.AppendLog(
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
                ProjectProcessingTransitions.Begin(
                    job,
                    message,
                    step,
                    DateTimeOffset.UtcNow),
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
                ProjectProcessingTransitions.Report(
                    job,
                    normalized,
                    message,
                    step,
                    DateTimeOffset.UtcNow),
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
                ProjectProcessingTransitions.AwaitOutput(
                    job,
                    message,
                    DateTimeOffset.UtcNow),
            $"AWAITING OUTPUT · {message}");
    }

    public static ProjectProcessingJob? CompleteActive(
        ProjectProcessingWorkerKind worker,
        string message)
    {
        return UpdateActive(
            worker,
            job =>
                ProjectProcessingTransitions.Complete(
                    job,
                    message,
                    DateTimeOffset.UtcNow),
            $"SUCCESS · {message}");
    }

    public static ProjectProcessingJob? FailActive(
        ProjectProcessingWorkerKind worker,
        string error)
    {
        return UpdateActive(
            worker,
            job =>
                ProjectProcessingTransitions.Fail(
                    job,
                    error,
                    DateTimeOffset.UtcNow),
            $"FAILED · {error}");
    }

    public static ProjectProcessingJob? CancelActive(
        ProjectProcessingWorkerKind worker,
        string message)
    {
        return UpdateActive(
            worker,
            job =>
                ProjectProcessingTransitions.Cancel(
                    job,
                    message,
                    DateTimeOffset.UtcNow),
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
                ProjectProcessingStore.Load(projectPath);

            var index =
                ProjectProcessingState.FindLatestJobIndex(
                    ledger,
                    pipeline.JobId,
                    worker);

            if (index < 0)
                return;

            ProjectProcessingStore.AppendLog(
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
                ProjectProcessingStore.Load(
                    projectPath);

            var index =
                ProjectProcessingState.FindLatestJobIndex(
                    ledger,
                    pipeline.JobId,
                    worker);

            if (index < 0)
                return null;

            var job =
                ProjectProcessingOutputTracker.Record(
                    projectPath,
                    ledger.Jobs[index],
                    fullPath,
                    kind,
                    DateTimeOffset.UtcNow);

            ledger =
                ProjectProcessingState.ReplaceJob(
                    ledger,
                    index,
                    job);

            ProjectProcessingStore.Save(
                projectPath,
                ledger);

            ProjectProcessingStore.AppendLog(
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

        return ProjectProcessingStore.ResolvePath(
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
                ProjectProcessingStore.Load(projectPath);

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
                ProjectProcessingState.FindLatestJobIndex(
                    ledger,
                    pipeline.JobId,
                    worker);

            if (index < 0)
            {
                var created =
                    ProjectProcessingState.CreateJob(
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
                ProjectProcessingState.ResolveCurrent(
                    ledger);

            var target =
                ledger.Jobs[index];

            if (target.IsTerminal)
                return target;

            var updated =
                update(
                    target);

            var keepNewerCurrent =
                previousCurrent is not null &&
                previousCurrent.Id !=
                    updated.Id &&
                previousCurrent.PipelineJobId ==
                    pipeline.JobId &&
                !previousCurrent.IsTerminal;

            ledger =
                ProjectProcessingState.ReplaceJob(
                    ledger,
                    index,
                    updated) with
                {
                    CurrentJobId =
                        keepNewerCurrent
                            ? previousCurrent!.Id
                            : updated.Id
                };

            ProjectProcessingStore.Save(
                projectPath,
                ledger);

            if (!string.IsNullOrWhiteSpace(
                    logLine))
            {
                ProjectProcessingStore.AppendLog(
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

            if (!ProjectProcessingPolicy.HasEvidenceForWorker(
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

            ProjectProcessingStore.AppendLog(
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
