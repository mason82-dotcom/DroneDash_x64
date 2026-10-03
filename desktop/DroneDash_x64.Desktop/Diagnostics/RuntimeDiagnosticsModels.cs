namespace DroneDash_x64.Desktop.Diagnostics;

public enum RuntimeDiagnosticState
{
    Ready,
    OptionalMissing,
    Warning,
    Error
}

public sealed record RuntimeDiagnosticItem(
    string Category,
    string Name,
    RuntimeDiagnosticState State,
    string? Version,
    string? Path,
    string Detail)
{
    public string StateText => State switch
    {
        RuntimeDiagnosticState.Ready => "Bereit",
        RuntimeDiagnosticState.OptionalMissing => "Optional / fehlt",
        RuntimeDiagnosticState.Warning => "Warnung",
        RuntimeDiagnosticState.Error => "Fehler",
        _ => State.ToString()
    };

    public string VersionText =>
        string.IsNullOrWhiteSpace(Version)
            ? "—"
            : Version;

    public string PathText =>
        string.IsNullOrWhiteSpace(Path)
            ? "—"
            : Path;
}

public sealed record RuntimeDiagnosticsSnapshot(
    DateTimeOffset CreatedAtUtc,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string FrameworkDescription,
    IReadOnlyList<RuntimeDiagnosticItem> Items)
{
    public int ReadyCount =>
        Items.Count(item =>
            item.State == RuntimeDiagnosticState.Ready);

    public int OptionalMissingCount =>
        Items.Count(item =>
            item.State == RuntimeDiagnosticState.OptionalMissing);

    public int WarningCount =>
        Items.Count(item =>
            item.State == RuntimeDiagnosticState.Warning);

    public int ErrorCount =>
        Items.Count(item =>
            item.State == RuntimeDiagnosticState.Error);

    public bool HasBlockingErrors =>
        ErrorCount > 0;

    public string SummaryText =>
        $"Bereit {ReadyCount} · Optional fehlt {OptionalMissingCount} · " +
        $"Warnungen {WarningCount} · Fehler {ErrorCount}";
}
