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
