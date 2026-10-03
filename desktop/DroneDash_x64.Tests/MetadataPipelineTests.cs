using System.IO;
using System.Text;
using DroneDash_x64.Desktop.Imaging;
using DroneDash_x64.Desktop.SmartFarming;
using Xunit;

namespace DroneDash_x64.Tests;

public sealed class MetadataPipelineTests
{
    [Fact]
    public void XmpReader_HandlesMarkerSplitAcrossReadBuffer()
    {
        var root = CreateTempDirectory();

        try
        {
            var path = Path.Combine(root, "split-xmp.bin");

            using (var stream = File.Create(path))
            {
                stream.Write(
                    Enumerable.Repeat((byte)'A', 65_531)
                        .ToArray());

                stream.Write(
                    Encoding.UTF8.GetBytes(
                        """<x:xmpmeta xmlns:x="adobe:ns:meta/" xmlns:drone-dji="http://www.dji.com/drone-dji/1.0/"><drone-dji:GPSLatitude>+49.123456</drone-dji:GPSLatitude><drone-dji:RtkFlag>50</drone-dji:RtkFlag></x:xmpmeta>"""));
            }

            var metadata = DjiXmpMetadataReader.Read(path);

            Assert.True(metadata.Found);
            Assert.Null(metadata.Error);
            Assert.Equal(
                "+49.123456",
                metadata.Fields["GPSLatitude"]);
            Assert.Equal(
                "50",
                metadata.Fields["RtkFlag"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SafeEnumerator_FiltersFilesAndHonorsCancellation()
    {
        var root = CreateTempDirectory();

        try
        {
            var nested = Directory.CreateDirectory(
                Path.Combine(root, "nested"));

            File.WriteAllText(
                Path.Combine(root, "one.jpg"),
                "x");
            File.WriteAllText(
                Path.Combine(nested.FullName, "two.tif"),
                "x");
            File.WriteAllText(
                Path.Combine(nested.FullName, "ignore.txt"),
                "x");

            var found = SafeDatasetFileEnumerator
                .EnumerateFiles(
                    root,
                    path =>
                        Path.GetExtension(path)
                            .Equals(
                                ".jpg",
                                StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(path)
                            .Equals(
                                ".tif",
                                StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToArray();

            Assert.Equal(
                ["one.jpg", "two.tif"],
                found);

            using var cts =
                new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                SafeDatasetFileEnumerator
                    .EnumerateFiles(
                        root,
                        _ => true,
                        cts.Token)
                    .ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanProgress_ClampsPercentage()
    {
        Assert.Equal(
            50d,
            new DatasetScanProgress(10, 5, "five.jpg").Percent);
        Assert.Equal(
            100d,
            new DatasetScanProgress(10, 12, null).Percent);
        Assert.Equal(
            0d,
            new DatasetScanProgress(0, 0, null).Percent);
    }

    [Fact]
    public void M3mCaptureParser_RecognizesMultispectralBand()
    {
        Assert.True(
            M3mCaptureKeyParser.TryParse(
                "DJI_20261003120000_0042_MS_NIR.TIF",
                out var key,
                out var kind));

        Assert.Equal(
            "DJI_20261003120000_0042",
            key);
        Assert.Equal(
            "MS_NIR",
            kind);
        Assert.Equal(
            M3mBand.Nir,
            M3mCaptureKeyParser.BandFromKind(kind));
    }

    [Fact]
    public void VegetationIndexMath_ComputesExpectedNdvi()
    {
        var result =
            M3mVegetationIndexEngine.ComputeFromSignals(
                "TEST",
                VegetationIndexType.Ndvi,
                [0.8f, 0.8f],
                [0.2f, 0.2f],
                2,
                1);

        Assert.Equal(
            0.6,
            result.Average,
            precision: 5);
        Assert.Equal(2, result.ValidPixelCount);
        Assert.False(result.IsOrthorectified);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "DroneDash_Tests_" +
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);
        return path;
    }
}
