using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using DroneDash_x64.Desktop.Photogrammetry;
using Microsoft.Web.WebView2.Core;

namespace DroneDash_x64.Desktop.Maps;

/// <summary>3D viewer (three.js) for a thinned point-cloud preview.</summary>
public partial class PointCloudViewerWindow : Window
{
    private const string ViewerHost = "maps.dronedash.local";
    private const string DataHost = "pointcloud.dronedash.local";

    private readonly PointCloudPreview _preview;
    private readonly string _previewFolder;
    private readonly string _title;

    public PointCloudViewerWindow(PointCloudPreview preview, string previewFolder, string title)
    {
        InitializeComponent();
        _preview = preview;
        _previewFolder = previewFolder;
        _title = title;
        Title = $"Punktwolke · {title}";
        Loaded += async (_, _) => await InitializeViewerAsync();
    }

    private async Task InitializeViewerAsync()
    {
        try
        {
            await ViewerView.EnsureCoreWebView2Async();
            var core = ViewerView.CoreWebView2;
            core.WebMessageReceived += ViewerView_WebMessageReceived;

            var mapsFolder = Path.Combine(AppContext.BaseDirectory, "maps");
            if (!File.Exists(Path.Combine(mapsFolder, "pointcloud-viewer.html")))
                throw new FileNotFoundException("Viewer-HTML fehlt.", Path.Combine(mapsFolder, "pointcloud-viewer.html"));

            core.SetVirtualHostNameToFolderMapping(ViewerHost, mapsFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping(DataHost, _previewFolder, CoreWebView2HostResourceAccessKind.Allow);

            ViewerView.Source = new Uri($"https://{ViewerHost}/pointcloud-viewer.html");
            StatusText.Text = "Viewer lädt …";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Viewer konnte nicht initialisiert werden: {ex.Message}";
        }
    }

    private string? DataUrl(string? file, long version) =>
        file is null ? null : $"https://{DataHost}/{Uri.EscapeDataString(file)}?v={version}";

    private void ViewerView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;

            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    var version = File.GetLastWriteTimeUtc(
                        Path.Combine(_previewFolder, PointCloudPreviewService.MetadataFileName)).Ticks;
                    var message = new
                    {
                        type = "load",
                        title = _title,
                        preview = _preview,
                        urls = new
                        {
                            positions = DataUrl(_preview.Files.Positions, version),
                            colors = DataUrl(_preview.Files.Colors, version),
                            classification = DataUrl(_preview.Files.Classification, version)
                        }
                    };
                    ViewerView.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    break;

                case "loaded":
                    StatusText.Text =
                        $"{_preview.Points:N0} von {_preview.TotalPoints:N0} Punkten · Quelle {_preview.Source}" +
                        " · Linke Maustaste drehen, rechte verschieben, Rad zoomen, Klick auf Punkt zeigt Koordinaten";
                    break;

                case "picked":
                    StatusText.Text = string.Create(
                        CultureInfo.GetCultureInfo("de-DE"),
                        $"Punkt X {root.GetProperty("x").GetDouble():N3} · Y {root.GetProperty("y").GetDouble():N3} · Z {root.GetProperty("z").GetDouble():N3} m");
                    break;

                case "error":
                    StatusText.Text = root.TryGetProperty("message", out var detail)
                        ? $"Fehler: {detail.GetString()}"
                        : "Fehler beim Laden der Punktwolke.";
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Viewer-Meldung ungültig: {ex.Message}";
        }
    }
}
