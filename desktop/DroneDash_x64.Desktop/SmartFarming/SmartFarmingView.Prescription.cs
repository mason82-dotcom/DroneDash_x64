using System.IO;
using System.Windows;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.SmartFarming;

public partial class SmartFarmingView
{
    private async void CreatePrescription_Click(object sender, RoutedEventArgs e)
    {
        double[] rates;
        double cell;
        try
        {
            rates =
            [
                ParseFlexibleDouble(RateZone1Box.Text, "Zone 1"),
                ParseFlexibleDouble(RateZone2Box.Text, "Zone 2"),
                ParseFlexibleDouble(RateZone3Box.Text, "Zone 3"),
                ParseFlexibleDouble(RateZone4Box.Text, "Zone 4"),
                ParseFlexibleDouble(RateZone5Box.Text, "Zone 5")
            ];
            cell = ParseFlexibleDouble(RateCellBox.Text, "Maschinenraster");
            PrescriptionMapService.FormatRates(rates);
        }
        catch (Exception ex)
        {
            PrescriptionStatusText.Text = ex.Message;
            return;
        }

        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "NDVI-Scouting-Zonenkarte (ndvi_scouting_zones.tif) wählen",
            Filter = "Zonenkarte (*.tif;*.tiff)|*.tif;*.tiff|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.SmartFarmingResults) ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        CreatePrescriptionButton.IsEnabled = false;
        try
        {
            PrescriptionStatusText.Text = "Applikationskarte wird erstellt …";
            _toolchain ??= await LocalImageToolchain.ProbeAsync();
            if (_toolchain.PythonExecutable is null || !_toolchain.GdalPythonAvailable)
            {
                throw new InvalidOperationException(
                    "Für die Applikationskarte wird Python mit GDAL/OGR benötigt; Python über DRONEDASH_PYTHON festlegen.");
            }

            var outputFolder = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? ".", "applikationskarte");
            var result = await PrescriptionMapService.CreateAsync(
                _toolchain.PythonExecutable,
                _toolchain.OpenCvWorkerPath,
                dialog.FileName,
                outputFolder,
                RateNameBox.Text,
                rates,
                cell,
                RateUnitBox.Text.Trim());

            PrescriptionStatusText.Text =
                result.SummaryText + Environment.NewLine +
                $"Shapefile: {result.Shapefile}" + Environment.NewLine + $"GeoJSON: {result.Geojson}";

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    await DroneDashProjectSession.RegisterFileAsync(result.Geojson, ProjectArtifactKind.SmartFarmingFieldProducts);
                }
                catch (Exception ex)
                {
                    PrescriptionStatusText.Text += Environment.NewLine + $"Projektregistrierung fehlgeschlagen: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            PrescriptionStatusText.Text = $"Applikationskarte fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            CreatePrescriptionButton.IsEnabled = true;
        }
    }
}
