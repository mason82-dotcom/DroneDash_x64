using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using DroneDash_x64.Desktop.Project;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView
{
    private void ImportGeometry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new WinForms.OpenFileDialog
            {
                Title = "Feldgrenze / Trasse importieren",
                Filter =
                    "Geometrie (*.kml;*.kmz;*.geojson;*.json)|*.kml;*.kmz;*.geojson;*.json|" +
                    "KML/KMZ (*.kml;*.kmz)|*.kml;*.kmz|" +
                    "GeoJSON (*.geojson;*.json)|*.geojson;*.json|" +
                    "Alle Dateien (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.Planning) is string planningFolder)
            {
                dialog.InitialDirectory = planningFolder;
            }

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            ApplyImportedGeometry(
                GeometryImporter.Import(dialog.FileName),
                Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = $"Import fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "Geometrie importieren",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ApplyImportedGeometry(ImportedGeometry imported, string fileName)
    {
        var targetMode = imported.Kind == ImportedGeometryKind.Line
            ? FlightPlanMode.MappingStrip
            : SelectedMode() == FlightPlanMode.MappingStrip
                ? FlightPlanMode.Mapping2D
                : SelectedMode();

        _geometry.Clear();
        _geometry.AddRange(imported.Points);
        _plan = null;

        if (targetMode != SelectedMode())
        {
            // Raises ModeBox_SelectionChanged, which re-renders the map.
            SelectComboByTag(ModeBox, targetMode.ToString());
        }

        PostMap(new { type = "setDrawing", value = false });
        MarkProjectChanged();
        UpdateGeometryInfo();
        RenderMap(fitBounds: true);

        var kind = imported.Kind == ImportedGeometryKind.Line ? "Trasse" : "Fläche";
        var label = imported.Name is null ? fileName : $"{imported.Name} ({fileName})";
        var status = $"{kind} importiert: {label} · {imported.Points.Count} Punkte.";
        if (imported.Warnings.Count > 0)
            status += " Hinweis: " + string.Join(" ", imported.Warnings);

        PlanningStatusText.Text = status;
    }

    /// <summary>Handles vertex edits from the map. Returns false for unknown message types.</summary>
    private bool TryHandleVertexMessage(string? type, JsonElement root)
    {
        switch (type)
        {
            case "vertexMoved":
            {
                var index = VertexIndex(root, _geometry.Count);
                _geometry[index] = MessagePoint(root);
                break;
            }

            case "vertexInserted":
            {
                var index = VertexIndex(root, _geometry.Count + 1);
                _geometry.Insert(index, MessagePoint(root));
                break;
            }

            case "vertexDeleted":
            {
                var index = VertexIndex(root, _geometry.Count);
                _geometry.RemoveAt(index);
                break;
            }

            default:
                return false;
        }

        _plan = null;
        MarkProjectChanged();
        UpdateGeometryInfo();
        RenderMap();
        return true;
    }

    private static int VertexIndex(JsonElement root, int exclusiveUpperBound)
    {
        var index = root.GetProperty("index").GetInt32();
        if (index < 0 || index >= exclusiveUpperBound)
            throw new ArgumentOutOfRangeException(nameof(index), "Eckpunkt-Index ungültig.");
        return index;
    }

    private static GeoPoint MessagePoint(JsonElement root)
    {
        var lat = root.GetProperty("lat").GetDouble();
        var lon = root.GetProperty("lon").GetDouble();

        if (!double.IsFinite(lat) || !double.IsFinite(lon) ||
            lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(root), "Kartenposition außerhalb von WGS84.");
        }

        return new GeoPoint(lat, lon);
    }

    private object GeometryMetricsMessage()
    {
        var strip = SelectedMode() == FlightPlanMode.MappingStrip;
        return new
        {
            strip,
            pointCount = _geometry.Count,
            areaSquareMeters = strip ? 0d : GeometryMetrics.PolygonAreaSquareMeters(_geometry),
            perimeterMeters = strip ? 0d : GeometryMetrics.PolygonPerimeterMeters(_geometry),
            lengthMeters = strip ? GeometryMetrics.PolylineLengthMeters(_geometry) : 0d,
            selfIntersecting = !strip && GeometryMetrics.IsSelfIntersecting(_geometry)
        };
    }

    private string GeometrySummaryText()
    {
        var culture = CultureInfo.GetCultureInfo("de-DE");

        if (SelectedMode() == FlightPlanMode.MappingStrip)
        {
            var length = GeometryMetrics.PolylineLengthMeters(_geometry);
            return string.Format(
                culture,
                "{0} Trassenpunkte · Länge {1:N0} m",
                _geometry.Count,
                length);
        }

        if (_geometry.Count < 3)
            return $"{_geometry.Count} Polygonpunkte";

        var text = string.Format(
            culture,
            "{0} Polygonpunkte · Fläche {1:N2} ha · Umfang {2:N0} m",
            _geometry.Count,
            GeometryMetrics.PolygonAreaSquareMeters(_geometry) / 10_000d,
            GeometryMetrics.PolygonPerimeterMeters(_geometry));

        return GeometryMetrics.IsSelfIntersecting(_geometry)
            ? text + " · ⚠ Fläche überschneidet sich selbst"
            : text;
    }
}
