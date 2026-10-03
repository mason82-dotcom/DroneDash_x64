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

    private async Task<LocalImageToolchainStatus> RequireToolchainAsync(bool requireGdal)
    {
        OdmStatusText.Text = "Python-Toolchain wird geprüft …";
        _toolchain ??= await LocalImageToolchain.ProbeAsync();

        if (_toolchain.PythonExecutable is null)
        {
            throw new InvalidOperationException(
                "Kein Python gefunden. Python über DRONEDASH_PYTHON festlegen (z. B. OSGeo4W oder conda).");
        }

        if (requireGdal && !_toolchain.GdalPythonAvailable)
        {
            throw new InvalidOperationException(
                "Für die Höhenmodell-Darstellung wird Python mit GDAL-Bindings (osgeo.gdal) und NumPy benötigt. " +
                "Python über DRONEDASH_PYTHON festlegen und z. B. per OSGeo4W oder conda installieren.");
        }

        return _toolchain;
    }

    private async void ShowPointCloud_Click(object sender, RoutedEventArgs e)
    {
        if (_odmProducts?.PointCloudPath is { } path)
            await ShowPointCloudAsync(path);
    }

    private async void OpenPointCloud_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Punktwolke öffnen",
            Filter = "Punktwolke (*.laz;*.las)|*.laz;*.las|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryResults) ??
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryProcessing) ??
                ""
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            await ShowPointCloudAsync(dialog.FileName);
    }

    private async Task ShowPointCloudAsync(string cloudPath)
    {
        if (_elevationBusy)
            return;

        _elevationBusy = true;
        OdmProgress.IsIndeterminate = true;

        try
        {
            var preview = PointCloudPreviewService.TryLoadCached(cloudPath);

            if (preview is null)
            {
                var toolchain = await RequireToolchainAsync(requireGdal: false);
                OdmStatusText.Text = $"Punktwolke wird für den 3D-Viewer aufbereitet: {cloudPath}";
                preview = await PointCloudPreviewService.RenderAsync(
                    toolchain.PythonExecutable!,
                    toolchain.OpenCvWorkerPath,
                    cloudPath);
            }

            OdmStatusText.Text =
                $"Punktwolke: {preview.Points:N0} von {preview.TotalPoints:N0} Punkten" +
                (preview.Stride > 1 ? $" (jeder {preview.Stride}.)" : "");

            new PointCloudViewerWindow(preview, PointCloudPreviewService.PreviewFolderFor(cloudPath), Path.GetFileName(cloudPath))
            {
                Owner = Window.GetWindow(this)
            }.Show();
        }
        catch (Exception ex)
        {
            OdmStatusText.Text = $"Punktwolke kann nicht angezeigt werden: {ex.Message}";
        }
        finally
        {
            OdmProgress.IsIndeterminate = false;
            _elevationBusy = false;
        }
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
                var toolchain = await RequireToolchainAsync(requireGdal: true);

                OdmStatusText.Text = $"Höhenmodell wird für die Karte aufbereitet: {demPath}";
                preview = await DemPreviewService.RenderAsync(
                    toolchain.PythonExecutable!,
                    toolchain.OpenCvWorkerPath,
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
