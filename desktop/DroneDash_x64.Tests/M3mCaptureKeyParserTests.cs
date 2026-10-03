using System.IO;
using DroneDash_x64.Desktop.SmartFarming;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class M3mCaptureKeyParserTests
{
    [Theory]
    [InlineData("DJI_20260101120000_0001_D.JPG", "DJI_20260101120000_0001", "D")]
    [InlineData("DJI_20260101120000_0001_MS_G.TIF", "DJI_20260101120000_0001", "MS_G")]
    [InlineData("dji_20260101120000_0042_ms_nir.tiff", "dji_20260101120000_0042", "MS_NIR")]
    public void TryParse_AcceptsM3mCaptureNames(string fileName, string expectedKey, string expectedKind)
    {
        Assert.True(M3mCaptureKeyParser.TryParse(fileName, out var key, out var kind));
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedKind, kind);
    }

    [Fact]
    public void TryParse_IgnoresDirectoryPart()
    {
        var path = Path.Combine("Daten", "DJI_20260101120000_0099_X", "DJI_20260101120000_0001_MS_RE.TIF");

        Assert.True(M3mCaptureKeyParser.TryParse(path, out var key, out var kind));
        Assert.Equal("DJI_20260101120000_0001", key);
        Assert.Equal("MS_RE", kind);
    }

    [Theory]
    [InlineData("DJI_20260101120000_0001_T.JPG")]
    [InlineData("DJI_20260101120000_0001_MS_B.TIF")]
    [InlineData("DJI_2026010112000_0001_D.JPG")]
    [InlineData("DJI_20260101120000_0001_D.PNG")]
    [InlineData("")]
    public void TryParse_RejectsOtherFiles(string fileName)
    {
        Assert.False(M3mCaptureKeyParser.TryParse(fileName, out var key, out var kind));
        Assert.Equal("", key);
        Assert.Equal("", kind);
    }

    [Theory]
    [InlineData("MS_G", M3mBand.Green)]
    [InlineData("ms_r", M3mBand.Red)]
    [InlineData("MS_RE", M3mBand.RedEdge)]
    [InlineData("MS_NIR", M3mBand.Nir)]
    public void BandFromKind_MapsMultispectralBands(string kind, M3mBand expected)
    {
        Assert.Equal(expected, M3mCaptureKeyParser.BandFromKind(kind));
    }

    [Fact]
    public void BandFromKind_ReturnsNullForRgb()
    {
        Assert.Null(M3mCaptureKeyParser.BandFromKind("D"));
    }
}
