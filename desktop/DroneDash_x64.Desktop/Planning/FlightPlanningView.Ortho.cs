using System.IO;
using System.Windows;
using DroneDash_x64.Desktop.Photogrammetry;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView
{
    private const string OrthoHost = "ortho.dronedash.local";

    private string? _orthoPath;
    private OrthoTileSet? _orthoTiles;
    private string? _orthoHostFolder;

    private async void SelectOrthophoto_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Orthomosaik (GeoTIFF) als Kartenhintergrund wählen",
            Filter = "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryResults) ??
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryProcessing) ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        await ShowOrthophotoAsync(dialog.FileName, quiet: false);
        MarkProjectChanged();
    }

    private void ClearOrthophoto_Click(object sender, RoutedEventArgs e)
    {
        _orthoPath = null;
        _orthoTiles = null;
        OrthophotoText.Text = "Kein Orthomosaik";
        MarkProjectChanged();
        RenderMap();
    }

    /// <summary>Loads (or renders once) the tile pyramid and shows it under the route.</summary>
    private async Task ShowOrthophotoAsync(string? path, bool quiet)
    {
        _orthoPath = path;
        _orthoTiles = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            OrthophotoText.Text = "Kein Orthomosaik";
            RenderMap();
            return;
        }

        if (!File.Exists(path))
        {
            OrthophotoText.Text = $"{path} (Datei nicht gefunden)";
            RenderMap();
            return;
        }

        try
        {
            var tiles = OrthoTileService.TryLoadCached(path);
            if (tiles is null)
            {
                OrthophotoText.Text = "Orthomosaik wird für die Karte gekachelt (einmalig) …";
                _terrainToolchain ??= await LocalImageToolchain.ProbeAsync();
                if (_terrainToolchain.PythonExecutable is null || !_terrainToolchain.GdalPythonAvailable)
                {
                    throw new InvalidOperationException(
                        "Für das Orthomosaik wird Python mit GDAL (osgeo.gdal) benötigt; Python über DRONEDASH_PYTHON festlegen.");
                }

                tiles = await OrthoTileService.RenderAsync(
                    _terrainToolchain.PythonExecutable, _terrainToolchain.OpenCvWorkerPath, path);
            }

            // Ignore a result for an image that was replaced while tiling ran.
            if (!string.Equals(_orthoPath, path, StringComparison.Ordinal))
                return;

            _orthoTiles = tiles;
            OrthophotoText.Text =
                $"{path} · {tiles.PixelSizeMeters * 100:F1} cm/px · Zoom {tiles.MinZoom}–{tiles.MaxZoom}";
            RenderMap();
        }
        catch (Exception ex)
        {
            OrthophotoText.Text = $"Orthomosaik nicht verfügbar: {ex.Message}";
            if (!quiet)
                PlanningStatusText.Text = OrthophotoText.Text;
            RenderMap();
        }
    }

    /// <summary>Tile layer for the map message; maps the tile folder to its virtual host on first use.</summary>
    private object? OrthoMapMessage()
    {
        if (_orthoTiles is null || _orthoPath is null || RouteMap.CoreWebView2 is not { } core)
            return null;

        var folder = OrthoTileService.TileFolderFor(_orthoPath);
        if (!string.Equals(_orthoHostFolder, folder, StringComparison.Ordinal))
        {
            if (_orthoHostFolder is not null)
                core.ClearVirtualHostNameToFolderMapping(OrthoHost);
            core.SetVirtualHostNameToFolderMapping(OrthoHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            _orthoHostFolder = folder;
        }

        return OrthoTileService.MapLayerMessage(_orthoTiles, _orthoPath, OrthoHost, Path.GetFileName(_orthoPath));
    }
}
