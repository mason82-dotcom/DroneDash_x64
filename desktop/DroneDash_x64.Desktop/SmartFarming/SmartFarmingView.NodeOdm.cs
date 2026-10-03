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
    private async void CheckNodeOdm_Click(object sender, RoutedEventArgs e)
    {
        StartNodeOdmTaskButton.IsEnabled = false;
        NodeOdmStatusText.Text = "NodeODM wird geprüft …";

        try
        {
            using var client = CreateNodeOdmClient();
            _nodeOdmServer = await client.ProbeAsync();

            NodeOdmStatusText.Text = _nodeOdmServer.Available
                ? $"NodeODM {_nodeOdmServer.ApiVersion ?? "?"} · Engine {_nodeOdmServer.Engine ?? "?"} {_nodeOdmServer.EngineVersion ?? "?"} · {_nodeOdmServer.M3mSupportText} · Queue {_nodeOdmServer.TaskQueueCount?.ToString() ?? "—"} · CPU {_nodeOdmServer.CpuCores?.ToString() ?? "—"}"
                : $"NodeODM nicht erreichbar: {_nodeOdmServer.Detail}";

            StartNodeOdmTaskButton.IsEnabled =
                _nodeOdmServer.SupportsMavic3M &&
                _dataset?.Captures.Any(capture => capture.IsComplete) == true;
        }
        catch (Exception ex)
        {
            NodeOdmStatusText.Text =
                $"NodeODM-Prüfung fehlgeschlagen: {ex.Message}";
        }
    }

    private async void StartNodeOdmTask_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null)
        {
            System.Windows.MessageBox.Show(
                "Zuerst den M3M-Datensatz prüfen.",
                "NodeODM",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var files = NodeOdmClient.GetM3mInputFiles(_dataset);
        if (files.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "Keine vollständigen M3M-Aufnahmen für ODM vorhanden.",
                "NodeODM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var calibration =
            (NodeOdmCalibrationCombo.SelectedItem as ComboBoxItem)?
                .Tag?.ToString() ?? "camera+sun";

        var confirmation = System.Windows.MessageBox.Show(
            $"{files.Count} Multispektral-TIFFs an {NodeOdmEndpointBox.Text.Trim()} senden und einen ODM-Task starten?\n\n" +
            $"Radiometrische Kalibrierung: {calibration}\nPrimary band: NIR\n\n" +
            "camera+sun ist laut ODM-Dokumentation experimentell.",
            "M3M NodeODM Task",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
            return;

        NodeOdmLogText.Clear();
        NodeOdmProgress.Value = 0;
        StartNodeOdmTaskButton.IsEnabled = false;
        DownloadNodeOdmButton.IsEnabled = false;

        ProjectProcessingCoordinator.BeginActive(
            ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
            "M3M-Datensatz wird an NodeODM übertragen.",
            "upload");

        var uploadProgress = new Progress<NodeOdmUploadProgress>(value =>
        {
            NodeOdmProgress.Value = value.Percent;
            NodeOdmStatusText.Text =
                $"Upload {value.UploadedFiles}/{value.TotalFiles}: {value.CurrentFile}";

            ProjectProcessingCoordinator.ReportActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                Math.Clamp(
                    value.Percent * 0.2,
                    0d,
                    20d),
                $"Upload {value.UploadedFiles}/{value.TotalFiles}: {value.CurrentFile}",
                "upload");

            AppendNodeOdmLog(
                $"Upload {value.UploadedFiles}/{value.TotalFiles}: {value.CurrentFile}");
        });

        try
        {
            using var client = CreateNodeOdmClient();

            _nodeOdmTaskUuid = await client.CreateM3mTaskAsync(
                _dataset,
                $"DroneDash M3M {DateTimeOffset.Now:yyyy-MM-dd HH-mm-ss}",
                calibration,
                uploadProgress);

            AppendNodeOdmLog(
                $"NodeODM Task gestartet: {_nodeOdmTaskUuid}");

            ProjectProcessingCoordinator.ReportActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                20d,
                $"NodeODM Task {_nodeOdmTaskUuid} gestartet.",
                "nodeodm");

            RefreshNodeOdmTaskButton.IsEnabled = true;
            CancelNodeOdmTaskButton.IsEnabled = true;

            _nodeOdmMonitorCts?.Cancel();
            _nodeOdmMonitorCts?.Dispose();
            _nodeOdmMonitorCts = new CancellationTokenSource();

            await MonitorNodeOdmTaskAsync(
                _nodeOdmMonitorCts.Token);
        }
        catch (OperationCanceledException)
        {
            ProjectProcessingCoordinator.CancelActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                "NodeODM-Upload/Monitoring lokal abgebrochen.");

            AppendNodeOdmLog("NodeODM-Upload/Monitoring lokal abgebrochen.");
        }
        catch (Exception ex)
        {
            ProjectProcessingCoordinator.FailActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                ex.Message);

            NodeOdmStatusText.Text =
                $"NodeODM-Task fehlgeschlagen: {ex.Message}";
            AppendNodeOdmLog(NodeOdmStatusText.Text);
        }
        finally
        {
            StartNodeOdmTaskButton.IsEnabled =
                _nodeOdmServer?.SupportsMavic3M == true &&
                _dataset?.Captures.Any(capture => capture.IsComplete) == true;
        }
    }

    private async Task MonitorNodeOdmTaskAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_nodeOdmTaskUuid))
            return;

        string? lastOutput = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = CreateNodeOdmClient();

            var info = await client.GetTaskInfoAsync(
                _nodeOdmTaskUuid,
                cancellationToken);

            NodeOdmProgress.Value =
                Math.Clamp(info.Progress, 0, 100);

            NodeOdmStatusText.Text =
                $"Task {info.Uuid} · {info.StatusText} · {info.Progress:F1}% · Bilder {info.ImagesCount} · Laufzeit {TimeSpan.FromMilliseconds(info.ProcessingTimeMilliseconds):g}";

            ProjectProcessingCoordinator.ReportActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                Math.Clamp(
                    20d + info.Progress * 0.7d,
                    20d,
                    90d),
                $"Task {info.Uuid} · {info.StatusText} · {info.Progress:F1}%",
                "nodeodm");

            try
            {
                var output = await client.GetTaskOutputAsync(
                    info.Uuid,
                    0,
                    cancellationToken);

                if (!string.Equals(
                        output,
                        lastOutput,
                        StringComparison.Ordinal))
                {
                    NodeOdmLogText.Text = output;
                    NodeOdmLogText.ScrollToEnd();
                    lastOutput = output;
                }
            }
            catch
            {
            }

            CancelNodeOdmTaskButton.IsEnabled =
                !info.IsTerminal;
            DownloadNodeOdmButton.IsEnabled =
                info.IsCompleted;

            if (info.IsTerminal)
            {
                if (info.IsCompleted)
                {
                    ProjectProcessingCoordinator.AwaitOutputActive(
                        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                        "NodeODM-Processing abgeschlossen; all.zip herunterladen und registrieren.");
                }
                else if (info.StatusCode == 50)
                {
                    ProjectProcessingCoordinator.CancelActive(
                        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                        $"NodeODM Task {info.Uuid} wurde abgebrochen.");
                }
                else
                {
                    ProjectProcessingCoordinator.FailActive(
                        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                        $"NodeODM Task {info.Uuid} endete mit {info.StatusText}.");
                }

                return;
            }

            await Task.Delay(
                TimeSpan.FromSeconds(3),
                cancellationToken);
        }
    }

    private async void RefreshNodeOdmTask_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nodeOdmTaskUuid))
            return;

        try
        {
            using var client = CreateNodeOdmClient();
            var info = await client.GetTaskInfoAsync(_nodeOdmTaskUuid);

            NodeOdmProgress.Value =
                Math.Clamp(info.Progress, 0, 100);

            NodeOdmStatusText.Text =
                $"Task {info.Uuid} · {info.StatusText} · {info.Progress:F1}% · Bilder {info.ImagesCount}";

            CancelNodeOdmTaskButton.IsEnabled = !info.IsTerminal;
            DownloadNodeOdmButton.IsEnabled = info.IsCompleted;

            ProjectProcessingCoordinator.ReportActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                Math.Clamp(
                    20d + info.Progress * 0.7d,
                    20d,
                    90d),
                $"Task {info.Uuid} · {info.StatusText} · {info.Progress:F1}%",
                "nodeodm");

            if (info.IsCompleted)
            {
                ProjectProcessingCoordinator.AwaitOutputActive(
                    ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                    "NodeODM-Processing abgeschlossen; all.zip herunterladen und registrieren.");
            }
            else if (info.StatusCode == 50)
            {
                ProjectProcessingCoordinator.CancelActive(
                    ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                    $"NodeODM Task {info.Uuid} wurde abgebrochen.");
            }
            else if (info.StatusCode == 30)
            {
                ProjectProcessingCoordinator.FailActive(
                    ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                    $"NodeODM Task {info.Uuid} ist fehlgeschlagen.");
            }
        }
        catch (Exception ex)
        {
            NodeOdmStatusText.Text =
                $"Statusabfrage fehlgeschlagen: {ex.Message}";
        }
    }

    private async void CancelNodeOdmTask_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nodeOdmTaskUuid))
            return;

        try
        {
            using var client = CreateNodeOdmClient();
            await client.CancelTaskAsync(_nodeOdmTaskUuid);
            _nodeOdmMonitorCts?.Cancel();

            NodeOdmStatusText.Text =
                $"Abbruch für Task {_nodeOdmTaskUuid} angefordert.";
            CancelNodeOdmTaskButton.IsEnabled = false;

            ProjectProcessingCoordinator.CancelActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                $"Abbruch für NodeODM Task {_nodeOdmTaskUuid} angefordert.");
        }
        catch (Exception ex)
        {
            NodeOdmStatusText.Text =
                $"NodeODM-Abbruch fehlgeschlagen: {ex.Message}";
        }
    }

    private async void DownloadNodeOdm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nodeOdmTaskUuid))
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für NodeODM all.zip auswählen",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.SmartFarmingProcessing) ??
                _sourceFolder ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var progress = new Progress<double>(value =>
        {
            NodeOdmProgress.Value = Math.Clamp(value, 0, 100);
            NodeOdmStatusText.Text =
                $"NodeODM-Ergebnis wird heruntergeladen: {value:F1}%";

            ProjectProcessingCoordinator.ReportActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                Math.Clamp(
                    90d + value * 0.1d,
                    90d,
                    100d),
                $"NodeODM-Ergebnis wird heruntergeladen: {value:F1}%",
                "download");
        });

        try
        {
            using var client = CreateNodeOdmClient();
            var path = await client.DownloadAllAsync(
                _nodeOdmTaskUuid,
                dialog.SelectedPath,
                progress);

            _lastNodeOdmZipPath = path;

            var registered = false;
            string? registrationError = null;

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    registered = await DroneDashProjectSession.RegisterFileAsync(
                        path,
                        ProjectArtifactKind.NodeOdmResultArchive);

                    ProjectProcessingCoordinator.RecordOutputActive(
                        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                        path,
                        ProjectArtifactKind.NodeOdmResultArchive);

                    ProjectProcessingCoordinator.CompleteActive(
                        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                        "NodeODM-Ergebnisarchiv heruntergeladen und im Projekt registriert.");
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            NodeOdmStatusText.Text =
                $"NodeODM-Ergebnis gespeichert: {path}" +
                (registered
                    ? " · im aktiven DroneDash-Projekt registriert"
                    : registrationError is not null
                        ? $" · Projektregistrierung fehlgeschlagen: {registrationError}"
                        : "");

            OdmImportedStatusText.Text =
                $"NodeODM ZIP heruntergeladen und bereit zum Import: {path}";
        }
        catch (Exception ex)
        {
            ProjectProcessingCoordinator.FailActive(
                ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
                $"NodeODM-Ergebnisdownload fehlgeschlagen: {ex.Message}");

            NodeOdmStatusText.Text =
                $"Download fehlgeschlagen: {ex.Message}";
        }
    }

    private NodeOdmClient CreateNodeOdmClient() =>
        new(
            NodeOdmEndpointBox.Text.Trim(),
            NodeOdmTokenBox.Password);

    private void AppendNodeOdmLog(string line)
    {
        ProjectProcessingCoordinator.LogActive(
            ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
            line);

        NodeOdmLogText.AppendText(
            $"[{DateTimeOffset.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        NodeOdmLogText.ScrollToEnd();
    }
}
