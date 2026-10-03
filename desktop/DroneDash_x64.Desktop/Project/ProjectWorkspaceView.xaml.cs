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

    private void NewProject_Click(
        object sender,
        RoutedEventArgs e)
    {
        using var dialog =
            new WinForms.SaveFileDialog
            {
                Title = "Neues DroneDash-Projekt",
                Filter = "DroneDash Projekt (*.ddproj)|*.ddproj|JSON (*.json)|*.json",
                AddExtension = true,
                DefaultExt = "ddproj",
                FileName = "DroneDash-Project.ddproj",
                OverwritePrompt = true
            };

        if (dialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            var path =
                Path.GetFullPath(
                    dialog.FileName);

            var name =
                Path.GetFileNameWithoutExtension(
                    path);

            var project =
                DroneDashProjectStore.Create(
                    name);

            DroneDashProjectStore.Save(
                path,
                project);

            var loaded =
                DroneDashProjectStore.Load(
                    path);

            DroneDashProjectSession.Activate(
                path,
                loaded,
                "Neues DroneDash-Projekt angelegt.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt anlegen",
                ex);
        }
    }

    private void OpenProject_Click(
        object sender,
        RoutedEventArgs e)
    {
        using var dialog =
            new WinForms.OpenFileDialog
            {
                Title = "DroneDash-Projekt öffnen",
                Filter = "DroneDash Projekt (*.ddproj)|*.ddproj|JSON (*.json)|*.json|Alle Dateien (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

        if (dialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            var path =
                Path.GetFullPath(
                    dialog.FileName);

            var project =
                DroneDashProjectStore.Load(
                    path);

            DroneDashProjectSession.Activate(
                path,
                project,
                "DroneDash-Projekt geöffnet.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt öffnen",
                ex);
        }
    }

    private async void SaveProject_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        try
        {
            await DroneDashProjectSession.SaveMetadataAsync(
                ProjectNameBox.Text,
                ProjectDescriptionBox.Text);
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt speichern",
                ex);
        }
    }

    private async void SaveProjectAs_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        using var dialog =
            new WinForms.SaveFileDialog
            {
                Title = "DroneDash-Projekt speichern unter",
                Filter = "DroneDash Projekt (*.ddproj)|*.ddproj|JSON (*.json)|*.json",
                AddExtension = true,
                DefaultExt = "ddproj",
                FileName =
                    Path.GetFileName(
                        _projectPath),
                OverwritePrompt = true
            };

        if (dialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            await DroneDashProjectSession.SaveAsAsync(
                dialog.FileName!,
                ProjectNameBox.Text,
                ProjectDescriptionBox.Text);
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt speichern unter",
                ex);
        }
    }

    private async void AddFiles_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        using var dialog =
            new WinForms.OpenFileDialog
            {
                Title = "DroneDash-Projektartefakte hinzufügen",
                Filter = "Alle unterstützten Dateien (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true
            };

        if (dialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        await AddFilesAsync(
            dialog.FileNames);
    }

    private async void AddFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        using var dialog =
            new WinForms.FolderBrowserDialog
            {
                Description = "Quell-/Datenordner zum DroneDash-Projekt hinzufügen",
                ShowNewFolderButton = false
            };

        if (dialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            if (DroneDashProjectSession.ContainsResolvedPath(
                    dialog.SelectedPath))
            {
                ProjectSummaryText.Text =
                    BuildSummary(
                        "Der Ordner ist bereits im Projekt referenziert.");

                return;
            }

            await DroneDashProjectSession.RegisterDirectoryAsync(
                dialog.SelectedPath);

            ProjectSummaryText.Text =
                BuildSummary(
                    "Datenordner hinzugefügt und Projekt gespeichert.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Datenordner hinzufügen",
                ex);
        }
    }

    private async void Discover_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        var projectDirectory =
            Path.GetDirectoryName(
                _projectPath!);

        if (string.IsNullOrWhiteSpace(
                projectDirectory))
        {
            return;
        }

        try
        {
            var discovered =
                await Task.Run(() =>
                    ProjectArtifactService.DiscoverKnownArtifacts(
                        projectDirectory));

            var pending =
                discovered
                    .Where(path =>
                        !DroneDashProjectSession.ContainsResolvedPath(
                            path))
                    .ToArray();

            if (pending.Length == 0)
            {
                ProjectSummaryText.Text =
                    BuildSummary(
                        "Keine neuen bekannten DroneDash-Artefakte im Projektordner gefunden.");

                return;
            }

            await AddFilesAsync(
                pending);

            ProjectSummaryText.Text =
                BuildSummary(
                    $"{pending.Length} bekannte Artefakte aus dem Projektordner registriert.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projektordner scannen",
                ex);
        }
    }

    private async void IngestDataset_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        using var dialog =
            new WinForms.FolderBrowserDialog
            {
                Description = "DJI-Datensatz für den Projekt-Pipeline-Ingest auswählen",
                ShowNewFolderButton = false,
                SelectedPath =
                    ProjectWorkspaceLayout.GetProjectDirectory(
                        _projectPath!)
            };

        if (dialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        IngestDatasetButton.IsEnabled = false;
        ContinuePipelineButton.IsEnabled = false;
        ProjectProgress.IsIndeterminate = true;
        PipelineStateText.Text =
            "Datensatz wird strukturell erkannt …";

        try
        {
            var flightPlan =
                DroneDashProjectSession.TryResolveLatestArtifactPath(
                    ProjectArtifactKind.FlightPlan);

            var state =
                await Task.Run(() =>
                    ProjectPipelineStore.Start(
                        _projectPath!,
                        dialog.SelectedPath,
                        flightPlan));

            _pipelineState =
                state;

            await DroneDashProjectSession.RegisterDirectoryAsync(
                dialog.SelectedPath,
                ProjectArtifactKind.SourceDataFolder);

            _project =
                DroneDashProjectSession.CurrentProject ??
                _project;

            RefreshPipelineUi(
                refreshStage: true);

            var target =
                ProjectPipelineStore.ToNavigationTarget(
                    state.Module);

            if (target.HasValue)
            {
                DroneDashProjectSession.RequestNavigation(
                    target.Value,
                    ProjectPipelineStore.ResolveFlightPlan(
                        _projectPath!,
                        _pipelineState!),
                    $"Dataset-Ingest · {state.Module}",
                    ProjectPipelineStore.ResolveSourceFolder(
                        _projectPath!,
                        _pipelineState!));
            }
            else
            {
                System.Windows.MessageBox.Show(
                    state.Probe.Detail,
                    "Dataset-Ingest",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowError(
                "Dataset-Ingest",
                ex);
        }
        finally
        {
            ProjectProgress.IsIndeterminate = false;
            IngestDatasetButton.IsEnabled =
                _project is not null;
            ContinuePipelineButton.IsEnabled =
                _pipelineState is not null;
        }
    }

    private void ContinuePipeline_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded() ||
            _pipelineState is null)
        {
            return;
        }

        var target =
            ProjectPipelineStore.ToNavigationTarget(
                _pipelineState.Module);

        if (!target.HasValue)
        {
            System.Windows.MessageBox.Show(
                _pipelineState.Probe.Detail,
                "Projekt-Pipeline",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            DroneDashProjectSession.RequestNavigation(
                target.Value,
                ProjectPipelineStore.ResolveFlightPlan(
                    _projectPath!,
                    _pipelineState),
                $"Pipeline fortsetzen · {_pipelineState.Stage} · {_pipelineState.Module}",
                ProjectPipelineStore.ResolveSourceFolder(
                    _projectPath!,
                    _pipelineState));
        }
        catch (Exception ex)
        {
            ShowError(
                "Pipeline fortsetzen",
                ex);
        }
    }

    private void RefreshPipeline_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        RefreshPipelineUi(
            refreshStage: true);

        ProjectSummaryText.Text =
            BuildSummary(
                "Pipeline-Gates aktualisiert.");
    }

    private async void RemoveArtifact_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_project is null)
            return;

        var selected =
            ArtifactGrid.SelectedItems
                .Cast<ProjectArtifactRow>()
                .Select(row =>
                    row.Id)
                .ToArray();

        if (selected.Length == 0)
            return;

        try
        {
            await DroneDashProjectSession.RemoveArtifactsAsync(
                selected);

            ProjectSummaryText.Text =
                BuildSummary(
                    $"{selected.Length} Referenz(en) entfernt. Dateien auf Datenträger wurden nicht gelöscht.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projektartefakt entfernen",
                ex);
        }
    }

    private async void Verify_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        VerifyButton.IsEnabled = false;
        ProjectProgress.Value = 0;

        try
        {
            var artifacts =
                _project!.Artifacts.ToArray();

            var verifications =
                new List<ProjectArtifactVerification>(
                    artifacts.Length);

            for (var index = 0;
                 index < artifacts.Length;
                 index++)
            {
                var verification =
                    await ProjectArtifactService.VerifyAsync(
                        _projectPath!,
                        artifacts[index]);

                verifications.Add(
                    verification);

                ProjectProgress.Value =
                    artifacts.Length == 0
                        ? 100
                        : (index + 1) * 100d /
                          artifacts.Length;

                ProjectSummaryText.Text =
                    $"Integrität {index + 1}/{artifacts.Length}: {verification.Artifact.Label}";
            }

            ApplyVerification(
                verifications);

            ProjectSummaryText.Text =
                BuildSummary(
                    "Integritätsprüfung abgeschlossen.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projektintegrität",
                ex);
        }
        finally
        {
            VerifyButton.IsEnabled =
                _project is not null;
        }
    }

    private void ArtifactGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        RemoveArtifactButton.IsEnabled =
            ArtifactGrid.SelectedItems.Count > 0;
    }

    private async Task AddFilesAsync(
        IEnumerable<string> paths)
    {
        if (!EnsureProjectLoaded())
            return;

        var uniquePaths =
            paths
                .Select(
                    Path.GetFullPath)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Where(path =>
                    !DroneDashProjectSession.ContainsResolvedPath(
                        path))
                .ToArray();

        if (uniquePaths.Length == 0)
        {
            ProjectSummaryText.Text =
                BuildSummary(
                    "Keine neuen Dateien hinzuzufügen.");

            return;
        }

        SetBusy(true);

        try
        {
            ProjectSummaryText.Text =
                $"{uniquePaths.Length} Artefakt(e) werden registriert …";

            var count =
                await DroneDashProjectSession.RegisterFilesAsync(
                    uniquePaths);

            ProjectProgress.IsIndeterminate = false;
            ProjectProgress.Value = 100;

            ProjectSummaryText.Text =
                BuildSummary(
                    $"{count} Datei(en) registriert und Projekt gespeichert.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projektartefakte hinzufügen",
                ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

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

    private void RefreshPipelineUi(
        bool refreshStage)
    {
        if (_project is null ||
            string.IsNullOrWhiteSpace(
                _projectPath))
        {
            ResetPipelineUi();
            return;
        }

        try
        {
            var state =
                ProjectPipelineStore.Load(
                    _projectPath);

            if (state is null)
            {
                ResetPipelineUi();
                IngestDatasetButton.IsEnabled = true;
                RefreshPipelineButton.IsEnabled = true;
                return;
            }

            if (refreshStage)
            {
                state =
                    ProjectPipelineStore.RefreshStage(
                        _projectPath,
                        _project,
                        state);
            }

            _pipelineState =
                state;

            var source =
                ProjectPipelineStore.ResolveSourceFolder(
                    _projectPath,
                    state);

            PipelineStateText.Text =
                $"Job {state.JobId} · Ziel {state.Module} · Stufe {state.Stage} · " +
                $"aktualisiert {state.UpdatedAtUtc.LocalDateTime:G}";

            PipelineSourceText.Text =
                $"Datensatz: {source}";

            PipelineProbeText.Text =
                $"Erkennung: {state.Probe.Detail} · Bilder {state.Probe.SupportedImageFiles:N0}/{state.Probe.TotalFiles:N0} Dateien";

            var gates =
                ProjectPipelineGateEvaluator.Evaluate(
                    _projectPath,
                    _project,
                    state);

            RenderPipelineGate(
                gates.FlightPlan,
                PipelinePlanGateStateText,
                PipelinePlanGateDetailText);

            RenderPipelineGate(
                gates.Dataset,
                PipelineDatasetGateStateText,
                PipelineDatasetGateDetailText);

            RenderPipelineGate(
                gates.Rtk,
                PipelineRtkGateStateText,
                PipelineRtkGateDetailText);

            RenderPipelineGate(
                gates.Processing,
                PipelineProcessingGateStateText,
                PipelineProcessingGateDetailText);

            RenderPipelineGate(
                gates.Results,
                PipelineResultGateStateText,
                PipelineResultGateDetailText);

            ContinuePipelineButton.IsEnabled =
                ProjectPipelineStore.ToNavigationTarget(
                    state.Module)
                .HasValue;

            IngestDatasetButton.IsEnabled = true;
            RefreshPipelineButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _pipelineState = null;

            PipelineStateText.Text =
                $"Pipeline-Status konnte nicht gelesen werden: {ex.Message}";

            PipelineSourceText.Text =
                "Datensatz: —";

            PipelineProbeText.Text =
                "Erkennung: —";

            ContinuePipelineButton.IsEnabled =
                false;

            IngestDatasetButton.IsEnabled =
                true;

            RefreshPipelineButton.IsEnabled =
                true;
        }
    }

    private static void RenderPipelineGate(
        ProjectPipelineGate gate,
        TextBlock stateText,
        TextBlock detailText)
    {
        stateText.Text =
            gate.StateText;

        detailText.Text =
            gate.Detail;
    }

    private void ResetPipelineUi()
    {
        _pipelineState = null;

        PipelineStateText.Text =
            "Kein aktiver Pipeline-Job.";

        PipelineSourceText.Text =
            "Datensatz: —";

        PipelineProbeText.Text =
            "Erkennung: —";

        foreach (var pair in
                 new[]
                 {
                     (PipelinePlanGateStateText, PipelinePlanGateDetailText),
                     (PipelineDatasetGateStateText, PipelineDatasetGateDetailText),
                     (PipelineRtkGateStateText, PipelineRtkGateDetailText),
                     (PipelineProcessingGateStateText, PipelineProcessingGateDetailText),
                     (PipelineResultGateStateText, PipelineResultGateDetailText)
                 })
        {
            pair.Item1.Text =
                "OFFEN";

            pair.Item2.Text =
                "—";
        }
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

        return
            $"{prefix} · Artefakte {_project.Artifacts.Count:N0} · " +
            $"{workflow.SummaryText} · " +
            $"{ProjectDashboardAnalyzer.Analyze(_projectPath, _project).SummaryText} · " +
            $"{pipelineText} · " +
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
