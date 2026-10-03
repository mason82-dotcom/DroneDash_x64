using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DroneDash_x64.Desktop.Imaging;
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
    private CancellationTokenSource? _datasetScanCts;
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

        _datasetScanCts?.Cancel();
        _datasetScanCts?.Dispose();
        _datasetScanCts = new CancellationTokenSource();
        var scanCts = _datasetScanCts;

        SetBusy(true);
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        StatusText.Text = "M3M-Aufnahmen und radiometrische XMP-Metadaten werden geprüft …";

        ProjectProcessingCoordinator.BeginActive(
            ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
            "M3M-Aufnahmen und radiometrische XMP-Metadaten werden geprüft.",
            "dataset-qa");

        try
        {
            var folder = _sourceFolder;
            var scanProgress = new Progress<DatasetScanProgress>(value =>
            {
                Progress.Value = value.Percent;
                StatusText.Text =
                    value.TotalFiles <= 0
                        ? "Keine M3M-Dateien im erwarteten Schema gefunden."
                        : $"M3M-Metadaten {value.ProcessedFiles}/{value.TotalFiles}" +
                          (string.IsNullOrWhiteSpace(value.CurrentFile)
                              ? ""
                              : $" · {value.CurrentFile}");
            });

            _dataset = await Task.Run(
                () => M3mDatasetScanner.Scan(
                    folder,
                    scanCts.Token,
                    scanProgress),
                scanCts.Token);

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
        catch (OperationCanceledException)
        {
            ProjectProcessingCoordinator.FailActive(
                ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
                "Datensatzprüfung abgebrochen.");

            StatusText.Text = "Datensatzprüfung abgebrochen.";
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

            if (ReferenceEquals(_datasetScanCts, scanCts))
            {
                _datasetScanCts.Dispose();
                _datasetScanCts = null;
            }
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
        _datasetScanCts?.Cancel();
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
