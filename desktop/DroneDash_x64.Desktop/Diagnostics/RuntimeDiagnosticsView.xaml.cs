using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace DroneDash_x64.Desktop.Diagnostics;

public partial class RuntimeDiagnosticsView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<RuntimeDiagnosticItem> _items = [];
    private CancellationTokenSource? _probeCancellation;
    private bool _initialProbeStarted;

    public RuntimeDiagnosticsView()
    {
        InitializeComponent();
        DiagnosticsGrid.ItemsSource = _items;
        Loaded += RuntimeDiagnosticsView_Loaded;
        Unloaded += RuntimeDiagnosticsView_Unloaded;
    }

    private async void RuntimeDiagnosticsView_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialProbeStarted)
            return;

        _initialProbeStarted = true;
        await RefreshAsync();
    }

    private void RuntimeDiagnosticsView_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        _probeCancellation?.Cancel();
    }

    private async void Refresh_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();

        var cancellation =
            new CancellationTokenSource();

        _probeCancellation =
            cancellation;

        RefreshButton.IsEnabled = false;
        ProbeProgress.Visibility = Visibility.Visible;
        SummaryText.Text = "Runtime-Komponenten werden geprüft …";

        try
        {
            var snapshot =
                await WindowsRuntimeDiagnostics.ProbeAsync(
                    cancellation.Token);

            if (cancellation.IsCancellationRequested)
                return;

            _items.Clear();

            foreach (var item in snapshot.Items)
                _items.Add(item);

            PlatformText.Text =
                $"{snapshot.OsDescription} · OS {snapshot.OsArchitecture} · " +
                $"Prozess {snapshot.ProcessArchitecture} · {snapshot.FrameworkDescription}";

            SummaryText.Text =
                snapshot.SummaryText +
                (snapshot.HasBlockingErrors
                    ? " · Desktop-Konfiguration prüfen"
                    : " · Desktopbasis bereit");

            LastProbeText.Text =
                $"Letzte Prüfung: {snapshot.CreatedAtUtc.LocalDateTime:G}";
        }
        catch (OperationCanceledException)
        {
            // A new probe or unloading the page cancels the previous pass.
        }
        catch (Exception ex)
        {
            SummaryText.Text =
                $"Runtime-Diagnose fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(
                    _probeCancellation,
                    cancellation))
            {
                RefreshButton.IsEnabled = true;
                ProbeProgress.Visibility = Visibility.Collapsed;
            }

            cancellation.Dispose();
        }
    }
}
