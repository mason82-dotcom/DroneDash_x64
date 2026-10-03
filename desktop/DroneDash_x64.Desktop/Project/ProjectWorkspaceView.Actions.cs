using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DroneDash_x64.Desktop.Planning;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Project;

public partial class ProjectWorkspaceView
{
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
}
