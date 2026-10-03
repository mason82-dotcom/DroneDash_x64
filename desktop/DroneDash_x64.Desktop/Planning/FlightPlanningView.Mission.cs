using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using DroneDash_x64.Desktop.Project;
using WinForms = System.Windows.Forms;


namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView
{
    private void ValidateKmz_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "DJI WPML/KMZ prüfen",
            Filter = "DJI Wayline (*.kmz)|*.kmz|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var report = DjiKmzValidator.Validate(dialog.FileName);
        RenderValidationReport(report, dialog.FileName);

        if (!report.IsValid)
        {
            System.Windows.MessageBox.Show(
                RenderValidationReportText(report),
                "DJI KMZ Validierung",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }


    private void GenerateGrid_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _plan = PhotogrammetryPlanner.Generate(_geometry, ReadSettings());
            RenderPlanSummary(_plan);
            RenderMap(fitBounds: true);
            PlanningStatusText.Text = "Flugroute berechnet.";
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(
                ex.Message,
                "Flugplanung",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void ExportKmz_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _plan ??= PhotogrammetryPlanner.Generate(_geometry, ReadSettings());

            using var dialog = new WinForms.SaveFileDialog
            {
                Title = "DJI WPML/KMZ exportieren",
                Filter = "DJI Wayline (*.kmz)|*.kmz",
                DefaultExt = "kmz",
                AddExtension = true,
                FileName = SafeFileName(_plan.Settings.Name) + ".kmz"
            };

            if (ProjectWorkspaceLayout.TryGetActiveFolder(
                    ProjectWorkspaceFolder.Planning) is string planningFolder)
            {
                dialog.InitialDirectory =
                    planningFolder;
            }

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            DjiWpmlExporter.ExportKmz(dialog.FileName, _plan);
            var report = DjiKmzValidator.Validate(dialog.FileName);
            RenderValidationReport(report, dialog.FileName);

            var registered = false;
            string? registrationError = null;

            if (report.IsValid &&
                DroneDashProjectSession.IsOpen)
            {
                try
                {
                    registered = await DroneDashProjectSession.RegisterFileAsync(
                        dialog.FileName,
                        ProjectArtifactKind.DjiWaylineKmz);
                }
                catch (Exception ex)
                {
                    registrationError = ex.Message;
                }
            }

            PlanningStatusText.Text = report.IsValid
                ? $"DJI KMZ exportiert und validiert: {dialog.FileName}" +
                  (registered
                      ? " · im aktiven DroneDash-Projekt registriert"
                      : registrationError is not null
                          ? $" · Projektregistrierung fehlgeschlagen: {registrationError}"
                          : "")
                : $"DJI KMZ exportiert, aber Validierung meldet Fehler: {dialog.FileName}";

            if (!report.IsValid)
            {
                System.Windows.MessageBox.Show(
                    RenderValidationReportText(report),
                    "DJI KMZ Export",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            PlanningStatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(
                ex.Message,
                "DJI KMZ Export",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    private void RenderPlanSummary(FlightPlanResult plan)
    {
        var secondary = plan.SecondaryGsdCentimeters is double ms
            ? $"\nMS GSD: {ms:F2} cm/px"
            : "";

        var geometryMetric = plan.Settings.Mode == FlightPlanMode.MappingStrip
            ? $"Korridorfläche (geschätzt): {plan.AreaSquareMeters / 10_000d:F2} ha"
            : $"Fläche: {plan.AreaSquareMeters / 10_000d:F2} ha";

        PlanningStatsText.Text =
            $"{plan.CameraProfile}\n" +
            $"Modus: {plan.Settings.Mode}\n" +
            $"{geometryMetric}\n" +
            $"RGB/Wide GSD: {plan.GsdCentimeters:F2} cm/px{secondary}\n" +
            $"Footprint: {plan.FootprintWidthMeters:F1} × {plan.FootprintHeightMeters:F1} m\n" +
            $"Linienabstand: {plan.LineSpacingMeters:F1} m\n" +
            $"Fotoabstand: {plan.PhotoSpacingMeters:F1} m\n" +
            $"Waylines: {plan.Passes.Count}\n" +
            $"Segmente: {plan.Segments.Count}\n" +
            $"Terrain Follow: {(plan.Settings.TerrainFollowEnabled ? "ja" : "nein")}\n" +
            $"Smart Oblique: {(plan.Settings.SmartObliqueEnabled ? "ja" : "nein")}\n" +
            $"Flugstrecke inkl. Transits: {plan.FlightDistanceMeters / 1000d:F2} km\n" +
            $"Geschätzte Bilder: {plan.EstimatedPhotos:N0}\n" +
            $"Reine Flugzeit: {plan.EstimatedFlightTime:hh\\:mm\\:ss}\n\n" +
            plan.SurveyNote;
    }

    private void RenderValidationReport(
        DjiKmzValidationReport report,
        string path)
    {
        KmzValidationText.Text =
            $"{Path.GetFileName(path)}\n{RenderValidationReportText(report)}";
    }

    private static string RenderValidationReportText(
        DjiKmzValidationReport report)
    {
        var lines = new List<string> { report.Summary };

        if (report.Errors.Count > 0)
        {
            lines.Add("");
            lines.Add("Fehler:");
            lines.AddRange(report.Errors.Select(e => "• " + e));
        }

        if (report.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Hinweise:");
            lines.AddRange(report.Warnings.Select(w => "• " + w));
        }

        return string.Join(Environment.NewLine, lines);
    }

}
