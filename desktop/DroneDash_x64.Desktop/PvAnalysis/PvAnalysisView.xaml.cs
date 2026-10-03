using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.Planning;
using DroneDash_x64.Desktop.Thermal;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.PvAnalysis;

public partial class PvAnalysisView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<PvImageAnalysisResult> _results = [];
    private string? _sourceFolder;
    private string? _flightPlanProjectPath;
    private PvDatasetResult? _dataset;
    private bool _mapReady;

    public PvAnalysisView()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _results;
        Loaded += async (_, _) => await InitializeMapAsync();

        using var sdk = new DjiThermalSdk();
        ThermalSdkStatusText.Text = sdk.Status;
        AnalyzeButton.IsEnabled = sdk.IsAvailable;
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
                $"{project.Settings.Mode}\n{_flightPlanProjectPath}";

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

    private async Task InitializeMapAsync()
    {
        if (_mapReady)
            return;

        try
        {
            await PvMap.EnsureCoreWebView2Async();
            PvMap.CoreWebView2.WebMessageReceived += PvMap_WebMessageReceived;

            var html = Path.Combine(
                AppContext.BaseDirectory,
                "pv",
                "pv-map.html");

            if (!File.Exists(html))
                throw new FileNotFoundException("PV-Karten-HTML fehlt.", html);

            PvMap.Source = new Uri(html);
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"PV-Karte konnte nicht initialisiert werden: {ex.Message}";
        }
    }

    private void PvMap_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            if (json.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() == "ready")
            {
                _mapReady = true;
                RenderMap();
            }
        }
        catch
        {
            // Invalid map messages are ignored; analysis data remains unaffected.
        }
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "M3T-Thermalbildordner für PV-Analyse auswählen",
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_sourceFolder)
                ? _sourceFolder
                : ProjectWorkspaceLayout.TryGetActiveFolder(
                      ProjectWorkspaceFolder.PvDataset) ?? ""
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
            Multiselect = false,
            InitialDirectory =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.Planning)
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        AcceptFlightPlan(
            dialog.FileName);
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

        SetBusy(true);
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
            RenderMap();
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
            SetBusy(false);
            RefreshSdkStatus();
        }
    }

    private void ResultGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        OpenDetailButton.IsEnabled =
            _dataset is not null &&
            ResultGrid.SelectedItem is PvImageAnalysisResult selected &&
            string.IsNullOrWhiteSpace(selected.ProcessingError);

        if (ResultGrid.SelectedItem is PvImageAnalysisResult image)
        {
            DetailSummaryText.Text =
                $"{image.FileName}\n" +
                $"{image.GpsText} · {image.RtkText}\n" +
                $"Max {image.MaxTemperatureText} · max ΔT {image.HighestDeltaText} · " +
                $"{image.Candidates.Count} Kandidat(en)";
            CandidateGrid.ItemsSource = image.Candidates;
        }
    }

    private async void OpenSelectedImage_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null ||
            ResultGrid.SelectedItem is not PvImageAnalysisResult selected)
        {
            return;
        }

        var path = Path.Combine(
            _dataset.SourceFolder,
            selected.RelativePath);

        if (!File.Exists(path))
        {
            System.Windows.MessageBox.Show(
                $"Thermaldatei nicht gefunden:\n{path}",
                "PV Thermal-Detail",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        OpenDetailButton.IsEnabled = false;
        DetailSummaryText.Text = $"{selected.FileName}\nThermalbild wird radiometrisch geladen …";

        try
        {
            var thermal = await Task.Run(() =>
            {
                using var sdk = new DjiThermalSdk();
                if (!sdk.IsAvailable)
                    throw new InvalidOperationException(sdk.Status);

                return sdk.Analyze(path, ThermalPalette.IronRed);
            });

            DetailImage.Source =
                PvThermalOverlayRenderer.Render(
                    thermal,
                    selected.Candidates);

            DetailPlaceholder.Visibility = Visibility.Collapsed;
            CandidateGrid.ItemsSource = selected.Candidates;
            DetailSummaryText.Text =
                $"{selected.FileName}\n" +
                $"{thermal.Width} × {thermal.Height} · " +
                $"Min {thermal.MinimumC:F2} °C · Ø {thermal.AverageC:F2} °C · " +
                $"Max {thermal.MaximumC:F2} °C\n" +
                $"Emissivität {thermal.Emissivity:F3} · Messdistanz {thermal.DistanceM:F2} m · " +
                $"{selected.Candidates.Count} Anomalie-Kandidat(en)";

            PvResultsTabs.SelectedIndex = 1;
        }
        catch (Exception ex)
        {
            DetailSummaryText.Text = $"Detailanalyse fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "PV Thermal-Detail",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            OpenDetailButton.IsEnabled =
                ResultGrid.SelectedItem is PvImageAnalysisResult current &&
                string.IsNullOrWhiteSpace(current.ProcessingError);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für PV-Analyse auswählen",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.PvResults) ??
                _sourceFolder ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var output = PvAnalysisExporter.Export(
                dialog.SelectedPath,
                _dataset);
            var report = PvInspectionReportExporter.ExportHtml(
                dialog.SelectedPath,
                _dataset);

            var registered = false;
            string? registrationError = null;

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    registered = await DroneDashProjectSession.RegisterFileAsync(
                        output.JsonPath,
                        ProjectArtifactKind.PvAnalysis);

                    await DroneDashProjectSession.RegisterDirectoryAsync(
                        _dataset.SourceFolder,
                        ProjectArtifactKind.SourceDataFolder);
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            StatusText.Text =
                $"PV-Analyse exportiert: {output.JsonPath} · " +
                $"{output.ImagesCsvPath} · {output.CandidatesCsvPath} · Bericht: {report}" +
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
                "PV-Analyse exportieren",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RenderMap()
    {
        if (!_mapReady)
            return;

        var points = (_dataset?.Images ?? [])
            .Where(image =>
                image.Latitude.HasValue &&
                image.Longitude.HasValue)
            .Select(image => new
            {
                lat = image.Latitude!.Value,
                lon = image.Longitude!.Value,
                file = image.FileName,
                severity = image.Severity.ToString(),
                delta = image.HighestDeltaText,
                candidates = image.Candidates.Count,
                route = image.RouteText
            })
            .ToArray();

        PvMap.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                type = "render",
                points
            }));
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

    private void SetBusy(bool busy)
    {
        AnalysisProgress.IsIndeterminate = busy;
        AnalyzeButton.IsEnabled = !busy;
        OpenDetailButton.IsEnabled = false;
        if (busy)
            ExportButton.IsEnabled = false;
    }

    private void RefreshSdkStatus()
    {
        using var sdk = new DjiThermalSdk();
        AnalyzeButton.IsEnabled = sdk.IsAvailable;
        ThermalSdkStatusText.Text = sdk.Status;

        if (_dataset is not null)
            ExportButton.IsEnabled = _dataset.Summary.ThermalImageCount > 0;
    }

    private void InvalidateDataset()
    {
        _dataset = null;
        _results.Clear();
        DetailImage.Source = null;
        DetailPlaceholder.Visibility = Visibility.Visible;
        CandidateGrid.ItemsSource = null;
        DetailSummaryText.Text = "Noch kein Detailbild geladen.";
        SummaryText.Text = "Datensatz geändert · Analyse neu starten.";
        StatusText.Text = "Bereit.";
        ExportButton.IsEnabled = false;
        OpenDetailButton.IsEnabled = false;
        RenderMap();
    }
}
