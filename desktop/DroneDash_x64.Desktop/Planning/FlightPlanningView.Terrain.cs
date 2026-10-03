using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView
{
    private string? _terrainModelPath;
    private GeoPoint? _takeOffPoint;
    private LocalImageToolchainStatus? _terrainToolchain;
    private TerrainCheckResult? _terrainResult;
    private FlightPlanResult? _terrainPlan;
    private bool _terrainBusy;

    /// <summary>The terrain result belongs to exactly the plan it was computed for.</summary>
    private TerrainCheckResult? CurrentTerrainResult =>
        _terrainResult is not null && ReferenceEquals(_terrainPlan, _plan) ? _terrainResult : null;

    private void SelectTerrainModel_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Höhenmodell (DSM, GeoTIFF) für die Geländeprüfung wählen",
            Filter = "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryResults) ??
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryProcessing) ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        _terrainModelPath = dialog.FileName;
        _terrainResult = null;
        TerrainModelText.Text = _terrainModelPath;
        TerrainResultText.Text = "Höhenmodell gewählt · Route prüfen.";
        RenderMap();
    }

    private void PickTakeOff_Click(object sender, RoutedEventArgs e)
    {
        PostMap(new { type = "setDrawing", value = false });
        PostMap(new { type = "pickStart" });
        PlanningStatusText.Text = "Startpunkt: Position auf der Karte anklicken.";
    }

    private void ClearTakeOff_Click(object sender, RoutedEventArgs e)
    {
        _takeOffPoint = null;
        _terrainResult = null;
        UpdateTakeOffText();
        RenderMap();
    }

    private void SetTakeOff(GeoPoint point)
    {
        _takeOffPoint = point;
        _terrainResult = null;
        UpdateTakeOffText();
        RenderMap();
        PlanningStatusText.Text = "Startpunkt gesetzt.";
    }

    private void UpdateTakeOffText()
    {
        TakeOffText.Text = _takeOffPoint is { } point
            ? string.Create(CultureInfo.InvariantCulture, $"{point.Latitude:F6}, {point.Longitude:F6}")
            : "Nicht gesetzt · erster Routenpunkt wird verwendet";
    }

    private async void CheckTerrain_Click(object sender, RoutedEventArgs e)
    {
        if (_terrainBusy)
            return;

        try
        {
            if (_plan is null)
                throw new InvalidOperationException("Zuerst die Route berechnen.");
            if (_terrainModelPath is null || !File.Exists(_terrainModelPath))
                throw new InvalidOperationException("Zuerst ein Höhenmodell (DSM) wählen.");

            var options = new TerrainCheckOptions(
                _takeOffPoint,
                ParseOptionalNumber(TakeOffElevationBox.Text, "Starthöhe"),
                ParseNumber(MinClearanceBox.Text, "Mindestabstand"),
                ParseNumber(TerrainBufferBox.Text, "Puffer"));

            _terrainBusy = true;
            CheckTerrainButton.IsEnabled = false;
            TerrainResultText.Text = "Python-Toolchain wird geprüft …";
            _terrainToolchain ??= await LocalImageToolchain.ProbeAsync();

            if (_terrainToolchain.PythonExecutable is null || !_terrainToolchain.GdalPythonAvailable)
            {
                throw new InvalidOperationException(
                    "Für die Geländeprüfung wird Python mit GDAL (osgeo.gdal) und NumPy benötigt. " +
                    "Python über DRONEDASH_PYTHON festlegen (z. B. OSGeo4W oder conda).");
            }

            TerrainResultText.Text = "Route wird gegen das Höhenmodell geprüft …";
            var plan = _plan;
            var result = await TerrainCheckService.RunAsync(
                _terrainToolchain.PythonExecutable,
                _terrainToolchain.OpenCvWorkerPath,
                _terrainModelPath,
                plan,
                options);

            _terrainResult = result;
            _terrainPlan = plan;
            TerrainResultText.Text = result.SummaryText;
            TerrainResultText.Foreground = result.HasCollision
                ? System.Windows.Media.Brushes.Firebrick
                : result.IsClear
                    ? System.Windows.Media.Brushes.ForestGreen
                    : System.Windows.Media.Brushes.DarkOrange;
            PlanningStatusText.Text = result.IsClear
                ? "Geländeprüfung: keine Engstellen."
                : "Geländeprüfung: Engstellen auf der Karte markiert.";
            RenderMap();
        }
        catch (Exception ex)
        {
            TerrainResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TerrainResultText.Text = ex.Message;
        }
        finally
        {
            _terrainBusy = false;
            CheckTerrainButton.IsEnabled = true;
        }
    }

    private object? TerrainMapMessage()
    {
        var result = CurrentTerrainResult;
        if (result is null)
            return null;

        return new
        {
            chunks = result.Chunks.Select(chunk => new { status = chunk.Status, pass = chunk.Pass, points = chunk.Points }),
            violations = result.Violations.Select(v => new
            {
                lat = v.Lat,
                lon = v.Lon,
                clearance = v.MinimumClearance,
                obstacle = v.Obstacle,
                collision = v.Collision,
                pass = v.Pass
            }),
            minClearance = result.MinClearance
        };
    }

    private static double ParseNumber(string text, string label) =>
        ParseOptionalNumber(text, label) ?? throw new FormatException($"{label}: Zahl erwartet.");

    private static double? ParseOptionalNumber(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               double.IsFinite(value)
            ? value
            : throw new FormatException($"{label}: ungültige Zahl '{text}'.");
    }

    private bool TryHandleTerrainMessage(string? type, JsonElement root)
    {
        if (type != "startPoint")
            return false;

        SetTakeOff(MessagePoint(root));
        return true;
    }
}
