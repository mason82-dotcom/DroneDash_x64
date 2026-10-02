using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView : System.Windows.Controls.UserControl
{
    private readonly List<GeoPoint> _geometry = [];
    private FlightPlanResult? _plan;
    private bool _mapReady;
    private bool _suppressModeChange;
    private string? _currentProjectPath;

    public FlightPlanningView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeMapAsync();
    }

    private async Task InitializeMapAsync()
    {
        if (_mapReady)
            return;

        try
        {
            await RouteMap.EnsureCoreWebView2Async();
            RouteMap.CoreWebView2.WebMessageReceived += RouteMap_WebMessageReceived;

            var html = Path.Combine(AppContext.BaseDirectory, "planning", "route-editor.html");
            if (!File.Exists(html))
                throw new FileNotFoundException("Karten-HTML fehlt.", html);

            RouteMap.Source = new Uri(html);
            PlanningStatusText.Text = "Karte lädt …";
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = $"Karte konnte nicht initialisiert werden: {ex.Message}";
        }
    }

    private void RouteMap_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "ready")
            {
                _mapReady = true;
                PlanningStatusText.Text = "Karte bereit.";
                RenderMap();
                return;
            }

            if (type == "polygonPoint")
            {
                var lat = root.GetProperty("lat").GetDouble();
                var lon = root.GetProperty("lon").GetDouble();
                _geometry.Add(new GeoPoint(lat, lon));
                _plan = null;
                MarkProjectChanged();
                UpdateGeometryInfo();
                RenderMap();
            }
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = $"Kartenmeldung ungültig: {ex.Message}";
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
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
                dialog.InitialDirectory = Path.GetDirectoryName(_currentProjectPath);

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            FlightPlanProjectStore.Save(dialog.FileName, ReadSettings(), _geometry);
            _currentProjectPath = dialog.FileName;
            ProjectInfoText.Text = $"Gespeichert: {_currentProjectPath}";
            PlanningStatusText.Text = "DroneDash-Flugplan gespeichert.";
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
            RenderMap();
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

    private void ValidateKmz_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "DJI WPML/KMZ prüfen",
            Filter = "DJI Wayline (*.kmz)|*.kmz|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var report = DjiKmzValidator.Validate(dialog.FileName);
        RenderValidationReport(report, dialog.FileName);

        if (!report.IsValid)
        {
            System.Windows.MessageBox.Show(
                RenderValidationReportText(report),
                "DJI KMZ Validierung",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void DrawPolygon_Click(object sender, RoutedEventArgs e)
    {
        PostMap(new { type = "setDrawing", value = true });
        PlanningStatusText.Text = SelectedMode() == FlightPlanMode.MappingStrip
            ? "Trassenmodus aktiv: Mittellinie Punkt für Punkt auf der Karte anklicken."
            : "Polygonmodus aktiv: Eckpunkte der Messfläche auf der Karte anklicken.";
    }

    private void StopDrawing_Click(object sender, RoutedEventArgs e)
    {
        PostMap(new { type = "setDrawing", value = false });
        PlanningStatusText.Text = "Zeichenmodus beendet.";
    }

    private void UndoPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_geometry.Count == 0)
            return;

        _geometry.RemoveAt(_geometry.Count - 1);
        _plan = null;
        MarkProjectChanged();
        UpdateGeometryInfo();
        RenderMap();
    }

    private void ClearPlan_Click(object sender, RoutedEventArgs e)
    {
        _geometry.Clear();
        _plan = null;
        PlanningStatsText.Text = "Noch keine Route berechnet.";
        KmzValidationText.Text = "Noch keine KMZ geprüft.";
        MarkProjectChanged();
        UpdateGeometryInfo();
        RenderMap();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModeChange)
            return;

        _plan = null;
        MarkProjectChanged();

        if (!IsLoaded)
            return;

        UpdateGeometryInfo();
        RenderMap();
        PlanningStatusText.Text = SelectedMode() switch
        {
            FlightPlanMode.Mapping3D =>
                "Mapping 3D: eine Nadir- und vier Oblique-Waylines werden geplant.",
            FlightPlanMode.MappingStrip =>
                "Strip-Mapping: gezeichnete Punkte bilden die Korridor-Mittellinie.",
            _ =>
                "Mapping 2D: Polygonfläche mit parallelem Mapping-Raster."
        };
    }

    private void GenerateGrid_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _plan = PhotogrammetryPlanner.Generate(_geometry, ReadSettings());
            RenderPlanSummary(_plan);
            RenderMap();
            PlanningStatusText.Text = "Flugroute berechnet.";
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(
                ex.Message,
                "Flugplanung",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ExportKmz_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _plan ??= PhotogrammetryPlanner.Generate(_geometry, ReadSettings());

            using var dialog = new WinForms.SaveFileDialog
            {
                Title = "DJI WPML/KMZ exportieren",
                Filter = "DJI Wayline (*.kmz)|*.kmz",
                DefaultExt = "kmz",
                AddExtension = true,
                FileName = SafeFileName(_plan.Settings.Name) + ".kmz"
            };

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            DjiWpmlExporter.ExportKmz(dialog.FileName, _plan);
            var report = DjiKmzValidator.Validate(dialog.FileName);
            RenderValidationReport(report, dialog.FileName);

            PlanningStatusText.Text = report.IsValid
                ? $"DJI KMZ exportiert und validiert: {dialog.FileName}"
                : $"DJI KMZ exportiert, aber Validierung meldet Fehler: {dialog.FileName}";

            if (!report.IsValid)
            {
                System.Windows.MessageBox.Show(
                    RenderValidationReportText(report),
                    "DJI KMZ Export",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(
                ex.Message,
                "DJI KMZ Export",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private FlightPlanSettings ReadSettings()
    {
        var aircraftTag =
            (AircraftBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "M3E";
        if (!Enum.TryParse<DjiAircraftProfile>(aircraftTag, out var aircraft))
            aircraft = DjiAircraftProfile.M3E;

        return new FlightPlanSettings(
            string.IsNullOrWhiteSpace(PlanNameBox.Text)
                ? "DroneDash Mapping"
                : PlanNameBox.Text.Trim(),
            aircraft,
            SelectedMode(),
            ParseDouble(AltitudeBox.Text, "Höhe"),
            ParseDouble(SpeedBox.Text, "Geschwindigkeit"),
            ParseInt(FrontOverlapBox.Text, "Front Overlap"),
            ParseInt(SideOverlapBox.Text, "Side Overlap"),
            ParseDouble(GridAngleBox.Text, "Rasterwinkel"),
            ParseDouble(GimbalPitchBox.Text, "Nadir Gimbal"),
            ParseDouble(ObliquePitchBox.Text, "Oblique Gimbal"),
            SmartObliqueBox.IsChecked == true,
            TerrainFollowBox.IsChecked == true,
            ParseDouble(StripHalfWidthBox.Text, "Strip Halbbreite"));
    }

    private FlightPlanMode SelectedMode()
    {
        var modeTag =
            (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Mapping2D";
        return Enum.TryParse<FlightPlanMode>(modeTag, out var mode)
            ? mode
            : FlightPlanMode.Mapping2D;
    }

    private void ApplySettings(FlightPlanSettings settings)
    {
        _suppressModeChange = true;
        try
        {
            PlanNameBox.Text = settings.Name;
            SelectComboByTag(ModeBox, settings.Mode.ToString());
            SelectComboByTag(AircraftBox, settings.Aircraft.ToString());
            AltitudeBox.Text = settings.AltitudeMeters.ToString(
                CultureInfo.InvariantCulture);
            SpeedBox.Text = settings.SpeedMetersPerSecond.ToString(
                CultureInfo.InvariantCulture);
            FrontOverlapBox.Text = settings.FrontOverlapPercent.ToString(
                CultureInfo.InvariantCulture);
            SideOverlapBox.Text = settings.SideOverlapPercent.ToString(
                CultureInfo.InvariantCulture);
            GridAngleBox.Text = settings.GridAngleDegrees.ToString(
                CultureInfo.InvariantCulture);
            GimbalPitchBox.Text = settings.GimbalPitchDegrees.ToString(
                CultureInfo.InvariantCulture);
            ObliquePitchBox.Text = settings.ObliqueGimbalPitchDegrees.ToString(
                CultureInfo.InvariantCulture);
            SmartObliqueBox.IsChecked = settings.SmartObliqueEnabled;
            TerrainFollowBox.IsChecked = settings.TerrainFollowEnabled;
            StripHalfWidthBox.Text = settings.StripHalfWidthMeters.ToString(
                CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressModeChange = false;
        }
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(
                    item.Tag?.ToString(),
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        throw new InvalidDataException(
            $"Projekt enthält einen nicht unterstützten Wert '{tag}'.");
    }

    private void RenderPlanSummary(FlightPlanResult plan)
    {
        var secondary = plan.SecondaryGsdCentimeters is double ms
            ? $"\nMS GSD: {ms:F2} cm/px"
            : "";

        var geometryMetric = plan.Settings.Mode == FlightPlanMode.MappingStrip
            ? $"Korridorfläche (geschätzt): {plan.AreaSquareMeters / 10_000d:F2} ha"
            : $"Fläche: {plan.AreaSquareMeters / 10_000d:F2} ha";

        PlanningStatsText.Text =
            $"{plan.CameraProfile}\n" +
            $"Modus: {plan.Settings.Mode}\n" +
            $"{geometryMetric}\n" +
            $"RGB/Wide GSD: {plan.GsdCentimeters:F2} cm/px{secondary}\n" +
            $"Footprint: {plan.FootprintWidthMeters:F1} × {plan.FootprintHeightMeters:F1} m\n" +
            $"Linienabstand: {plan.LineSpacingMeters:F1} m\n" +
            $"Fotoabstand: {plan.PhotoSpacingMeters:F1} m\n" +
            $"Waylines: {plan.Passes.Count}\n" +
            $"Segmente: {plan.Segments.Count}\n" +
            $"Terrain Follow: {(plan.Settings.TerrainFollowEnabled ? "ja" : "nein")}\n" +
            $"Smart Oblique: {(plan.Settings.SmartObliqueEnabled ? "ja" : "nein")}\n" +
            $"Flugstrecke inkl. Transits: {plan.FlightDistanceMeters / 1000d:F2} km\n" +
            $"Geschätzte Bilder: {plan.EstimatedPhotos:N0}\n" +
            $"Reine Flugzeit: {plan.EstimatedFlightTime:hh\\:mm\\:ss}\n\n" +
            plan.SurveyNote;
    }

    private void RenderValidationReport(
        DjiKmzValidationReport report,
        string path)
    {
        KmzValidationText.Text =
            $"{Path.GetFileName(path)}\n{RenderValidationReportText(report)}";
    }

    private static string RenderValidationReportText(
        DjiKmzValidationReport report)
    {
        var lines = new List<string> { report.Summary };

        if (report.Errors.Count > 0)
        {
            lines.Add("");
            lines.Add("Fehler:");
            lines.AddRange(report.Errors.Select(e => "• " + e));
        }

        if (report.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Hinweise:");
            lines.AddRange(report.Warnings.Select(w => "• " + w));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateGeometryInfo()
    {
        PolygonInfoText.Text = SelectedMode() == FlightPlanMode.MappingStrip
            ? $"{_geometry.Count} Trassenpunkte"
            : $"{_geometry.Count} Polygonpunkte";
    }

    private void MarkProjectChanged()
    {
        ProjectInfoText.Text = string.IsNullOrWhiteSpace(_currentProjectPath)
            ? "Neues, noch nicht gespeichertes Projekt · geändert"
            : $"Geändert seit Laden/Speichern: {_currentProjectPath}";
    }

    private void RenderMap()
    {
        if (!_mapReady)
            return;

        var message = new
        {
            type = "render",
            geometryMode =
                SelectedMode() == FlightPlanMode.MappingStrip
                    ? "strip"
                    : "polygon",
            polygon = _geometry.Select(p => new
            {
                latitude = p.Latitude,
                longitude = p.Longitude
            }),
            segments = (_plan?.Passes ?? Array.Empty<FlightPass>())
                .SelectMany(pass => pass.Segments.Select(segment => new
                {
                    pass = pass.WaylineId,
                    name = pass.Name,
                    start = new
                    {
                        latitude = segment.Start.Latitude,
                        longitude = segment.Start.Longitude
                    },
                    end = new
                    {
                        latitude = segment.End.Latitude,
                        longitude = segment.End.Longitude
                    }
                }))
        };

        RouteMap.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(message));
    }

    private void PostMap(object message)
    {
        if (!_mapReady)
            return;

        RouteMap.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(message));
    }

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

    private static string SafeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return string.IsNullOrWhiteSpace(value)
            ? "DroneDash_Mapping"
            : value;
    }
}
