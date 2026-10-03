using System.IO;

namespace DroneDash_x64.Desktop.Project;

internal static class ProjectProcessingOutputTracker
{
    public static ProjectProcessingJob Record(
        string projectPath,
        ProjectProcessingJob job,
        string fullPath,
        ProjectArtifactKind kind,
        DateTimeOffset now)
    {
        var stored =
            DroneDashProjectStore.ToStoredPath(
                projectPath,
                fullPath);

        var outputs =
            job.Outputs
                .Where(output =>
                {
                    var existing =
                        ProjectProcessingStore.ResolvePath(
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
                        now))
                .ToArray();

        return job with
        {
            Outputs =
                outputs,
            UpdatedAtUtc =
                now
        };
    }
}
