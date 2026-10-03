using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using DroneDash_x64.Desktop.SmartFarming.Odm;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.Planning;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.SmartFarming;

public partial class SmartFarmingView
{
    private async void CheckTools_Click(object sender, RoutedEventArgs e)
    {
        ToolchainSummaryText.Text = "Lokale Processing-Tools werden geprüft …";
        CreateLocalPlanButton.IsEnabled = false;

        try
        {
            _toolchain = await LocalImageToolchain.ProbeAsync();

            _toolStatuses.Clear();
            foreach (var tool in _toolchain.Tools)
                _toolStatuses.Add(tool);

            ToolchainSummaryText.Text = _toolchain.IsReady
                ? "Toolchain bereit: OpenCV-ECC → OTB-Radiometrie → GDAL-VRT → OTB NDVI/NDRE/GNDVI."
                : "Toolchain unvollständig. Fehlende Pfade installieren bzw. über DRONEDASH_GDAL_BIN, DRONEDASH_OTB_BIN oder DRONEDASH_PYTHON setzen.";

            CreateLocalPlanButton.IsEnabled =
                _toolchain.IsReady &&
                CaptureGrid.SelectedItem is M3mCaptureGroup capture &&
                capture.IsQuicklookReady;
        }
        catch (Exception ex)
        {
            ToolchainSummaryText.Text = $"Toolchain-Prüfung fehlgeschlagen: {ex.Message}";
        }
    }

    private async void CreateLocalPlan_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureGrid.SelectedItem is not M3mCaptureGroup capture)
        {
            System.Windows.MessageBox.Show(
                "Zuerst eine M3M-Aufnahme auswählen.",
                "Lokales Processing",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!capture.IsQuicklookReady)
        {
            System.Windows.MessageBox.Show(
                "Die ausgewählte Aufnahme ist nicht Quicklook-bereit.",
                "Lokales Processing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _toolchain ??= await LocalImageToolchain.ProbeAsync();
        if (!_toolchain.IsReady)
        {
            System.Windows.MessageBox.Show(
                "GDAL, Orfeo ToolBox und Python/OpenCV müssen zuerst vollständig verfügbar sein.",
                "Lokales Processing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für lokalen Smart-Farming-Processing-Workspace auswählen",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.SmartFarmingProcessing) ??
                _sourceFolder ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var workspace = Path.Combine(dialog.SelectedPath, capture.CaptureKey);
            var plan = LocalProcessingPlanBuilder.Build(capture, workspace, _toolchain);
            var output = LocalProcessingPlanExporter.Export(plan);

            _currentLocalPlan = plan;
            RunLocalPlanButton.IsEnabled = true;
            LocalProcessingProgress.Value = 0;
            LocalProcessingStatusText.Text = "Processing-Plan bereit zur Ausführung.";

            var registered = false;
            string? registrationError = null;

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    registered = await DroneDashProjectSession.RegisterFileAsync(
                        output.JsonPath,
                        ProjectArtifactKind.LocalProcessingPlan);

                    await DroneDashProjectSession.RegisterDirectoryAsync(
                        workspace,
                        ProjectArtifactKind.ProcessingWorkspace);
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            PlanPreviewText.Text =
                string.Join(
                    Environment.NewLine,
                    plan.Steps.Select((step, index) =>
                        $"{index + 1:00}. [{step.Tool}] {step.Description}" +
                        Environment.NewLine +
                        $"    → {step.OutputPath ?? "kein Dateioutput"}")) +
                Environment.NewLine +
                Environment.NewLine +
                "Hinweise:" +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    plan.Warnings.Select(warning => "• " + warning));

            StatusText.Text =
                $"Lokaler Processing-Workspace erzeugt: {output.JsonPath} · {output.ScriptPath}" +
                (registered
                    ? " · im aktiven DroneDash-Projekt registriert"
                    : registrationError is not null
                        ? $" · Projektregistrierung fehlgeschlagen: {registrationError}"
                        : "");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Lokales Processing",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RunLocalPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLocalPlan is null)
            return;

        var confirmation = System.Windows.MessageBox.Show(
            "Die erzeugte Pipeline jetzt mit den lokal installierten Drittanbieter-Tools ausführen?\n\n" +
            "Die M3M-Quelldateien werden nur gelesen. Zwischenprodukte werden ausschließlich in den gewählten Workspace geschrieben.",
            "Smart Farming Processing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
            return;

        _localProcessingCts?.Dispose();
        _localProcessingCts = new CancellationTokenSource();

        RunLocalPlanButton.IsEnabled = false;
        CancelLocalPlanButton.IsEnabled = true;
        LocalRunLogText.Clear();
        LocalProcessingProgress.Value = 0;

        var progress = new Progress<LocalProcessingProgress>(value =>
        {
            LocalProcessingProgress.Value = value.Percent;
            LocalProcessingStatusText.Text =
                $"{value.CompletedSteps}/{value.TotalSteps} · {value.Message}";
        });

        try
        {
            var result = await LocalProcessingRunner.RunAsync(
                _currentLocalPlan,
                progress,
                line => Dispatcher.BeginInvoke(() =>
                {
                    LocalRunLogText.AppendText(line + Environment.NewLine);
                    LocalRunLogText.ScrollToEnd();
                }),
                _localProcessingCts.Token);

            LocalProcessingProgress.Value =
                result.Success ? 100 : LocalProcessingProgress.Value;

            LocalProcessingStatusText.Text = result.Success
                ? $"Pipeline erfolgreich abgeschlossen · Log: {result.LogPath}"
                : result.Canceled
                    ? $"Pipeline abgebrochen · Log: {result.LogPath}"
                    : $"Pipeline fehlgeschlagen bei {result.FailedStepId} · ExitCode {result.ExitCode} · Log: {result.LogPath}";
        }
        catch (Exception ex)
        {
            LocalProcessingStatusText.Text =
                $"Pipeline-Ausführung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            CancelLocalPlanButton.IsEnabled = false;
            RunLocalPlanButton.IsEnabled = _currentLocalPlan is not null;
        }
    }

    private void CancelLocalPlan_Click(object sender, RoutedEventArgs e)
    {
        _localProcessingCts?.Cancel();
        LocalProcessingStatusText.Text = "Abbruch angefordert …";
    }
}
