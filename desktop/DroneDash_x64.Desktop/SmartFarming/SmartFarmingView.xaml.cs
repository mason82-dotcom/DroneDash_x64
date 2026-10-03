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

public partial class SmartFarmingView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<M3mCaptureGroup> _captures = [];
    private readonly ObservableCollection<LocalImageToolStatus> _toolStatuses = [];
    private string? _sourceFolder;
    private string? _flightPlanProjectPath;
    private M3mDatasetResult? _dataset;
    private VegetationIndexResult? _currentIndex;
    private BitmapSource? _currentIndexBitmap;
    private LocalImageToolchainStatus? _toolchain;
    private LocalProcessingPlan? _currentLocalPlan;
    private CancellationTokenSource? _localProcessingCts;
    private string? _nodeOdmTaskUuid;
    private CancellationTokenSource? _nodeOdmMonitorCts;
    private NodeOdmServerInfo? _nodeOdmServer;
    private string? _lastNodeOdmZipPath;
    private OdmImportedResult? _odmImported;
    private CancellationTokenSource? _odmFieldProductCts;

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

    public void AcceptDatasetFolder(
        string sourceFolder)
    {
        var fullPath =
            Path.GetFullPath(
                sourceFolder);

        if (!Directory.Exists(fullPath))
        {
            StatusText.Text =
                $"Datensatzordner fehlt: {fullPath}";
            return;
        }

        _sourceFolder =
            fullPath;

        FolderText.Text =
            _sourceFolder;

        InvalidateDataset();

        StatusText.Text =
            "Datensatz aus dem Projekt-Pipeline-Job übernommen.";
    }

    public void AcceptFlightPlan(
        string flightPlanPath)
    {
        try
        {
            var fullPath =
                Path.GetFullPath(
                    flightPlanPath);

            var project =
                FlightPlanProjectStore.Load(
                    fullPath);

            _flightPlanProjectPath =
                fullPath;

            FlightPlanText.Text =
                $"{project.Settings.Name} · {project.Settings.Aircraft} · " +
                $"{project.Settings.Mode} · {project.Geometry.Count} Geometriepunkte\n" +
                _flightPlanProjectPath;

            InvalidateDataset();

            StatusText.Text =
                "Flugplan aus dem DroneDash-Projekt übernommen.";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Flugplan konnte nicht übernommen werden: {ex.Message}";
        }
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "DJI Mavic 3M Datensatz auswählen",
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_sourceFolder)
                ? _sourceFolder
                : ProjectWorkspaceLayout.TryGetActiveFolder(
                      ProjectWorkspaceFolder.SmartFarmingDataset) ?? ""
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

        ProjectProcessingCoordinator.BeginActive(
            ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
            "M3M-Aufnahmen und radiometrische XMP-Metadaten werden geprüft.",
            "dataset-qa");

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

            ProjectProcessingCoordinator.AwaitOutputActive(
                ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
                _dataset.Captures.Count == 0
                    ? "Datensatzprüfung abgeschlossen; kein QA-Manifest exportierbar."
                    : "Datensatzprüfung abgeschlossen; QA-Manifest exportieren.");
        }
        catch (Exception ex)
        {
            ProjectProcessingCoordinator.FailActive(
                ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
                ex.Message);

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

    private async void ExportDataset_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für Smart-Farming-Datensatz-QA auswählen",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.SmartFarmingDataset) ??
                _sourceFolder ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var output = SmartFarmingExporter.ExportDataset(
                dialog.SelectedPath,
                _dataset,
                _flightPlanProjectPath);

            var registered = false;
            string? registrationError = null;

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    registered = await DroneDashProjectSession.RegisterFileAsync(
                        output.JsonPath,
                        ProjectArtifactKind.SmartFarmingDataset);

                    await DroneDashProjectSession.RegisterDirectoryAsync(
                        _dataset.SourceFolder,
                        ProjectArtifactKind.SourceDataFolder);

                    ProjectProcessingCoordinator.RecordOutputActive(
                        ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
                        output.JsonPath,
                        ProjectArtifactKind.SmartFarmingDataset);

                    ProjectProcessingCoordinator.CompleteActive(
                        ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
                        "M3M-Datensatz-QA exportiert und im Projekt registriert.");
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            StatusText.Text =
                $"Datensatz-QA exportiert: {output.JsonPath} · {output.CsvPath}" +
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
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.SmartFarmingResults) ??
                _sourceFolder ??
                ""
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

    private async void ImportNodeOdmZip_Click(object sender, RoutedEventArgs e)
    {
        using var fileDialog = new WinForms.OpenFileDialog
        {
            Title = "NodeODM all.zip importieren",
            Filter = "ZIP-Archive (*.zip)|*.zip|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_lastNodeOdmZipPath) &&
            File.Exists(_lastNodeOdmZipPath))
        {
            fileDialog.InitialDirectory =
                Path.GetDirectoryName(_lastNodeOdmZipPath);
            fileDialog.FileName =
                Path.GetFileName(_lastNodeOdmZipPath);
        }

        if (fileDialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        using var folderDialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner zum sicheren Extrahieren des NodeODM-Ergebnisses auswählen",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.SmartFarmingProcessing) ??
                Path.GetDirectoryName(fileDialog.FileName) ??
                ""
        };

        if (folderDialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        GenerateOdmFieldProductsButton.IsEnabled = false;
        OdmFieldProductProgress.Value = 0;
        OdmImportedStatusText.Text =
            "NodeODM ZIP wird sicher extrahiert und das Orthomosaik wird geprüft …";

        try
        {
            var zipPath =
                Path.GetFullPath(fileDialog.FileName);

            var extractionRoot =
                Path.Combine(
                    folderDialog.SelectedPath,
                    Path.GetFileNameWithoutExtension(zipPath) +
                    "_extracted");

            var extracted =
                await Task.Run(() =>
                    OdmResultImporter.ExtractSafely(
                        zipPath,
                        extractionRoot));

            var orthophoto =
                OdmResultImporter.FindOrthophoto(
                    extracted);

            var info =
                await OdmOrthophotoInspector.InspectAsync(
                    orthophoto);

            _odmImported =
                new OdmImportedResult(
                    zipPath,
                    extracted,
                    orthophoto,
                    info,
                    info.Warnings);

            _lastNodeOdmZipPath =
                zipPath;

            string? registrationError = null;

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    await DroneDashProjectSession.RegisterFileAsync(
                        zipPath,
                        ProjectArtifactKind.NodeOdmResultArchive);

                    await DroneDashProjectSession.RegisterDirectoryAsync(
                        extracted,
                        ProjectArtifactKind.ProcessingWorkspace);
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            OdmImportedStatusText.Text =
                $"Importiert: {zipPath}\n" +
                $"Orthomosaik: {info.SummaryText}\n" +
                $"CRS: {info.CoordinateSystemName ?? "—"}\n" +
                $"Bandbeschreibungen: {string.Join(" | ", info.BandDescriptions)}" +
                (info.Warnings.Count > 0
                    ? "\nHinweise: " +
                      string.Join(" | ", info.Warnings)
                    : "") +
                (registrationError is not null
                    ? $"\nProjektregistrierung fehlgeschlagen: {registrationError}"
                    : "");

            GenerateOdmFieldProductsButton.IsEnabled = true;
            OdmFieldProductStatusText.Text =
                "ODM-Orthomosaik bereit für Feldprodukte.";
        }
        catch (Exception ex)
        {
            _odmImported = null;
            OdmImportedStatusText.Text =
                $"NodeODM-Import fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "ODM-Feldprodukte",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void GenerateOdmFieldProducts_Click(object sender, RoutedEventArgs e)
    {
        if (_odmImported is null)
        {
            System.Windows.MessageBox.Show(
                "Zuerst ein NodeODM all.zip importieren.",
                "ODM-Feldprodukte",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        NdviScoutingZoneSettings zoneSettings;

        try
        {
            zoneSettings =
                new NdviScoutingZoneSettings(
                    ParseFlexibleDouble(
                        ZoneThreshold1Box.Text,
                        "Z1/Z2"),
                    ParseFlexibleDouble(
                        ZoneThreshold2Box.Text,
                        "Z2/Z3"),
                    ParseFlexibleDouble(
                        ZoneThreshold3Box.Text,
                        "Z3/Z4"),
                    ParseFlexibleDouble(
                        ZoneThreshold4Box.Text,
                        "Z4/Z5"));

            zoneSettings.Validate();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "NDVI-Scouting-Zonen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _toolchain ??=
            await LocalImageToolchain.ProbeAsync();

        if (!_toolchain.Otb.Available ||
            string.IsNullOrWhiteSpace(_toolchain.OtbBandMath) ||
            string.IsNullOrWhiteSpace(_toolchain.OtbBandMathX))
        {
            System.Windows.MessageBox.Show(
                "Orfeo ToolBox mit BandMath und BandMathX wird für die georeferenzierten Feldprodukte benötigt.",
                "ODM-Feldprodukte",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        using var folderDialog =
            new WinForms.FolderBrowserDialog
            {
                Description = "Zielordner für georeferenzierte Smart-Farming-Feldprodukte auswählen",
                ShowNewFolderButton = true,
                SelectedPath =
                    ProjectWorkspaceLayout.TryGetActiveFolder(
                        ProjectWorkspaceFolder.SmartFarmingResults) ??
                    _odmImported.ExtractedRoot
            };

        if (folderDialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        var workspace =
            Path.Combine(
                folderDialog.SelectedPath,
                "DroneDash_FieldProducts");

        var plan =
            OdmFieldProductPlanBuilder.Build(
                _odmImported.Orthophoto,
                workspace,
                _toolchain,
                zoneSettings);

        var output =
            LocalProcessingPlanExporter.Export(
                plan);

        string? planRegistrationError = null;

        if (DroneDashProjectSession.IsOpen)
        {
            try
            {
                await DroneDashProjectSession.RegisterFileAsync(
                    output.JsonPath,
                    ProjectArtifactKind.LocalProcessingPlan);

                await DroneDashProjectSession.RegisterDirectoryAsync(
                    workspace,
                    ProjectArtifactKind.ProcessingWorkspace);
            }
            catch (Exception ex)
            {
                planRegistrationError = ex.Message;
            }
        }

        var confirmation =
            System.Windows.MessageBox.Show(
                "Georeferenzierte Feldprodukte jetzt berechnen?\n\n" +
                $"{_odmImported.Orthophoto.BandMap.ToDisplayText()}\n" +
                $"{zoneSettings.LegendText}\n\n" +
                "Die NDVI-Zonen sind ausschließlich Scouting-Klassen und keine automatische Betriebsmittel-Empfehlung.",
                "ODM-Feldprodukte",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (confirmation !=
            MessageBoxResult.Yes)
        {
            OdmFieldProductStatusText.Text =
                $"Processing-Plan erzeugt: {output.JsonPath}" +
                (planRegistrationError is not null
                    ? $" · Projektregistrierung fehlgeschlagen: {planRegistrationError}"
                    : "");
            return;
        }

        _odmFieldProductCts?.Dispose();
        _odmFieldProductCts =
            new CancellationTokenSource();

        GenerateOdmFieldProductsButton.IsEnabled =
            false;
        CancelOdmFieldProductsButton.IsEnabled =
            true;
        OdmFieldProductProgress.Value = 0;
        OdmFieldProductLogText.Clear();

        ProjectProcessingCoordinator.BeginActive(
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
            "Georeferenzierte Feldprodukte werden berechnet.",
            "field-products");

        var progress =
            new Progress<LocalProcessingProgress>(
                value =>
                {
                    OdmFieldProductProgress.Value =
                        value.Percent;

                    OdmFieldProductStatusText.Text =
                        $"{value.CompletedSteps}/{value.TotalSteps} · {value.Message}";

                    ProjectProcessingCoordinator.ReportActive(
                        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                        value.Percent,
                        value.Message,
                        value.CurrentStepId);
                });

        try
        {
            var result =
                await LocalProcessingRunner.RunAsync(
                    plan,
                    progress,
                    line =>
                        Dispatcher.BeginInvoke(() =>
                        {
                            ProjectProcessingCoordinator.LogActive(
                                ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                                line);

                            OdmFieldProductLogText.AppendText(
                                line +
                                Environment.NewLine);

                            OdmFieldProductLogText.ScrollToEnd();
                        }),
                    _odmFieldProductCts.Token);

            OdmFieldProductProgress.Value =
                result.Success
                    ? 100
                    : OdmFieldProductProgress.Value;

            if (result.Success)
            {
                var manifest =
                    OdmFieldProductExporter.ExportManifest(
                        workspace,
                        _odmImported.Orthophoto,
                        zoneSettings);

                var productPaths =
                    plan.Steps
                        .Select(step => step.OutputPath)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Cast<string>()
                        .Prepend(manifest)
                        .ToArray();

                var registered = 0;
                string? registrationError = null;

                if (DroneDashProjectSession.IsOpen)
                {
                    try
                    {
                        registered =
                            await DroneDashProjectSession.RegisterFilesAsync(
                                productPaths);

                        foreach (var productPath in productPaths)
                        {
                            ProjectProcessingCoordinator.RecordOutputActive(
                                ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                                productPath,
                                ProjectArtifactService.ClassifyFile(
                                    productPath));
                        }

                        ProjectProcessingCoordinator.CompleteActive(
                            ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                            "Georeferenzierte Feldprodukte erzeugt und im Projekt registriert.");
                    }
                    catch (Exception ex)
                    {
                        registrationError = ex.Message;
                    }
                }

                OdmFieldProductStatusText.Text =
                    $"Feldprodukte abgeschlossen · {manifest} · Log: {result.LogPath}" +
                    (registered > 0
                        ? $" · {registered} Ergebnisartefakt(e) im aktiven DroneDash-Projekt registriert"
                        : registrationError is not null
                            ? $" · Projektregistrierung fehlgeschlagen: {registrationError}"
                            : "");
            }
            else
            {
                if (result.Canceled)
                {
                    ProjectProcessingCoordinator.CancelActive(
                        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                        "Feldproduktberechnung wurde abgebrochen.");
                }
                else
                {
                    ProjectProcessingCoordinator.FailActive(
                        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                        $"Fehler bei {result.FailedStepId} · ExitCode {result.ExitCode}");
                }

                OdmFieldProductStatusText.Text =
                    result.Canceled
                        ? $"Feldproduktberechnung abgebrochen · Log: {result.LogPath}"
                        : $"Feldproduktberechnung fehlgeschlagen bei {result.FailedStepId} · ExitCode {result.ExitCode} · Log: {result.LogPath}";
            }
        }
        catch (Exception ex)
        {
            ProjectProcessingCoordinator.FailActive(
                ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                ex.Message);

            OdmFieldProductStatusText.Text =
                $"Feldproduktberechnung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            CancelOdmFieldProductsButton.IsEnabled =
                false;

            GenerateOdmFieldProductsButton.IsEnabled =
                _odmImported is not null;
        }
    }

    private void CancelOdmFieldProducts_Click(object sender, RoutedEventArgs e)
    {
        _odmFieldProductCts?.Cancel();

        ProjectProcessingCoordinator.CancelActive(
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
            "Abbruch der Feldproduktberechnung angefordert.");

        OdmFieldProductStatusText.Text =
            "Abbruch angefordert …";
    }

    private static double ParseFlexibleDouble(
        string text,
        string label)
    {
        if (double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var invariant) &&
            double.IsFinite(invariant))
        {
            return invariant;
        }

        if (double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out var current) &&
            double.IsFinite(current))
        {
            return current;
        }

        throw new FormatException(
            $"{label}: ungültige Zahl.");
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
