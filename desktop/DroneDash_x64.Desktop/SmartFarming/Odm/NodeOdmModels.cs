namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public sealed record NodeOdmServerInfo(
    bool Available,
    string Endpoint,
    string? ApiVersion,
    string? Engine,
    string? EngineVersion,
    int? TaskQueueCount,
    int? MaxImages,
    int? CpuCores,
    long? AvailableMemoryBytes,
    string Detail)
{
    public bool SupportsMavic3M =>
        Available &&
        VersionAtLeast(
            EngineVersion,
            new Version(3, 5, 3));

    public string StateText =>
        Available ? "Bereit" : "Nicht erreichbar";

    public string M3mSupportText =>
        !Available
            ? "—"
            : SupportsMavic3M
                ? "M3M unterstützt"
                : "ODM < 3.5.3 oder Version unbekannt";

    private static bool VersionAtLeast(
        string? value,
        Version minimum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var numeric = new string(
            value
                .SkipWhile(character => !char.IsDigit(character))
                .TakeWhile(character =>
                    char.IsDigit(character) ||
                    character == '.')
                .ToArray());

        return Version.TryParse(
                   numeric,
                   out var parsed) &&
               parsed >= minimum;
    }
}

public sealed record NodeOdmOption(
    string Name,
    object Value);

public sealed record NodeOdmUploadProgress(
    int UploadedFiles,
    int TotalFiles,
    string CurrentFile)
{
    public double Percent =>
        TotalFiles <= 0
            ? 0
            : UploadedFiles * 100d / TotalFiles;
}

public sealed record NodeOdmTaskInfo(
    string Uuid,
    string Name,
    int StatusCode,
    double Progress,
    int ImagesCount,
    long ProcessingTimeMilliseconds)
{
    public string StatusText => StatusCode switch
    {
        10 => "QUEUED",
        20 => "RUNNING",
        30 => "FAILED",
        40 => "COMPLETED",
        50 => "CANCELED",
        _ => $"UNKNOWN ({StatusCode})"
    };

    public bool IsTerminal =>
        StatusCode is 30 or 40 or 50;

    public bool IsCompleted =>
        StatusCode == 40;
}
