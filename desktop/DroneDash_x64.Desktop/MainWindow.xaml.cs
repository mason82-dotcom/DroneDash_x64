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
            MessageBox.Show(this, "Mindestens eine Mediendatei auswählen.", "Download",
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
            (Color)ColorConverter.ConvertFromString(connected ? "#46B96B" : "#B94A48"));
        ConnectionText.Text = text;
    }

    private void ShowError(string title, Exception ex)
    {
        FooterText.Text = ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string Percent(int? value) => value is int p ? $"{p} %" : "—";
    private static string Number(double? value, string format) =>
        value is double d ? d.ToString(format, CultureInfo.InvariantCulture) : "—";
    private static string Degrees(double? value) => value is double d ? $"{d:F1}°" : "—";
}
