using System.IO;
using System.Text.Json;
using System.Windows;
using DroneDash_x64.Desktop.Photogrammetry;
using Microsoft.Web.WebView2.Core;

namespace DroneDash_x64.Desktop.Maps;

/// <summary>Shows a rendered elevation preview (DSM/DTM) on a Leaflet map with height readout.</summary>
public partial class ElevationMapWindow : Window
{
    private const string MapHost = "maps.dronedash.local";
    private const string DataHost = "dem.dronedash.local";

    private readonly DemPreview _preview;
    private readonly string _previewFolder;
    private readonly string _layerTitle;
    private readonly ElevationAnalysisContext? _analysis;
    private readonly CancellationTokenSource _closing = new();

    public ElevationMapWindow(
        DemPreview preview,
        string previewFolder,
        string layerTitle,
        ElevationAnalysisContext? analysis = null)
    {
        InitializeComponent();
        _preview = preview;
        _previewFolder = previewFolder;
        _layerTitle = layerTitle;
        _analysis = analysis;
        Title = $"Höhenmodell · {layerTitle}";
        Loaded += async (_, _) => await InitializeMapAsync();
        Closed += (_, _) => _closing.Cancel();
    }

    private async Task InitializeMapAsync()
    {
        try
        {
            await MapView.EnsureCoreWebView2Async();
            var core = MapView.CoreWebView2;
            core.Settings.UserAgent = "DroneDash_x64/0.2 (+https://github.com/mason82-dotcom/DroneDash_x64)";
            core.WebMessageReceived += MapView_WebMessageReceived;

            var mapsFolder = Path.Combine(AppContext.BaseDirectory, "maps");
            if (!File.Exists(Path.Combine(mapsFolder, "dem-map.html")))
                throw new FileNotFoundException("Karten-HTML fehlt.", Path.Combine(mapsFolder, "dem-map.html"));

            core.SetVirtualHostNameToFolderMapping(MapHost, mapsFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            // The map page fetches the elevation grid from this second origin.
            core.SetVirtualHostNameToFolderMapping(DataHost, _previewFolder, CoreWebView2HostResourceAccessKind.Allow);

            MapView.Source = new Uri($"https://{MapHost}/dem-map.html");
            StatusText.Text = "Karte lädt …";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Karte konnte nicht initialisiert werden: {ex.Message}";
        }
    }

    private static IReadOnlyList<(double Lon, double Lat)> ReadCoordinates(JsonElement root) =>
        root.GetProperty("coordinates")
            .EnumerateArray()
            .Select(point => (point[0].GetDouble(), point[1].GetDouble()))
            .ToArray();

    private async Task RunAnalysisAsync(JsonElement request)
    {
        var id = request.GetProperty("id").GetInt32();
        var type = request.GetProperty("type").GetString();
        object reply;

        try
        {
            if (_analysis is null)
                throw new InvalidOperationException("Für dieses Höhenmodell sind keine Analysen verfügbar.");

            var coordinates = ReadCoordinates(request);
            StatusText.Text = type == "profileRequest" ? "Höhenprofil wird berechnet …" : "Volumen wird berechnet …";

            if (type == "profileRequest")
            {
                var profile = await ElevationAnalysisService.ProfileAsync(_analysis, coordinates, _closing.Token);
                reply = new { type = "analysisResult", id, kind = "profile", result = profile };
                StatusText.Text = $"Höhenprofil · Länge {profile.LengthMeters:N2} m";
            }
            else
            {
                var baseMode = Enum.Parse<VolumeBase>(request.GetProperty("base").GetString()!, ignoreCase: true);
                double? baseHeight = request.TryGetProperty("baseHeight", out var h) && h.ValueKind == JsonValueKind.Number
                    ? h.GetDouble()
                    : null;
                var volume = await ElevationAnalysisService.VolumeAsync(_analysis, coordinates, baseMode, baseHeight, _closing.Token);
                reply = new { type = "analysisResult", id, kind = "volume", result = volume };
                StatusText.Text = $"Volumen · Auftrag {volume.CutCubicMeters:N1} m³ · Abtrag {volume.FillCubicMeters:N1} m³ · Fläche {volume.AreaSquareMeters:N1} m²";
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            reply = new { type = "analysisError", id, message = ex.Message };
            StatusText.Text = $"Analyse fehlgeschlagen: {ex.Message}";
        }

        MapView.CoreWebView2?.PostWebMessageAsJson(
            JsonSerializer.Serialize(reply, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private void MapView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;

            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    // Cache-bust so a re-rendered preview is never served stale.
                    var version = File.GetLastWriteTimeUtc(
                        Path.Combine(_previewFolder, DemPreviewService.MetadataFileName)).Ticks;
                    var message = new
                    {
                        type = "load",
                        title = _layerTitle,
                        preview = _preview,
                        imageUrl = $"https://{DataHost}/{Uri.EscapeDataString(_preview.Image)}?v={version}",
                        gridUrl = $"https://{DataHost}/{Uri.EscapeDataString(_preview.Grid)}?v={version}",
                        analysis = _analysis is null
                            ? null
                            : new { hasBaseModel = _analysis.BaseModelPath is not null }
                    };
                    MapView.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    break;

                case "loaded":
                    StatusText.Text =
                        $"{_layerTitle} · Quelle {_preview.Source} · {_preview.SourceCrs ?? "CRS unbekannt"} · " +
                        $"{_preview.SourceWidth}×{_preview.SourceHeight} px (Vorschau {_preview.Width}×{_preview.Height})";
                    break;

                case "error":
                    StatusText.Text = root.TryGetProperty("message", out var detail)
                        ? $"Fehler: {detail.GetString()}"
                        : "Fehler beim Laden des Höhenmodells.";
                    break;

                case "profileRequest":
                case "volumeRequest":
                    _ = RunAnalysisAsync(root.Clone());
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Kartenmeldung ungültig: {ex.Message}";
        }
    }
}
