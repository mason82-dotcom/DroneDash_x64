namespace DroneDash_x64.Desktop.SmartFarming.LocalProcessing;

public enum LocalImageToolKind
{
    Gdal,
    OrfeoToolBox,
    PythonOpenCv
}

public sealed record LocalImageToolStatus(
    LocalImageToolKind Kind,
    string Name,
    bool Available,
    string? Version,
    string? PrimaryExecutable,
    string Detail)
{
    public string StateText => Available ? "Bereit" : "Fehlt";
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version;
    public string ExecutableText => string.IsNullOrWhiteSpace(PrimaryExecutable)
        ? "—"
        : PrimaryExecutable;
}

public sealed record LocalImageToolchainStatus(
    LocalImageToolStatus Gdal,
    LocalImageToolStatus Otb,
    LocalImageToolStatus OpenCv,
    string? GdalBuildVrt,
    string? OtbBandMath,
    string? OtbBandMathX,
    string? PythonExecutable,
    string OpenCvWorkerPath)
{
    public bool IsReady =>
        Gdal.Available &&
        Otb.Available &&
        OpenCv.Available &&
        !string.IsNullOrWhiteSpace(GdalBuildVrt) &&
        !string.IsNullOrWhiteSpace(OtbBandMath) &&
        !string.IsNullOrWhiteSpace(OtbBandMathX) &&
        !string.IsNullOrWhiteSpace(PythonExecutable);

    public IReadOnlyList<LocalImageToolStatus> Tools =>
        [Gdal, Otb, OpenCv];
}

public sealed record LocalProcessingCommand(
    string Id,
    string Tool,
    string Program,
    IReadOnlyList<string> Arguments,
    string? OutputPath,
    string Description);

public sealed record LocalProcessingPlan(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string CaptureKey,
    string Workspace,
    IReadOnlyList<LocalProcessingCommand> Steps,
    IReadOnlyList<string> Warnings)
{
    public const int CurrentSchemaVersion = 1;
}
