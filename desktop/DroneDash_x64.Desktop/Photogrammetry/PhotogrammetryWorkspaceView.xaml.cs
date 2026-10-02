using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Photogrammetry;

public partial class PhotogrammetryWorkspaceView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<PhotogrammetryImageRecord> _images = [];
    private string? _sourceFolder;
    private string? _flightPlanProjectPath;
    private PhotogrammetryDatasetResult? _dataset;

    public PhotogrammetryWorkspaceView()
    {
        InitializeComponent();
        DatasetGrid.ItemsSource = _images;
    }

    private void SelectImageFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "DJI-Bildordner für Photogrammetrie auswählen",
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_sourceFolder)
                ? _sourceFolder
                : ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        _sourceFolder = dialog.SelectedPath;
        ImageFolderText.Text = _sourceFolder;
        InvalidateDataset();
    }

    private void SelectFlightPlan_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "DroneDash-Flugplan verknüpfen",
            Filter = "DroneDash Flugplan (*.ddplan;*.json)|*.ddplan;*.json|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var project = Planning.FlightPlanProjectStore.Load(dialog.FileName);
            _flightPlanProjectPath = dialog.FileName;
            FlightPlanText.Text =
                $"{project.Settings.Name} · {project.Settings.Aircraft} · " +
                $"{project.Settings.Mode} · {project.Geometry.Count} Geometriepunkte\n" +
                _flightPlanProjectPath;
            InvalidateDataset();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Flugplan verknüpfen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourceFolder) ||
            !Directory.Exists(_sourceFolder))
        {
            System.Windows.MessageBox.Show(
                "Zuerst einen Bildordner auswählen.",
                "Photogrammetrie",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        AnalyzeButton.IsEnabled = false;
        ExportManifestButton.IsEnabled = false;
        DatasetProgress.IsIndeterminate = true;
        DatasetStatusText.Text = "DJI-XMP/RTK-Metadaten werden analysiert …";

        try
        {
            var source = _sourceFolder;
            var plan = _flightPlanProjectPath;

            _dataset = await Task.Run(() =>
                PhotogrammetryDatasetAnalyzer.Analyze(source, plan));

            _images.Clear();
            foreach (var image in _dataset.Images)
                _images.Add(image);

            DatasetSummaryText.Text = _dataset.Summary.ToDisplayText();
            DatasetStatusText.Text =
                _dataset.Images.Count == 0
                    ? "Keine unterstützten JPG/JPEG/DNG/TIF/TIFF-Dateien gefunden."
                    : "Analyse abgeschlossen. Quelldateien wurden nicht verändert.";
            ExportManifestButton.IsEnabled = _dataset.Images.Count > 0;
        }
        catch (Exception ex)
        {
            DatasetStatusText.Text = $"Analyse fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "Photogrammetrie-Analyse",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            DatasetProgress.IsIndeterminate = false;
            AnalyzeButton.IsEnabled = true;
        }
    }

    private void ExportManifest_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _dataset.Images.Count == 0)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für Processing-Manifest auswählen",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var output = PhotogrammetryManifestExporter.Export(
                dialog.SelectedPath,
                _dataset);

            DatasetStatusText.Text =
                $"Manifest exportiert: {output.JsonPath} · CSV: {output.CsvPath} · Bildliste: {output.ImageListPath}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Manifest exportieren",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void InvalidateDataset()
    {
        _dataset = null;
        _images.Clear();
        DatasetSummaryText.Text = "Datensatz geändert · Analyse neu starten.";
        DatasetStatusText.Text = "Bereit zur Analyse.";
        ExportManifestButton.IsEnabled = false;
    }
}
