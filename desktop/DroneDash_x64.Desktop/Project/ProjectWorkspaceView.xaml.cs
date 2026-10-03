using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DroneDash_x64.Desktop.Planning;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Project;

public partial class ProjectWorkspaceView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<ProjectArtifactRow> _rows = [];
    private DroneDashProject? _project;
    private string? _projectPath;
    private ProjectPipelineState? _pipelineState;

    public ProjectWorkspaceView()
    {
        InitializeComponent();
        ArtifactGrid.ItemsSource = _rows;

        Loaded += ProjectWorkspaceView_Loaded;
        Unloaded += ProjectWorkspaceView_Unloaded;
    }

    private void ProjectWorkspaceView_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        DroneDashProjectSession.Changed -=
            ProjectSession_Changed;

        DroneDashProjectSession.Changed +=
            ProjectSession_Changed;

        ProjectProcessingCoordinator.Changed -=
            ProjectProcessing_Changed;

        ProjectProcessingCoordinator.Changed +=
            ProjectProcessing_Changed;

        ApplySession(
            DroneDashProjectSession.CurrentProjectPath,
            DroneDashProjectSession.CurrentProject,
            DroneDashProjectSession.IsOpen
                ? "Aktives Projekt synchronisiert."
                : "Kein Projekt geladen.");
    }

    private void ProjectWorkspaceView_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        DroneDashProjectSession.Changed -=
            ProjectSession_Changed;

        ProjectProcessingCoordinator.Changed -=
            ProjectProcessing_Changed;
    }

    private void ProjectProcessing_Changed(
        object? sender,
        ProjectProcessingChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(
                _projectPath) ||
            !Path.GetFullPath(
                    e.ProjectPath)
                .Equals(
                    Path.GetFullPath(
                        _projectPath),
                    StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() =>
                RenderProcessingJob(
                    e.Job));

            return;
        }

        RenderProcessingJob(
            e.Job);
    }

    private void ProjectSession_Changed(
        object? sender,
        ProjectSessionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() =>
                ApplySession(
                    e.ProjectPath,
                    e.Project,
                    e.Reason));

            return;
        }

        ApplySession(
            e.ProjectPath,
            e.Project,
            e.Reason);
    }

    private void ApplyVerification(
        IReadOnlyList<ProjectArtifactVerification> verifications)
    {
        var map =
            verifications.ToDictionary(
                item =>
                    item.Artifact.Id);

        foreach (var row in _rows)
        {
            if (!map.TryGetValue(
                    row.Id,
                    out var verification))
            {
                continue;
            }

            row.Integrity =
                verification.Integrity;

            row.Detail =
                verification.Detail;
        }

        ArtifactGrid.Items.Refresh();
    }

    private string BuildSummary(
        string prefix)
    {
        if (_project is null)
            return prefix;

        var ok =
            _rows.Count(row =>
                row.Integrity ==
                ProjectArtifactIntegrity.Ok);

        var modified =
            _rows.Count(row =>
                row.Integrity ==
                ProjectArtifactIntegrity.Modified);

        var missing =
            _rows.Count(row =>
                row.Integrity ==
                ProjectArtifactIntegrity.Missing);

        var errors =
            _rows.Count(row =>
                row.Integrity ==
                ProjectArtifactIntegrity.Error);

        var workflow =
            ProjectWorkflowAnalyzer.Analyze(
                _project);

        var pipelineText =
            _pipelineState is null
                ? "Pipeline offen"
                : $"Pipeline {_pipelineState.Module}/{_pipelineState.Stage}";

        var processingJob =
            string.IsNullOrWhiteSpace(
                _projectPath)
                ? null
                : ProjectProcessingCoordinator.GetCurrent(
                    _projectPath);

        var processingText =
            processingJob is null
                ? "Worker offen"
                : $"Worker {processingJob.Worker}/{processingJob.StatusText}";

        return
            $"{prefix} · Artefakte {_project.Artifacts.Count:N0} · " +
            $"{workflow.SummaryText} · " +
            $"{ProjectDashboardAnalyzer.Analyze(_projectPath, _project).SummaryText} · " +
            $"{pipelineText} · {processingText} · " +
            $"OK {ok:N0} · geändert {modified:N0} · fehlt {missing:N0} · Fehler {errors:N0}";
    }

    private bool EnsureProjectLoaded()
    {
        if (_project is not null &&
            !string.IsNullOrWhiteSpace(
                _projectPath) &&
            DroneDashProjectSession.IsOpen)
        {
            return true;
        }

        System.Windows.MessageBox.Show(
            "Zuerst ein DroneDash-Projekt neu anlegen oder öffnen.",
            "DroneDash Projekt",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        return false;
    }

    private void SetBusy(
        bool busy)
    {
        ProjectProgress.IsIndeterminate =
            busy;

        AddFilesButton.IsEnabled =
            !busy &&
            _project is not null;

        AddFolderButton.IsEnabled =
            !busy &&
            _project is not null;

        DiscoverButton.IsEnabled =
            !busy &&
            _project is not null;

        VerifyButton.IsEnabled =
            !busy &&
            _project is not null;

        IngestDatasetButton.IsEnabled =
            !busy &&
            _project is not null;

        ContinuePipelineButton.IsEnabled =
            !busy &&
            _pipelineState is not null &&
            ProjectPipelineStore.ToNavigationTarget(
                _pipelineState.Module)
            .HasValue;

        RefreshPipelineButton.IsEnabled =
            !busy &&
            _project is not null;

        if (!busy)
        {
            ProjectProgress.IsIndeterminate =
                false;
        }
    }

    private void ShowError(
        string title,
        Exception ex)
    {
        ProjectSummaryText.Text =
            ex.Message;

        System.Windows.MessageBox.Show(
            ex.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
