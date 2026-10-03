using System.IO;

namespace DroneDash_x64.Desktop.Project;

public sealed class ProjectSessionChangedEventArgs : EventArgs
{
    public ProjectSessionChangedEventArgs(
        string reason,
        string? projectPath,
        DroneDashProject? project)
    {
        Reason = reason;
        ProjectPath = projectPath;
        Project = project;
    }

    public string Reason { get; }
    public string? ProjectPath { get; }
    public DroneDashProject? Project { get; }
}

public static class DroneDashProjectSession
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static event EventHandler<ProjectSessionChangedEventArgs>? Changed;
    public static event EventHandler<ProjectNavigationRequest>? NavigationRequested;

    public static string? CurrentProjectPath { get; private set; }
    public static DroneDashProject? CurrentProject { get; private set; }

    public static bool IsOpen =>
        CurrentProject is not null &&
        !string.IsNullOrWhiteSpace(CurrentProjectPath);

    public static void Activate(
        string projectPath,
        DroneDashProject project,
        string reason = "Projekt aktiviert")
    {
        CurrentProjectPath =
            Path.GetFullPath(projectPath);

        CurrentProject =
            project;

        TryEnsureWorkspaceFolders();
        RaiseChanged(reason);
    }

    public static void Clear(
        string reason = "Projekt geschlossen")
    {
        CurrentProjectPath = null;
        CurrentProject = null;
        RaiseChanged(reason);
    }

    public static async Task<bool> RegisterFileAsync(
        string filePath,
        ProjectArtifactKind? explicitKind = null,
        CancellationToken cancellationToken = default)
    {
        var count =
            await RegisterFilesAsync(
                [filePath],
                explicitKind,
                cancellationToken);

        return count > 0;
    }

    public static async Task<int> RegisterFilesAsync(
        IEnumerable<string> filePaths,
        ProjectArtifactKind? explicitKind = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            return 0;

        await Gate.WaitAsync(
            cancellationToken);

        string? reason = null;
        var changedCount = 0;

        try
        {
            if (!TryGetCurrent(
                    out var projectPath,
                    out var project))
            {
                return 0;
            }

            var paths =
                filePaths
                    .Where(path =>
                        !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            if (paths.Length == 0)
                return 0;

            var artifacts =
                project.Artifacts.ToList();

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var artifact =
                    await ProjectArtifactService.CreateFileArtifactAsync(
                        projectPath,
                        path,
                        explicitKind,
                        cancellationToken);

                var existingIndex =
                    FindArtifactIndexByResolvedPath(
                        projectPath,
                        artifacts,
                        path);

                if (existingIndex >= 0)
                {
                    var previous =
                        artifacts[existingIndex];

                    artifacts[existingIndex] =
                        artifact with
                        {
                            Id = previous.Id,
                            AddedAtUtc =
                                previous.AddedAtUtc
                        };
                }
                else
                {
                    artifacts.Add(
                        artifact);
                }

                changedCount++;
            }

            var updated =
                project with
                {
                    Artifacts =
                        artifacts.ToArray()
                };

            SaveAndReload(
                projectPath,
                updated);

            reason =
                changedCount == 1
                    ? "Projektartefakt automatisch registriert"
                    : $"{changedCount} Projektartefakte automatisch registriert";
        }
        finally
        {
            Gate.Release();
        }

        if (reason is not null)
            RaiseChanged(reason);

        return changedCount;
    }

    public static async Task<bool> RegisterDirectoryAsync(
        string directoryPath,
        ProjectArtifactKind kind =
            ProjectArtifactKind.SourceDataFolder,
        CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            return false;

        await Gate.WaitAsync(
            cancellationToken);

        var changed = false;

        try
        {
            if (!TryGetCurrent(
                    out var projectPath,
                    out var project))
            {
                return false;
            }

            var fullPath =
                Path.GetFullPath(
                    directoryPath);

            var artifact =
                ProjectArtifactService.CreateDirectoryArtifact(
                    projectPath,
                    fullPath,
                    kind);

            var artifacts =
                project.Artifacts.ToList();

            var existingIndex =
                FindArtifactIndexByResolvedPath(
                    projectPath,
                    artifacts,
                    fullPath);

            if (existingIndex >= 0)
            {
                var previous =
                    artifacts[existingIndex];

                artifacts[existingIndex] =
                    artifact with
                    {
                        Id = previous.Id,
                        AddedAtUtc =
                            previous.AddedAtUtc
                    };
            }
            else
            {
                artifacts.Add(
                    artifact);
            }

            var updated =
                project with
                {
                    Artifacts =
                        artifacts.ToArray()
                };

            SaveAndReload(
                projectPath,
                updated);

            changed = true;
        }
        finally
        {
            Gate.Release();
        }

        if (changed)
        {
            RaiseChanged(
                "Projektordner automatisch registriert");
        }

        return changed;
    }

    public static async Task SaveMetadataAsync(
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            throw new InvalidOperationException(
                "Kein DroneDash-Projekt geöffnet.");

        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            if (!TryGetCurrent(
                    out var projectPath,
                    out var project))
            {
                throw new InvalidOperationException(
                    "Kein DroneDash-Projekt geöffnet.");
            }

            var updated =
                DroneDashProjectStore.WithMetadata(
                    project,
                    name,
                    description);

            SaveAndReload(
                projectPath,
                updated);
        }
        finally
        {
            Gate.Release();
        }

        RaiseChanged(
            "Projekt gespeichert");
    }

    public static async Task SaveAsAsync(
        string newProjectPath,
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            throw new InvalidOperationException(
                "Kein DroneDash-Projekt geöffnet.");

        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            if (!TryGetCurrent(
                    out var oldProjectPath,
                    out var project))
            {
                throw new InvalidOperationException(
                    "Kein DroneDash-Projekt geöffnet.");
            }

            var target =
                Path.GetFullPath(
                    newProjectPath);

            var metadata =
                DroneDashProjectStore.WithMetadata(
                    project,
                    name,
                    description);

            var rebased =
                DroneDashProjectStore.Rebase(
                    oldProjectPath,
                    target,
                    metadata);

            DroneDashProjectStore.Save(
                target,
                rebased);

            CurrentProjectPath =
                target;

            CurrentProject =
                DroneDashProjectStore.Load(
                    target);

            TryEnsureWorkspaceFolders();
        }
        finally
        {
            Gate.Release();
        }

        RaiseChanged(
            "Projekt unter neuem Pfad gespeichert");
    }

    public static async Task RemoveArtifactsAsync(
        IEnumerable<Guid> artifactIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            return;

        var ids =
            artifactIds
                .Where(id =>
                    id != Guid.Empty)
                .ToHashSet();

        if (ids.Count == 0)
            return;

        await Gate.WaitAsync(
            cancellationToken);

        var changed = false;

        try
        {
            if (!TryGetCurrent(
                    out var projectPath,
                    out var project))
            {
                return;
            }

            var remaining =
                project.Artifacts
                    .Where(artifact =>
                        !ids.Contains(artifact.Id))
                    .ToArray();

            if (remaining.Length ==
                project.Artifacts.Count)
            {
                return;
            }

            SaveAndReload(
                projectPath,
                project with
                {
                    Artifacts = remaining
                });

            changed = true;
        }
        finally
        {
            Gate.Release();
        }

        if (changed)
        {
            RaiseChanged(
                $"{ids.Count} Projektartefakt-Referenz(en) entfernt");
        }
    }

    public static bool ContainsResolvedPath(
        string path)
    {
        if (!TryGetCurrent(
                out var projectPath,
                out var project))
        {
            return false;
        }

        return FindArtifactIndexByResolvedPath(
                   projectPath,
                   project.Artifacts,
                   path) >= 0;
    }

    public static string? TryResolveLatestArtifactPath(
        ProjectArtifactKind kind)
    {
        if (!TryGetCurrent(
                out var projectPath,
                out var project))
        {
            return null;
        }

        var artifact =
            project.Artifacts
                .Where(item =>
                    item.Kind == kind)
                .OrderByDescending(item =>
                    item.AddedAtUtc)
                .FirstOrDefault();

        if (artifact is null)
            return null;

        try
        {
            var path =
                DroneDashProjectStore.ResolveArtifactPath(
                    projectPath,
                    artifact);

            return artifact.ReferenceKind ==
                   ProjectReferenceKind.Directory
                ? Directory.Exists(path)
                    ? path
                    : null
                : File.Exists(path)
                    ? path
                    : null;
        }
        catch
        {
            return null;
        }
    }

    public static void RequestNavigation(
        ProjectNavigationTarget target,
        string? flightPlanPath = null,
        string reason = "Workflow-Navigation",
        string? datasetFolder = null)
    {
        var resolvedFlightPlan =
            string.IsNullOrWhiteSpace(flightPlanPath)
                ? TryResolveLatestArtifactPath(
                    ProjectArtifactKind.FlightPlan)
                : Path.GetFullPath(flightPlanPath);

        var resolvedDataset =
            string.IsNullOrWhiteSpace(datasetFolder)
                ? null
                : Path.GetFullPath(datasetFolder);

        NavigationRequested?.Invoke(
            null,
            new ProjectNavigationRequest(
                target,
                resolvedFlightPlan,
                reason,
                resolvedDataset));
    }

    private static bool TryGetCurrent(
        out string projectPath,
        out DroneDashProject project)
    {
        if (CurrentProject is null ||
            string.IsNullOrWhiteSpace(
                CurrentProjectPath))
        {
            projectPath = "";
            project = null!;
            return false;
        }

        projectPath =
            CurrentProjectPath;

        project =
            CurrentProject;

        return true;
    }

    private static int FindArtifactIndexByResolvedPath(
        string projectPath,
        IReadOnlyList<DroneDashProjectArtifact> artifacts,
        string candidatePath)
    {
        var normalizedCandidate =
            NormalizePath(
                candidatePath);

        for (var index = 0;
             index < artifacts.Count;
             index++)
        {
            try
            {
                var resolved =
                    DroneDashProjectStore.ResolveArtifactPath(
                        projectPath,
                        artifacts[index]);

                if (NormalizePath(resolved).Equals(
                        normalizedCandidate,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            catch
            {
            }
        }

        return -1;
    }

    private static string NormalizePath(
        string path) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));

    private static void TryEnsureWorkspaceFolders()
    {
        if (string.IsNullOrWhiteSpace(
                CurrentProjectPath))
        {
            return;
        }

        try
        {
            ProjectWorkspaceLayout.EnsureStandardFolders(
                CurrentProjectPath);
        }
        catch
        {
            // A read-only or unavailable project root must not prevent
            // opening the project. Module dialogs will gracefully fall
            // back to their previous/default locations.
        }
    }

    private static void SaveAndReload(
        string projectPath,
        DroneDashProject project)
    {
        DroneDashProjectStore.Save(
            projectPath,
            project);

        CurrentProjectPath =
            Path.GetFullPath(
                projectPath);

        CurrentProject =
            DroneDashProjectStore.Load(
                CurrentProjectPath);
    }

    private static void RaiseChanged(
        string reason)
    {
        Changed?.Invoke(
            null,
            new ProjectSessionChangedEventArgs(
                reason,
                CurrentProjectPath,
                CurrentProject));
    }
}
