using System.IO;
using DroneDash_x64.Desktop.Project;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DroneDash_ProjectStoreTests_" + Guid.NewGuid().ToString("N"));

    public ProjectStoreTests() => Directory.CreateDirectory(_root);

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

    private string ProjectPath(string folder) =>
        Path.Combine(_root, folder, "field.ddproj");

    private static DroneDashProjectArtifact Artifact(string storedPath, bool isRelative) =>
        new(
            Guid.NewGuid(),
            ProjectArtifactKind.FlightPlan,
            ProjectReferenceKind.File,
            storedPath,
            isRelative,
            "Plan",
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            null);

    [Fact]
    public void ToStoredPath_StoresFilesBelowProjectAsRelative()
    {
        var projectPath = ProjectPath("project");
        var target = Path.Combine(_root, "project", "01_Planning", "plan.ddplan");

        var (stored, isRelative) = DroneDashProjectStore.ToStoredPath(projectPath, target);

        Assert.True(isRelative);
        Assert.Equal(Path.Combine("01_Planning", "plan.ddplan"), stored);
    }

    [Fact]
    public void ToStoredPath_KeepsFilesOutsideProjectAbsolute()
    {
        var projectPath = ProjectPath("project");
        var target = Path.Combine(_root, "other", "plan.ddplan");

        var (stored, isRelative) = DroneDashProjectStore.ToStoredPath(projectPath, target);

        Assert.False(isRelative);
        Assert.Equal(Path.GetFullPath(target), stored);
    }

    [Fact]
    public void ToStoredPath_TreatsDotDotPrefixedFolderNameAsInsideProject()
    {
        var projectPath = ProjectPath("project");
        var target = Path.Combine(_root, "project", "..data", "plan.ddplan");

        var (stored, isRelative) = DroneDashProjectStore.ToStoredPath(projectPath, target);

        Assert.True(isRelative);
        Assert.Equal(Path.Combine("..data", "plan.ddplan"), stored);
    }

    [Fact]
    public void ResolveArtifactPath_RejectsRelativePathEscapingProject()
    {
        var escaping = Artifact(Path.Combine("..", "outside.ddplan"), isRelative: true);

        Assert.Throws<InvalidDataException>(() =>
            DroneDashProjectStore.ResolveArtifactPath(ProjectPath("project"), escaping));
    }

    [Fact]
    public void ResolveArtifactPath_RejectsAbsolutePathMarkedRelative()
    {
        var mislabeled = Artifact(Path.Combine(_root, "plan.ddplan"), isRelative: true);

        Assert.Throws<InvalidDataException>(() =>
            DroneDashProjectStore.ResolveArtifactPath(ProjectPath("project"), mislabeled));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsArtifacts()
    {
        var projectPath = ProjectPath("project");
        var project = DroneDashProjectStore.Create("Feld Nord", "Test") with
        {
            Artifacts = [Artifact(Path.Combine("01_Planning", "plan.ddplan"), isRelative: true)]
        };

        DroneDashProjectStore.Save(projectPath, project);
        var loaded = DroneDashProjectStore.Load(projectPath);

        Assert.Equal(project.ProjectId, loaded.ProjectId);
        Assert.Equal("Feld Nord", loaded.Name);
        var artifact = Assert.Single(loaded.Artifacts);
        Assert.Equal(
            Path.Combine(_root, "project", "01_Planning", "plan.ddplan"),
            DroneDashProjectStore.ResolveArtifactPath(projectPath, artifact));
    }

    [Fact]
    public void Save_RejectsDuplicateArtifactIds()
    {
        var artifact = Artifact("plan.ddplan", isRelative: true);
        var project = DroneDashProjectStore.Create("Duplikat") with
        {
            Artifacts = [artifact, artifact with { Label = "Kopie" }]
        };

        Assert.Throws<InvalidDataException>(() =>
            DroneDashProjectStore.Save(ProjectPath("project"), project));
    }

    [Fact]
    public void Rebase_KeepsArtifactsPointingAtSameTargets()
    {
        var oldPath = ProjectPath("old");
        var newPath = ProjectPath("new");
        var insideOld = Path.Combine(_root, "old", "01_Planning", "plan.ddplan");
        var project = DroneDashProjectStore.Create("Umzug") with
        {
            Artifacts = [Artifact(Path.Combine("01_Planning", "plan.ddplan"), isRelative: true)]
        };

        var rebased = DroneDashProjectStore.Rebase(oldPath, newPath, project);

        var artifact = Assert.Single(rebased.Artifacts);
        Assert.False(artifact.IsRelative);
        Assert.Equal(
            Path.GetFullPath(insideOld),
            DroneDashProjectStore.ResolveArtifactPath(newPath, artifact));
    }

    [Fact]
    public void ProcessingTransitions_FollowJobLifecycle()
    {
        var created = DateTimeOffset.UnixEpoch;
        var job = new ProjectProcessingJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            default,
            default,
            ProjectProcessingJobStatus.Queued,
            0,
            null,
            "Wartend",
            new ProjectPipelinePath("job.log", true),
            [],
            created,
            null,
            null,
            created,
            null);

        var started = ProjectProcessingTransitions.Begin(job, "Start", "prepare", created.AddSeconds(1));
        Assert.Equal(ProjectProcessingJobStatus.Running, started.Status);
        Assert.Equal(1d, started.Percent);
        Assert.Equal(created.AddSeconds(1), started.StartedAtUtc);

        var reported = ProjectProcessingTransitions.Report(started, 140, "Fast fertig", "warp", created.AddSeconds(2));
        Assert.Equal(100d, reported.Percent);
        Assert.Equal(created.AddSeconds(1), reported.StartedAtUtc);

        var awaiting = ProjectProcessingTransitions.AwaitOutput(
            started with { Percent = 40 },
            "Prüfe Output",
            created.AddSeconds(3));
        Assert.Equal(ProjectProcessingJobStatus.AwaitingOutput, awaiting.Status);
        Assert.Equal(95d, awaiting.Percent);

        var failed = ProjectProcessingTransitions.Fail(awaiting, "kaputt", created.AddSeconds(4));
        Assert.Equal(ProjectProcessingJobStatus.Failed, failed.Status);
        Assert.Equal("kaputt", failed.Error);
        Assert.Equal(created.AddSeconds(4), failed.FinishedAtUtc);

        var restarted = ProjectProcessingTransitions.Begin(failed, "Erneut", null, created.AddSeconds(5));
        Assert.Null(restarted.Error);
        Assert.Null(restarted.FinishedAtUtc);
        Assert.Equal(created.AddSeconds(1), restarted.StartedAtUtc);

        var completed = ProjectProcessingTransitions.Complete(restarted, "Fertig", created.AddSeconds(6));
        Assert.Equal(ProjectProcessingJobStatus.Succeeded, completed.Status);
        Assert.Equal(100d, completed.Percent);
        Assert.Null(completed.CurrentStep);
    }
}
