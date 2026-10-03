using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DroneDash_x64.Desktop.SmartFarming;

namespace DroneDash_x64.Desktop.Project;

public enum ProjectPipelineModule
{
    Unknown,
    Photogrammetry,
    PvAnalysis,
    SmartFarming
}

public enum ProjectPipelineStage
{
    Ingested,
    DatasetQa,
    Processing,
    Results
}

public enum ProjectPipelineGateState
{
    Open,
    Pass,
    Warning,
    Blocked
}

public sealed record ProjectPipelinePath(
    string StoredPath,
    bool IsRelative);

public sealed record ProjectDatasetProbe(
    ProjectPipelineModule Module,
    int TotalFiles,
    int SupportedImageFiles,
    int ThermalFiles,
    int M3mFiles,
    int M3mBandFiles,
    bool Ambiguous,
    string Detail);

public sealed record ProjectPipelineState(
    int SchemaVersion,
    Guid JobId,
    ProjectPipelineModule Module,
    ProjectPipelineStage Stage,
    ProjectPipelinePath SourceFolder,
    ProjectPipelinePath? FlightPlan,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ProjectDatasetProbe Probe)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ProjectPipelineGate(
    string Name,
    ProjectPipelineGateState State,
    string Detail)
{
    public string StateText => State switch
    {
        ProjectPipelineGateState.Pass => "PASS",
        ProjectPipelineGateState.Warning => "PRÜFEN",
        ProjectPipelineGateState.Blocked => "BLOCKIERT",
        _ => "OFFEN"
    };
}

public sealed record ProjectPipelineGateSnapshot(
    ProjectPipelineGate FlightPlan,
    ProjectPipelineGate Dataset,
    ProjectPipelineGate Rtk,
    ProjectPipelineGate Processing,
    ProjectPipelineGate Results)
{
    public IReadOnlyList<ProjectPipelineGate> Gates =>
        [FlightPlan, Dataset, Rtk, Processing, Results];

    public int PassedCount =>
        Gates.Count(gate =>
            gate.State ==
            ProjectPipelineGateState.Pass);

    public bool HasBlocker =>
        Gates.Any(gate =>
            gate.State ==
            ProjectPipelineGateState.Blocked);

    public string SummaryText =>
        $"{PassedCount}/5 Gates PASS" +
        (HasBlocker
            ? " · Blocker vorhanden"
            : "");
}

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

        var state =
            JsonSerializer.Deserialize<ProjectPipelineState>(
                File.ReadAllText(path),
                JsonOptions)
            ?? throw new InvalidDataException(
                "Projekt-Pipeline konnte nicht gelesen werden.");

        if (state.SchemaVersion !=
            ProjectPipelineState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Nicht unterstützte Pipeline-Version {state.SchemaVersion}; erwartet {ProjectPipelineState.CurrentSchemaVersion}.");
        }

        return state;
    }

    public static void Save(
        string projectPath,
        ProjectPipelineState state)
    {
        var path =
            GetStatePath(
                projectPath);

        var tempPath =
            path + ".tmp";

        File.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(
                state,
                JsonOptions),
            new UTF8Encoding(false));

        File.Move(
            tempPath,
            path,
            overwrite: true);
    }

    public static ProjectPipelineState Rebase(
        string oldProjectPath,
        string newProjectPath,
        ProjectPipelineState state)
    {
        var source =
            ResolveSourceFolder(
                oldProjectPath,
                state);

        var flightPlan =
            ResolveFlightPlan(
                oldProjectPath,
                state);

        return state with
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
    }

    public static ProjectPipelineState RefreshStage(
        string projectPath,
        DroneDashProject project,
        ProjectPipelineState state)
    {
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
        string sourceFolder)
    {
        var total = 0;
        var supported = 0;
        var thermal = 0;
        var m3m = 0;
        var m3mBands = 0;

        foreach (var path in
                 Directory.EnumerateFiles(
                     sourceFolder,
                     "*",
                     SearchOption.AllDirectories))
        {
            total++;

            var extension =
                Path.GetExtension(path);

            if (!IsSupportedImageExtension(
                    extension))
            {
                continue;
            }

            supported++;

            var name =
                Path.GetFileName(path);

            if (name.EndsWith(
                    "_T.JPG",
                    StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(
                    "_T.JPEG",
                    StringComparison.OrdinalIgnoreCase))
            {
                thermal++;
            }

            if (M3mCaptureKeyParser.TryParse(
                    name,
                    out _,
                    out var kind))
            {
                m3m++;

                if (!kind.Equals(
                        "D",
                        StringComparison.OrdinalIgnoreCase))
                {
                    m3mBands++;
                }
            }
        }

        var ambiguous =
            thermal > 0 &&
            m3mBands > 0;

        var module =
            ambiguous
                ? ProjectPipelineModule.Unknown
                : m3mBands >= 4
                    ? ProjectPipelineModule.SmartFarming
                    : thermal > 0
                        ? ProjectPipelineModule.PvAnalysis
                        : supported > 0
                            ? ProjectPipelineModule.Photogrammetry
                            : ProjectPipelineModule.Unknown;

        var detail =
            ambiguous
                ? $"Gemischter Datensatz: {thermal:N0} Thermaldateien und {m3mBands:N0} M3M-Banddateien. Automatische Modulwahl aus Sicherheitsgründen ausgesetzt."
                : module switch
                {
                    ProjectPipelineModule.SmartFarming =>
                        $"M3M erkannt · {m3m:N0} M3M-Dateien, davon {m3mBands:N0} Multispektralbänder.",
                    ProjectPipelineModule.PvAnalysis =>
                        $"PV/Thermal erkannt · {thermal:N0} _T.JPG/_T.JPEG-Dateien.",
                    ProjectPipelineModule.Photogrammetry =>
                        $"Photogrammetrie erkannt · {supported:N0} unterstützte Bilddateien.",
                    _ =>
                        "Kein unterstützter DJI-Bilddatensatz erkannt."
                };

        return new(
            module,
            total,
            supported,
            thermal,
            m3m,
            m3mBands,
            ambiguous,
            detail);
    }

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

    private static bool IsSupportedImageExtension(
        string extension) =>
        extension.Equals(
            ".jpg",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".jpeg",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".dng",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".tif",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".tiff",
            StringComparison.OrdinalIgnoreCase);

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
            return Path.GetFullPath(
                path.StoredPath);

        return Path.GetFullPath(
            Path.Combine(
                ProjectWorkspaceLayout.GetProjectDirectory(
                    projectPath),
                path.StoredPath));
    }
}

public static class ProjectPipelineGateEvaluator
{
    public static ProjectPipelineGateSnapshot Evaluate(
        string projectPath,
        DroneDashProject project,
        ProjectPipelineState state)
    {
        var dashboard =
            ProjectDashboardAnalyzer.Analyze(
                projectPath,
                project);

        return new(
            EvaluateFlightPlan(
                projectPath,
                state),
            EvaluateDataset(
                project,
                state),
            FromDashboard(
                "RTK",
                dashboard.Rtk),
            EvaluateProcessing(
                projectPath,
                project,
                state),
            EvaluateResults(
                dashboard,
                project,
                state));
    }

    private static ProjectPipelineGate EvaluateFlightPlan(
        string projectPath,
        ProjectPipelineState state)
    {
        var path =
            ProjectPipelineStore.ResolveFlightPlan(
                projectPath,
                state);

        if (string.IsNullOrWhiteSpace(path))
        {
            return new(
                "Flugplan",
                ProjectPipelineGateState.Warning,
                "Kein Flugplan mit dem Pipeline-Job verknüpft.");
        }

        return File.Exists(path)
            ? new(
                "Flugplan",
                ProjectPipelineGateState.Pass,
                Path.GetFileName(path))
            : new(
                "Flugplan",
                ProjectPipelineGateState.Blocked,
                $"Verknüpfter Flugplan fehlt: {path}");
    }

    private static ProjectPipelineGate EvaluateDataset(
        DroneDashProject project,
        ProjectPipelineState state)
    {
        var qaPresent =
            state.Module switch
            {
                ProjectPipelineModule.Photogrammetry =>
                    Has(
                        project,
                        ProjectArtifactKind.PhotogrammetryManifest),
                ProjectPipelineModule.PvAnalysis =>
                    Has(
                        project,
                        ProjectArtifactKind.PvAnalysis),
                ProjectPipelineModule.SmartFarming =>
                    Has(
                        project,
                        ProjectArtifactKind.SmartFarmingDataset),
                _ => false
            };

        if (state.Module ==
            ProjectPipelineModule.Unknown)
        {
            return new(
                "Datensatz",
                ProjectPipelineGateState.Blocked,
                state.Probe.Detail);
        }

        return qaPresent
            ? new(
                "Datensatz",
                ProjectPipelineGateState.Pass,
                $"QA-Artefakt für {state.Module} registriert.")
            : new(
                "Datensatz",
                ProjectPipelineGateState.Warning,
                $"Ingest abgeschlossen; {state.Module}-Datensatz-QA noch ausführen.");
    }

    private static ProjectPipelineGate EvaluateProcessing(
        string projectPath,
        DroneDashProject project,
        ProjectPipelineState state)
    {
        var job =
            ProjectProcessingCoordinator.GetCurrent(
                projectPath);

        if (job is not null &&
            job.PipelineJobId ==
                state.JobId)
        {
            if (job.Status ==
                ProjectProcessingJobStatus.Failed)
            {
                return new(
                    "Processing",
                    ProjectPipelineGateState.Blocked,
                    $"{job.Worker} fehlgeschlagen: {job.Error ?? job.Message}");
            }

            if (job.Status ==
                ProjectProcessingJobStatus.Canceled)
            {
                return new(
                    "Processing",
                    ProjectPipelineGateState.Warning,
                    $"{job.Worker} abgebrochen: {job.Message}");
            }

            if (job.Status is
                    ProjectProcessingJobStatus.Running or
                    ProjectProcessingJobStatus.AwaitingOutput)
            {
                return new(
                    "Processing",
                    ProjectPipelineGateState.Warning,
                    $"{job.Worker} · {job.StatusText} · {job.Percent:F1}% · {job.Message}");
            }
        }

        return state.Module switch
        {
            ProjectPipelineModule.PvAnalysis =>
                Has(
                    project,
                    ProjectArtifactKind.PvAnalysis)
                    ? new(
                        "Processing",
                        ProjectPipelineGateState.Pass,
                        "PV-Batchanalyse abgeschlossen und registriert.")
                    : new(
                        "Processing",
                        ProjectPipelineGateState.Open,
                        "PV-Batchanalyse noch nicht abgeschlossen."),

            ProjectPipelineModule.SmartFarming =>
                Has(
                    project,
                    ProjectArtifactKind.NodeOdmResultArchive) ||
                Has(
                    project,
                    ProjectArtifactKind.SmartFarmingFieldProducts)
                    ? new(
                        "Processing",
                        ProjectPipelineGateState.Pass,
                        "NodeODM-/Feldprodukt-Ergebnis registriert.")
                    : Has(
                        project,
                        ProjectArtifactKind.LocalProcessingPlan) ||
                      Has(
                        project,
                        ProjectArtifactKind.ProcessingWorkspace)
                        ? new(
                            "Processing",
                            ProjectPipelineGateState.Warning,
                            "Processing vorbereitet; Ergebnis noch offen.")
                        : new(
                            "Processing",
                            ProjectPipelineGateState.Open,
                            "Noch kein Smart-Farming-Processing gestartet."),

            ProjectPipelineModule.Photogrammetry =>
                Has(
                    project,
                    ProjectArtifactKind.PhotogrammetryManifest)
                    ? new(
                        "Processing",
                        ProjectPipelineGateState.Warning,
                        "Processing-Manifest bereit; finaler Photogrammetrie-Worker/Output noch offen.")
                    : new(
                        "Processing",
                        ProjectPipelineGateState.Open,
                        "Photogrammetrie-Manifest noch nicht erzeugt."),

            _ =>
                new(
                    "Processing",
                    ProjectPipelineGateState.Blocked,
                    "Kein eindeutiges Zielmodul.")
        };
    }

    private static ProjectPipelineGate EvaluateResults(
        ProjectDashboardSnapshot dashboard,
        DroneDashProject project,
        ProjectPipelineState state)
    {
        if (state.Module ==
            ProjectPipelineModule.Photogrammetry)
        {
            return new(
                "Ergebnis",
                ProjectPipelineGateState.Open,
                "Finale Photogrammetrie-Ergebnisartefakte sind in dieser Stufe noch nicht angebunden.");
        }

        if (state.Module ==
            ProjectPipelineModule.PvAnalysis &&
            !Has(
                project,
                ProjectArtifactKind.PvAnalysis))
        {
            return new(
                "Ergebnis",
                ProjectPipelineGateState.Open,
                "PV-Ergebnis noch nicht registriert.");
        }

        if (state.Module ==
                ProjectPipelineModule.SmartFarming &&
            !Has(
                project,
                ProjectArtifactKind.SmartFarmingFieldProducts) &&
            !Has(
                project,
                ProjectArtifactKind.VegetationRaster))
        {
            return new(
                "Ergebnis",
                ProjectPipelineGateState.Open,
                "Smart-Farming-Feldprodukte noch nicht registriert.");
        }

        return FromDashboard(
            "Ergebnis",
            dashboard.Results);
    }

    private static ProjectPipelineGate FromDashboard(
        string name,
        ProjectQaIndicator indicator) =>
        new(
            name,
            indicator.State switch
            {
                ProjectQaState.Good =>
                    ProjectPipelineGateState.Pass,
                ProjectQaState.Warning =>
                    ProjectPipelineGateState.Warning,
                ProjectQaState.Error =>
                    ProjectPipelineGateState.Blocked,
                _ =>
                    ProjectPipelineGateState.Open
            },
            indicator.Detail);

    private static bool Has(
        DroneDashProject project,
        ProjectArtifactKind kind) =>
        project.Artifacts.Any(
            artifact =>
                artifact.Kind == kind);
}
