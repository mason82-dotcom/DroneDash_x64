using System.Windows;
using DroneDash_x64.Desktop.Models;

namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView
{
    private readonly LiveAircraftTrack _liveTrack = new();
    private LiveAircraftState? _liveAircraft;

    private bool LiveAircraftVisible => LiveAircraftBox.IsChecked == true;

    /// <summary>
    /// Called with every bridge status poll (null when the poll failed). Display only: DroneDash
    /// never sends flight commands from here.
    /// </summary>
    public void UpdateLiveAircraft(StatusDto? status)
    {
        var state = LiveAircraftState.FromStatus(status, DateTimeOffset.Now);
        if (state is null && _liveAircraft is not null && status is null)
        {
            // Lost bridge: keep the last position on the map, but say how old it is.
            LiveAircraftText.Text = DescribeLiveAircraft(_liveAircraft);
            return;
        }

        _liveAircraft = state;
        var appended = state is not null && state.IsFlying != false && _liveTrack.Add(state.Position);
        LiveAircraftText.Text = DescribeLiveAircraft(state);

        if (!LiveAircraftVisible)
            return;

        PostMap(new
        {
            type = "aircraft",
            aircraft = AircraftMessage(state),
            append = appended ? new[] { state!.Position.Latitude, state.Position.Longitude } : null
        });
    }

    private string DescribeLiveAircraft(LiveAircraftState? state) =>
        LiveAircraftDescription.Describe(state, _plan, DateTimeOffset.Now);

    private void LiveAircraftBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            RenderMap();
    }

    private void ClearTrack_Click(object sender, RoutedEventArgs e)
    {
        _liveTrack.Clear();
        RenderMap();
    }

    /// <summary>Full aircraft layer for a complete map render (marker, home and the whole track).</summary>
    private object? LiveMapMessage()
    {
        if (!LiveAircraftVisible)
            return null;

        return new
        {
            aircraft = AircraftMessage(_liveAircraft),
            track = _liveTrack.Points.Select(p => new[] { p.Latitude, p.Longitude })
        };
    }

    private static object? AircraftMessage(LiveAircraftState? state) =>
        state is null
            ? null
            : new
            {
                lat = state.Position.Latitude,
                lon = state.Position.Longitude,
                heading = state.HeadingDegrees,
                altitude = state.RelativeAltitudeMeters,
                speed = state.GroundSpeedMs,
                flying = state.IsFlying,
                home = state.Home is { } home ? new[] { home.Latitude, home.Longitude } : null
            };
}
