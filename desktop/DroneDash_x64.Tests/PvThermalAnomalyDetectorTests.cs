using DroneDash_x64.Desktop.PvAnalysis;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class PvThermalAnomalyDetectorTests
{
    private const int Width = 64;
    private const int Height = 48;

    private static readonly PvAnalysisSettings Settings = new(
        WarningDeltaC: 5,
        CriticalDeltaC: 15,
        LocalWindowRadiusPixels: 8,
        MinimumClusterPixels: 6);

    private static float[] Uniform(float temperature) =>
        Enumerable.Repeat(temperature, Width * Height).ToArray();

    private static void AddBlock(float[] temperatures, int x0, int y0, int size, float delta)
    {
        for (var y = y0; y < y0 + size; y++)
        {
            for (var x = x0; x < x0 + size; x++)
            {
                temperatures[y * Width + x] += delta;
            }
        }
    }

    [Fact]
    public void Detect_UniformPanelHasNoCandidates()
    {
        Assert.Empty(PvThermalAnomalyDetector.Detect(Uniform(35f), Width, Height, Settings));
    }

    [Fact]
    public void Detect_AllNonFiniteMatrixHasNoCandidates()
    {
        Assert.Empty(PvThermalAnomalyDetector.Detect(Uniform(float.NaN), Width, Height, Settings));
    }

    [Fact]
    public void Detect_ClassifiesSeverityByDelta()
    {
        var temperatures = Uniform(35f);
        AddBlock(temperatures, 8, 8, 4, 8f);
        AddBlock(temperatures, 44, 30, 4, 20f);

        var candidates = PvThermalAnomalyDetector.Detect(temperatures, Width, Height, Settings);

        Assert.Equal(2, candidates.Count);
        var warning = Assert.Single(candidates, c => c.PeakX is >= 8 and < 12);
        var critical = Assert.Single(candidates, c => c.PeakX is >= 44 and < 48);
        Assert.Equal(PvAnomalySeverity.Warning, warning.Severity);
        Assert.Equal(PvAnomalySeverity.Critical, critical.Severity);
        Assert.Equal(16, critical.PixelCount);
        Assert.Equal(20d, critical.DeltaC, 1);
    }

    [Fact]
    public void Detect_IgnoresNanPixelsInsideHotspotNeighborhood()
    {
        var temperatures = Uniform(35f);
        AddBlock(temperatures, 20, 20, 4, 20f);
        temperatures[19 * Width + 19] = float.NaN;
        temperatures[0] = float.PositiveInfinity;

        var candidate = Assert.Single(
            PvThermalAnomalyDetector.Detect(temperatures, Width, Height, Settings));

        Assert.True(double.IsFinite(candidate.PeakTemperatureC));
        Assert.Equal(55d, candidate.PeakTemperatureC, 3);
    }

    [Fact]
    public void Detect_DropsClustersBelowMinimumSize()
    {
        var temperatures = Uniform(35f);
        AddBlock(temperatures, 30, 20, 2, 25f);

        Assert.Empty(PvThermalAnomalyDetector.Detect(temperatures, Width, Height, Settings));
    }

    [Fact]
    public void Detect_RejectsMatrixThatDoesNotMatchResolution()
    {
        Assert.Throws<ArgumentException>(() =>
            PvThermalAnomalyDetector.Detect(new float[10], Width, Height, Settings));
    }

    [Fact]
    public void Detect_RejectsNonPositiveResolution()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PvThermalAnomalyDetector.Detect([], 0, Height, Settings));
    }

    [Theory]
    [InlineData(0, 15, 8, 6)]
    [InlineData(5, 5, 8, 6)]
    [InlineData(5, 15, 1, 6)]
    [InlineData(5, 15, 8, 0)]
    public void Settings_RejectInvalidThresholds(
        double warning,
        double critical,
        int radius,
        int minimumCluster)
    {
        var settings = new PvAnalysisSettings(warning, critical, radius, minimumCluster);

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }
}
