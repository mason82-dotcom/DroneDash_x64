using System.IO;
using System.Windows;
using DroneDash_x64.Desktop.Maps;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Photogrammetry;

public partial class PhotogrammetryWorkspaceView
{
    private LocalImageToolchainStatus? _toolchain;
    private bool _elevationBusy;

    private async void ShowDsm_Click(object sender, RoutedEventArgs e)
    {
        if (_odmProducts?.DsmPath is { } path)
            await ShowElevationModelAsync(path, "Oberflächenmodell (DSM)");
    }

    private async void ShowDtm_Click(object sender, RoutedEventArgs e)
    {
        if (_odmProducts?.DtmPath is { } path)
            await ShowElevationModelAsync(path, "Geländemodell (DTM)");
    }

    private async void OpenElevationModel_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Höhenmodell (GeoTIFF) öffnen",
            Filter = "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryResults) ??
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryProcessing) ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var name = Path.GetFileName(dialog.FileName);
        var title = name.Equals("dtm.tif", StringComparison.OrdinalIgnoreCase)
            ? "Geländemodell (DTM)"
            : name.Equals("dsm.tif", StringComparison.OrdinalIgnoreCase)
                ? "Oberflächenmodell (DSM)"
                : name;

        await ShowElevationModelAsync(dialog.FileName, title);
    }

    private async Task ShowElevationModelAsync(string demPath, string title)
    {
        if (_elevationBusy)
            return;

        _elevationBusy = true;
        OdmProgress.IsIndeterminate = true;

        try
        {
            var preview = DemPreviewService.TryLoadCached(demPath);

            if (preview is null)
            {
                OdmStatusText.Text = "Python/GDAL-Toolchain wird geprüft …";
                _toolchain ??= await LocalImageToolchain.ProbeAsync();

                if (_toolchain.PythonExecutable is null || !_toolchain.GdalPythonAvailable)
                {
                    throw new InvalidOperationException(
                        "Für die Höhenmodell-Darstellung wird Python mit GDAL-Bindings (osgeo.gdal) und NumPy benötigt. " +
                        "Python über DRONEDASH_PYTHON festlegen und z. B. per OSGeo4W oder conda installieren.");
                }

                OdmStatusText.Text = $"Höhenmodell wird für die Karte aufbereitet: {demPath}";
                preview = await DemPreviewService.RenderAsync(
                    _toolchain.PythonExecutable,
                    _toolchain.OpenCvWorkerPath,
                    demPath);
            }

            OdmStatusText.Text =
                $"{title}: {preview.Minimum:F2} – {preview.Maximum:F2} m · Mittel {preview.Mean:F2} m";

            new ElevationMapWindow(preview, DemPreviewService.PreviewFolderFor(demPath), title)
            {
                Owner = Window.GetWindow(this)
            }.Show();
        }
        catch (Exception ex)
        {
            OdmStatusText.Text = $"Höhenmodell kann nicht angezeigt werden: {ex.Message}";
        }
        finally
        {
            OdmProgress.IsIndeterminate = false;
            _elevationBusy = false;
        }
    }
}
