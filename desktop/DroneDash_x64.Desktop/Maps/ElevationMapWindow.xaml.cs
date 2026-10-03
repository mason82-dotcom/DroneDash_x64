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

    public ElevationMapWindow(DemPreview preview, string previewFolder, string layerTitle)
    {
        InitializeComponent();
        _preview = preview;
        _previewFolder = previewFolder;
        _layerTitle = layerTitle;
        Title = $"Höhenmodell · {layerTitle}";
        Loaded += async (_, _) => await InitializeMapAsync();
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
                        gridUrl = $"https://{DataHost}/{Uri.EscapeDataString(_preview.Grid)}?v={version}"
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
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Kartenmeldung ungültig: {ex.Message}";
        }
    }
}
