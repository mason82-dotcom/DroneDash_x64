using System.IO;
using DroneDash_x64.Desktop.Photogrammetry;
using DroneDash_x64.Desktop.Thermal;

namespace DroneDash_x64.Desktop.PvAnalysis;

public static class PvDatasetAnalyzer
{
    public static PvDatasetResult Analyze(
        string sourceFolder,
        PvAnalysisSettings settings,
        string? flightPlanProjectPath = null)
    {
        settings.Validate();

        var metadataDataset =
            PhotogrammetryDatasetAnalyzer.Analyze(
                sourceFolder,
                flightPlanProjectPath);

        var thermalMetadata = metadataDataset.Images
            .Where(IsThermalCandidate)
            .ToArray();

        using var sdk = new DjiThermalSdk();
        if (!sdk.IsAvailable)
            throw new InvalidOperationException(sdk.Status);

        var results = new List<PvImageAnalysisResult>(thermalMetadata.Length);

        foreach (var metadata in thermalMetadata)
        {
            var path = Path.Combine(
                metadataDataset.SourceFolder,
                metadata.RelativePath);

            try
            {
                var thermal = sdk.Analyze(path, ThermalPalette.IronRed);
                var candidates = PvThermalAnomalyDetector.Detect(
                    thermal.Temperatures,
                    thermal.Width,
                    thermal.Height,
                    settings);

                results.Add(new PvImageAnalysisResult(
                    metadata.FileName,
                    metadata.RelativePath,
                    metadata.ImageSource,
                    metadata.Latitude,
                    metadata.Longitude,
                    metadata.RtkFlag,
                    metadata.RtkStdLongitudeMeters,
                    metadata.RtkStdLatitudeMeters,
                    metadata.RtkStdHeightMeters,
                    metadata.AssignedWaylineId,
                    metadata.AssignedPassName,
                    metadata.AssignedSegmentIndex,
                    metadata.DistanceToRouteMeters,
                    thermal.Width,
                    thermal.Height,
                    thermal.MinimumC,
                    thermal.MaximumC,
                    thermal.AverageC,
                    thermal.Emissivity,
                    thermal.DistanceM,
                    candidates,
                    null));
            }
            catch (Exception ex)
            {
                results.Add(new PvImageAnalysisResult(
                    metadata.FileName,
                    metadata.RelativePath,
                    metadata.ImageSource,
                    metadata.Latitude,
                    metadata.Longitude,
                    metadata.RtkFlag,
                    metadata.RtkStdLongitudeMeters,
                    metadata.RtkStdLatitudeMeters,
                    metadata.RtkStdHeightMeters,
                    metadata.AssignedWaylineId,
                    metadata.AssignedPassName,
                    metadata.AssignedSegmentIndex,
                    metadata.DistanceToRouteMeters,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    ex.Message));
            }
        }

        var successful = results.Count(result =>
            string.IsNullOrWhiteSpace(result.ProcessingError));

        var candidateImages = results.Count(result =>
            result.Candidates.Count > 0);

        var warningImages = results.Count(result =>
            result.Severity == PvAnomalySeverity.Warning);

        var criticalImages = results.Count(result =>
            result.Severity == PvAnomalySeverity.Critical);

        var allCandidates = results
            .SelectMany(result => result.Candidates)
            .ToArray();

        var summary = new PvDatasetSummary(
            thermalMetadata.Length,
            successful,
            thermalMetadata.Length - successful,
            candidateImages,
            warningImages,
            criticalImages,
            allCandidates.Length,
            allCandidates.Length == 0
                ? null
                : allCandidates.Max(candidate => candidate.DeltaC));

        return new PvDatasetResult(
            metadataDataset.SourceFolder,
            metadataDataset.FlightPlanProjectPath,
            settings,
            results,
            summary);
    }

    private static bool IsThermalCandidate(
        PhotogrammetryImageRecord image)
    {
        if (image.ImageSource?.Contains(
                "Infrared",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var name = image.FileName;
        return name.EndsWith(
                   "_T.JPG",
                   StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(
                   "_T.JPEG",
                   StringComparison.OrdinalIgnoreCase);
    }
}
