using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.Project;

internal static class ProjectProcessingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetLedgerPath(
        string projectPath) =>
        Path.Combine(
            ProjectWorkspaceLayout.GetProjectDirectory(
                projectPath),
            "project-processing-jobs.json");

    public static string GetJobsDirectory(
        string projectPath)
    {
        var path =
            Path.Combine(
                ProjectWorkspaceLayout.GetProjectDirectory(
                    projectPath),
                "03_Processing",
                "_ProjectJobs");

        Directory.CreateDirectory(path);
        return path;
    }

    public static ProjectProcessingLedger Load(
        string projectPath)
    {
        var path =
            GetLedgerPath(
                projectPath);

        if (!File.Exists(path))
        {
            return new(
                ProjectProcessingLedger.CurrentSchemaVersion,
                null,
                []);
        }

        var ledger =
            JsonSerializer.Deserialize<ProjectProcessingLedger>(
                File.ReadAllText(path),
                JsonOptions)
            ?? throw new InvalidDataException(
                "Processing-Job-Ledger konnte nicht gelesen werden.");

        if (ledger.SchemaVersion !=
            ProjectProcessingLedger.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Nicht unterstützte Processing-Job-Version {ledger.SchemaVersion}; erwartet {ProjectProcessingLedger.CurrentSchemaVersion}.");
        }

        return ledger;
    }

    public static void Save(
        string projectPath,
        ProjectProcessingLedger ledger)
    {
        var path =
            GetLedgerPath(
                projectPath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        var tempPath =
            path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false)))
            {
                writer.Write(
                    JsonSerializer.Serialize(
                        ledger,
                        JsonOptions));

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(
                tempPath,
                path,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Cleanup must not hide the ledger result.
            }
        }
    }

    public static void CopyRebased(
        string oldProjectPath,
        string newProjectPath)
    {
        if (!File.Exists(
                GetLedgerPath(
                    oldProjectPath)))
        {
            return;
        }

        var ledger =
            Load(
                oldProjectPath);

        var jobs =
            ledger.Jobs
                .Select(job =>
                {
                    var oldLogPath =
                        ResolvePath(
                            oldProjectPath,
                            job.LogPath);

                    var rebasedLog =
                        ToStoredPath(
                            newProjectPath,
                            oldLogPath);

                    var outputs =
                        job.Outputs
                            .Select(output =>
                            {
                                var oldOutput =
                                    ResolvePath(
                                        oldProjectPath,
                                        output.Path);

                                return output with
                                {
                                    Path =
                                        ToStoredPath(
                                            newProjectPath,
                                            oldOutput)
                                };
                            })
                            .ToArray();

                    return job with
                    {
                        LogPath =
                            rebasedLog,
                        Outputs =
                            outputs,
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    };
                })
                .ToArray();

        Save(
            newProjectPath,
            ledger with
            {
                Jobs = jobs
            });
    }

    public static void AppendLog(
        string projectPath,
        ProjectProcessingJob job,
        string line)
    {
        try
        {
            var path =
                ResolvePath(
                    projectPath,
                    job.LogPath);

            Directory.CreateDirectory(
                Path.GetDirectoryName(
                    path)!);

            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Job state is authoritative. Logging remains best-effort.
        }
    }

    public static ProjectPipelinePath ToStoredPath(
        string projectPath,
        string targetPath)
    {
        var stored =
            DroneDashProjectStore.ToStoredPath(
                projectPath,
                targetPath);

        return new(
            stored.StoredPath,
            stored.IsRelative);
    }

    public static string ResolvePath(
        string projectPath,
        ProjectPipelinePath path)
    {
        if (!path.IsRelative)
        {
            return Path.GetFullPath(
                path.StoredPath);
        }

        return Path.GetFullPath(
            Path.Combine(
                ProjectWorkspaceLayout.GetProjectDirectory(
                    projectPath),
                path.StoredPath));
    }
}
