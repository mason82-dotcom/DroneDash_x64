using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using DroneDash_x64.Desktop.SmartFarming.Odm;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.SmartFarming;

public partial class SmartFarmingView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<M3mCaptureGroup> _captures = [];
    private readonly ObservableCollection<LocalImageToolStatus> _toolStatuses = [];
    private string? _sourceFolder;
    private M3mDatasetResult? _dataset;
    private VegetationIndexResult? _currentIndex;
    private BitmapSource? _currentIndexBitmap;
    private LocalImageToolchainStatus? _toolchain;
    private LocalProcessingPlan? _currentLocalPlan;
    private CancellationTokenSource? _localProcessingCts;
    private string? _nodeOdmTaskUuid;
    private CancellationTokenSource? _nodeOdmMonitorCts;
    private NodeOdmServerInfo? _nodeOdmServer;

    public SmartFarmingView()
    {
        InitializeComponent();
        CaptureGrid.ItemsSource = _captures;
        ToolGrid.ItemsSource = _toolStatuses;
        NodeOdmEndpointBox.Text = NodeOdmClient.EndpointFromEnvironment();

        var nodeOdmToken = NodeOdmClient.TokenFromEnvironment();
        if (!string.IsNullOrWhiteSpace(nodeOdmToken))
            NodeOdmTokenBox.Password = nodeOdmToken;
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "DJI Mavic 3M Datensatz auswählen",
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_sourceFolder) ? _sourceFolder : ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        _sourceFolder = dialog.SelectedPath;
        FolderText.Text = _sourceFolder;
        InvalidateDataset();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourceFolder) || !Directory.Exists(_sourceFolder))
        {
            System.Windows.MessageBox.Show(
                "Zuerst einen M3M-Bildordner auswählen.",
                "Smart Farming",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        StatusText.Text = "M3M-Aufnahmen und radiometrische XMP-Metadaten werden geprüft …";

        try
        {
            var folder = _sourceFolder;
            _dataset = await Task.Run(() => M3mDatasetScanner.Scan(folder));

            _captures.Clear();
            foreach (var capture in _dataset.Captures)
                _captures.Add(capture);

            SummaryText.Text = _dataset.Summary.ToDisplayText();
            StatusText.Text = _dataset.Captures.Count == 0
                ? "Keine DJI-M3M-Aufnahmen im erwarteten Dateischema gefunden."
                : "Datensatzprüfung abgeschlossen. Quelldateien wurden nicht verändert.";

            ExportDatasetButton.IsEnabled = _dataset.Captures.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Datensatzprüfung fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "Smart Farming",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CaptureGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentIndex = null;
        _currentIndexBitmap = null;
        IndexImage.Source = null;
        IndexPlaceholder.Visibility = Visibility.Visible;
        ExportQuicklookButton.IsEnabled = false;
        CreateLocalPlanButton.IsEnabled =
            _toolchain?.IsReady == true &&
            CaptureGrid.SelectedItem is M3mCaptureGroup selectedForProcessing &&
            selectedForProcessing.IsQuicklookReady;

        if (CaptureGrid.SelectedItem is not M3mCaptureGroup capture)
        {
            IndexStatsText.Text = "Noch kein Index berechnet.";
            return;
        }

        IndexStatsText.Text =
            $"{capture.CaptureKey}\n" +
            $"GPS {capture.GpsText} · RTK {capture.RtkText}\n" +
            $"Status: {capture.StatusText}";
    }

    private async void ComputeIndex_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureGrid.SelectedItem is not M3mCaptureGroup capture)
        {
            System.Windows.MessageBox.Show(
                "Zuerst eine M3M-Aufnahme auswählen.",
                "Vegetationsindex",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!capture.IsQuicklookReady)
        {
            System.Windows.MessageBox.Show(
                "Diese Aufnahme ist nicht Quicklook-bereit. Prüfe die vier Multispektralbänder und die DJI-Radiometriemetadaten.",
                "Vegetationsindex",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (sender is not System.Windows.Controls.Button button ||
            !Enum.TryParse<VegetationIndexType>(
                button.Tag?.ToString(),
                ignoreCase: true,
                out var type))
        {
            return;
        }

        SetBusy(true);
        StatusText.Text = $"{type} wird aus der ausgewählten M3M-Aufnahme berechnet …";

        try
        {
            _currentIndex = await Task.Run(() =>
                M3mVegetationIndexEngine.Compute(capture, type));

            _currentIndexBitmap = M3mIndexRenderer.Render(_currentIndex);
            IndexImage.Source = _currentIndexBitmap;
            IndexPlaceholder.Visibility = Visibility.Collapsed;
            IndexStatsText.Text =
                $"{_currentIndex.StatisticsText}\n" +
                $"{_currentIndex.QualityLabel}\n" +
                "Exposure/Gain/Irradiance kompensiert · nicht orthorektifiziert · nicht vollständig co-registriert.";

            ExportQuicklookButton.IsEnabled = true;
            StatusText.Text = $"{type}-Quicklook berechnet.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{type}-Berechnung fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "Vegetationsindex",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ExportDataset_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für Smart-Farming-Datensatz-QA auswählen",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var output = SmartFarmingExporter.ExportDataset(dialog.SelectedPath, _dataset);
            StatusText.Text = $"Datensatz-QA exportiert: {output.JsonPath} · {output.CsvPath}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Smart Farming Export",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExportQuicklook_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex is null || _currentIndexBitmap is null)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für Vegetationsindex-Quicklook auswählen",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var output = SmartFarmingExporter.ExportQuicklook(
                dialog.SelectedPath,
                _currentIndex,
                _currentIndexBitmap);
            StatusText.Text = $"Quicklook exportiert: {output.PngPath} · {output.JsonPath}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Quicklook Export",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

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
            ShowNewFolderButton = true
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
                $"Lokaler Processing-Workspace erzeugt: {output.JsonPath} · {output.ScriptPath}";
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

        var uploadProgress = new Progress<NodeOdmUploadProgress>(value =>
        {
            NodeOdmProgress.Value = value.Percent;
            NodeOdmStatusText.Text =
                $"Upload {value.UploadedFiles}/{value.TotalFiles}: {value.CurrentFile}";
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
            AppendNodeOdmLog("NodeODM-Upload/Monitoring lokal abgebrochen.");
        }
        catch (Exception ex)
        {
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
                return;

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
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var progress = new Progress<double>(value =>
        {
            NodeOdmProgress.Value = Math.Clamp(value, 0, 100);
            NodeOdmStatusText.Text =
                $"NodeODM-Ergebnis wird heruntergeladen: {value:F1}%";
        });

        try
        {
            using var client = CreateNodeOdmClient();
            var path = await client.DownloadAllAsync(
                _nodeOdmTaskUuid,
                dialog.SelectedPath,
                progress);

            NodeOdmStatusText.Text =
                $"NodeODM-Ergebnis gespeichert: {path}";
        }
        catch (Exception ex)
        {
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
        NodeOdmLogText.AppendText(
            $"[{DateTimeOffset.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        NodeOdmLogText.ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        Progress.IsIndeterminate = busy;
        ScanButton.IsEnabled = !busy;
    }

    private void InvalidateDataset()
    {
        _dataset = null;
        _captures.Clear();
        _currentIndex = null;
        _currentIndexBitmap = null;
        IndexImage.Source = null;
        IndexPlaceholder.Visibility = Visibility.Visible;
        SummaryText.Text = "Datensatz geändert · Prüfung neu starten.";
        IndexStatsText.Text = "Noch kein Index berechnet.";
        StatusText.Text = "Bereit.";
        ExportDatasetButton.IsEnabled = false;
        ExportQuicklookButton.IsEnabled = false;
        CreateLocalPlanButton.IsEnabled = false;
        RunLocalPlanButton.IsEnabled = false;
        CancelLocalPlanButton.IsEnabled = false;
        _currentLocalPlan = null;
        PlanPreviewText.Text = "Noch kein lokaler Processing-Plan erzeugt.";
        LocalRunLogText.Text = "Live-Log erscheint hier.";
        LocalProcessingProgress.Value = 0;
        LocalProcessingStatusText.Text = "Keine lokale Pipeline aktiv.";
        StartNodeOdmTaskButton.IsEnabled = false;
    }
}
