using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using DroneDash_x64.Desktop.Project;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Planning;

public partial class FlightPlanningView : System.Windows.Controls.UserControl
{
    private readonly List<GeoPoint> _geometry = [];
    private FlightPlanResult? _plan;
    private bool _mapReady;
    private bool _suppressModeChange;
    private string? _currentProjectPath;

    public FlightPlanningView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeMapAsync();
    }

}
