namespace DroneDash_x64.Desktop.Project;

internal static class ProjectProcessingTransitions
{
    public static ProjectProcessingJob Begin(
        ProjectProcessingJob job,
        string message,
        string? step,
        DateTimeOffset now) =>
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
                now,
            FinishedAtUtc =
                null,
            UpdatedAtUtc =
                now,
            Error =
                null
        };

    public static ProjectProcessingJob Report(
        ProjectProcessingJob job,
        double percent,
        string message,
        string? step,
        DateTimeOffset now) =>
        job with
        {
            Status =
                ProjectProcessingJobStatus.Running,
            Percent =
                Math.Clamp(
                    percent,
                    0d,
                    100d),
            CurrentStep =
                step,
            Message =
                message,
            StartedAtUtc =
                job.StartedAtUtc ??
                now,
            UpdatedAtUtc =
                now,
            Error =
                null
        };

    public static ProjectProcessingJob AwaitOutput(
        ProjectProcessingJob job,
        string message,
        DateTimeOffset now) =>
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
                now,
            Error =
                null
        };

    public static ProjectProcessingJob Complete(
        ProjectProcessingJob job,
        string message,
        DateTimeOffset now) =>
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
                now,
            UpdatedAtUtc =
                now,
            Error =
                null
        };

    public static ProjectProcessingJob Fail(
        ProjectProcessingJob job,
        string error,
        DateTimeOffset now) =>
        job with
        {
            Status =
                ProjectProcessingJobStatus.Failed,
            Message =
                error,
            FinishedAtUtc =
                now,
            UpdatedAtUtc =
                now,
            Error =
                error
        };

    public static ProjectProcessingJob Cancel(
        ProjectProcessingJob job,
        string message,
        DateTimeOffset now) =>
        job with
        {
            Status =
                ProjectProcessingJobStatus.Canceled,
            Message =
                message,
            FinishedAtUtc =
                now,
            UpdatedAtUtc =
                now,
            Error =
                null
        };
}
