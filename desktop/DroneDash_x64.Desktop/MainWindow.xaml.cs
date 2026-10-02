using System.Linq;
using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DroneDash_x64.Desktop.Api;
using DroneDash_x64.Desktop.Models;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop;

public partial class MainWindow : Window
{
    private readonly RcApiClient _api = new();
    private readonly ObservableCollection<MediaItemDto> _media = [];
    private readonly DispatcherTimer _pollTimer;
    private bool _statusRequestRunning;

    public MainWindow()
    {
        InitializeComponent();
        MediaGrid.ItemsSource = _media;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollStatusAsync();

        Closed += (_, _) => _api.Dispose();
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

            _pollTimer.Start();
            await PollStatusAsync();
        }
        catch (Exception ex)
        {
            _pollTimer.Stop();
            SetConnected(false, "Offline");
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
            SetConnected(true, s.ProductConnected ? "Aircraft verbunden" : $"RC-Agent · {s.SdkPhase}");
        }
        catch (Exception ex)
        {
            SetConnected(false, "Statusfehler");
            FooterText.Text = ex.Message;
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
        }
        catch (Exception ex)
        {
            MediaInfoText.Text = "Fehler beim Laden.";
            ShowError("Medienliste konnte nicht geladen werden", ex);
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
        }
        catch (Exception ex)
        {
            ShowError("Download fehlgeschlagen", ex);
        }
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
}
