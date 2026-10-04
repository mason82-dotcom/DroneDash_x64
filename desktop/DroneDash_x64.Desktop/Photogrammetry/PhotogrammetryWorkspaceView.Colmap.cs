using System.IO;
using System.Windows;
using System.Windows.Controls;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Photogrammetry;

public partial class PhotogrammetryWorkspaceView
{
    private ColmapStatus? _colmap;
    private bool _colmapPythonReady;
    private CancellationTokenSource? _colmapCts;

    private void UpdateColmapButtons()
    {
        var running = _colmapCts is not null;
        StartColmapButton.IsEnabled =
            _colmap?.CanReconstructDense == true &&
            _colmapPythonReady &&
            _dataset?.Images.Count > 0 &&
            !running;
        CancelColmapButton.IsEnabled = running;
    }

    private PhotogrammetryOdmPreset SelectedColmapPreset() =>
        Enum.TryParse<PhotogrammetryOdmPreset>(
            (ColmapPresetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var preset)
            ? preset
            : PhotogrammetryOdmPreset.Standard;

    private async void CheckColmap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ColmapStatusText.Text = "COLMAP wird geprüft …";
            _colmap = await ColmapToolchain.ProbeAsync();
            _colmapPythonReady = false;

            var lines = new List<string> { $"{_colmap.Summary} · {_colmap.Executable ?? "—"}", _colmap.Detail };
            if (_colmap.Available)
            {
                var toolchain = await RequireToolchainAsync(requireGdal: true);
                var (ready, detail) = await ColmapToolchain.ProbePythonAsync(toolchain.PythonExecutable!);
                _colmapPythonReady = ready;
                lines.Add((ready ? "" : "⚠ ") + detail);
            }

            ColmapStatusText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            ColmapStatusText.Text = $"COLMAP-Prüfung fehlgeschlagen: {ex.Message}";
        }

        UpdateColmapButtons();
    }

    private async void StartColmap_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _colmap is null || _toolchain?.PythonExecutable is null)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Arbeitsordner für die COLMAP-Berechnung wählen (braucht viel Speicherplatz)",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryProcessing) ??
                Path.GetDirectoryName(_dataset.SourceFolder) ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        ColmapProcessingPlan plan;
        try
        {
            var workspace = Path.Combine(dialog.SelectedPath, $"colmap_{DateTime.Now:yyyyMMdd_HHmmss}");
            plan = ColmapPlanBuilder.Build(
                _dataset,
                _colmap,
                _toolchain.PythonExecutable,
                _toolchain.OpenCvWorkerPath,
                workspace,
                SelectedColmapPreset());
        }
        catch (Exception ex)
        {
            ColmapStatusText.Text = $"COLMAP-Plan nicht möglich: {ex.Message}";
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"{plan.Images.Count} RGB-Bilder lokal mit COLMAP (CUDA) rekonstruieren?" +
            Environment.NewLine + $"Arbeitsordner: {plan.Plan.Workspace}" +
            (plan.Plan.Warnings.Count > 0
                ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, plan.Plan.Warnings)
                : ""),
            "COLMAP lokal berechnen",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.OK)
            return;

        _colmapCts = new CancellationTokenSource();
        UpdateColmapButtons();
        ColmapLogText.Clear();
        ColmapProductsText.Text = "";
        foreach (var warning in plan.Plan.Warnings)
            AppendColmapLog("⚠ " + warning);

        try
        {
            await Task.Run(() => ColmapPlanBuilder.PrepareWorkspace(plan));

            var progress = new Progress<LocalProcessingProgress>(value =>
            {
                ColmapProgress.Value = Math.Clamp(value.Percent, 0, 100);
                ColmapStatusText.Text =
                    $"Schritt {Math.Min(value.CompletedSteps + 1, value.TotalSteps)}/{value.TotalSteps} · {value.Message}";
            });

            var result = await LocalProcessingRunner.RunAsync(
                plan.Plan,
                progress,
                line => Dispatcher.BeginInvoke(() => AppendColmapLog(line)),
                _colmapCts.Token);

            if (result.Canceled)
            {
                ColmapStatusText.Text = "COLMAP-Berechnung abgebrochen.";
                return;
            }

            if (!result.Success)
            {
                ColmapStatusText.Text =
                    $"COLMAP-Schritt '{result.FailedStepId}' fehlgeschlagen (ExitCode {result.ExitCode?.ToString() ?? "—"}). " +
                    $"Details im Log: {result.LogPath}";
                return;
            }

            await ShowColmapProductsAsync(plan);
        }
        catch (Exception ex)
        {
            ColmapStatusText.Text = $"COLMAP-Berechnung fehlgeschlagen: {ex.Message}";
            AppendColmapLog("FEHLER " + ex.Message);
        }
        finally
        {
            _colmapCts?.Dispose();
            _colmapCts = null;
            UpdateColmapButtons();
        }
    }

    private void CancelColmap_Click(object sender, RoutedEventArgs e)
    {
        _colmapCts?.Cancel();
        AppendColmapLog("Abbruch angefordert.");
    }

    private async Task ShowColmapProductsAsync(ColmapProcessingPlan plan)
    {
        var report = ColmapProductsReport.TryRead(plan.ProductsReportPath);
        var warnings = new List<string>(plan.Plan.Warnings);
        if (report?.Warning is { } qualityWarning)
            warnings.Add(qualityWarning);

        var products = new OdmElevationProducts(
            plan.ProductsFolder,
            File.Exists(plan.DsmPath) ? plan.DsmPath : null,
            null,
            null,
            File.Exists(plan.PointCloudPath) ? plan.PointCloudPath : null,
            File.Exists(plan.PointCloudPath) ? PointCloudFormat.Laz : null,
            warnings);

        _odmProducts = products;
        UpdateElevationButtons();
        ColmapShowDsmButton.IsEnabled = products.DsmPath is not null;
        ColmapShowPointCloudButton.IsEnabled = products.PointCloudPath is not null;

        var lines = new List<string>();
        if (report is not null)
            lines.Add(report.SummaryText);
        lines.Add($"DSM: {products.DsmPath ?? "—"}");
        lines.Add($"Punktwolke (LAZ): {products.PointCloudPath ?? "—"}");
        lines.AddRange(warnings.Select(warning => "⚠ " + warning));
        lines.Add("DTM ableiten, Bestandshöhe und DSM-Vergleich: Schaltflächen im Bereich „DSM / DTM / Punktwolke mit NodeODM“.");

        if (DroneDashProjectSession.IsOpen)
        {
            try
            {
                var count = 0;
                if (products.DsmPath is not null &&
                    await DroneDashProjectSession.RegisterFileAsync(products.DsmPath, ProjectArtifactKind.ElevationModel))
                    count++;
                if (products.PointCloudPath is not null &&
                    await DroneDashProjectSession.RegisterFileAsync(products.PointCloudPath, ProjectArtifactKind.PointCloud))
                    count++;
                lines.Add($"{count} Artefakte im aktiven DroneDash-Projekt registriert.");
            }
            catch (Exception ex)
            {
                lines.Add($"⚠ Projektregistrierung fehlgeschlagen: {ex.Message}");
            }
        }

        ColmapProductsText.Text = string.Join(Environment.NewLine, lines);
        ColmapStatusText.Text = $"COLMAP-Berechnung abgeschlossen: {plan.ProductsFolder}";
        ColmapProgress.Value = 100;
    }

    private void AppendColmapLog(string line)
    {
        // Keep the view responsive on long runs: the full log stays in local-processing.log.
        if (ColmapLogText.LineCount > 2000)
            ColmapLogText.Text = ColmapLogText.Text[(ColmapLogText.Text.Length / 2)..];

        ColmapLogText.AppendText(line + Environment.NewLine);
        ColmapLogText.ScrollToEnd();
    }
}
