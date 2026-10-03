using System.IO;

namespace DroneDash_x64.Desktop.Project;

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
