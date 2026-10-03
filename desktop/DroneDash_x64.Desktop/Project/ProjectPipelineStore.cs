using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.Project;

public static class ProjectPipelineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetStatePath(
        string projectPath) =>
        Path.Combine(
            ProjectWorkspaceLayout.GetProjectDirectory(
                projectPath),
            "project-pipeline.json");

    public static ProjectPipelineState Start(
        string projectPath,
        string sourceFolder,
        string? flightPlanPath = null)
    {
        var source =
            Path.GetFullPath(
                sourceFolder);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                source);
        }

        var probe =
            ProbeDataset(
                source);

        var now =
            DateTimeOffset.UtcNow;

        var state =
            new ProjectPipelineState(
                ProjectPipelineState.CurrentSchemaVersion,
                Guid.NewGuid(),
                probe.Module,
                ProjectPipelineStage.Ingested,
                ToStoredPath(
                    projectPath,
                    source),
                string.IsNullOrWhiteSpace(
                    flightPlanPath)
                    ? null
                    : ToStoredPath(
                        projectPath,
                        Path.GetFullPath(
                            flightPlanPath)),
                now,
                now,
                probe);

        Save(
            projectPath,
            state);

        return state;
    }

    public static ProjectPipelineState? Load(
        string projectPath)
    {
        var path =
            GetStatePath(
                projectPath);

        if (!File.Exists(path))
            return null;

        ProjectPipelineState state;

        try
        {
            state =
                JsonSerializer.Deserialize<ProjectPipelineState>(
                    File.ReadAllText(path),
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "Projekt-Pipeline konnte nicht gelesen werden.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Projekt-Pipeline enthält ungültiges JSON.",
                ex);
        }

        ValidateState(
            projectPath,
            state);

        return state;
    }

    public static void Save(
        string projectPath,
        ProjectPipelineState state)
    {
        ValidateState(
            projectPath,
            state);

        var path =
            Path.GetFullPath(
                GetStatePath(
                    projectPath));

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        var tempPath =
            path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false)))
            {
                writer.Write(
                    JsonSerializer.Serialize(
                        state,
                        JsonOptions));

                writer.Flush();
                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                tempPath,
                path,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Cleanup must not hide the pipeline save result.
            }
        }
    }

    public static ProjectPipelineState Rebase(
        string oldProjectPath,
        string newProjectPath,
        ProjectPipelineState state)
    {
        ValidateState(
            oldProjectPath,
            state);

        var source =
            ResolveSourceFolder(
                oldProjectPath,
                state);

        var flightPlan =
            ResolveFlightPlan(
                oldProjectPath,
                state);

        var rebased =
            state with
            {
                SourceFolder =
                    ToStoredPath(
                        newProjectPath,
                        source),
                FlightPlan =
                    string.IsNullOrWhiteSpace(
                        flightPlan)
                        ? null
                        : ToStoredPath(
                            newProjectPath,
                            flightPlan),
                UpdatedAtUtc =
                    DateTimeOffset.UtcNow
            };

        ValidateState(
            newProjectPath,
            rebased);

        return rebased;
    }

    public static ProjectPipelineState RefreshStage(
        string projectPath,
        DroneDashProject project,
        ProjectPipelineState state)
    {
        ValidateState(
            projectPath,
            state);

        var gates =
            ProjectPipelineGateEvaluator.Evaluate(
                projectPath,
                project,
                state);

        var stage =
            gates.Dataset.State !=
                ProjectPipelineGateState.Pass
                ? ProjectPipelineStage.DatasetQa
                : gates.Processing.State !=
                    ProjectPipelineGateState.Pass
                    ? ProjectPipelineStage.Processing
                    : ProjectPipelineStage.Results;

        var updated =
            state with
            {
                Stage = stage,
                UpdatedAtUtc =
                    DateTimeOffset.UtcNow
            };

        Save(
            projectPath,
            updated);

        return updated;
    }

    public static string ResolveSourceFolder(
        string projectPath,
        ProjectPipelineState state) =>
        ResolvePath(
            projectPath,
            state.SourceFolder);

    public static string? ResolveFlightPlan(
        string projectPath,
        ProjectPipelineState state) =>
        state.FlightPlan is null
            ? null
            : ResolvePath(
                projectPath,
                state.FlightPlan);

    public static ProjectDatasetProbe ProbeDataset(
        string sourceFolder) =>
        ProjectDatasetProbeDetector.Probe(
            sourceFolder);

    public static ProjectNavigationTarget? ToNavigationTarget(
        ProjectPipelineModule module) =>
        module switch
        {
            ProjectPipelineModule.Photogrammetry =>
                ProjectNavigationTarget.Photogrammetry,
            ProjectPipelineModule.PvAnalysis =>
                ProjectNavigationTarget.PvAnalysis,
            ProjectPipelineModule.SmartFarming =>
                ProjectNavigationTarget.SmartFarming,
            _ => null
        };

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
        if (string.IsNullOrWhiteSpace(
                path.StoredPath))
        {
            throw new InvalidDataException(
                "Pipeline enthält einen leeren Pfad.");
        }

        if (!path.IsRelative)
        {
            return Path.GetFullPath(
                path.StoredPath);
        }

        if (Path.IsPathRooted(
                path.StoredPath))
        {
            throw new InvalidDataException(
                "Pipeline markiert einen absoluten Pfad fälschlich als relativ.");
        }

        var projectDirectory =
            Path.GetFullPath(
                ProjectWorkspaceLayout.GetProjectDirectory(
                    projectPath));

        var resolved =
            Path.GetFullPath(
                Path.Combine(
                    projectDirectory,
                    path.StoredPath));

        var rootPrefix =
            projectDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        if (!resolved.Equals(
                projectDirectory,
                StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Relativer Pipeline-Pfad verlässt den Projektordner: {path.StoredPath}");
        }

        return resolved;
    }

    private static void ValidateState(
        string projectPath,
        ProjectPipelineState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SchemaVersion !=
            ProjectPipelineState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Nicht unterstützte Pipeline-Version {state.SchemaVersion}; erwartet {ProjectPipelineState.CurrentSchemaVersion}.");
        }

        if (state.JobId ==
            Guid.Empty)
        {
            throw new InvalidDataException(
                "Projekt-Pipeline enthält keine gültige Job-ID.");
        }

        if (!Enum.IsDefined(
                state.Module) ||
            !Enum.IsDefined(
                state.Stage))
        {
            throw new InvalidDataException(
                "Projekt-Pipeline enthält einen unbekannten Modul- oder Stage-Wert.");
        }

        if (state.SourceFolder is null)
        {
            throw new InvalidDataException(
                "Projekt-Pipeline enthält keinen Quelldatensatzpfad.");
        }

        _ =
            ResolvePath(
                projectPath,
                state.SourceFolder);

        if (state.FlightPlan is not null)
        {
            _ =
                ResolvePath(
                    projectPath,
                    state.FlightPlan);
        }

        if (state.Probe is null)
        {
            throw new InvalidDataException(
                "Projekt-Pipeline enthält keine Dataset-Probe.");
        }

        if (!Enum.IsDefined(
                state.Probe.Module) ||
            state.Probe.Module !=
                state.Module)
        {
            throw new InvalidDataException(
                "Pipeline-Modul und Dataset-Probe sind inkonsistent.");
        }

        if (state.Probe.TotalFiles < 0 ||
            state.Probe.SupportedImageFiles < 0 ||
            state.Probe.ThermalFiles < 0 ||
            state.Probe.M3mFiles < 0 ||
            state.Probe.M3mBandFiles < 0 ||
            state.Probe.SupportedImageFiles >
                state.Probe.TotalFiles ||
            state.Probe.ThermalFiles >
                state.Probe.SupportedImageFiles ||
            state.Probe.M3mFiles >
                state.Probe.SupportedImageFiles ||
            state.Probe.M3mBandFiles >
                state.Probe.M3mFiles)
        {
            throw new InvalidDataException(
                "Dataset-Probe enthält inkonsistente Dateizähler.");
        }

        var expectedAmbiguous =
            state.Probe.ThermalFiles > 0 &&
            state.Probe.M3mBandFiles > 0;

        if (state.Probe.Ambiguous !=
            expectedAmbiguous)
        {
            throw new InvalidDataException(
                "Dataset-Probe enthält einen inkonsistenten Ambiguous-Status.");
        }

        if (string.IsNullOrWhiteSpace(
                state.Probe.Detail))
        {
            throw new InvalidDataException(
                "Dataset-Probe enthält keine Beschreibung.");
        }
    }
}
