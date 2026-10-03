namespace DroneDash_x64.Desktop.Project;

internal static class ProjectProcessingPolicy
{
    public static ProjectProcessingWorkerKind? ResolveExpectedWorker(
        DroneDashProject project,
        ProjectPipelineState pipeline)
    {
        return pipeline.Module switch
        {
            ProjectPipelineModule.Photogrammetry =>
                Has(
                    project,
                    ProjectArtifactKind.PhotogrammetryManifest)
                    ? null
                    : ProjectProcessingWorkerKind.PhotogrammetryDatasetAnalysis,

            ProjectPipelineModule.PvAnalysis =>
                Has(
                    project,
                    ProjectArtifactKind.PvAnalysis)
                    ? null
                    : ProjectProcessingWorkerKind.PvThermalBatchAnalysis,

            ProjectPipelineModule.SmartFarming =>
                !Has(
                    project,
                    ProjectArtifactKind.SmartFarmingDataset)
                    ? ProjectProcessingWorkerKind.SmartFarmingDatasetQa
                    : !Has(
                        project,
                        ProjectArtifactKind.NodeOdmResultArchive)
                        ? ProjectProcessingWorkerKind.SmartFarmingNodeOdm
                        : !Has(
                            project,
                            ProjectArtifactKind.SmartFarmingFieldProducts) &&
                          !Has(
                            project,
                            ProjectArtifactKind.VegetationRaster)
                            ? ProjectProcessingWorkerKind.SmartFarmingFieldProducts
                            : null,

            _ => null
        };
    }

    public static bool HasEvidenceForWorker(
        DroneDashProject project,
        ProjectProcessingWorkerKind worker) =>
        worker switch
        {
            ProjectProcessingWorkerKind.PhotogrammetryDatasetAnalysis =>
                Has(
                    project,
                    ProjectArtifactKind.PhotogrammetryManifest),
            ProjectProcessingWorkerKind.PvThermalBatchAnalysis =>
                Has(
                    project,
                    ProjectArtifactKind.PvAnalysis),
            ProjectProcessingWorkerKind.SmartFarmingDatasetQa =>
                Has(
                    project,
                    ProjectArtifactKind.SmartFarmingDataset),
            ProjectProcessingWorkerKind.SmartFarmingNodeOdm =>
                Has(
                    project,
                    ProjectArtifactKind.NodeOdmResultArchive),
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts =>
                Has(
                    project,
                    ProjectArtifactKind.SmartFarmingFieldProducts) ||
                Has(
                    project,
                    ProjectArtifactKind.VegetationRaster),
            _ => false
        };

    public static string InitialMessage(
        ProjectProcessingWorkerKind worker) =>
        worker switch
        {
            ProjectProcessingWorkerKind.PhotogrammetryDatasetAnalysis =>
                "Photogrammetrie-Datensatzanalyse und Manifest warten auf Ausführung.",
            ProjectProcessingWorkerKind.PvThermalBatchAnalysis =>
                "PV-Thermal-Batchanalyse wartet auf Ausführung.",
            ProjectProcessingWorkerKind.SmartFarmingDatasetQa =>
                "M3M-Datensatz-QA wartet auf Ausführung.",
            ProjectProcessingWorkerKind.SmartFarmingNodeOdm =>
                "NodeODM-M3M-Processing wartet auf Ausführung.",
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts =>
                "Georeferenzierte Smart-Farming-Feldprodukte warten auf Ausführung.",
            _ =>
                "Worker wartet auf Ausführung."
        };

    private static bool Has(
        DroneDashProject project,
        ProjectArtifactKind kind) =>
        project.Artifacts.Any(
            artifact =>
                artifact.Kind == kind);
}
