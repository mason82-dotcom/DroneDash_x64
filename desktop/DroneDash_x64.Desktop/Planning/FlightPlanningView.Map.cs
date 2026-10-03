using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using DroneDash_x64.Desktop.Project;
using WinForms = System.Windows.Forms;


namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView
{
    private async Task InitializeMapAsync()
    {
        if (_mapReady)
            return;

        try
        {
            await RouteMap.EnsureCoreWebView2Async();
            RouteMap.CoreWebView2.WebMessageReceived += RouteMap_WebMessageReceived;
            RouteMap.CoreWebView2.Settings.UserAgent =
                "DroneDash_x64/0.2 (+https://github.com/mason82-dotcom/DroneDash_x64)";

            var folder = Path.Combine(AppContext.BaseDirectory, "planning");
            var html = Path.Combine(folder, "route-editor.html");
            if (!File.Exists(html))
                throw new FileNotFoundException("Karten-HTML fehlt.", html);

            RouteMap.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "planning.dronedash.local",
                folder,
                CoreWebView2HostResourceAccessKind.DenyCors);

            RouteMap.Source = new Uri(
                "https://planning.dronedash.local/route-editor.html");
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
                RenderMap(fitBounds: true);
                return;
            }

            if (type == "polygonPoint")
            {
                _geometry.Add(MessagePoint(root));
                _plan = null;
                MarkProjectChanged();
                UpdateGeometryInfo();
                RenderMap();
                return;
            }

            if (TryHandleTerrainMessage(type, root))
                return;

            TryHandleVertexMessage(type, root);
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


    private void UpdateGeometryInfo()
    {
        PolygonInfoText.Text = GeometrySummaryText();
    }

    private void MarkProjectChanged()
    {
        ProjectInfoText.Text = string.IsNullOrWhiteSpace(_currentProjectPath)
            ? "Neues, noch nicht gespeichertes Projekt · geändert"
            : $"Geändert seit Laden/Speichern: {_currentProjectPath}";
    }

    /// <param name="fitBounds">
    /// Zoom to the geometry. Off for interactive edits so the view does not jump.
    /// </param>
    private void RenderMap(bool fitBounds = false)
    {
        // Every edit that invalidates the plan re-renders; the terrain result then no longer applies.
        UpdateRaiseAltitudeButton();

        if (!_mapReady)
            return;

        var message = new
        {
            type = "render",
            fitBounds,
            metrics = GeometryMetricsMessage(),
            geometryMode =
                SelectedMode() == FlightPlanMode.MappingStrip
                    ? "strip"
                    : "polygon",
            startPoint = _takeOffPoint is { } takeOff
                ? new { latitude = takeOff.Latitude, longitude = takeOff.Longitude }
                : null,
            terrain = TerrainMapMessage(),
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

}
