using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using DroneDash_x64.Desktop.Planning;
using DroneDash_x64.Desktop.Thermal;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.PvAnalysis;

public partial class PvAnalysisView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<PvImageAnalysisResult> _results = [];
    private string? _sourceFolder;
    private string? _flightPlanProjectPath;
    private PvDatasetResult? _dataset;

    public PvAnalysisView()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _results;

        using var sdk = new DjiThermalSdk();
        ThermalSdkStatusText.Text = sdk.Status;
        AnalyzeButton.IsEnabled = sdk.IsAvailable;
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "M3T-Thermalbildordner für PV-Analyse auswählen",
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_sourceFolder)
                ? _sourceFolder
                : ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        _sourceFolder = dialog.SelectedPath;
        FolderText.Text = _sourceFolder;
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
            var project = FlightPlanProjectStore.Load(dialog.FileName);
            _flightPlanProjectPath = dialog.FileName;
            FlightPlanText.Text =
                $"{project.Settings.Name} · {project.Settings.Aircraft} · " +
                $"{project.Settings.Mode}\n{_flightPlanProjectPath}";
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
                "Zuerst einen Thermal-Bildordner auswählen.",
                "PV-Analyse",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        PvAnalysisSettings settings;
        try
        {
            settings = ReadSettings();
            settings.Validate();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "PV-Analyse Einstellungen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AnalyzeButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        AnalysisProgress.IsIndeterminate = true;
        StatusText.Text = "Radiometrische M3T-Bilder werden analysiert …";

        try
        {
            var folder = _sourceFolder;
            var plan = _flightPlanProjectPath;

            _dataset = await Task.Run(() =>
                PvDatasetAnalyzer.Analyze(folder, settings, plan));

            _results.Clear();
            foreach (var result in _dataset.Images)
                _results.Add(result);

            SummaryText.Text = _dataset.Summary.ToDisplayText();
            StatusText.Text =
                _dataset.Summary.ThermalImageCount == 0
                    ? "Keine DJI-Infrarot-R-JPEGs erkannt."
                    : "Analyse abgeschlossen. Thermal-Anomalien sind Kandidaten und müssen fachlich verifiziert werden.";

            ExportButton.IsEnabled =
                _dataset.Summary.ThermalImageCount > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"PV-Analyse fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "PV-Analyse",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            AnalysisProgress.IsIndeterminate = false;

            using var sdk = new DjiThermalSdk();
            AnalyzeButton.IsEnabled = sdk.IsAvailable;
            ThermalSdkStatusText.Text = sdk.Status;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für PV-Analyse auswählen",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var output = PvAnalysisExporter.Export(
                dialog.SelectedPath,
                _dataset);

            StatusText.Text =
                $"PV-Analyse exportiert: {output.JsonPath} · " +
                $"{output.ImagesCsvPath} · {output.CandidatesCsvPath}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "PV-Analyse exportieren",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private PvAnalysisSettings ReadSettings() =>
        new(
            ParseDouble(WarningDeltaBox.Text, "Warn ΔT"),
            ParseDouble(CriticalDeltaBox.Text, "Kritisch ΔT"),
            ParseInt(WindowRadiusBox.Text, "Lokaler Radius"),
            ParseInt(MinimumClusterBox.Text, "Minimale Clustergröße"));

    private static double ParseDouble(string value, string label)
    {
        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var invariant))
        {
            return invariant;
        }

        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out var current))
        {
            return current;
        }

        throw new FormatException($"{label}: ungültige Zahl.");
    }

    private static int ParseInt(string value, string label)
    {
        if (int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result))
        {
            return result;
        }

        throw new FormatException($"{label}: ungültige Ganzzahl.");
    }

    private void InvalidateDataset()
    {
        _dataset = null;
        _results.Clear();
        SummaryText.Text = "Datensatz geändert · Analyse neu starten.";
        StatusText.Text = "Bereit.";
        ExportButton.IsEnabled = false;
    }
}
