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
    private FlightPlanSettings ReadSettings()
    {
        var aircraftTag =
            (AircraftBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "M3E";
        if (!Enum.TryParse<DjiAircraftProfile>(aircraftTag, out var aircraft))
            aircraft = DjiAircraftProfile.M3E;

        return new FlightPlanSettings(
            string.IsNullOrWhiteSpace(PlanNameBox.Text)
                ? "DroneDash Mapping"
                : PlanNameBox.Text.Trim(),
            aircraft,
            SelectedMode(),
            ParseDouble(AltitudeBox.Text, "Höhe"),
            ParseDouble(SpeedBox.Text, "Geschwindigkeit"),
            ParseInt(FrontOverlapBox.Text, "Front Overlap"),
            ParseInt(SideOverlapBox.Text, "Side Overlap"),
            ParseDouble(GridAngleBox.Text, "Rasterwinkel"),
            ParseDouble(GimbalPitchBox.Text, "Nadir Gimbal"),
            ParseDouble(ObliquePitchBox.Text, "Oblique Gimbal"),
            SmartObliqueBox.IsChecked == true,
            TerrainFollowBox.IsChecked == true,
            ParseDouble(StripHalfWidthBox.Text, "Strip Halbbreite"));
    }

    private FlightPlanMode SelectedMode()
    {
        var modeTag =
            (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Mapping2D";
        return Enum.TryParse<FlightPlanMode>(modeTag, out var mode)
            ? mode
            : FlightPlanMode.Mapping2D;
    }

    private void ApplySettings(FlightPlanSettings settings)
    {
        _suppressModeChange = true;
        try
        {
            PlanNameBox.Text = settings.Name;
            SelectComboByTag(ModeBox, settings.Mode.ToString());
            SelectComboByTag(AircraftBox, settings.Aircraft.ToString());
            AltitudeBox.Text = settings.AltitudeMeters.ToString(
                CultureInfo.InvariantCulture);
            SpeedBox.Text = settings.SpeedMetersPerSecond.ToString(
                CultureInfo.InvariantCulture);
            FrontOverlapBox.Text = settings.FrontOverlapPercent.ToString(
                CultureInfo.InvariantCulture);
            SideOverlapBox.Text = settings.SideOverlapPercent.ToString(
                CultureInfo.InvariantCulture);
            GridAngleBox.Text = settings.GridAngleDegrees.ToString(
                CultureInfo.InvariantCulture);
            GimbalPitchBox.Text = settings.GimbalPitchDegrees.ToString(
                CultureInfo.InvariantCulture);
            ObliquePitchBox.Text = settings.ObliqueGimbalPitchDegrees.ToString(
                CultureInfo.InvariantCulture);
            SmartObliqueBox.IsChecked = settings.SmartObliqueEnabled;
            TerrainFollowBox.IsChecked = settings.TerrainFollowEnabled;
            StripHalfWidthBox.Text = settings.StripHalfWidthMeters.ToString(
                CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressModeChange = false;
        }
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(
                    item.Tag?.ToString(),
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        throw new InvalidDataException(
            $"Projekt enthält einen nicht unterstützten Wert '{tag}'.");
    }


    private static double ParseDouble(string value, string label)
    {
        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var invariant))
        {
            return invariant;
        }

        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out var current))
        {
            return current;
        }

        throw new FormatException($"{label}: ungültige Zahl.");
    }

    private static int ParseInt(string value, string label)
    {
        if (int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result))
        {
            return result;
        }

        throw new FormatException($"{label}: ungültige Ganzzahl.");
    }

}
