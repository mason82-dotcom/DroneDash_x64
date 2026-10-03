using System.IO;

namespace DroneDash_x64.Desktop.Photogrammetry;

public enum PointCloudFormat
{
    Copc,
    Laz,
    Las,
    Ply
}

public sealed record OdmElevationProducts(
    string ResultFolder,
    string? DsmPath,
    string? DtmPath,
    string? OrthophotoPath,
    string? PointCloudPath,
    PointCloudFormat? CloudFormat,
    IReadOnlyList<string> Warnings)
{
    public bool HasAny =>
        DsmPath is not null || DtmPath is not null ||
        OrthophotoPath is not null || PointCloudPath is not null;

    public IEnumerable<string> Files =>
        new[] { DsmPath, DtmPath, OrthophotoPath, PointCloudPath }
            .Where(path => path is not null)
            .Select(path => path!);

    /// <summary>
    /// Finds the elevation and point-cloud products of an extracted ODM result
    /// (NodeODM all.zip layout).
    /// </summary>
    public static OdmElevationProducts Locate(string resultFolder)
    {
        if (!Directory.Exists(resultFolder))
            throw new DirectoryNotFoundException($"ODM-Ergebnisordner fehlt: {resultFolder}");

        var root = Path.GetFullPath(resultFolder);
        var warnings = new List<string>();

        string? Find(params string[] relativePaths) =>
            relativePaths
                .Select(relative => Path.Combine(root, relative))
                .FirstOrDefault(File.Exists);

        var dsm = Find(Path.Combine("odm_dem", "dsm.tif"));
        var dtm = Find(Path.Combine("odm_dem", "dtm.tif"));
        var orthophoto = Find(Path.Combine("odm_orthophoto", "odm_orthophoto.tif"));

        // Prefer the cloud-optimized variant, then compressed, then plain formats.
        (string Relative, PointCloudFormat Format)[] cloudCandidates =
        [
            (Path.Combine("odm_georeferencing", "odm_georeferenced_model.copc.laz"), PointCloudFormat.Copc),
            (Path.Combine("odm_georeferencing", "odm_georeferenced_model.laz"), PointCloudFormat.Laz),
            (Path.Combine("odm_georeferencing", "odm_georeferenced_model.las"), PointCloudFormat.Las),
            (Path.Combine("odm_georeferencing", "odm_georeferenced_model.ply"), PointCloudFormat.Ply)
        ];

        var cloud = cloudCandidates.FirstOrDefault(c => File.Exists(Path.Combine(root, c.Relative)));
        var cloudPath = cloud.Relative is null ? null : Path.Combine(root, cloud.Relative);
        PointCloudFormat? cloudFormat = cloud.Relative is null ? null : cloud.Format;

        if (dsm is null)
            warnings.Add("Kein DSM (odm_dem/dsm.tif) gefunden; wurde der Task mit --dsm verarbeitet?");
        if (dtm is null)
            warnings.Add("Kein DTM (odm_dem/dtm.tif) gefunden; wurde der Task mit --dtm verarbeitet?");
        if (cloudPath is null)
            warnings.Add("Keine georeferenzierte Punktwolke (odm_georeferencing) gefunden.");
        if (orthophoto is null)
            warnings.Add("Kein Orthomosaik (odm_orthophoto/odm_orthophoto.tif) gefunden.");

        return new OdmElevationProducts(root, dsm, dtm, orthophoto, cloudPath, cloudFormat, warnings);
    }
}
