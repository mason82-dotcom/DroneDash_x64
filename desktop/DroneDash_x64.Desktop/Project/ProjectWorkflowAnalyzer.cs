namespace DroneDash_x64.Desktop.Project;

public sealed record ProjectWorkflowStage(
    string Name,
    bool Present,
    int ArtifactCount,
    string Detail)
{
    public string StateText =>
        Present ? "Vorhanden" : "Offen";
}

public sealed record ProjectWorkflowSnapshot(
    ProjectWorkflowStage Planning,
    ProjectWorkflowStage Dataset,
    ProjectWorkflowStage Processing,
    ProjectWorkflowStage Analysis)
{
    public IReadOnlyList<ProjectWorkflowStage> Stages =>
        [Planning, Dataset, Processing, Analysis];

    public int PresentStageCount =>
        Stages.Count(stage => stage.Present);

    public string SummaryText =>
        $"{PresentStageCount}/4 Workflow-Stufen mit Projektartefakten belegt";
}

public static class ProjectWorkflowAnalyzer
{
    public static ProjectWorkflowSnapshot Analyze(
        DroneDashProject? project)
    {
        var artifacts =
            project?.Artifacts ??
            [];

        var planning =
            Count(
                artifacts,
                ProjectArtifactKind.FlightPlan,
                ProjectArtifactKind.DjiWaylineKmz);

        var dataset =
            Count(
                artifacts,
                ProjectArtifactKind.PhotogrammetryManifest,
                ProjectArtifactKind.SmartFarmingDataset,
                ProjectArtifactKind.SourceDataFolder,
                ProjectArtifactKind.ThermalImage,
                ProjectArtifactKind.MultispectralImage);

        var processing =
            Count(
                artifacts,
                ProjectArtifactKind.LocalProcessingPlan,
                ProjectArtifactKind.NodeOdmResultArchive,
                ProjectArtifactKind.ProcessingWorkspace);

        var analysis =
            Count(
                artifacts,
                ProjectArtifactKind.PvAnalysis,
                ProjectArtifactKind.SmartFarmingFieldProducts,
                ProjectArtifactKind.VegetationRaster);

        return new(
            new(
                "Planung",
                planning > 0,
                planning,
                planning > 0
                    ? $"{planning} Flugplan-/KMZ-Artefakt(e)"
                    : "Noch kein .ddplan oder DJI-KMZ registriert."),
            new(
                "Datensatz",
                dataset > 0,
                dataset,
                dataset > 0
                    ? $"{dataset} Datensatz-/Quellartefakt(e)"
                    : "Noch kein Photogrammetrie-/Smart-Farming-Datensatz oder Quellordner registriert."),
            new(
                "Processing",
                processing > 0,
                processing,
                processing > 0
                    ? $"{processing} Processing-/NodeODM-Artefakt(e)"
                    : "Noch kein Processing-Plan, Workspace oder NodeODM-Ergebnis registriert."),
            new(
                "Analyse / Feldprodukt",
                analysis > 0,
                analysis,
                analysis > 0
                    ? $"{analysis} Analyse-/Feldprodukt(e)"
                    : "Noch keine PV-Analyse oder Smart-Farming-Feldprodukte registriert."));
    }

    private static int Count(
        IReadOnlyList<DroneDashProjectArtifact> artifacts,
        params ProjectArtifactKind[] kinds)
    {
        var set =
            kinds.ToHashSet();

        return artifacts.Count(
            artifact =>
                set.Contains(
                    artifact.Kind));
    }
}
