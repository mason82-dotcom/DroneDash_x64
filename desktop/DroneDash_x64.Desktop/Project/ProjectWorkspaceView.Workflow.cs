using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DroneDash_x64.Desktop.Planning;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Project;

public partial class ProjectWorkspaceView
{
    private void ApplySession(
        string? projectPath,
        DroneDashProject? project,
        string reason)
    {
        _projectPath =
            projectPath;

        _project =
            project;

        if (_project is null ||
            string.IsNullOrWhiteSpace(
                _projectPath))
        {
            ResetUi(
                reason);

            return;
        }

        ProjectPathText.Text =
            _projectPath;

        ProjectNameBox.Text =
            _project.Name;

        ProjectDescriptionBox.Text =
            _project.Description ?? "";

        ProjectIdText.Text =
            _project.ProjectId.ToString();

        ProjectNameBox.IsEnabled = true;
        ProjectDescriptionBox.IsEnabled = true;
        SaveProjectButton.IsEnabled = true;
        SaveProjectAsButton.IsEnabled = true;
        AddFilesButton.IsEnabled = true;
        AddFolderButton.IsEnabled = true;
        DiscoverButton.IsEnabled = true;
        VerifyButton.IsEnabled = true;
        IngestDatasetButton.IsEnabled = true;
        RefreshPipelineButton.IsEnabled = true;

        RefreshRows();
        RefreshWorkflow();
        RefreshPipelineUi(
            refreshStage: true);

        ProjectSummaryText.Text =
            BuildSummary(
                reason);
    }

    private void ResetUi(
        string reason)
    {
        _rows.Clear();

        ProjectPathText.Text =
            "Noch kein Projekt geöffnet.";

        ProjectNameBox.Text = "";
        ProjectDescriptionBox.Text = "";
        ProjectIdText.Text = "—";

        ProjectNameBox.IsEnabled = false;
        ProjectDescriptionBox.IsEnabled = false;
        SaveProjectButton.IsEnabled = false;
        SaveProjectAsButton.IsEnabled = false;
        AddFilesButton.IsEnabled = false;
        AddFolderButton.IsEnabled = false;
        DiscoverButton.IsEnabled = false;
        RemoveArtifactButton.IsEnabled = false;
        VerifyButton.IsEnabled = false;
        IngestDatasetButton.IsEnabled = false;
        ContinuePipelineButton.IsEnabled = false;
        RefreshPipelineButton.IsEnabled = false;

        ResetPipelineUi();
        RefreshWorkflow();

        ProjectSummaryText.Text =
            reason;
    }

    private void RefreshRows()
    {
        _rows.Clear();

        if (_project is null ||
            string.IsNullOrWhiteSpace(
                _projectPath))
        {
            return;
        }

        foreach (var artifact in
                 _project.Artifacts)
        {
            string resolved;

            try
            {
                resolved =
                    DroneDashProjectStore.ResolveArtifactPath(
                        _projectPath,
                        artifact);
            }
            catch
            {
                resolved =
                    artifact.StoredPath;
            }

            _rows.Add(
                new ProjectArtifactRow
                {
                    Artifact = artifact,
                    ResolvedPath = resolved,
                    Integrity =
                        ProjectArtifactIntegrity.Unknown,
                    Detail =
                        artifact.Notes ??
                        "Ungeprüft"
                });
        }

        RemoveArtifactButton.IsEnabled =
            false;
    }

    private void RefreshWorkflow()
    {
        var snapshot =
            ProjectWorkflowAnalyzer.Analyze(
                _project);

        PlanningStageStateText.Text =
            snapshot.Planning.StateText;

        PlanningStageDetailText.Text =
            snapshot.Planning.Detail;

        DatasetStageStateText.Text =
            snapshot.Dataset.StateText;

        DatasetStageDetailText.Text =
            snapshot.Dataset.Detail;

        ProcessingStageStateText.Text =
            snapshot.Processing.StateText;

        ProcessingStageDetailText.Text =
            snapshot.Processing.Detail;

        AnalysisStageStateText.Text =
            snapshot.Analysis.StateText;

        AnalysisStageDetailText.Text =
            snapshot.Analysis.Detail;

        PlanningStageButton.IsEnabled =
            _project is not null;

        DatasetStageButton.IsEnabled =
            _project is not null;

        ProcessingStageButton.IsEnabled =
            _project is not null;

        AnalysisStageButton.IsEnabled =
            _project is not null;

        var dashboard =
            ProjectDashboardAnalyzer.Analyze(
                _projectPath,
                _project);

        RtkQaStateText.Text =
            dashboard.Rtk.StateText;

        RtkQaDetailText.Text =
            dashboard.Rtk.Detail;

        DatasetQaStateText.Text =
            dashboard.Dataset.StateText;

        DatasetQaDetailText.Text =
            dashboard.Dataset.Detail;

        ProcessingQaStateText.Text =
            dashboard.Processing.StateText;

        ProcessingQaDetailText.Text =
            dashboard.Processing.Detail;

        ResultsQaStateText.Text =
            dashboard.Results.StateText;

        ResultsQaDetailText.Text =
            dashboard.Results.Detail;
    }

    private void WorkflowStage_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded() ||
            sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        var stage =
            button.Tag?.ToString() ??
            "";

        var target =
            stage.Equals(
                "Planning",
                StringComparison.OrdinalIgnoreCase)
                ? ProjectNavigationTarget.Planning
                : ResolveModuleTarget();

        DroneDashProjectSession.RequestNavigation(
            target,
            reason:
                $"Projektworkflow · {stage}");
    }

    private ProjectNavigationTarget ResolveModuleTarget()
    {
        if (_pipelineState is not null)
        {
            var pipelineTarget =
                ProjectPipelineStore.ToNavigationTarget(
                    _pipelineState.Module);

            if (pipelineTarget.HasValue)
                return pipelineTarget.Value;
        }

        if (_project is null)
            return ProjectNavigationTarget.Photogrammetry;

        var kinds =
            _project.Artifacts
                .Select(item =>
                    item.Kind)
                .ToHashSet();

        if (kinds.Contains(
                ProjectArtifactKind.SmartFarmingDataset) ||
            kinds.Contains(
                ProjectArtifactKind.SmartFarmingFieldProducts) ||
            kinds.Contains(
                ProjectArtifactKind.VegetationRaster) ||
            kinds.Contains(
                ProjectArtifactKind.LocalProcessingPlan) ||
            kinds.Contains(
                ProjectArtifactKind.NodeOdmResultArchive))
        {
            return ProjectNavigationTarget.SmartFarming;
        }

        if (kinds.Contains(
                ProjectArtifactKind.PvAnalysis) ||
            kinds.Contains(
                ProjectArtifactKind.ThermalImage))
        {
            return ProjectNavigationTarget.PvAnalysis;
        }

        if (kinds.Contains(
                ProjectArtifactKind.PhotogrammetryManifest))
        {
            return ProjectNavigationTarget.Photogrammetry;
        }

        var flightPlan =
            DroneDashProjectSession.TryResolveLatestArtifactPath(
                ProjectArtifactKind.FlightPlan);

        if (!string.IsNullOrWhiteSpace(
                flightPlan))
        {
            try
            {
                var plan =
                    FlightPlanProjectStore.Load(
                        flightPlan);

                return plan.Settings.Aircraft switch
                {
                    DjiAircraftProfile.M3M =>
                        ProjectNavigationTarget.SmartFarming,
                    DjiAircraftProfile.M3T =>
                        ProjectNavigationTarget.PvAnalysis,
                    _ =>
                        ProjectNavigationTarget.Photogrammetry
                };
            }
            catch
            {
            }
        }

        return ProjectNavigationTarget.Photogrammetry;
    }
}
