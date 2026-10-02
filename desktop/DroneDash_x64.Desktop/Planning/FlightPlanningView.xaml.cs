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

    private void RouteMap_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
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
                UpdateGeometryInfo();
                RenderMap();
            }
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = $"Kartenmeldung ungültig: {ex.Message}";
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
        UpdateGeometryInfo();
        RenderMap();
    }

    private void ClearPlan_Click(object sender, RoutedEventArgs e)
    {
        _geometry.Clear();
        _plan = null;
        PlanningStatsText.Text = "Noch keine Route berechnet.";
        UpdateGeometryInfo();
        RenderMap();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _plan = null;
        if (!IsLoaded)
            return;

        UpdateGeometryInfo();
        RenderMap();
        PlanningStatusText.Text = SelectedMode() switch
        {
            FlightPlanMode.Mapping3D => "Mapping 3D: eine Nadir- und vier Oblique-Waylines werden geplant.",
            FlightPlanMode.MappingStrip => "Strip-Mapping: gezeichnete Punkte bilden die Korridor-Mittellinie.",
            _ => "Mapping 2D: Polygonfläche mit parallelem Mapping-Raster."
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
            System.Windows.MessageBox.Show(ex.Message, "Flugplanung", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            PlanningStatusText.Text = $"DJI KMZ exportiert: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(ex.Message, "DJI KMZ Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private FlightPlanSettings ReadSettings()
    {
        var aircraftTag = (AircraftBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "M3E";
        if (!Enum.TryParse<DjiAircraftProfile>(aircraftTag, out var aircraft))
            aircraft = DjiAircraftProfile.M3E;

        var mode = SelectedMode();

        return new FlightPlanSettings(
            string.IsNullOrWhiteSpace(PlanNameBox.Text) ? "DroneDash Mapping" : PlanNameBox.Text.Trim(),
            aircraft,
            mode,
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
        var modeTag = (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Mapping2D";
        return Enum.TryParse<FlightPlanMode>(modeTag, out var mode)
            ? mode
            : FlightPlanMode.Mapping2D;
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

    private void UpdateGeometryInfo()
    {
        PolygonInfoText.Text = SelectedMode() == FlightPlanMode.MappingStrip
            ? $"{_geometry.Count} Trassenpunkte"
            : $"{_geometry.Count} Polygonpunkte";
    }

    private void RenderMap()
    {
        if (!_mapReady)
            return;

        var message = new
        {
            type = "render",
            geometryMode = SelectedMode() == FlightPlanMode.MappingStrip ? "strip" : "polygon",
            polygon = _geometry.Select(p => new { latitude = p.Latitude, longitude = p.Longitude }),
            segments = (_plan?.Passes ?? Array.Empty<FlightPass>())
                .SelectMany(pass => pass.Segments.Select(segment => new
                {
                    pass = pass.WaylineId,
                    name = pass.Name,
                    start = new { latitude = segment.Start.Latitude, longitude = segment.Start.Longitude },
                    end = new { latitude = segment.End.Latitude, longitude = segment.End.Longitude }
                }))
        };

        RouteMap.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void PostMap(object message)
    {
        if (!_mapReady)
            return;

        RouteMap.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private static double ParseDouble(string value, string label)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant))
            return invariant;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var current))
            return current;
        throw new FormatException($"{label}: ungültige Zahl.");
    }

    private static int ParseInt(string value, string label)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        throw new FormatException($"{label}: ungültige Ganzzahl.");
    }

    private static string SafeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "DroneDash_Mapping" : value;
    }
}
