using System.IO;

namespace DroneDash_x64.Desktop.Project;

public enum ProjectWorkspaceFolder
{
    Planning,
    PhotogrammetryDataset,
    PhotogrammetryProcessing,
    PhotogrammetryResults,
    PvDataset,
    PvProcessing,
    PvResults,
    SmartFarmingDataset,
    SmartFarmingProcessing,
    SmartFarmingResults,
    Exports
}

public enum ProjectNavigationTarget
{
    Planning,
    Photogrammetry,
    PvAnalysis,
    SmartFarming
}

public sealed record ProjectNavigationRequest(
    ProjectNavigationTarget Target,
    string? FlightPlanPath,
    string Reason,
    string? DatasetFolder = null);

public static class ProjectWorkspaceLayout
{
    private static readonly IReadOnlyDictionary<ProjectWorkspaceFolder, string> RelativeFolders =
        new Dictionary<ProjectWorkspaceFolder, string>
        {
            [ProjectWorkspaceFolder.Planning] = "01_Planning",
            [ProjectWorkspaceFolder.PhotogrammetryDataset] = Path.Combine("02_Datasets", "Photogrammetry"),
            [ProjectWorkspaceFolder.PvDataset] = Path.Combine("02_Datasets", "PV"),
            [ProjectWorkspaceFolder.SmartFarmingDataset] = Path.Combine("02_Datasets", "SmartFarming"),
            [ProjectWorkspaceFolder.PhotogrammetryProcessing] = Path.Combine("03_Processing", "Photogrammetry"),
            [ProjectWorkspaceFolder.PvProcessing] = Path.Combine("03_Processing", "PV"),
            [ProjectWorkspaceFolder.SmartFarmingProcessing] = Path.Combine("03_Processing", "SmartFarming"),
            [ProjectWorkspaceFolder.PhotogrammetryResults] = Path.Combine("04_Results", "Photogrammetry"),
            [ProjectWorkspaceFolder.PvResults] = Path.Combine("04_Results", "PV"),
            [ProjectWorkspaceFolder.SmartFarmingResults] = Path.Combine("04_Results", "SmartFarming"),
            [ProjectWorkspaceFolder.Exports] = "05_Exports"
        };

    public static string GetProjectDirectory(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);

        return Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "Projektpfad besitzt keinen gültigen Projektordner.");
    }

    public static string GetFolder(
        string projectPath,
        ProjectWorkspaceFolder folder,
        bool ensureExists = true)
    {
        if (!RelativeFolders.TryGetValue(folder, out var relative))
            throw new ArgumentOutOfRangeException(nameof(folder));

        var path = Path.GetFullPath(
            Path.Combine(
                GetProjectDirectory(projectPath),
                relative));

        if (ensureExists)
            Directory.CreateDirectory(path);

        return path;
    }

    public static string? TryGetActiveFolder(
        ProjectWorkspaceFolder folder,
        bool ensureExists = true)
    {
        if (!DroneDashProjectSession.IsOpen ||
            string.IsNullOrWhiteSpace(
                DroneDashProjectSession.CurrentProjectPath))
        {
            return null;
        }

        try
        {
            return GetFolder(
                DroneDashProjectSession.CurrentProjectPath,
                folder,
                ensureExists);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<ProjectWorkspaceFolder, string> EnsureStandardFolders(
        string projectPath)
    {
        var result =
            new Dictionary<ProjectWorkspaceFolder, string>();

        foreach (var folder in RelativeFolders.Keys)
        {
            result[folder] =
                GetFolder(
                    projectPath,
                    folder,
                    ensureExists: true);
        }

        return result;
    }
}
