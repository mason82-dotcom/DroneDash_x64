using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Project;

public partial class ProjectWorkspaceView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<ProjectArtifactRow> _rows = [];
    private DroneDashProject? _project;
    private string? _projectPath;

    public ProjectWorkspaceView()
    {
        InitializeComponent();
        ArtifactGrid.ItemsSource = _rows;
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.SaveFileDialog
        {
            Title = "Neues DroneDash-Projekt",
            Filter = "DroneDash Projekt (*.ddproj)|*.ddproj|JSON (*.json)|*.json",
            AddExtension = true,
            DefaultExt = "ddproj",
            FileName = "DroneDash-Project.ddproj",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var name =
                Path.GetFileNameWithoutExtension(dialog.FileName);

            _project =
                DroneDashProjectStore.Create(name);

            _projectPath =
                Path.GetFullPath(dialog.FileName);

            DroneDashProjectStore.Save(
                _projectPath,
                _project);

            _project =
                DroneDashProjectStore.Load(
                    _projectPath);

            LoadProjectIntoUi();
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt anlegen",
                ex);
        }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "DroneDash-Projekt öffnen",
            Filter = "DroneDash Projekt (*.ddproj)|*.ddproj|JSON (*.json)|*.json|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            _projectPath =
                Path.GetFullPath(
                    dialog.FileName);

            _project =
                DroneDashProjectStore.Load(
                    _projectPath);

            LoadProjectIntoUi();
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt öffnen",
                ex);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null ||
            string.IsNullOrWhiteSpace(_projectPath))
        {
            return;
        }

        try
        {
            _project =
                DroneDashProjectStore.WithMetadata(
                    _project,
                    ProjectNameBox.Text,
                    ProjectDescriptionBox.Text);

            DroneDashProjectStore.Save(
                _projectPath,
                _project);

            _project =
                DroneDashProjectStore.Load(
                    _projectPath);

            RefreshRows();
            ProjectSummaryText.Text =
                BuildSummary("Projekt gespeichert.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt speichern",
                ex);
        }
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null ||
            string.IsNullOrWhiteSpace(_projectPath))
        {
            return;
        }

        using var dialog = new WinForms.SaveFileDialog
        {
            Title = "DroneDash-Projekt speichern unter",
            Filter = "DroneDash Projekt (*.ddproj)|*.ddproj|JSON (*.json)|*.json",
            AddExtension = true,
            DefaultExt = "ddproj",
            FileName = Path.GetFileName(_projectPath),
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var newPath =
                Path.GetFullPath(
                    dialog.FileName);

            var metadata =
                DroneDashProjectStore.WithMetadata(
                    _project,
                    ProjectNameBox.Text,
                    ProjectDescriptionBox.Text);

            var rebased =
                DroneDashProjectStore.Rebase(
                    _projectPath,
                    newPath,
                    metadata);

            DroneDashProjectStore.Save(
                newPath,
                rebased);

            _projectPath =
                newPath;

            _project =
                DroneDashProjectStore.Load(
                    newPath);

            LoadProjectIntoUi();
        }
        catch (Exception ex)
        {
            ShowError(
                "Projekt speichern unter",
                ex);
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "DroneDash-Projektartefakte hinzufügen",
            Filter = "Alle unterstützten Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        await AddFilesAsync(
            dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Quell-/Datenordner zum DroneDash-Projekt hinzufügen",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var artifact =
                ProjectArtifactService.CreateDirectoryArtifact(
                    _projectPath!,
                    dialog.SelectedPath);

            if (ContainsResolvedPath(
                    dialog.SelectedPath))
            {
                ProjectSummaryText.Text =
                    BuildSummary(
                        "Der Ordner ist bereits im Projekt referenziert.");
                return;
            }

            _project =
                _project! with
                {
                    Artifacts =
                        _project.Artifacts
                            .Append(artifact)
                            .ToArray()
                };

            RefreshRows();
            ProjectSummaryText.Text =
                BuildSummary(
                    "Datenordner hinzugefügt. Projekt noch speichern.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Datenordner hinzufügen",
                ex);
        }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectLoaded())
            return;

        var projectDirectory =
            Path.GetDirectoryName(_projectPath!);

        if (string.IsNullOrWhiteSpace(projectDirectory))
            return;

        try
        {
            var discovered =
                await Task.Run(() =>
                    ProjectArtifactService.DiscoverKnownArtifacts(
                        projectDirectory));

            var pending =
                discovered
                    .Where(path =>
                        !ContainsResolvedPath(path))
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
                    $"{pending.Length} bekannte Artefakte aus dem Projektordner hinzugefügt.");
        }
        catch (Exception ex)
        {
            ShowError(
                "Projektordner scannen",
                ex);
        }
    }

    private void RemoveArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;

        var selected =
            ArtifactGrid.SelectedItems
                .Cast<ProjectArtifactRow>()
                .Select(row => row.Id)
                .ToHashSet();

        if (selected.Count == 0)
            return;

        _project =
            _project with
            {
                Artifacts =
                    _project.Artifacts
                        .Where(artifact =>
                            !selected.Contains(artifact.Id))
                        .ToArray()
            };

        RefreshRows();

        ProjectSummaryText.Text =
            BuildSummary(
                $"{selected.Count} Referenz(en) entfernt. Dateien auf Datenträger wurden nicht gelöscht.");
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
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

            for (var index = 0; index < artifacts.Length; index++)
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
                        : (index + 1) * 100d / artifacts.Length;

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
        if (_project is null ||
            string.IsNullOrWhiteSpace(_projectPath))
        {
            return;
        }

        var uniquePaths =
            paths
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path =>
                    !ContainsResolvedPath(path))
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
            var artifacts =
                _project.Artifacts.ToList();

            for (var index = 0;
                 index < uniquePaths.Length;
                 index++)
            {
                var path =
                    uniquePaths[index];

                ProjectSummaryText.Text =
                    $"Artefakt {index + 1}/{uniquePaths.Length}: {Path.GetFileName(path)}";

                var artifact =
                    await ProjectArtifactService.CreateFileArtifactAsync(
                        _projectPath,
                        path);

                artifacts.Add(
                    artifact);

                ProjectProgress.Value =
                    (index + 1) * 100d /
                    uniquePaths.Length;
            }

            _project =
                _project with
                {
                    Artifacts =
                        artifacts.ToArray()
                };

            RefreshRows();

            ProjectSummaryText.Text =
                BuildSummary(
                    $"{uniquePaths.Length} Datei(en) hinzugefügt. Projekt noch speichern.");
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

    private void LoadProjectIntoUi()
    {
        if (_project is null ||
            string.IsNullOrWhiteSpace(_projectPath))
        {
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

        RefreshRows();

        ProjectSummaryText.Text =
            BuildSummary(
                "Projekt geladen.");
    }

    private void RefreshRows()
    {
        _rows.Clear();

        if (_project is null ||
            string.IsNullOrWhiteSpace(_projectPath))
        {
            return;
        }

        foreach (var artifact in _project.Artifacts)
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

        RemoveArtifactButton.IsEnabled = false;
    }

    private void ApplyVerification(
        IReadOnlyList<ProjectArtifactVerification> verifications)
    {
        var map =
            verifications.ToDictionary(
                item => item.Artifact.Id);

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

        return
            $"{prefix} · Artefakte {_project.Artifacts.Count:N0} · " +
            $"OK {ok:N0} · geändert {modified:N0} · fehlt {missing:N0} · Fehler {errors:N0}";
    }

    private bool ContainsResolvedPath(
        string path)
    {
        if (_project is null ||
            string.IsNullOrWhiteSpace(_projectPath))
        {
            return false;
        }

        var candidate =
            NormalizePath(
                path);

        return _project.Artifacts.Any(
            artifact =>
            {
                try
                {
                    var resolved =
                        DroneDashProjectStore.ResolveArtifactPath(
                            _projectPath,
                            artifact);

                    return NormalizePath(resolved)
                        .Equals(
                            candidate,
                            StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
    }

    private static string NormalizePath(
        string path) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));

    private bool EnsureProjectLoaded()
    {
        if (_project is not null &&
            !string.IsNullOrWhiteSpace(_projectPath))
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

    private void SetBusy(bool busy)
    {
        ProjectProgress.IsIndeterminate = busy;
        AddFilesButton.IsEnabled = !busy && _project is not null;
        AddFolderButton.IsEnabled = !busy && _project is not null;
        DiscoverButton.IsEnabled = !busy && _project is not null;
        VerifyButton.IsEnabled = !busy && _project is not null;

        if (!busy)
            ProjectProgress.IsIndeterminate = false;
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
