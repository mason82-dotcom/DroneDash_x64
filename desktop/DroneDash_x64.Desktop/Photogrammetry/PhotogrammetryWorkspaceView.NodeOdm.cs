using System.IO;
using System.Windows;
using System.Windows.Controls;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.SmartFarming.Odm;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Photogrammetry;

public partial class PhotogrammetryWorkspaceView
{
    private NodeOdmServerInfo? _odmServer;
    private IReadOnlySet<string>? _odmOptionNames;
    private string? _odmTaskUuid;
    private bool _odmTaskCompleted;
    private CancellationTokenSource? _odmMonitorCts;
    private OdmElevationProducts? _odmProducts;

    private void InitializeOdm()
    {
        OdmEndpointBox.Text = NodeOdmClient.EndpointFromEnvironment();
        var token = NodeOdmClient.TokenFromEnvironment();
        if (!string.IsNullOrWhiteSpace(token))
            OdmTokenBox.Password = token;
    }

    private NodeOdmClient CreateOdmClient() =>
        new(OdmEndpointBox.Text.Trim(), OdmTokenBox.Password);

    private void UpdateOdmButtons()
    {
        var running = _odmTaskUuid is not null && !_odmTaskCompleted && _odmMonitorCts is not null;

        StartOdmButton.IsEnabled =
            _odmServer?.Available == true &&
            _odmOptionNames is not null &&
            _dataset?.Images.Count > 0 &&
            !running;
        CancelOdmButton.IsEnabled = running;
        ImportOdmButton.IsEnabled = _odmTaskUuid is not null && _odmTaskCompleted;
        UpdateColmapButtons();
    }

    private PhotogrammetryOdmPreset SelectedOdmPreset() =>
        Enum.TryParse<PhotogrammetryOdmPreset>(
            (OdmPresetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var preset)
            ? preset
            : PhotogrammetryOdmPreset.Standard;

    private async void CheckOdmServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OdmStatusText.Text = "NodeODM wird geprüft …";
            using var client = CreateOdmClient();
            _odmServer = await client.ProbeAsync();
            _odmOptionNames = null;

            if (_odmServer.Available)
            {
                _odmOptionNames = await client.GetSupportedOptionNamesAsync();
                var hasDem = _odmOptionNames.Contains("dsm") && _odmOptionNames.Contains("dtm");
                OdmStatusText.Text =
                    $"NodeODM {_odmServer.ApiVersion ?? "?"} · Engine {_odmServer.Engine ?? "?"} {_odmServer.EngineVersion ?? "?"} · " +
                    $"Queue {_odmServer.TaskQueueCount?.ToString() ?? "—"} · CPU {_odmServer.CpuCores?.ToString() ?? "—"} · " +
                    (hasDem ? "DSM/DTM verfügbar" : "⚠ DSM/DTM-Optionen fehlen");
            }
            else
            {
                OdmStatusText.Text = $"NodeODM nicht erreichbar: {_odmServer.Detail}";
            }
        }
        catch (Exception ex)
        {
            _odmServer = null;
            _odmOptionNames = null;
            OdmStatusText.Text = $"NodeODM-Prüfung fehlgeschlagen: {ex.Message}";
        }

        UpdateOdmButtons();
    }

    private async void StartOdmTask_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _odmOptionNames is null)
            return;

        try
        {
            var inputs = PhotogrammetryOdmPlan.SelectInputFiles(_dataset);
            if (inputs.Files.Count < 3)
            {
                throw new InvalidOperationException(
                    $"Für eine Rekonstruktion sind mindestens 3 georeferenzierte RGB-Bilder nötig (gefunden: {inputs.Files.Count}).");
            }

            var preset = SelectedOdmPreset();
            var options = PhotogrammetryOdmPlan.BuildOptions(preset, _odmOptionNames);
            var notes = inputs.Notes.Concat(options.Notes).ToArray();

            var confirmation = System.Windows.MessageBox.Show(
                $"{inputs.Files.Count} RGB-Bilder an {OdmEndpointBox.Text.Trim()} senden und DSM, DTM, Orthomosaik und Punktwolke berechnen?" +
                (notes.Length > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, notes) : ""),
                "NodeODM-Task starten",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.OK)
                return;

            _odmTaskUuid = null;
            _odmTaskCompleted = false;
            OdmLogText.Clear();
            OdmProductsText.Text = "";
            foreach (var note in notes)
                AppendOdmLog(note);

            var taskName =
                $"DroneDash Photogrammetrie {Path.GetFileName(_dataset.SourceFolder)} {DateTime.Now:yyyy-MM-dd HH:mm}";

            var upload = new Progress<NodeOdmUploadProgress>(value =>
            {
                OdmProgress.Value = Math.Clamp(value.Percent, 0, 100);
                OdmStatusText.Text =
                    $"Upload {value.UploadedFiles}/{value.TotalFiles} · {value.CurrentFile}";
            });

            StartOdmButton.IsEnabled = false;
            using (var client = CreateOdmClient())
            {
                _odmTaskUuid = await client.CreateTaskAsync(
                    inputs.Files,
                    taskName,
                    options.Options,
                    upload);
            }

            AppendOdmLog($"Task {_odmTaskUuid} gestartet ({preset}, {inputs.Files.Count} Bilder).");

            _odmMonitorCts?.Cancel();
            _odmMonitorCts?.Dispose();
            _odmMonitorCts = new CancellationTokenSource();
            UpdateOdmButtons();

            await MonitorOdmTaskAsync(_odmMonitorCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OdmStatusText.Text = $"NodeODM-Task fehlgeschlagen: {ex.Message}";
            AppendOdmLog($"FEHLER {ex.Message}");
        }
        finally
        {
            _odmMonitorCts?.Dispose();
            _odmMonitorCts = null;
            UpdateOdmButtons();
        }
    }

    private async Task MonitorOdmTaskAsync(CancellationToken cancellationToken)
    {
        if (_odmTaskUuid is null)
            return;

        string? lastOutput = null;

        while (true)
        {
            using var client = CreateOdmClient();
            var info = await client.GetTaskInfoAsync(_odmTaskUuid, cancellationToken);

            OdmProgress.Value = Math.Clamp(info.Progress, 0, 100);
            OdmStatusText.Text =
                $"Task {info.Uuid} · {info.StatusText} · {info.Progress:F1}% · Bilder {info.ImagesCount} · " +
                $"Laufzeit {TimeSpan.FromMilliseconds(info.ProcessingTimeMilliseconds):g}";

            try
            {
                var output = await client.GetTaskOutputAsync(info.Uuid, 0, cancellationToken);
                if (!string.Equals(output, lastOutput, StringComparison.Ordinal))
                {
                    OdmLogText.Text = output;
                    OdmLogText.ScrollToEnd();
                    lastOutput = output;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The console log is informational; status polling continues.
            }

            if (info.IsTerminal)
            {
                _odmTaskCompleted = info.IsCompleted;
                OdmStatusText.Text += info.IsCompleted
                    ? " · fertig, Ergebnis kann geladen werden."
                    : " · beendet ohne Ergebnis.";
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async void CancelOdmTask_Click(object sender, RoutedEventArgs e)
    {
        if (_odmTaskUuid is null)
            return;

        try
        {
            using var client = CreateOdmClient();
            await client.CancelTaskAsync(_odmTaskUuid);
            AppendOdmLog($"Abbruch für Task {_odmTaskUuid} angefordert.");
        }
        catch (Exception ex)
        {
            OdmStatusText.Text = $"Abbruch fehlgeschlagen: {ex.Message}";
        }
    }

    private async void ImportOdmResult_Click(object sender, RoutedEventArgs e)
    {
        if (_odmTaskUuid is null)
            return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für das NodeODM-Ergebnis (all.zip) auswählen",
            ShowNewFolderButton = true,
            SelectedPath =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryProcessing) ??
                _sourceFolder ??
                ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            ImportOdmButton.IsEnabled = false;
            var download = new Progress<double>(value =>
            {
                OdmProgress.Value = Math.Clamp(value, 0, 100);
                OdmStatusText.Text = $"Ergebnis wird heruntergeladen: {value:F1}%";
            });

            string zipPath;
            using (var client = CreateOdmClient())
            {
                zipPath = await client.DownloadAllAsync(_odmTaskUuid, dialog.SelectedPath, download);
            }

            await ImportOdmZipAsync(zipPath);
        }
        catch (Exception ex)
        {
            OdmStatusText.Text = $"Download/Import fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            UpdateOdmButtons();
        }
    }

    private async void ImportExistingOdmZip_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "NodeODM-Ergebnis (all.zip) importieren",
            Filter = "NodeODM-Ergebnis (*.zip)|*.zip|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory =
                ProjectWorkspaceLayout.TryGetActiveFolder(ProjectWorkspaceFolder.PhotogrammetryProcessing) ?? ""
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            await ImportOdmZipAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            OdmStatusText.Text = $"Import fehlgeschlagen: {ex.Message}";
        }
    }

    private async Task ImportOdmZipAsync(string zipPath)
    {
        var target = UniqueExtractionFolder(zipPath);
        OdmStatusText.Text = $"Ergebnis wird entpackt nach {target} …";
        OdmProgress.IsIndeterminate = true;

        OdmElevationProducts products;
        try
        {
            products = await Task.Run(() =>
            {
                var root = OdmResultImporter.ExtractSafely(zipPath, target);
                return OdmElevationProducts.Locate(root);
            });
        }
        finally
        {
            OdmProgress.IsIndeterminate = false;
        }

        var lines = new List<string>();
        void Add(string label, string? path)
        {
            lines.Add(path is null ? $"{label}: —" : $"{label}: {Path.GetRelativePath(products.ResultFolder, path)}");
        }

        Add("DSM", products.DsmPath);
        Add("DTM", products.DtmPath);
        Add("Orthomosaik", products.OrthophotoPath);
        Add(
            products.CloudFormat is { } format ? $"Punktwolke ({format.ToString().ToUpperInvariant()})" : "Punktwolke",
            products.PointCloudPath);
        lines.AddRange(products.Warnings.Select(warning => "⚠ " + warning));

        _odmProducts = products;
        ShowDsmButton.IsEnabled = products.DsmPath is not null;
        ShowDtmButton.IsEnabled = products.DtmPath is not null;
        ShowPointCloudButton.IsEnabled = products.PointCloudPath is not null;
        ChmButton.IsEnabled = products.DsmPath is not null && products.DtmPath is not null;

        var registration = await RegisterOdmProductsAsync(zipPath, products);
        if (registration is not null)
            lines.Add(registration);

        OdmProductsText.Text = string.Join(Environment.NewLine, lines);
        OdmStatusText.Text = products.HasAny
            ? $"ODM-Ergebnis importiert: {products.ResultFolder}"
            : "Im Archiv wurden keine ODM-Produkte gefunden.";
        AppendOdmLog(OdmStatusText.Text);
    }

    private static async Task<string?> RegisterOdmProductsAsync(string zipPath, OdmElevationProducts products)
    {
        if (!DroneDashProjectSession.IsOpen)
            return null;

        try
        {
            var count = 0;
            if (await DroneDashProjectSession.RegisterFileAsync(zipPath, ProjectArtifactKind.NodeOdmResultArchive))
                count++;

            (string? Path, ProjectArtifactKind Kind)[] outputs =
            [
                (products.DsmPath, ProjectArtifactKind.ElevationModel),
                (products.DtmPath, ProjectArtifactKind.ElevationModel),
                (products.OrthophotoPath, ProjectArtifactKind.Orthomosaic),
                (products.PointCloudPath, ProjectArtifactKind.PointCloud)
            ];

            foreach (var (path, kind) in outputs)
            {
                if (path is not null && await DroneDashProjectSession.RegisterFileAsync(path, kind))
                    count++;
            }

            return $"{count} Artefakte im aktiven DroneDash-Projekt registriert.";
        }
        catch (Exception ex)
        {
            return $"⚠ Projektregistrierung fehlgeschlagen: {ex.Message}";
        }
    }

    private static string UniqueExtractionFolder(string zipPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(zipPath);
        var candidate = Path.Combine(directory, baseName);

        if (!Directory.Exists(candidate) || !Directory.EnumerateFileSystemEntries(candidate).Any())
            return candidate;

        return Path.Combine(directory, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}");
    }

    private void AppendOdmLog(string line)
    {
        OdmLogText.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        OdmLogText.ScrollToEnd();
    }
}
