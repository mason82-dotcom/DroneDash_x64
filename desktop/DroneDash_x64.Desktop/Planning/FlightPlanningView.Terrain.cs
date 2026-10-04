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

            var options = ReadTerrainOptions();
            SetTerrainBusy(true);
            var toolchain = await RequireTerrainToolchainAsync();

            TerrainResultText.Text = "Route wird gegen das Höhenmodell geprüft …";
            var plan = _plan;
            var result = await TerrainCheckService.RunAsync(
                toolchain.PythonExecutable!, toolchain.OpenCvWorkerPath, _terrainModelPath!, plan, options);

            ShowTerrainResult(plan, result);
        }
        catch (Exception ex)
        {
            TerrainResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TerrainResultText.Text = ex.Message;
        }
        finally
        {
            SetTerrainBusy(false);
        }
    }

    private async void RaiseAltitude_Click(object sender, RoutedEventArgs e)
    {
        if (_terrainBusy || _plan is null || CurrentTerrainResult is not { } current ||
            TerrainCheckService.RequiredAltitude(current) is not { } suggested)
        {
            return;
        }

        var aboveLimit = suggested > TerrainCheckService.OpenCategoryMaxAglMeters
            ? Environment.NewLine + Environment.NewLine +
              $"⚠ Mehr als {TerrainCheckService.OpenCategoryMaxAglMeters:F0} m über dem Startpunkt: in der offenen Kategorie " +
              "nur zulässig, wenn der Abstand zum Boden darunter 120 m nicht überschreitet; sonst ist eine Genehmigung nötig."
            : "";

        var confirmation = System.Windows.MessageBox.Show(
            $"Flughöhe von {current.Altitude:F0} m auf mindestens {suggested:F0} m anheben, Route neu berechnen und erneut prüfen?" +
            Environment.NewLine + "Linienabstand, GSD und Flugzeit ändern sich mit der Höhe." + aboveLimit,
            "Höhe anpassen",
            MessageBoxButton.OKCancel,
            aboveLimit.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.OK)
            return;

        try
        {
            var options = ReadTerrainOptions();
            var settings = ReadSettings();
            SetTerrainBusy(true);
            var toolchain = await RequireTerrainToolchainAsync();

            var adjustment = await TerrainCheckService.RaiseAltitudeAsync(
                _plan,
                current,
                altitude => PhotogrammetryPlanner.Generate(_geometry, settings with { AltitudeMeters = altitude }),
                plan =>
                {
                    TerrainResultText.Text = $"Route mit {plan.Settings.AltitudeMeters:F0} m wird geprüft …";
                    return TerrainCheckService.RunAsync(
                        toolchain.PythonExecutable!, toolchain.OpenCvWorkerPath, _terrainModelPath!, plan, options);
                });

            _plan = adjustment.Plan;
            AltitudeBox.Text = adjustment.Plan.Settings.AltitudeMeters.ToString("0.##", CultureInfo.InvariantCulture);
            MarkProjectChanged();
            RenderPlanSummary(_plan);
            ShowTerrainResult(_plan, adjustment.Result);
            PlanningStatusText.Text = adjustment.Satisfied
                ? $"Flughöhe auf {_plan.Settings.AltitudeMeters:F0} m angehoben; Mindestabstand überall eingehalten."
                : $"Flughöhe auf {_plan.Settings.AltitudeMeters:F0} m angehoben, aber noch Engstellen nach {adjustment.Iterations} Schritten.";
        }
        catch (Exception ex)
        {
            TerrainResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TerrainResultText.Text = ex.Message;
        }
        finally
        {
            SetTerrainBusy(false);
        }
    }

    private TerrainCheckOptions ReadTerrainOptions()
    {
        if (_terrainModelPath is null || !File.Exists(_terrainModelPath))
            throw new InvalidOperationException("Zuerst ein Höhenmodell (DSM) wählen.");

        return new TerrainCheckOptions(
            _takeOffPoint,
            ParseOptionalNumber(TakeOffElevationBox.Text, "Starthöhe"),
            ParseNumber(MinClearanceBox.Text, "Mindestabstand"),
            ParseNumber(TerrainBufferBox.Text, "Puffer"));
    }

    private async Task<LocalImageToolchainStatus> RequireTerrainToolchainAsync()
    {
        TerrainResultText.Text = "Python-Toolchain wird geprüft …";
        _terrainToolchain ??= await LocalImageToolchain.ProbeAsync();

        if (_terrainToolchain.PythonExecutable is null || !_terrainToolchain.GdalPythonAvailable)
        {
            throw new InvalidOperationException(
                "Für die Geländeprüfung wird Python mit GDAL (osgeo.gdal) und NumPy benötigt. " +
                "Python über DRONEDASH_PYTHON festlegen (z. B. OSGeo4W oder conda).");
        }

        return _terrainToolchain;
    }

    private void SetTerrainBusy(bool busy)
    {
        _terrainBusy = busy;
        CheckTerrainButton.IsEnabled = !busy;
        UpdateRaiseAltitudeButton();
    }

    private void UpdateRaiseAltitudeButton()
    {
        RaiseAltitudeButton.IsEnabled =
            !_terrainBusy &&
            CurrentTerrainResult is { } result &&
            TerrainCheckService.RequiredAltitude(result) is not null;
    }

    private void ShowTerrainResult(FlightPlanResult plan, TerrainCheckResult result)
    {
        _terrainResult = result;
        _terrainPlan = plan;

        var lines = result.SummaryText;
        if (TerrainCheckService.RequiredAltitude(result) is { } required)
            lines += Environment.NewLine + $"→ Mit mindestens {required:F0} m Flughöhe wäre der Mindestabstand eingehalten („Höhe anpassen“).";

        TerrainResultText.Text = lines;
        TerrainResultText.Foreground = result.HasCollision
            ? System.Windows.Media.Brushes.Firebrick
            : result.IsClear
                ? System.Windows.Media.Brushes.ForestGreen
                : System.Windows.Media.Brushes.DarkOrange;
        PlanningStatusText.Text = result.IsClear
            ? "Geländeprüfung: keine Engstellen."
            : "Geländeprüfung: Engstellen auf der Karte markiert.";
        UpdateRaiseAltitudeButton();
        RenderMap();
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
            minClearance = result.MinClearance,
            profile = result.Profile ?? []
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

    /// <summary>Current terrain inputs for the plan file; unparsable numbers fall back to the defaults.</summary>
    private FlightPlanTerrainSettings ReadTerrainSettings()
    {
        static double? TryRead(string text) =>
            double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value)
                ? value
                : null;

        var clearance = TryRead(MinClearanceBox.Text) is >= 0 and double c ? c : FlightPlanTerrainSettings.DefaultMinimumClearanceMeters;
        var buffer = TryRead(TerrainBufferBox.Text) is >= 0 and <= 200 and double b ? b : FlightPlanTerrainSettings.DefaultBufferMeters;

        return new FlightPlanTerrainSettings(
            _terrainModelPath,
            _takeOffPoint,
            TryRead(TakeOffElevationBox.Text),
            clearance,
            buffer);
    }

    private void ApplyTerrainSettings(FlightPlanTerrainSettings? terrain)
    {
        _terrainResult = null;
        _takeOffPoint = terrain?.TakeOff;
        _terrainModelPath = terrain?.ModelPath;
        TakeOffElevationBox.Text = terrain?.TakeOffElevation?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        MinClearanceBox.Text = (terrain?.MinimumClearanceMeters ?? FlightPlanTerrainSettings.DefaultMinimumClearanceMeters)
            .ToString("0.##", CultureInfo.InvariantCulture);
        TerrainBufferBox.Text = (terrain?.BufferMeters ?? FlightPlanTerrainSettings.DefaultBufferMeters)
            .ToString("0.##", CultureInfo.InvariantCulture);

        TerrainModelText.Text = _terrainModelPath switch
        {
            null => "Kein Höhenmodell gewählt",
            var path when File.Exists(path) => path,
            var path => $"{path} (Datei nicht gefunden)"
        };
        TerrainResultText.Foreground = System.Windows.Media.Brushes.Black;
        TerrainResultText.Text = "Noch nicht geprüft.";
        UpdateTakeOffText();
    }

    private bool TryHandleTerrainMessage(string? type, JsonElement root)
    {
        if (type != "startPoint")
            return false;

        SetTakeOff(MessagePoint(root));
        return true;
    }
}
