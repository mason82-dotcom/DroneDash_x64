using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
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

    public SmartFarmingView()
    {
        InitializeComponent();
        CaptureGrid.ItemsSource = _captures;
        ToolGrid.ItemsSource = _toolStatuses;
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
        PlanPreviewText.Text = "Noch kein lokaler Processing-Plan erzeugt.";
    }
}
