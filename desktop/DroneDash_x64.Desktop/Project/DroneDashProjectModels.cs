namespace DroneDash_x64.Desktop.Project;

public enum ProjectArtifactKind
{
    FlightPlan,
    DjiWaylineKmz,
    PhotogrammetryManifest,
    PvAnalysis,
    SmartFarmingDataset,
    SmartFarmingFieldProducts,
    LocalProcessingPlan,
    NodeOdmResultArchive,
    VegetationRaster,
    ThermalImage,
    MultispectralImage,
    SourceDataFolder,
    ProcessingWorkspace,
    Other,
    Orthomosaic,
    ElevationModel,
    PointCloud
}

public enum ProjectReferenceKind
{
    File,
    Directory
}

public enum ProjectArtifactIntegrity
{
    Unknown,
    Ok,
    Modified,
    Missing,
    Error
}

public sealed record DroneDashProjectArtifact(
    Guid Id,
    ProjectArtifactKind Kind,
    ProjectReferenceKind ReferenceKind,
    string StoredPath,
    bool IsRelative,
    string Label,
    DateTimeOffset AddedAtUtc,
    long? SizeBytes,
    DateTimeOffset? LastWriteUtc,
    string? Sha256,
    string? Notes)
{
    public string PathModeText =>
        IsRelative ? "relativ" : "absolut";
}

public sealed record DroneDashProject(
    int SchemaVersion,
    Guid ProjectId,
    string Name,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset SavedAtUtc,
    IReadOnlyList<DroneDashProjectArtifact> Artifacts)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ProjectArtifactVerification(
    DroneDashProjectArtifact Artifact,
    string ResolvedPath,
    ProjectArtifactIntegrity Integrity,
    string Detail)
{
    public string IntegrityText => Integrity switch
    {
        ProjectArtifactIntegrity.Ok => "OK",
        ProjectArtifactIntegrity.Modified => "Geändert",
        ProjectArtifactIntegrity.Missing => "Fehlt",
        ProjectArtifactIntegrity.Error => "Fehler",
        _ => "Ungeprüft"
    };
}

public sealed class ProjectArtifactRow
{
    public required DroneDashProjectArtifact Artifact { get; init; }
    public required string ResolvedPath { get; init; }
    public ProjectArtifactIntegrity Integrity { get; set; }
    public string Detail { get; set; } = "Ungeprüft";

    public Guid Id => Artifact.Id;
    public string KindText => Artifact.Kind.ToString();
    public string Label => Artifact.Label;
    public string StoredPath => Artifact.StoredPath;
    public string PathModeText => Artifact.PathModeText;
    public string IntegrityText => Integrity switch
    {
        ProjectArtifactIntegrity.Ok => "OK",
        ProjectArtifactIntegrity.Modified => "Geändert",
        ProjectArtifactIntegrity.Missing => "Fehlt",
        ProjectArtifactIntegrity.Error => "Fehler",
        _ => "Ungeprüft"
    };
    public string SizeText => Artifact.SizeBytes is long size
        ? FormatBytes(size)
        : "—";

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)value;
        var unit = 0;

        while (size >= 1024d && unit < units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
