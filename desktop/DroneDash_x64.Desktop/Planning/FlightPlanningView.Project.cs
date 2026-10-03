using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using DroneDash_x64.Desktop.Project;
using WinForms = System.Windows.Forms;


namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView
{
    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new WinForms.SaveFileDialog
            {
                Title = "DroneDash Flugplan speichern",
                Filter = "DroneDash Flugplan (*.ddplan)|*.ddplan|JSON (*.json)|*.json",
                DefaultExt = "ddplan",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(_currentProjectPath)
                    ? SafeFileName(PlanNameBox.Text) + ".ddplan"
                    : Path.GetFileName(_currentProjectPath)
            };

            if (!string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                dialog.InitialDirectory =
                    Path.GetDirectoryName(
                        _currentProjectPath);
            }
            else if (ProjectWorkspaceLayout.TryGetActiveFolder(
                         ProjectWorkspaceFolder.Planning) is string planningFolder)
            {
                dialog.InitialDirectory =
                    planningFolder;
            }

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            FlightPlanProjectStore.Save(dialog.FileName, ReadSettings(), _geometry);
            _currentProjectPath = dialog.FileName;
            ProjectInfoText.Text = $"Gespeichert: {_currentProjectPath}";

            var registered = false;
            string? registrationError = null;

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    registered = await DroneDashProjectSession.RegisterFileAsync(
                        _currentProjectPath,
                        ProjectArtifactKind.FlightPlan);
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            PlanningStatusText.Text =
                "DroneDash-Flugplan gespeichert." +
                (registered
                    ? " Im aktiven DroneDash-Projekt registriert."
                    : registrationError is not null
                        ? $" Projektregistrierung fehlgeschlagen: {registrationError}"
                        : "");
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(
                ex.Message,
                "Projekt speichern",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new WinForms.OpenFileDialog
            {
                Title = "DroneDash Flugplan laden",
                Filter = "DroneDash Flugplan (*.ddplan;*.json)|*.ddplan;*.json|Alle Dateien (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            var project = FlightPlanProjectStore.Load(dialog.FileName);
            ApplySettings(project.Settings);

            _geometry.Clear();
            _geometry.AddRange(project.Geometry);
            _currentProjectPath = dialog.FileName;
            ProjectInfoText.Text =
                $"Geladen: {_currentProjectPath} · Schema {project.SchemaVersion} · " +
                $"{project.SavedAtUtc.LocalDateTime:G}";

            _plan = null;
            try
            {
                _plan = PhotogrammetryPlanner.Generate(_geometry, project.Settings);
                RenderPlanSummary(_plan);
                PlanningStatusText.Text = "Projekt geladen und Route neu berechnet.";
            }
            catch (Exception planError)
            {
                PlanningStatsText.Text = "Projekt geladen; Route muss neu berechnet werden.";
                PlanningStatusText.Text =
                    $"Projekt geladen, aber die Route konnte nicht automatisch berechnet werden: {planError.Message}";
            }

            UpdateGeometryInfo();
            RenderMap(fitBounds: true);
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(
                ex.Message,
                "Projekt laden",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    private async void HandoffFlightPlan_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            !Enum.TryParse<ProjectNavigationTarget>(
                button.Tag?.ToString(),
                ignoreCase: true,
                out var target))
        {
            return;
        }

        try
        {
            var path =
                await SaveForHandoffAsync();

            if (string.IsNullOrWhiteSpace(path))
                return;

            DroneDashProjectSession.RequestNavigation(
                target,
                path,
                $"Flugplan direkt an {target} übergeben");

            PlanningStatusText.Text =
                $"Flugplan gespeichert und an {target} übergeben: {path}";
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text =
                $"Flugplan-Übergabe fehlgeschlagen: {ex.Message}";

            System.Windows.MessageBox.Show(
                ex.Message,
                "Flugplan übergeben",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<string?> SaveForHandoffAsync()
    {
        var path =
            _currentProjectPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            var projectFolder =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.Planning);

            if (!string.IsNullOrWhiteSpace(projectFolder))
            {
                var baseName =
                    SafeFileName(
                        PlanNameBox.Text);

                path =
                    Path.Combine(
                        projectFolder,
                        baseName + ".ddplan");

                if (File.Exists(path))
                {
                    path =
                        Path.Combine(
                            projectFolder,
                            $"{baseName}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.ddplan");
                }
            }
            else
            {
                using var dialog =
                    new WinForms.SaveFileDialog
                    {
                        Title = "Flugplan für Übergabe speichern",
                        Filter = "DroneDash Flugplan (*.ddplan)|*.ddplan|JSON (*.json)|*.json",
                        DefaultExt = "ddplan",
                        AddExtension = true,
                        FileName =
                            SafeFileName(
                                PlanNameBox.Text) +
                            ".ddplan"
                    };

                if (dialog.ShowDialog() !=
                    WinForms.DialogResult.OK)
                {
                    return null;
                }

                path =
                    dialog.FileName;
            }
        }

        FlightPlanProjectStore.Save(
            path,
            ReadSettings(),
            _geometry);

        _currentProjectPath =
            Path.GetFullPath(path);

        ProjectInfoText.Text =
            $"Gespeichert: {_currentProjectPath}";

        if (DroneDashProjectSession.IsOpen)
        {
            await DroneDashProjectSession.RegisterFileAsync(
                _currentProjectPath,
                ProjectArtifactKind.FlightPlan);
        }

        return _currentProjectPath;
    }


    private static string SafeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return string.IsNullOrWhiteSpace(value)
            ? "DroneDash_Mapping"
            : value;
    }
}
