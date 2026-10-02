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
using DroneDash_x64.Desktop.Thermal;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop;

public partial class MainWindow : Window
{
    private readonly RcApiClient _api = new();
    private readonly DjiThermalSdk _thermalSdk = new();
    private readonly ObservableCollection<MediaItemDto> _media = [];
    private readonly ObservableCollection<ImageMetadataEntryDto> _imageMetadata = [];
    private readonly ObservableCollection<DiagnosticEventDto> _events = [];
    private readonly DispatcherTimer _pollTimer;
    private bool _statusRequestRunning;
    private StatusDto? _lastStatus;
    private string? _lastPollError;
    private int _lastAircraftBatteryBand = -1;
    private int _lastRcBatteryBand = -1;
    private string? _currentPreviewPath;
    private System.Windows.Media.Imaging.BitmapSource? _originalPreview;

    public MainWindow()
    {
        InitializeComponent();
        MediaGrid.ItemsSource = _media;
        MetadataGrid.ItemsSource = _imageMetadata;
        EventGrid.ItemsSource = _events;

        ThermalSdkStatusText.Text = _thermalSdk.Status;
        ThermalAnalyzeButton.IsEnabled = _thermalSdk.IsAvailable;
        AddEvent(_thermalSdk.IsAvailable ? "INFO" : "WARNING", "Thermal",
            _thermalSdk.IsAvailable ? "DJI Thermal SDK v1.8 verfügbar." : _thermalSdk.Status);
        AddEvent("INFO", "App", "DroneDash_x64 gestartet.");

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollStatusAsync();

        Closed += (_, _) =>
        {
            _api.Dispose();
            _thermalSdk.Dispose();
            CleanupPreviewCache();
        };
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfigureApi();
            FooterText.Text = "Bridge wird geprüft …";
            var health = await _api.GetHealthAsync();
            SetConnected(true, health.Registered
                ? $"Online · {health.SdkVersion}"
                : $"Agent online · {health.SdkPhase}");

            AddEvent("INFO", "Bridge",
                $"Verbunden · SDK {health.SdkVersion} · Phase {health.SdkPhase} · Registered {health.Registered}.");

            _pollTimer.Start();
            await PollStatusAsync();
        }
        catch (Exception ex)
        {
            _pollTimer.Stop();
            SetConnected(false, "Offline");
            AddEvent("ERROR", "Bridge", $"Verbindung fehlgeschlagen: {ex.Message}");
            ShowError("Verbindung fehlgeschlagen", ex);
        }
    }

    private void ConfigureApi() => _api.Configure(EndpointBox.Text.Trim(), TokenBox.Password);

    private async Task PollStatusAsync()
    {
        if (_statusRequestRunning)
        {
            return;
        }

        _statusRequestRunning = true;
        try
        {
            ConfigureApi();
            var s = await _api.GetStatusAsync();
            RenderStatus(s);
            LogStatusChanges(s);
            _lastPollError = null;
            SetConnected(true, s.ProductConnected ? "Aircraft verbunden" : $"RC-Agent · {s.SdkPhase}");
        }
        catch (Exception ex)
        {
            SetConnected(false, "Statusfehler");
            FooterText.Text = ex.Message;
            if (!string.Equals(_lastPollError, ex.Message, StringComparison.Ordinal))
            {
                AddEvent("ERROR", "Telemetry", ex.Message);
                _lastPollError = ex.Message;
            }
        }
        finally
        {
            _statusRequestRunning = false;
        }
    }

    private void RenderStatus(StatusDto s)
    {
        SdkText.Text = $"{s.SdkVersion} · {s.SdkPhase}";
        AircraftText.Text = $"{Display(s.ProductType)}\nFW {Display(s.AircraftFirmware)}";
        RcText.Text = $"{Display(s.RemoteControllerType)}\nFW {Display(s.RemoteControllerFirmware)}";
        AircraftBatteryText.Text = Percent(s.AircraftBatteryPercent);
        RcBatteryText.Text = Percent(s.RemoteControllerBatteryPercent);
        SatelliteText.Text = s.SatelliteCount?.ToString(CultureInfo.InvariantCulture) ?? "—";
        FlightModeText.Text = Display(s.FlightMode);
        FlyingText.Text = s.IsFlying switch { true => "Ja", false => "Nein", _ => "—" };
        LatitudeText.Text = Number(s.Latitude, "F7");
        LongitudeText.Text = Number(s.Longitude, "F7");
        AltitudeText.Text = s.AltitudeMeters is double altitude ? $"{altitude:F1} m" : "—";
        PitchText.Text = Degrees(s.PitchDegrees);
        RollText.Text = Degrees(s.RollDegrees);
        YawText.Text = Degrees(s.YawDegrees);

        GpsSignalText.Text = $"{Display(s.GpsSignalLevel)} · {s.SatelliteCount?.ToString(CultureInfo.InvariantCulture) ?? "—"} Sat";
        HomePointText.Text = s.HomeLocationSet switch
        {
            true when s.HomeLatitude is double lat && s.HomeLongitude is double lon =>
                $"gesetzt · {lat:F7}, {lon:F7}",
            true => "gesetzt · Koordinaten nicht verfügbar",
            false => "nicht gesetzt",
            _ => "—"
        };
        CompassText.Text = s.CompassHeadingDegrees is double heading
            ? $"{heading:F1}° · {(s.CompassHasError == true ? "FEHLER" : "OK")}"
            : "—";

        GroundSpeedText.Text = Speed(s.GroundSpeedMs);
        VerticalSpeedText.Text = SignedSpeed(s.VerticalSpeedMs);
        VelocityNedText.Text =
            $"N {SignedSpeed(s.VelocityNorthMs)} · E {SignedSpeed(s.VelocityEastMs)} · D {SignedSpeed(s.VelocityDownMs)}";
        WindText.Text = s.WindSpeedMs is double wind
            ? $"{wind:F1} m/s · {Display(s.WindDirection)} · {Display(s.WindWarning)}"
            : $"{Display(s.WindDirection)} · {Display(s.WindWarning)}";

        BatteryElectricalText.Text =
            $"{Voltage(s.AircraftBatteryVoltageMv)} · {Current(s.AircraftBatteryCurrentMa)} · {Temperature(s.AircraftBatteryTemperatureC)}";
        BatteryCapacityText.Text =
            s.AircraftBatteryRemainingMah is int remaining && s.AircraftBatteryFullChargeMah is int full
                ? $"{remaining} / {full} mAh"
                : "—";
        MotorTakeoffText.Text =
            $"Motoren: {BoolText(s.AreMotorsOn)} · Takeoff Alt: {(s.TakeoffAltitudeMeters is double takeoff ? $"{takeoff:F1} m" : "—")}";

        RtkStatusText.Text =
            $"Enabled {BoolText(s.RtkEnabled)} · Healthy {BoolText(s.RtkHealthy)} · Maintain {BoolText(s.RtkMaintainAccuracyEnabled)} · {Display(s.RtkPositioningSolution)}";
        RtkSourceText.Text = Display(s.RtkReferenceStationSource);
        RtkMobileText.Text = Coordinate3D(s.RtkMobileLatitude, s.RtkMobileLongitude, s.RtkMobileAltitudeMeters);
        RtkBaseText.Text = Coordinate3D(s.RtkBaseLatitude, s.RtkBaseLongitude, s.RtkBaseAltitudeMeters);
        RtkAccuracyText.Text =
            $"Lon {Meters(s.RtkStdLongitudeMeters)} · Lat {Meters(s.RtkStdLatitudeMeters)} · Alt {Meters(s.RtkStdAltitudeMeters)}";
        RtkHeadingText.Text =
            $"RTK {Display(s.RtkHeading)} · Fusion {Display(s.RtkRealHeading)}";
        RtkSatellitesText.Text = FormatRtkSatellites(s.RtkSatelliteCounts);
        RtkErrorText.Text = string.IsNullOrWhiteSpace(s.RtkError) ? "—" : s.RtkError;

        CameraIdentityText.Text =
            $"{Display(s.CameraType)} · FW {Display(s.CameraFirmware)} · Speicher {Display(s.CameraCurrentStorage)}";
        CameraActivityText.Text =
            $"{Display(s.CameraMode)} · Foto {BoolText(s.CameraIsShootingPhoto)} · REC {BoolText(s.CameraIsRecording)}";
        SdStorageText.Text =
            $"{Display(s.SdStorageState)} · {StorageCapacity(s.SdStorageLeftMb, s.SdStorageCapacityMb)}";
        SdRemainingText.Text =
            $"{CountText(s.SdAvailablePhotoCount, "Fotos")} · {DurationText(s.SdAvailableVideoSeconds)}";
        InternalStorageText.Text =
            $"{Display(s.InternalStorageState)} · {StorageCapacity(s.InternalStorageLeftMb, s.InternalStorageCapacityMb)}";
        InternalRemainingText.Text =
            $"{CountText(s.InternalAvailablePhotoCount, "Fotos")} · {DurationText(s.InternalAvailableVideoSeconds)}";
        GimbalModeText.Text = Display(s.GimbalMode);
        GimbalAttitudeText.Text =
            $"P {Degrees(s.GimbalPitchDegrees)} · R {Degrees(s.GimbalRollDegrees)} · Y {Degrees(s.GimbalYawDegrees)}";

        AircraftIdentityDetailText.Text =
            $"{Display(s.ProductType)}\nFirmware: {Display(s.AircraftFirmware)}";
        AircraftFlightDetailText.Text =
            $"Mode: {Display(s.FlightMode)}\nFlying: {BoolText(s.IsFlying)}\nMotoren: {BoolText(s.AreMotorsOn)}\nTakeoff Alt: {(s.TakeoffAltitudeMeters is double takeoffAlt ? $"{takeoffAlt:F1} m" : "—")}";
        AircraftPositionDetailText.Text =
            $"Aircraft: {Coordinate3D(s.Latitude, s.Longitude, s.AltitudeMeters)}\nHome: {Coordinate3D(s.HomeLatitude, s.HomeLongitude, null)}\nHome gesetzt: {BoolText(s.HomeLocationSet)}";
        AircraftMotionDetailText.Text =
            $"Ground: {Speed(s.GroundSpeedMs)}\nVertikal: {SignedSpeed(s.VerticalSpeedMs)}\nN/E/D: {SignedSpeed(s.VelocityNorthMs)} / {SignedSpeed(s.VelocityEastMs)} / {SignedSpeed(s.VelocityDownMs)}\nWind: {Speed(s.WindSpeedMs)} · {Display(s.WindDirection)} · {Display(s.WindWarning)}";
        AircraftAttitudeDetailText.Text =
            $"Pitch {Degrees(s.PitchDegrees)}\nRoll {Degrees(s.RollDegrees)}\nYaw {Degrees(s.YawDegrees)}";
        AircraftNavDetailText.Text =
            $"GPS: {Display(s.GpsSignalLevel)} · {s.SatelliteCount?.ToString(CultureInfo.InvariantCulture) ?? "—"} Satelliten\nKompass: {Degrees(s.CompassHeadingDegrees)} · {(s.CompassHasError == true ? "FEHLER" : "OK")}";

        RcIdentityDetailText.Text =
            $"{Display(s.RemoteControllerType)}\nFirmware: {Display(s.RemoteControllerFirmware)}";
        RcBatteryDetailText.Text = $"Akku: {Percent(s.RemoteControllerBatteryPercent)}";
        RcBridgeDetailText.Text = $"SDK: {Display(s.SdkVersion)}\nPhase: {Display(s.SdkPhase)}";
        RcConnectionDetailText.Text =
            $"Aircraft verbunden: {BoolText(s.ProductConnected)}\nLetztes Paket: {s.Timestamp.ToLocalTime():HH:mm:ss}";

        EnergyAircraftDetailText.Text =
            $"{Percent(s.AircraftBatteryPercent)}\n{Voltage(s.AircraftBatteryVoltageMv)} · {Current(s.AircraftBatteryCurrentMa)} · {Temperature(s.AircraftBatteryTemperatureC)}";
        EnergyCapacityDetailText.Text =
            s.AircraftBatteryRemainingMah is int capRemaining && s.AircraftBatteryFullChargeMah is int capFull
                ? $"{capRemaining} / {capFull} mAh · {capRemaining * 100d / Math.Max(1, capFull):F1} % rechnerisch"
                : "—";
        EnergyRcDetailText.Text = Percent(s.RemoteControllerBatteryPercent);
        EnergyHealthDetailText.Text =
            $"Aircraft: {BatteryBandText(BatteryBand(s.AircraftBatteryPercent, 30, 15))}\nRC: {BatteryBandText(BatteryBand(s.RemoteControllerBatteryPercent, 20, 10))}";

        RtkDetailStatusText.Text =
            $"Enabled {BoolText(s.RtkEnabled)} · Healthy {BoolText(s.RtkHealthy)}\nMaintain {BoolText(s.RtkMaintainAccuracyEnabled)}\nSolution {Display(s.RtkPositioningSolution)}";
        RtkDetailSourceText.Text = Display(s.RtkReferenceStationSource);
        RtkDetailMobileText.Text = Coordinate3D(s.RtkMobileLatitude, s.RtkMobileLongitude, s.RtkMobileAltitudeMeters);
        RtkDetailBaseText.Text = Coordinate3D(s.RtkBaseLatitude, s.RtkBaseLongitude, s.RtkBaseAltitudeMeters);
        RtkDetailAccuracyText.Text =
            $"Lon {Meters(s.RtkStdLongitudeMeters)} · Lat {Meters(s.RtkStdLatitudeMeters)} · Alt {Meters(s.RtkStdAltitudeMeters)}\nRTK Heading {Display(s.RtkHeading)}\nFusion {Display(s.RtkRealHeading)}";
        RtkDetailSatellitesText.Text =
            $"{FormatRtkSatellites(s.RtkSatelliteCounts)}\nFehler: {(string.IsNullOrWhiteSpace(s.RtkError) ? "—" : s.RtkError)}";

        CameraDetailIdentityText.Text =
            $"{Display(s.CameraType)}\nFirmware: {Display(s.CameraFirmware)}\nStorage: {Display(s.CameraCurrentStorage)}";
        CameraDetailActivityText.Text =
            $"Mode: {Display(s.CameraMode)}\nFoto: {BoolText(s.CameraIsShootingPhoto)}\nRecording: {BoolText(s.CameraIsRecording)}";
        CameraDetailStorageText.Text =
            $"SD: {Display(s.SdStorageState)} · {StorageCapacity(s.SdStorageLeftMb, s.SdStorageCapacityMb)} · {CountText(s.SdAvailablePhotoCount, "Fotos")} · {DurationText(s.SdAvailableVideoSeconds)}\nIntern: {Display(s.InternalStorageState)} · {StorageCapacity(s.InternalStorageLeftMb, s.InternalStorageCapacityMb)}";
        GimbalDetailText.Text =
            $"Mode: {Display(s.GimbalMode)}\nPitch {Degrees(s.GimbalPitchDegrees)} · Roll {Degrees(s.GimbalRollDegrees)} · Yaw {Degrees(s.GimbalYawDegrees)}";

        TimestampText.Text = s.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        FooterText.Text = s.ProductConnected ? "Live-Telemetrie aktiv" : "RC-Agent erreichbar; Aircraft nicht verbunden";
    }

    private async void ReadConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfigureApi();
            var config = await _api.GetConfigurationAsync();
            RenderConfiguration(config);
            FooterText.Text = "Konfiguration gelesen.";
            AddEvent("INFO", "Config", "Flugkonfiguration gelesen.");
        }
        catch (Exception ex)
        {
            ShowError("Konfiguration konnte nicht gelesen werden", ex);
        }
    }

    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfigureApi();
            var height = ParseOptionalInt(HeightLimitBox.Text, "Max. Flughöhe");
            var rth = ParseOptionalInt(GoHomeHeightBox.Text, "RTH-Höhe");

            if (height is null && rth is null)
            {
                throw new InvalidOperationException("Mindestens einen Wert eingeben.");
            }

            var result = await _api.UpdateConfigurationAsync(new ConfigurationUpdateDto(height, rth));
            RenderConfiguration(result);
            FooterText.Text = "Konfiguration gespeichert und erneut gelesen.";
            AddEvent("WARNING", "Config",
                $"Konfiguration geschrieben · Max Höhe {height?.ToString() ?? "unverändert"} m · RTH {rth?.ToString() ?? "unverändert"} m.");
        }
        catch (Exception ex)
        {
            ShowError("Konfiguration konnte nicht gespeichert werden", ex);
        }
    }

    private void RenderConfiguration(ConfigurationDto config)
    {
        HeightLimitBox.Text = config.HeightLimitMeters?.ToString(CultureInfo.InvariantCulture) ?? "";
        GoHomeHeightBox.Text = config.GoHomeHeightMeters?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

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

            RemoveMetadataGroup("Thermal");
            RemoveMetadataGroup("Thermal Parameter");
            foreach (var entry in result.ToMetadata())
                _imageMetadata.Add(entry);

            MediaPreviewImage.Source = result.PseudoColorImage;
            MediaPreviewPlaceholder.Visibility = Visibility.Collapsed;
            ThermalSummaryText.Text =
                $"Min {result.MinimumC:F2} °C @ {result.MinimumX},{result.MinimumY} · " +
                $"Ø {result.AverageC:F2} °C · Mitte {result.CenterC:F2} °C · " +
                $"Max {result.MaximumC:F2} °C @ {result.MaximumX},{result.MaximumY} · " +
                $"ε {result.Emissivity:F3} · Distanz {result.DistanceM:F2} m";
            MediaPreviewSummary.Text =
                $"Thermal {result.Width} × {result.Height} px · Palette {palette} · DJI TSDK v{DjiThermalSdk.SupportedSdkVersion}";
            FitPreview();

            FooterText.Text = "Thermal-Analyse abgeschlossen.";
            AddEvent("INFO", "Thermal",
                $"R-JPEG analysiert · Min {result.MinimumC:F2} °C · Max {result.MaximumC:F2} °C · Ø {result.AverageC:F2} °C.");
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

    private void LogStatusChanges(StatusDto current)
    {
        if (_lastStatus is null)
        {
            AddEvent("INFO", "Telemetry",
                $"Live-Telemetrie gestartet · Aircraft {(current.ProductConnected ? "verbunden" : "nicht verbunden")}.");
            _lastAircraftBatteryBand = BatteryBand(current.AircraftBatteryPercent, 30, 15);
            _lastRcBatteryBand = BatteryBand(current.RemoteControllerBatteryPercent, 20, 10);
            _lastStatus = current;
            return;
        }

        var previous = _lastStatus;

        if (previous.ProductConnected != current.ProductConnected)
            AddEvent(current.ProductConnected ? "INFO" : "WARNING", "Aircraft",
                current.ProductConnected ? "Aircraft verbunden." : "Aircraft getrennt.");

        if (previous.AreMotorsOn != current.AreMotorsOn && current.AreMotorsOn is bool motors)
            AddEvent(motors ? "WARNING" : "INFO", "Aircraft", motors ? "Motoren eingeschaltet." : "Motoren ausgeschaltet.");

        if (previous.IsFlying != current.IsFlying && current.IsFlying is bool flying)
            AddEvent(flying ? "WARNING" : "INFO", "Flight", flying ? "Flugzustand aktiv." : "Aircraft am Boden.");

        if (!string.Equals(previous.FlightMode, current.FlightMode, StringComparison.Ordinal))
            AddEvent("INFO", "Flight", $"Flugmodus: {Display(previous.FlightMode)} → {Display(current.FlightMode)}.");

        if (previous.CompassHasError != current.CompassHasError && current.CompassHasError == true)
            AddEvent("ERROR", "Compass", "Kompassfehler gemeldet.");

        if (!string.Equals(previous.RtkPositioningSolution, current.RtkPositioningSolution, StringComparison.Ordinal))
            AddEvent("INFO", "RTK",
                $"Positioning Solution: {Display(previous.RtkPositioningSolution)} → {Display(current.RtkPositioningSolution)}.");

        if (previous.RtkHealthy != current.RtkHealthy && current.RtkEnabled == true)
            AddEvent(current.RtkHealthy == true ? "INFO" : "WARNING", "RTK",
                current.RtkHealthy == true ? "RTK ist healthy." : "RTK ist nicht healthy.");

        if (!string.IsNullOrWhiteSpace(current.RtkError) &&
            !string.Equals(previous.RtkError, current.RtkError, StringComparison.Ordinal))
            AddEvent("ERROR", "RTK", current.RtkError);

        if (previous.CameraIsRecording != current.CameraIsRecording && current.CameraIsRecording is bool recording)
            AddEvent("INFO", "Camera", recording ? "Videoaufnahme aktiv." : "Videoaufnahme beendet.");

        if (!string.Equals(previous.SdStorageState, current.SdStorageState, StringComparison.Ordinal))
            AddEvent(IsNormalState(current.SdStorageState) ? "INFO" : "WARNING", "Storage",
                $"SD-Status: {Display(previous.SdStorageState)} → {Display(current.SdStorageState)}.");

        var aircraftBand = BatteryBand(current.AircraftBatteryPercent, 30, 15);
        if (aircraftBand != _lastAircraftBatteryBand)
        {
            AddBatteryEvent("Aircraft Battery", current.AircraftBatteryPercent, aircraftBand);
            _lastAircraftBatteryBand = aircraftBand;
        }

        var rcBand = BatteryBand(current.RemoteControllerBatteryPercent, 20, 10);
        if (rcBand != _lastRcBatteryBand)
        {
            AddBatteryEvent("RC Battery", current.RemoteControllerBatteryPercent, rcBand);
            _lastRcBatteryBand = rcBand;
        }

        _lastStatus = current;
    }

    private void AddBatteryEvent(string source, int? percent, int band)
    {
        var level = band switch { 2 => "ERROR", 1 => "WARNING", _ => "INFO" };
        AddEvent(level, source, $"Ladezustand {Percent(percent)} · {BatteryBandText(band)}.");
    }

    private void AddEvent(string level, string source, string message)
    {
        _events.Insert(0, new DiagnosticEventDto(DateTimeOffset.Now, level, source, message));
        while (_events.Count > 500)
            _events.RemoveAt(_events.Count - 1);

        EventCountText.Text = $"{_events.Count} Ereignis{(_events.Count == 1 ? "" : "se")}";
    }

    private void ClearEvents_Click(object sender, RoutedEventArgs e)
    {
        _events.Clear();
        EventCountText.Text = "0 Ereignisse";
        AddEvent("INFO", "App", "Diagnoselog geleert.");
    }

    private void ExportEvents_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.SaveFileDialog
        {
            Title = "Diagnoselog exportieren",
            Filter = "CSV-Datei (*.csv)|*.csv",
            DefaultExt = "csv",
            AddExtension = true,
            FileName = $"DroneDash_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var csv = new StringBuilder();
        csv.AppendLine("timestamp,level,source,message");
        foreach (var item in _events.Reverse())
        {
            csv.Append(Csv(item.Timestamp.ToString("O")));
            csv.Append(',');
            csv.Append(Csv(item.Level));
            csv.Append(',');
            csv.Append(Csv(item.Source));
            csv.Append(',');
            csv.AppendLine(Csv(item.Message));
        }

        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        FooterText.Text = $"Diagnoselog exportiert: {dialog.FileName}";
        AddEvent("INFO", "App", $"Diagnoselog exportiert: {dialog.FileName}");
    }

    private static int? ParseOptionalInt(string raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"{label}: ungültige Ganzzahl.");
        }

        return value;
    }

    private static string SanitizeFileName(string? name, int index)
    {
        var value = string.IsNullOrWhiteSpace(name) ? $"DJI_MEDIA_{index}" : name;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    private void SetConnected(bool connected, string text)
    {
        ConnectionDot.Fill = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(connected ? "#46B96B" : "#B94A48"));
        ConnectionText.Text = text;
    }

    private void ShowError(string title, Exception ex)
    {
        FooterText.Text = ex.Message;
        System.Windows.MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string Percent(int? value) => value is int p ? $"{p} %" : "—";
    private static string Number(double? value, string format) =>
        value is double d ? d.ToString(format, CultureInfo.InvariantCulture) : "—";
    private static string Degrees(double? value) => value is double d ? $"{d:F1}°" : "—";
    private static string Speed(double? value) => value is double d ? $"{d:F1} m/s" : "—";
    private static string SignedSpeed(double? value) => value is double d ? $"{d:+0.0;-0.0;0.0} m/s" : "—";
    private static string Voltage(int? millivolts) => millivolts is int mv ? $"{mv / 1000d:F2} V" : "—";
    private static string Current(int? milliamps) => milliamps is int ma ? $"{ma / 1000d:+0.00;-0.00;0.00} A" : "—";
    private static string Temperature(double? value) => value is double d ? $"{d:F1} °C" : "—";
    private static string BoolText(bool? value) => value switch { true => "AN", false => "AUS", _ => "—" };
    private static string Meters(double? value) => value is double d ? $"{d:F3} m" : "—";

    private static string Coordinate3D(double? latitude, double? longitude, double? altitude)
    {
        if (latitude is not double lat || longitude is not double lon)
        {
            return "—";
        }

        return altitude is double alt
            ? $"{lat:F7}, {lon:F7} · {alt:F2} m"
            : $"{lat:F7}, {lon:F7}";
    }

    private static string FormatRtkSatellites(IReadOnlyDictionary<string, int>? counts)
    {
        if (counts is null || counts.Count == 0)
        {
            return "—";
        }

        return string.Join(" · ", counts.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value}"));
    }

    private static string StorageCapacity(int? leftMb, int? totalMb)
    {
        if (leftMb is not int left)
        {
            return "—";
        }

        return totalMb is int total && total > 0
            ? $"{left / 1024d:F1} / {total / 1024d:F1} GB frei/gesamt"
            : $"{left / 1024d:F1} GB frei";
    }

    private static string CountText(int? value, string unit) =>
        value is int count ? $"{count:N0} {unit}" : "—";

    private static string DurationText(int? seconds) =>
        seconds is int s && s >= 0 ? $"Video {TimeSpan.FromSeconds(s):hh\\:mm\\:ss}" : "Video —";

    private static int BatteryBand(int? percent, int warningThreshold, int criticalThreshold)
    {
        if (percent is not int value)
            return -1;
        if (value <= criticalThreshold)
            return 2;
        if (value <= warningThreshold)
            return 1;
        return 0;
    }

    private static string BatteryBandText(int band) =>
        band switch { 2 => "kritisch", 1 => "niedrig", 0 => "normal", _ => "unbekannt" };

    private static bool IsNormalState(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("NORMAL", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("READY", StringComparison.OrdinalIgnoreCase);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
