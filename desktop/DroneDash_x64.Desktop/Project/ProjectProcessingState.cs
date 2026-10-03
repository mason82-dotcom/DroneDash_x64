namespace DroneDash_x64.Desktop.Project;

internal static class ProjectProcessingState
{
    public static ProjectProcessingJob CreateJob(
        string projectPath,
        ProjectPipelineState pipeline,
        ProjectProcessingWorkerKind worker)
    {
        var id =
            Guid.NewGuid();

        var jobsDirectory =
            ProjectProcessingStore.GetJobsDirectory(
                projectPath);

        var logPath =
            Path.Combine(
                jobsDirectory,
                $"{pipeline.JobId:N}_{id:N}_{worker}.log");

        var stored =
            ProjectProcessingStore.ToStoredPath(
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
            ProjectProcessingPolicy.InitialMessage(worker),
            stored,
            [],
            now,
            null,
            null,
            now,
            null);
    }

    public static int FindLatestJobIndex(
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

    public static ProjectProcessingLedger ReplaceJob(
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

    public static ProjectProcessingJob? ResolveCurrent(
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
}
