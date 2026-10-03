using System.Linq;
using System.IO;
using System.Text;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DroneDash_x64.Desktop.Api;
using DroneDash_x64.Desktop.Imaging;
using DroneDash_x64.Desktop.Models;
using DroneDash_x64.Desktop.Project;
using DroneDash_x64.Desktop.Thermal;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop;

public partial class MainWindow
{
    private async void RefreshMedia_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfigureApi();
            MediaInfoText.Text = "Aircraft-Medien werden gelesen …";
            var items = await _api.GetMediaAsync();

            _media.Clear();
            foreach (var item in items.OrderByDescending(x => x.Index))
            {
                _media.Add(item);
            }

            MediaInfoText.Text = $"{_media.Count} Datei(en)";
            FooterText.Text = "Medienliste aktualisiert.";
            AddEvent("INFO", "Media", $"Medienliste aktualisiert · {_media.Count} Datei(en).");
        }
        catch (Exception ex)
        {
            MediaInfoText.Text = "Fehler beim Laden.";
            ShowError("Medienliste konnte nicht geladen werden", ex);
        }
    }

    private async void PreviewMedia_Click(object sender, RoutedEventArgs e)
    {
        if (MediaGrid.SelectedItem is not MediaItemDto item)
        {
            System.Windows.MessageBox.Show(this, "Ein Bild in der Medienliste auswählen.", "Bildviewer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await PreviewRemoteMediaAsync(item);
    }

    private async void MediaGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MediaGrid.SelectedItem is MediaItemDto item)
            await PreviewRemoteMediaAsync(item);
    }

    private async Task PreviewRemoteMediaAsync(MediaItemDto item)
    {
        if (!IsSupportedImageName(item.Name))
        {
            System.Windows.MessageBox.Show(this,
                $"„{item.Name}“ ist kein unterstütztes Bildformat. Videos bleiben über die Download-Funktion verfügbar.",
                "Bildviewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            ConfigureApi();
            var cachePath = PreviewCachePath(item);
            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p * 100d;
                MediaInfoText.Text = $"Vorschau {item.Name} · {p:P0}";
            });

            await _api.DownloadMediaAsync(item.Index, cachePath, progress);
            LoadImagePreview(cachePath, item);
            DownloadProgress.Value = 100;
            MediaInfoText.Text = $"Vorschau geladen · {_imageMetadata.Count} Metadatenfelder";
            AddEvent("INFO", "Media", $"Bildvorschau geladen: {item.Name}.");
        }
        catch (Exception ex)
        {
            ShowError("Bildvorschau konnte nicht geladen werden", ex);
        }
    }

    private void OpenM3tFixture_Click(object sender, RoutedEventArgs e)
    {
        var fixture = FindRepositoryFixture("DJI_20261002154302_0001_T.JPG");
        if (fixture is null)
        {
            System.Windows.MessageBox.Show(this,
                "Das M3T-Testbild wurde nicht gefunden. Die Funktion ist für einen lokalen Clone des DroneDash_x64-Repositories gedacht.",
                "M3T Testbild",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            LoadImagePreview(fixture, null);
            MediaInfoText.Text = $"M3T Thermal-Fixture geladen · {_imageMetadata.Count} Metadatenfelder";
            AddEvent("INFO", "Thermal", "Reales M3T-R-JPEG-Fixture aus dem Repository geladen.");

            if (_thermalSdk.IsAvailable)
            {
                AnalyzeCurrentThermalImage();
            }
            else
            {
                ThermalSummaryText.Text =
                    "Reales M3T-R-JPEG geladen. DJI Thermal SDK v1.8 ist lokal noch nicht verfügbar; Metadatenanalyse ist aktiv.";
            }
        }
        catch (Exception ex)
        {
            ShowError("M3T-Testbild konnte nicht geöffnet werden", ex);
        }
    }

    private static string? FindRepositoryFixture(string fileName)
    {
        var roots = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            var current = new DirectoryInfo(Path.GetFullPath(root));
            for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private void OpenLocalImage_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Bild für Vorschau und Metadaten öffnen",
            Filter = "Bild- und DNG-Dateien|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif;*.dng|DNG RAW|*.dng|Alle Dateien|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            LoadImagePreview(dialog.FileName, null);
            MediaInfoText.Text = $"Lokales Bild · {_imageMetadata.Count} Metadatenfelder";
            AddEvent("INFO", "Media", $"Lokales Bild geöffnet: {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            ShowError("Bild konnte nicht geöffnet werden", ex);
        }
    }

    private void LoadImagePreview(string path, MediaItemDto? remoteItem)
    {
        var inspection = ImageMetadataReader.Load(path);

        _imageMetadata.Clear();
        if (remoteItem is not null)
        {
            _imageMetadata.Add(new ImageMetadataEntryDto("DJI Media", "Index", remoteItem.Index.ToString(CultureInfo.InvariantCulture)));
            _imageMetadata.Add(new ImageMetadataEntryDto("DJI Media", "Dateiname", remoteItem.Name));
            _imageMetadata.Add(new ImageMetadataEntryDto("DJI Media", "Typ", remoteItem.Type));
            _imageMetadata.Add(new ImageMetadataEntryDto("DJI Media", "API-Größe", remoteItem.SizeText));
            _imageMetadata.Add(new ImageMetadataEntryDto("DJI Media", "API-Datum", remoteItem.Date));
        }

        foreach (var entry in inspection.Metadata)
            _imageMetadata.Add(entry);

        _currentPreviewPath = path;
        _originalPreview = inspection.Preview;
        _currentThermalResult = null;
        _currentThermalPalette = ThermalPalette.IronRed;
        ThermalCursorPanel.Visibility = Visibility.Collapsed;
        ThermalHistogramImage.Source = null;
        ThermalHistogramImage.Visibility = Visibility.Collapsed;
        ThermalOverlayButton.IsEnabled = false;
        ThermalExportButton.IsEnabled = false;
        ThermalSummaryText.Text = "Noch keine Thermal-Analyse.";
        MediaPreviewImage.Source = inspection.Preview;
        MediaPreviewTitle.Text = remoteItem?.Name ?? Path.GetFileName(path);
        PhotogrammetrySummaryText.Text = inspection.PhotogrammetrySummary;

        if (inspection.Preview is not null)
        {
            MediaPreviewPlaceholder.Visibility = Visibility.Collapsed;
            MediaPreviewSummary.Text =
                $"{inspection.PixelWidth} × {inspection.PixelHeight} px · {_imageMetadata.Count} Metadatenfelder · {inspection.PreviewStatus}";
            FitPreview();
        }
        else
        {
            MediaPreviewImage.Source = null;
            MediaPreviewPlaceholder.Text =
                "DNG/RAW-Metadaten geladen. Für die Bildvorschau ist auf diesem Windows-System kein passender WIC-RAW-Decoder verfügbar.";
            MediaPreviewPlaceholder.Visibility = Visibility.Visible;
            MediaPreviewSummary.Text =
                $"{_imageMetadata.Count} Metadatenfelder · {inspection.PreviewStatus}";
        }
    }

    private void ThermalAnalyze_Click(object sender, RoutedEventArgs e)
    {
        AnalyzeCurrentThermalImage();
    }

    private void AnalyzeCurrentThermalImage()
    {
        if (!_thermalSdk.IsAvailable)
        {
            System.Windows.MessageBox.Show(this,
                _thermalSdk.Status + "\n\nTSDK v1.8 lokal installieren; siehe third_party/dji-tsdk/README.md.",
                "DJI Thermal SDK",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentPreviewPath) || !File.Exists(_currentPreviewPath))
        {
            System.Windows.MessageBox.Show(this,
                "Zuerst ein DJI R-JPEG im Viewer öffnen.",
                "Thermal-Analyse",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var palette = SelectedThermalPalette();
            FooterText.Text = "DJI Thermal SDK analysiert R-JPEG …";

            var result = _thermalSdk.Analyze(_currentPreviewPath, palette);
            _currentThermalResult = result;
            _currentThermalPalette = palette;

            RemoveMetadataGroup("Thermal");
            RemoveMetadataGroup("Thermal Parameter");
            foreach (var entry in result.ToMetadata())
                _imageMetadata.Add(entry);

            MediaPreviewImage.Source =
                ThermalVisualization.RenderOverlay(result);
            MediaPreviewPlaceholder.Visibility = Visibility.Collapsed;

            ThermalHistogramImage.Source =
                ThermalVisualization.RenderHistogram(result);
            ThermalHistogramImage.Visibility = Visibility.Visible;
            ThermalOverlayButton.IsEnabled = true;
            ThermalExportButton.IsEnabled = true;

            ThermalSummaryText.Text =
                $"Min {result.MinimumC:F2} °C @ {result.MinimumX},{result.MinimumY} · " +
                $"P05 {result.P05C:F2} °C · Median {result.MedianC:F2} °C · " +
                $"Ø {result.AverageC:F2} °C · σ {result.StandardDeviationC:F2} °C · " +
                $"P95 {result.P95C:F2} °C · Max {result.MaximumC:F2} °C @ {result.MaximumX},{result.MaximumY} · " +
                $"ε {result.Emissivity:F3} · Distanz {result.DistanceM:F2} m";
            MediaPreviewSummary.Text =
                $"Thermal {result.Width} × {result.Height} px · {result.ValidPixelCount:N0} gültige Pixel · " +
                $"Palette {palette} · DJI TSDK v{DjiThermalSdk.SupportedSdkVersion}";
            FitPreview();

            FooterText.Text = "Thermal-Analyse abgeschlossen.";
            AddEvent("INFO", "Thermal",
                $"R-JPEG analysiert · Min {result.MinimumC:F2} °C · Max {result.MaximumC:F2} °C · " +
                $"Ø {result.AverageC:F2} °C · P05/P50/P95 {result.P05C:F2}/{result.MedianC:F2}/{result.P95C:F2} °C.");
        }
        catch (ThermalSdkException ex) when (ex.Code is -4 or -5 or -6 or -7 or -10)
        {
            ThermalSummaryText.Text = "Datei ist kein kompatibles DJI R-JPEG bzw. wird von dieser TSDK-Version nicht erkannt.";
            AddEvent("WARNING", "Thermal", ex.Message);
            System.Windows.MessageBox.Show(this,
                "Die ausgewählte Datei ist kein kompatibles DJI R-JPEG oder wird von TSDK v1.8 nicht erkannt.\n\n" + ex.Message,
                "Thermal-Analyse",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddEvent("ERROR", "Thermal", ex.Message);
            ShowError("Thermal-Analyse fehlgeschlagen", ex);
        }
    }

    private void MediaPreviewImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_currentThermalResult is null || MediaPreviewImage.Source is null)
        {
            ThermalCursorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var point = e.GetPosition(MediaPreviewImage);
        var sourceWidth = (double)_currentThermalResult.Width;
        var sourceHeight = (double)_currentThermalResult.Height;

        double scale;
        double offsetX;
        double offsetY;

        if (MediaPreviewImage.Stretch == Stretch.None)
        {
            scale = 1d;
            offsetX = 0d;
            offsetY = 0d;
        }
        else
        {
            scale = Math.Min(
                MediaPreviewImage.ActualWidth / sourceWidth,
                MediaPreviewImage.ActualHeight / sourceHeight);

            if (scale <= 0 || !double.IsFinite(scale))
            {
                ThermalCursorPanel.Visibility = Visibility.Collapsed;
                return;
            }

            offsetX = (MediaPreviewImage.ActualWidth - sourceWidth * scale) / 2d;
            offsetY = (MediaPreviewImage.ActualHeight - sourceHeight * scale) / 2d;
        }

        var x = (int)Math.Floor((point.X - offsetX) / scale);
        var y = (int)Math.Floor((point.Y - offsetY) / scale);
        var temperature = _currentThermalResult.TemperatureAt(x, y);

        if (temperature is not float value)
        {
            ThermalCursorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ThermalCursorText.Text = $"x={x} · y={y} · {value:F2} °C";
        ThermalCursorPanel.Visibility = Visibility.Visible;
    }

    private void MediaPreviewImage_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ThermalCursorPanel.Visibility = Visibility.Collapsed;
    }

    private void ThermalOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_currentThermalResult is null)
            return;

        MediaPreviewImage.Source =
            ThermalVisualization.RenderOverlay(
                _currentThermalResult);

        MediaPreviewPlaceholder.Visibility =
            Visibility.Collapsed;

        FooterText.Text =
            "Thermal-Minimum, Maximum und Bildmitte als Overlay angezeigt.";

        FitPreview();
    }

    private async void ThermalExport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentThermalResult is null ||
            string.IsNullOrWhiteSpace(_currentPreviewPath) ||
            !File.Exists(_currentPreviewPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "Zuerst ein DJI R-JPEG analysieren.",
                "Thermal-Export",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        using var dialog =
            new WinForms.FolderBrowserDialog
            {
                Description =
                    "Zielordner für Thermal-Analyseexport auswählen",
                UseDescriptionForTitle = true
            };

        if (dialog.ShowDialog() !=
            WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            FooterText.Text =
                "Thermal-Daten werden exportiert …";

            var export =
                ThermalAnalysisExporter.Export(
                    _currentThermalResult,
                    _currentPreviewPath,
                    dialog.SelectedPath,
                    _currentThermalPalette);

            await DroneDashProjectSession.RegisterFilesAsync(
                export.Paths);

            FooterText.Text =
                $"Thermal-Export abgeschlossen: {dialog.SelectedPath}";

            AddEvent(
                "INFO",
                "Thermal",
                $"Thermal-Export erzeugt · {export.Paths.Count} Dateien · {dialog.SelectedPath}.");

            System.Windows.MessageBox.Show(
                this,
                "Thermal-Export abgeschlossen.\n\n" +
                string.Join(
                    Environment.NewLine,
                    export.Paths.Select(Path.GetFileName)),
                "Thermal-Export",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddEvent(
                "ERROR",
                "Thermal",
                $"Export fehlgeschlagen: {ex.Message}");

            ShowError(
                "Thermal-Export fehlgeschlagen",
                ex);
        }
    }

    private void RestoreOriginalPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_originalPreview is null)
            return;

        MediaPreviewImage.Source = _originalPreview;
        MediaPreviewPlaceholder.Visibility = Visibility.Collapsed;
        ThermalSummaryText.Text = "Originaldarstellung aktiv; Thermal-Messdaten bleiben in der Tabelle erhalten.";
        FitPreview();
    }

    private ThermalPalette SelectedThermalPalette()
    {
        if (ThermalPaletteBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<ThermalPalette>(tag, out var palette))
        {
            return palette;
        }

        return ThermalPalette.IronRed;
    }

    private void RemoveMetadataGroup(string group)
    {
        for (var index = _imageMetadata.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_imageMetadata[index].Group, group, StringComparison.OrdinalIgnoreCase))
                _imageMetadata.RemoveAt(index);
        }
    }

    private void FitPreview_Click(object sender, RoutedEventArgs e) => FitPreview();

    private void FitPreview()
    {
        MediaPreviewImage.Stretch = Stretch.Uniform;
        MediaPreviewImage.Width = double.NaN;
        MediaPreviewImage.Height = double.NaN;
        MediaPreviewImage.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        MediaPreviewImage.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        MediaPreviewScroll.ScrollToHome();
    }

    private void ActualSizePreview_Click(object sender, RoutedEventArgs e)
    {
        if (MediaPreviewImage.Source is not System.Windows.Media.Imaging.BitmapSource bitmap)
            return;

        MediaPreviewImage.Stretch = Stretch.None;
        MediaPreviewImage.Width = bitmap.PixelWidth;
        MediaPreviewImage.Height = bitmap.PixelHeight;
        MediaPreviewImage.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        MediaPreviewImage.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        MediaPreviewScroll.ScrollToHome();
    }

    private static bool IsSupportedImageName(string? name)
    {
        var extension = Path.GetExtension(name ?? "");
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);
    }

    private static string PreviewCachePath(MediaItemDto item)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DroneDash_x64", "preview");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{item.Index}_{SanitizeFileName(item.Name, item.Index)}");
    }

    private static void CleanupPreviewCache()
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "DroneDash_x64", "preview");
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch
        {
            // Preview cache cleanup is best effort only.
        }
    }

    private async void DownloadMedia_Click(object sender, RoutedEventArgs e)
    {
        var selected = MediaGrid.SelectedItems.Cast<MediaItemDto>().ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Mindestens eine Mediendatei auswählen.", "Download",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Zielordner für DJI Medien auswählen",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            ConfigureApi();
            for (var i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                var safeName = SanitizeFileName(item.Name, item.Index);
                var destination = Path.Combine(dialog.SelectedPath, safeName);
                var itemIndex = i;

                var progress = new Progress<double>(p =>
                {
                    DownloadProgress.Value = p * 100d;
                    MediaInfoText.Text = $"{item.Name} · {p:P0} · {itemIndex + 1}/{selected.Count}";
                });

                await _api.DownloadMediaAsync(item.Index, destination, progress);
            }

            DownloadProgress.Value = 100;
            MediaInfoText.Text = $"{selected.Count} Datei(en) heruntergeladen.";
            FooterText.Text = $"Download abgeschlossen: {dialog.SelectedPath}";
            AddEvent("INFO", "Media", $"{selected.Count} Datei(en) heruntergeladen nach {dialog.SelectedPath}.");
        }
        catch (Exception ex)
        {
            ShowError("Download fehlgeschlagen", ex);
        }
    }
}
