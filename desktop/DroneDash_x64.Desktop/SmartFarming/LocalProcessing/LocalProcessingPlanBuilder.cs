using System.Globalization;
using System.IO;

namespace DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

public static class LocalProcessingPlanBuilder
{
    public static LocalProcessingPlan Build(
        M3mCaptureGroup capture,
        string workspace,
        LocalImageToolchainStatus toolchain)
    {
        if (!capture.IsQuicklookReady)
            throw new InvalidOperationException(
                "Die ausgewählte M3M-Aufnahme ist nicht Quicklook-bereit.");

        if (!toolchain.IsReady)
            throw new InvalidOperationException(
                "GDAL, OTB und Python/OpenCV müssen verfügbar sein.");

        var root = Path.GetFullPath(workspace);
        var registration = Path.Combine(root, "registration");
        var registered = Path.Combine(root, "registered");
        var corrected = Path.Combine(root, "corrected");
        var indices = Path.Combine(root, "indices");

        var steps = new List<LocalProcessingCommand>();

        AddRegistration(
            steps,
            "register-green",
            capture.Green!,
            capture.Nir!,
            Path.Combine(registered, "green_to_nir.tif"),
            Path.Combine(registration, "green_to_nir.json"),
            toolchain);

        AddRegistration(
            steps,
            "register-red",
            capture.Red!,
            capture.Nir!,
            Path.Combine(registered, "red_to_nir.tif"),
            Path.Combine(registration, "red_to_nir.json"),
            toolchain);

        AddRegistration(
            steps,
            "register-rededge",
            capture.RedEdge!,
            capture.Nir!,
            Path.Combine(registered, "rededge_to_nir.tif"),
            Path.Combine(registration, "rededge_to_nir.json"),
            toolchain);

        var correctedGreen = Path.Combine(corrected, "green.tif");
        var correctedRed = Path.Combine(corrected, "red.tif");
        var correctedRedEdge = Path.Combine(corrected, "rededge.tif");
        var correctedNir = Path.Combine(corrected, "nir.tif");

        AddCorrection(
            steps,
            "correct-green",
            Path.Combine(registered, "green_to_nir.tif"),
            correctedGreen,
            capture.Green!,
            toolchain);

        AddCorrection(
            steps,
            "correct-red",
            Path.Combine(registered, "red_to_nir.tif"),
            correctedRed,
            capture.Red!,
            toolchain);

        AddCorrection(
            steps,
            "correct-rededge",
            Path.Combine(registered, "rededge_to_nir.tif"),
            correctedRedEdge,
            capture.RedEdge!,
            toolchain);

        AddCorrection(
            steps,
            "correct-nir",
            capture.Nir!.FilePath,
            correctedNir,
            capture.Nir!,
            toolchain);

        var stack = Path.Combine(root, "m3m_corrected_stack.vrt");
        steps.Add(new(
            "stack-bands",
            "GDAL",
            toolchain.GdalBuildVrt!,
            [
                "-separate",
                stack,
                correctedGreen,
                correctedRed,
                correctedRedEdge,
                correctedNir
            ],
            stack,
            "Erzeugt einen 4-Band-VRT-Stack in der Reihenfolge Green, Red, Red Edge, NIR."));

        AddIndex(
            steps,
            "index-ndvi",
            stack,
            Path.Combine(indices, "ndvi.tif"),
            "(im1b4-im1b2)/(im1b4+im1b2+1e-12)",
            toolchain);

        AddIndex(
            steps,
            "index-ndre",
            stack,
            Path.Combine(indices, "ndre.tif"),
            "(im1b4-im1b3)/(im1b4+im1b3+1e-12)",
            toolchain);

        AddIndex(
            steps,
            "index-gndvi",
            stack,
            Path.Combine(indices, "gndvi.tif"),
            "(im1b4-im1b1)/(im1b4+im1b1+1e-12)",
            toolchain);

        return new LocalProcessingPlan(
            LocalProcessingPlan.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            capture.CaptureKey,
            root,
            steps,
            [
                "Diese Pipeline erzeugt co-registrierte Einzelaufnahme-Quicklooks, keine orthorektifizierte Feldkarte.",
                toolchain.CudaAvailable
                    ? "NVIDIA CUDA ist verfügbar: OpenCV nutzt CUDA automatisch für Resize und finales Warping; ECC selbst bleibt CPU-basiert."
                    : "NVIDIA CUDA ist optional. Ohne CUDA-fähigen OpenCV-Build läuft die Registrierung automatisch vollständig auf der CPU.",
                "Der OpenCV-Worker schreibt registrierte TIFF-Pixel neu; Geo-/XMP-Metadaten dieser Zwischenprodukte sind nicht als Survey-Georeferenzierung zu verwenden.",
                "Für quantitative Feldkarten folgen später ODM/Photogrammetrie, Orthorektifizierung und optional Reflektanzpanel-Kalibrierung."
            ]);
    }

    private static void AddRegistration(
        ICollection<LocalProcessingCommand> steps,
        string id,
        M3mBandMetadata moving,
        M3mBandMetadata reference,
        string output,
        string transform,
        LocalImageToolchainStatus toolchain)
    {
        steps.Add(new(
            id,
            "Python/OpenCV",
            toolchain.PythonExecutable!,
            [
                toolchain.OpenCvWorkerPath,
                "--register",
                "--reference", reference.FilePath,
                "--moving", moving.FilePath,
                "--output", output,
                "--transform", transform,
                "--motion", "affine",
                "--backend", "auto"
            ],
            output,
            $"{moving.Band} per ECC affin auf NIR co-registrieren · Backend: {toolchain.OpenCvBackend}."));
    }

    private static void AddCorrection(
        ICollection<LocalProcessingCommand> steps,
        string id,
        string input,
        string output,
        M3mBandMetadata metadata,
        LocalImageToolchainStatus toolchain)
    {
        var divisor = Math.Pow(2d, metadata.BitsPerSample);
        var expSeconds = metadata.ExposureTimeMicroseconds!.Value / 1_000_000d;

        var expression = string.Format(
            CultureInfo.InvariantCulture,
            "max(0,(im1b1/{0:R}-{1:R}/{0:R})/({2:R}*{3:R})*{4:R}/{5:R})",
            divisor,
            metadata.BlackLevel!.Value,
            metadata.SensorGain!.Value,
            expSeconds,
            metadata.SensorGainAdjustment!.Value,
            metadata.Irradiance!.Value);

        steps.Add(new(
            id,
            "Orfeo ToolBox",
            toolchain.OtbBandMath!,
            [
                "-il", input,
                "-exp", expression,
                "-out", output, "float"
            ],
            output,
            $"{metadata.Band}: DJI DN-Werte mit BlackLevel/Gain/Exposure/GainAdjustment/Irradiance kompensieren."));
    }

    private static void AddIndex(
        ICollection<LocalProcessingCommand> steps,
        string id,
        string stack,
        string output,
        string expression,
        LocalImageToolchainStatus toolchain)
    {
        steps.Add(new(
            id,
            "Orfeo ToolBox",
            toolchain.OtbBandMathX!,
            [
                "-il", stack,
                "-exp", expression,
                "-out", output, "float"
            ],
            output,
            $"{id.Replace("index-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant()} aus dem korrigierten 4-Band-Stack berechnen."));
    }
}
