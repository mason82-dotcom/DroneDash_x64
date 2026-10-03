using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

namespace DroneDash_x64.Desktop.Photogrammetry;

public sealed record ColmapReferenceImage(
    string Name,
    double Latitude,
    double Longitude,
    double? Altitude);

public sealed record ColmapProcessingPlan(
    LocalProcessingPlan Plan,
    string ImageListPath,
    string ReferenceImagesPath,
    IReadOnlyList<ColmapReferenceImage> Images,
    string ProductsFolder,
    string DsmPath,
    string PointCloudPath,
    string ProductsReportPath,
    IReadOnlyList<string> Directories);

/// <summary>
/// Local photogrammetry with COLMAP: GPU features and GPS-guided matching, sparse
/// reconstruction, CUDA PatchMatch stereo and fusion; DroneDash's worker then georeferences
/// the cloud against the image positions and writes a LAZ point cloud and a DSM.
/// </summary>
public static class ColmapPlanBuilder
{
    // Principal points / focal lengths within this many pixels count as one calibration.
    private const double CalibrationTolerancePixels = 0.5;

    public static ColmapProcessingPlan Build(
        PhotogrammetryDatasetResult dataset,
        ColmapStatus colmap,
        string pythonExecutable,
        string workerPath,
        string workspace,
        PhotogrammetryOdmPreset preset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(colmap);

        if (!colmap.Available || string.IsNullOrWhiteSpace(colmap.Executable))
            throw new InvalidOperationException("COLMAP wurde nicht gefunden.");
        if (colmap.CudaEnabled != true)
        {
            throw new InvalidOperationException(
                "Die gefundene COLMAP-Installation hat keine CUDA-Unterstützung. Die dichte " +
                "Rekonstruktion (patch_match_stereo) braucht die CUDA-Version von COLMAP und eine NVIDIA-GPU.");
        }

        if (string.IsNullOrWhiteSpace(pythonExecutable) || string.IsNullOrWhiteSpace(workerPath))
            throw new InvalidOperationException("Python-Worker fehlt für Georeferenzierung und DSM.");

        var warnings = new List<string>();
        var inputs = PhotogrammetryOdmPlan.SelectInputFiles(dataset);
        warnings.AddRange(inputs.Notes);

        var imageRoot = Path.GetFullPath(dataset.SourceFolder);
        var byPath = dataset.Images.ToDictionary(
            image => Path.GetFullPath(Path.Combine(dataset.SourceFolder, image.RelativePath)),
            StringComparer.OrdinalIgnoreCase);

        var selected = new List<(PhotogrammetryImageRecord Image, string Name)>();
        var withSpaces = 0;
        foreach (var file in inputs.Files)
        {
            var full = Path.GetFullPath(file);
            if (!byPath.TryGetValue(full, out var image))
                continue;

            // COLMAP addresses images by their path relative to --image_path, always with '/'.
            var name = Path.GetRelativePath(imageRoot, full).Replace('\\', '/');
            if (name.Any(char.IsWhiteSpace))
            {
                withSpaces++;
                continue;
            }

            selected.Add((image, name));
        }

        if (withSpaces > 0)
            warnings.Add($"{withSpaces} Bilder mit Leerzeichen im Pfad ausgeschlossen (COLMAP-Bildliste).");

        if (selected.Count < 3)
            throw new InvalidOperationException("Für COLMAP werden mindestens 3 RGB-Bilder mit GPS gebraucht.");

        var withAltitude = selected.Count(entry => entry.Image.AbsoluteAltitudeMeters.HasValue);
        if (withAltitude < 3)
        {
            throw new InvalidOperationException(
                "Für die Georeferenzierung brauchen mindestens 3 Bilder eine absolute Höhe (XMP AbsoluteAltitude).");
        }

        if (withAltitude < selected.Count)
            warnings.Add($"{selected.Count - withAltitude} Bilder ohne absolute Höhe zählen nicht zur Georeferenzierung.");

        var (featureSize, denseSize, resolution) = preset switch
        {
            PhotogrammetryOdmPreset.Fast => (1600, 1200, 0.2),
            PhotogrammetryOdmPreset.Standard => (3200, 2000, 0.1),
            PhotogrammetryOdmPreset.High => (4800, 3200, 0.05),
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };

        // Close gaps up to about one metre; larger holes stay nodata.
        var fillDistance = Math.Max(3, (int)Math.Ceiling(1.0 / resolution));

        workspace = Path.GetFullPath(workspace);
        var georefFolder = Path.Combine(workspace, "georef");
        var sparseFolder = Path.Combine(workspace, "sparse");
        var model = Path.Combine(sparseFolder, "0");
        var denseFolder = Path.Combine(workspace, "dense");
        var productsFolder = Path.Combine(workspace, "products");
        var database = Path.Combine(workspace, "database.db");
        var imageList = Path.Combine(workspace, "image-list.txt");
        var referenceImages = Path.Combine(workspace, "images.json");
        var fused = Path.Combine(denseFolder, "fused.ply");
        var dsm = Path.Combine(productsFolder, "dsm.tif");
        var cloud = Path.Combine(productsFolder, "colmap_georeferenced_model.laz");
        var report = Path.Combine(productsFolder, "colmap-products.json");

        var options = colmap.Options;
        var colmapExe = colmap.Executable;
        var steps = new List<LocalProcessingCommand>
        {
            new(
                "georef",
                "Python/GDAL",
                pythonExecutable,
                [workerPath, "--colmap-georef", "--images", referenceImages, "--output-dir", georefFolder],
                Path.Combine(georefFolder, "georef.json"),
                "GPS-Positionen der Bilder in UTM umrechnen")
        };

        var extractor = new List<string>
        {
            "feature_extractor",
            "--database_path", database,
            "--image_path", imageRoot,
            "--image_list_path", imageList,
            // RADIAL keeps one focal length; OPENCV's separate fx/fy drift apart on nadir grids.
            "--ImageReader.camera_model", "RADIAL",
            "--ImageReader.single_camera_per_folder", "1"
        };

        var calibration = CommonCalibration(selected.Select(entry => entry.Image).ToArray());
        if (calibration is { } known)
        {
            extractor.Add("--ImageReader.camera_params");
            extractor.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{known.Focal:0.###},{known.Cx:0.###},{known.Cy:0.###},0,0"));
        }
        else
        {
            warnings.Add(
                "Keine einheitliche DJI-Kamerakalibrierung (XMP) gefunden; COLMAP schätzt die Brennweite " +
                "selbst. Höhen können dadurch systematisch abweichen.");
        }

        AddOption(extractor, options, warnings, "1", "SiftExtraction.use_gpu", "FeatureExtraction.use_gpu");
        AddOption(extractor, options, warnings, featureSize.ToString(CultureInfo.InvariantCulture),
            "SiftExtraction.max_image_size", "FeatureExtraction.max_image_size");
        steps.Add(new("features", "COLMAP", colmapExe, extractor, database, "Merkmale extrahieren (GPU)"));

        var matcher = new List<string> { "spatial_matcher", "--database_path", database };
        AddOption(matcher, options, warnings, "1", "SiftMatching.use_gpu", "FeatureMatching.use_gpu");
        AddOption(matcher, options, warnings, "1", "SpatialMatching.is_gps");
        AddOption(matcher, options, warnings, "50", "SpatialMatching.max_num_neighbors");
        steps.Add(new("matching", "COLMAP", colmapExe, matcher, null, "Nachbarbilder per GPS matchen (GPU)"));

        var mapper = new List<string>
        {
            "mapper",
            "--database_path", database,
            "--image_path", imageRoot,
            "--output_path", sparseFolder
        };
        // With the DJI calibration fixed, bundle adjustment must not let the focal length drift:
        // a scaled focal length shows up as a wrong flight height and a tilted surface.
        var refineIntrinsics = calibration is null ? "1" : "0";
        AddOption(mapper, options, warnings, refineIntrinsics, "Mapper.ba_refine_focal_length");
        AddOption(mapper, options, warnings, "0", "Mapper.ba_refine_principal_point");
        AddOption(mapper, options, warnings, "1", "Mapper.ba_refine_extra_params");
        steps.Add(new(
            "mapper", "COLMAP", colmapExe, mapper, Path.Combine(model, "images.bin"),
            "Kameras und dünne Punktwolke rekonstruieren"));

        var undistorter = new List<string>
        {
            "image_undistorter",
            "--image_path", imageRoot,
            "--input_path", model,
            "--output_path", denseFolder,
            "--output_type", "COLMAP"
        };
        AddOption(undistorter, options, warnings, denseSize.ToString(CultureInfo.InvariantCulture), "max_image_size");
        steps.Add(new(
            "undistort", "COLMAP", colmapExe, undistorter, Path.Combine(denseFolder, "sparse", "images.bin"),
            "Bilder entzerren"));

        var stereo = new List<string>
        {
            "patch_match_stereo",
            "--workspace_path", denseFolder,
            "--workspace_format", "COLMAP"
        };
        AddOption(stereo, options, warnings, "true", "PatchMatchStereo.geom_consistency");
        AddOption(stereo, options, warnings, denseSize.ToString(CultureInfo.InvariantCulture),
            "PatchMatchStereo.max_image_size");
        steps.Add(new("stereo", "COLMAP", colmapExe, stereo, null, "Tiefenkarten berechnen (CUDA)"));

        steps.Add(new(
            "fusion", "COLMAP", colmapExe,
            [
                "stereo_fusion",
                "--workspace_path", denseFolder,
                "--workspace_format", "COLMAP",
                "--input_type", "geometric",
                "--output_path", fused
            ],
            fused,
            "Tiefenkarten zur dichten Punktwolke fusionieren"));

        steps.Add(new(
            "products",
            "Python/GDAL",
            pythonExecutable,
            [
                workerPath, "--colmap-products",
                "--ply", fused,
                "--sparse", model,
                "--georef", Path.Combine(georefFolder, "georef.json"),
                "--output-dir", productsFolder,
                "--resolution", resolution.ToString(CultureInfo.InvariantCulture),
                "--fill-distance", fillDistance.ToString(CultureInfo.InvariantCulture)
            ],
            dsm,
            "Georeferenzieren, LAZ-Punktwolke und DSM schreiben"));

        var images = selected
            .Select(entry => new ColmapReferenceImage(
                entry.Name,
                entry.Image.Latitude!.Value,
                entry.Image.Longitude!.Value,
                entry.Image.AbsoluteAltitudeMeters))
            .ToArray();

        var plan = new LocalProcessingPlan(
            LocalProcessingPlan.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "colmap",
            workspace,
            steps,
            warnings);

        return new ColmapProcessingPlan(
            plan,
            imageList,
            referenceImages,
            images,
            productsFolder,
            dsm,
            cloud,
            report,
            [workspace, georefFolder, sparseFolder, denseFolder, productsFolder]);
    }

    /// <summary>
    /// Creates the workspace and writes the image list, GPS references and the plan. A previous COLMAP
    /// database is removed: feature_extractor would otherwise add to it.
    /// </summary>
    public static void PrepareWorkspace(ColmapProcessingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var directory in plan.Directories)
            Directory.CreateDirectory(directory);

        var database = Path.Combine(plan.Plan.Workspace, "database.db");
        foreach (var file in new[] { database, database + "-wal", database + "-shm" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }

        var utf8 = new UTF8Encoding(false);
        File.WriteAllLines(plan.ImageListPath, plan.Images.Select(image => image.Name), utf8);
        File.WriteAllText(
            plan.ReferenceImagesPath,
            JsonSerializer.Serialize(
                plan.Images.Select(image => new
                {
                    name = image.Name,
                    lat = image.Latitude,
                    lon = image.Longitude,
                    alt = image.Altitude
                })),
            utf8);

        // The exact commands, for reruns outside DroneDash and for support.
        File.WriteAllText(
            Path.Combine(plan.Plan.Workspace, "colmap-plan.json"),
            JsonSerializer.Serialize(plan.Plan, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }),
            utf8);
    }

    /// <summary>Focal length and principal point shared by all images, or null.</summary>
    internal static (double Focal, double Cx, double Cy)? CommonCalibration(
        IReadOnlyList<PhotogrammetryImageRecord> images)
    {
        if (images.Count == 0)
            return null;

        var first = images[0];
        if (first.CalibratedFocalLength is not > 0 ||
            first.OpticalCenterX is not > 0 ||
            first.OpticalCenterY is not > 0)
        {
            return null;
        }

        foreach (var image in images)
        {
            if (image.CalibratedFocalLength is not { } focal ||
                image.OpticalCenterX is not { } cx ||
                image.OpticalCenterY is not { } cy ||
                Math.Abs(focal - first.CalibratedFocalLength.Value) > CalibrationTolerancePixels ||
                Math.Abs(cx - first.OpticalCenterX.Value) > CalibrationTolerancePixels ||
                Math.Abs(cy - first.OpticalCenterY.Value) > CalibrationTolerancePixels)
            {
                return null;
            }
        }

        return (first.CalibratedFocalLength.Value, first.OpticalCenterX.Value, first.OpticalCenterY.Value);
    }

    /// <summary>
    /// Adds the first option name the installed COLMAP knows. When no option of that group is
    /// known at all, the help of the command could not be read (patch_match_stereo prints no
    /// help without CUDA); then the first (COLMAP 3.9 – 3.11) name is used.
    /// </summary>
    private static void AddOption(
        List<string> arguments,
        IReadOnlySet<string> supported,
        List<string> warnings,
        string value,
        params string[] names)
    {
        var groups = names
            .Where(name => name.Contains('.'))
            .Select(name => name[..(name.IndexOf('.') + 1)])
            .ToArray();
        var helpKnown = groups.Length == 0
            ? supported.Count > 0
            : supported.Any(option => groups.Any(group => option.StartsWith(group, StringComparison.Ordinal)));

        var name = helpKnown
            ? names.FirstOrDefault(supported.Contains)
            : names[0];

        if (name is null)
        {
            warnings.Add($"COLMAP kennt die Option {names[0]} nicht; Standardwert wird verwendet.");
            return;
        }

        arguments.Add("--" + name);
        arguments.Add(value);
    }
}
