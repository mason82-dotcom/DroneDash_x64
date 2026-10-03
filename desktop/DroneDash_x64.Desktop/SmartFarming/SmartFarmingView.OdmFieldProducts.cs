using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using DroneDash_x64.Desktop.SmartFarming.Odm;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.Planning;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.SmartFarming;

public partial class SmartFarmingView
{
    private async void ImportNodeOdmZip_Click(object sender, RoutedEventArgs e)
    {
        using var fileDialog = new WinForms.OpenFileDialog
        {
            Title = "NodeODM all.zip importieren",
            Filter = "ZIP-Archive (*.zip)|*.zip|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_lastNodeOdmZipPath) &&
            File.Exists(_lastNodeOdmZipPath))
        {
            fileDialog.InitialDirectory =
                Path.GetDirectoryName(_lastNodeOdmZipPath);
            fileDialog.FileName =
                Path.GetFileName(_lastNodeOdmZipPath);
        }

        if (fileDialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        using var folderDialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner zum sicheren Extrahieren des NodeODM-Ergebnisses auswählen",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.SmartFarmingProcessing) ??
                Path.GetDirectoryName(fileDialog.FileName) ??
                ""
        };

        if (folderDialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        GenerateOdmFieldProductsButton.IsEnabled = false;
        OdmFieldProductProgress.Value = 0;
        OdmImportedStatusText.Text =
            "NodeODM ZIP wird sicher extrahiert und das Orthomosaik wird geprüft …";

        try
        {
            var zipPath =
                Path.GetFullPath(fileDialog.FileName);

            var extractionRoot =
                Path.Combine(
                    folderDialog.SelectedPath,
                    Path.GetFileNameWithoutExtension(zipPath) +
                    "_extracted");

            var extracted =
                await Task.Run(() =>
                    OdmResultImporter.ExtractSafely(
                        zipPath,
                        extractionRoot));

            var orthophoto =
                OdmResultImporter.FindOrthophoto(
                    extracted);

            var info =
                await OdmOrthophotoInspector.InspectAsync(
                    orthophoto);

            _odmImported =
                new OdmImportedResult(
                    zipPath,
                    extracted,
                    orthophoto,
                    info,
                    info.Warnings);

            _lastNodeOdmZipPath =
                zipPath;

            string? registrationError = null;

            if (DroneDashProjectSession.IsOpen)
            {
                try
                {
                    await DroneDashProjectSession.RegisterFileAsync(
                        zipPath,
                        ProjectArtifactKind.NodeOdmResultArchive);

                    await DroneDashProjectSession.RegisterDirectoryAsync(
                        extracted,
                        ProjectArtifactKind.ProcessingWorkspace);
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            OdmImportedStatusText.Text =
                $"Importiert: {zipPath}\n" +
                $"Orthomosaik: {info.SummaryText}\n" +
                $"CRS: {info.CoordinateSystemName ?? "—"}\n" +
                $"Bandbeschreibungen: {string.Join(" | ", info.BandDescriptions)}" +
                (info.Warnings.Count > 0
                    ? "\nHinweise: " +
                      string.Join(" | ", info.Warnings)
                    : "") +
                (registrationError is not null
                    ? $"\nProjektregistrierung fehlgeschlagen: {registrationError}"
                    : "");

            GenerateOdmFieldProductsButton.IsEnabled = true;
            OdmFieldProductStatusText.Text =
                "ODM-Orthomosaik bereit für Feldprodukte.";
        }
        catch (Exception ex)
        {
            _odmImported = null;
            OdmImportedStatusText.Text =
                $"NodeODM-Import fehlgeschlagen: {ex.Message}";
            System.Windows.MessageBox.Show(
                ex.Message,
                "ODM-Feldprodukte",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void GenerateOdmFieldProducts_Click(object sender, RoutedEventArgs e)
    {
        if (_odmImported is null)
        {
            System.Windows.MessageBox.Show(
                "Zuerst ein NodeODM all.zip importieren.",
                "ODM-Feldprodukte",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        NdviScoutingZoneSettings zoneSettings;

        try
        {
            zoneSettings =
                new NdviScoutingZoneSettings(
                    ParseFlexibleDouble(
                        ZoneThreshold1Box.Text,
                        "Z1/Z2"),
                    ParseFlexibleDouble(
                        ZoneThreshold2Box.Text,
                        "Z2/Z3"),
                    ParseFlexibleDouble(
                        ZoneThreshold3Box.Text,
                        "Z3/Z4"),
                    ParseFlexibleDouble(
                        ZoneThreshold4Box.Text,
                        "Z4/Z5"));

            zoneSettings.Validate();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "NDVI-Scouting-Zonen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _toolchain ??=
            await LocalImageToolchain.ProbeAsync();

        if (!_toolchain.Otb.Available ||
            string.IsNullOrWhiteSpace(_toolchain.OtbBandMath) ||
            string.IsNullOrWhiteSpace(_toolchain.OtbBandMathX))
        {
            System.Windows.MessageBox.Show(
                "Orfeo ToolBox mit BandMath und BandMathX wird für die georeferenzierten Feldprodukte benötigt.",
                "ODM-Feldprodukte",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        using var folderDialog =
            new WinForms.FolderBrowserDialog
            {
                Description = "Zielordner für georeferenzierte Smart-Farming-Feldprodukte auswählen",
                ShowNewFolderButton = true,
                SelectedPath =
                    ProjectWorkspaceLayout.TryGetActiveFolder(
                        ProjectWorkspaceFolder.SmartFarmingResults) ??
                    _odmImported.ExtractedRoot
            };

        if (folderDialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        var workspace =
            Path.Combine(
                folderDialog.SelectedPath,
                "DroneDash_FieldProducts");

        var plan =
            OdmFieldProductPlanBuilder.Build(
                _odmImported.Orthophoto,
                workspace,
                _toolchain,
                zoneSettings);

        var output =
            LocalProcessingPlanExporter.Export(
                plan);

        string? planRegistrationError = null;

        if (DroneDashProjectSession.IsOpen)
        {
            try
            {
                await DroneDashProjectSession.RegisterFileAsync(
                    output.JsonPath,
                    ProjectArtifactKind.LocalProcessingPlan);

                await DroneDashProjectSession.RegisterDirectoryAsync(
                    workspace,
                    ProjectArtifactKind.ProcessingWorkspace);
            }
            catch (Exception ex)
            {
                planRegistrationError = ex.Message;
            }
        }

        var confirmation =
            System.Windows.MessageBox.Show(
                "Georeferenzierte Feldprodukte jetzt berechnen?\n\n" +
                $"{_odmImported.Orthophoto.BandMap.ToDisplayText()}\n" +
                $"{zoneSettings.LegendText}\n\n" +
                "Die NDVI-Zonen sind ausschließlich Scouting-Klassen und keine automatische Betriebsmittel-Empfehlung.",
                "ODM-Feldprodukte",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (confirmation !=
            MessageBoxResult.Yes)
        {
            OdmFieldProductStatusText.Text =
                $"Processing-Plan erzeugt: {output.JsonPath}" +
                (planRegistrationError is not null
                    ? $" · Projektregistrierung fehlgeschlagen: {planRegistrationError}"
                    : "");
            return;
        }

        _odmFieldProductCts?.Dispose();
        _odmFieldProductCts =
            new CancellationTokenSource();

        GenerateOdmFieldProductsButton.IsEnabled =
            false;
        CancelOdmFieldProductsButton.IsEnabled =
            true;
        OdmFieldProductProgress.Value = 0;
        OdmFieldProductLogText.Clear();

        ProjectProcessingCoordinator.BeginActive(
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
            "Georeferenzierte Feldprodukte werden berechnet.",
            "field-products");

        var progress =
            new Progress<LocalProcessingProgress>(
                value =>
                {
                    OdmFieldProductProgress.Value =
                        value.Percent;

                    OdmFieldProductStatusText.Text =
                        $"{value.CompletedSteps}/{value.TotalSteps} · {value.Message}";

                    ProjectProcessingCoordinator.ReportActive(
                        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                        value.Percent,
                        value.Message,
                        value.CurrentStepId);
                });

        try
        {
            var result =
                await LocalProcessingRunner.RunAsync(
                    plan,
                    progress,
                    line =>
                        Dispatcher.BeginInvoke(() =>
                        {
                            ProjectProcessingCoordinator.LogActive(
                                ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                                line);

                            OdmFieldProductLogText.AppendText(
                                line +
                                Environment.NewLine);

                            OdmFieldProductLogText.ScrollToEnd();
                        }),
                    _odmFieldProductCts.Token);

            OdmFieldProductProgress.Value =
                result.Success
                    ? 100
                    : OdmFieldProductProgress.Value;

            if (result.Success)
            {
                var manifest =
                    OdmFieldProductExporter.ExportManifest(
                        workspace,
                        _odmImported.Orthophoto,
                        zoneSettings);

                var productPaths =
                    plan.Steps
                        .Select(step => step.OutputPath)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Cast<string>()
                        .Prepend(manifest)
                        .ToArray();

                var registered = 0;
                string? registrationError = null;

                if (DroneDashProjectSession.IsOpen)
                {
                    try
                    {
                        registered =
                            await DroneDashProjectSession.RegisterFilesAsync(
                                productPaths);

                        foreach (var productPath in productPaths)
                        {
                            ProjectProcessingCoordinator.RecordOutputActive(
                                ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                                productPath,
                                ProjectArtifactService.ClassifyFile(
                                    productPath));
                        }

                        ProjectProcessingCoordinator.CompleteActive(
                            ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                            "Georeferenzierte Feldprodukte erzeugt und im Projekt registriert.");
                    }
                    catch (Exception ex)
                    {
                        registrationError = ex.Message;
                    }
                }

                OdmFieldProductStatusText.Text =
                    $"Feldprodukte abgeschlossen · {manifest} · Log: {result.LogPath}" +
                    (registered > 0
                        ? $" · {registered} Ergebnisartefakt(e) im aktiven DroneDash-Projekt registriert"
                        : registrationError is not null
                            ? $" · Projektregistrierung fehlgeschlagen: {registrationError}"
                            : "");
            }
            else
            {
                if (result.Canceled)
                {
                    ProjectProcessingCoordinator.CancelActive(
                        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                        "Feldproduktberechnung wurde abgebrochen.");
                }
                else
                {
                    ProjectProcessingCoordinator.FailActive(
                        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                        $"Fehler bei {result.FailedStepId} · ExitCode {result.ExitCode}");
                }

                OdmFieldProductStatusText.Text =
                    result.Canceled
                        ? $"Feldproduktberechnung abgebrochen · Log: {result.LogPath}"
                        : $"Feldproduktberechnung fehlgeschlagen bei {result.FailedStepId} · ExitCode {result.ExitCode} · Log: {result.LogPath}";
            }
        }
        catch (Exception ex)
        {
            ProjectProcessingCoordinator.FailActive(
                ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
                ex.Message);

            OdmFieldProductStatusText.Text =
                $"Feldproduktberechnung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            CancelOdmFieldProductsButton.IsEnabled =
                false;

            GenerateOdmFieldProductsButton.IsEnabled =
                _odmImported is not null;
        }
    }

    private void CancelOdmFieldProducts_Click(object sender, RoutedEventArgs e)
    {
        _odmFieldProductCts?.Cancel();

        ProjectProcessingCoordinator.CancelActive(
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
            "Abbruch der Feldproduktberechnung angefordert.");

        OdmFieldProductStatusText.Text =
            "Abbruch angefordert …";
    }
}
