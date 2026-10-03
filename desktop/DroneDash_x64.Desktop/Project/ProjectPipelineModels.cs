namespace DroneDash_x64.Desktop.Project;

public enum ProjectPipelineModule
{
    Unknown,
    Photogrammetry,
    PvAnalysis,
    SmartFarming
}

public enum ProjectPipelineStage
{
    Ingested,
    DatasetQa,
    Processing,
    Results
}

public enum ProjectPipelineGateState
{
    Open,
    Pass,
    Warning,
    Blocked
}

public sealed record ProjectPipelinePath(
    string StoredPath,
    bool IsRelative);

public sealed record ProjectDatasetProbe(
    ProjectPipelineModule Module,
    int TotalFiles,
    int SupportedImageFiles,
    int ThermalFiles,
    int M3mFiles,
    int M3mBandFiles,
    bool Ambiguous,
    string Detail);

public sealed record ProjectPipelineState(
    int SchemaVersion,
    Guid JobId,
    ProjectPipelineModule Module,
    ProjectPipelineStage Stage,
    ProjectPipelinePath SourceFolder,
    ProjectPipelinePath? FlightPlan,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ProjectDatasetProbe Probe)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ProjectPipelineGate(
    string Name,
    ProjectPipelineGateState State,
    string Detail)
{
    public string StateText => State switch
    {
        ProjectPipelineGateState.Pass => "PASS",
        ProjectPipelineGateState.Warning => "PRÜFEN",
        ProjectPipelineGateState.Blocked => "BLOCKIERT",
        _ => "OFFEN"
    };
}

public sealed record ProjectPipelineGateSnapshot(
    ProjectPipelineGate FlightPlan,
    ProjectPipelineGate Dataset,
    ProjectPipelineGate Rtk,
    ProjectPipelineGate Processing,
    ProjectPipelineGate Results)
{
    public IReadOnlyList<ProjectPipelineGate> Gates =>
        [FlightPlan, Dataset, Rtk, Processing, Results];

    public int PassedCount =>
        Gates.Count(gate =>
            gate.State ==
            ProjectPipelineGateState.Pass);

    public bool HasBlocker =>
        Gates.Any(gate =>
            gate.State ==
            ProjectPipelineGateState.Blocked);

    public string SummaryText =>
        $"{PassedCount}/5 Gates PASS" +
        (HasBlocker
            ? " · Blocker vorhanden"
            : "");
}
