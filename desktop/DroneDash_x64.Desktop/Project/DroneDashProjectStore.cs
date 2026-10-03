using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroneDash_x64.Desktop.Project;

public static class DroneDashProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DroneDashProject Create(
        string name,
        string? description = null)
    {
        ValidateName(name);

        var now = DateTimeOffset.UtcNow;

        return new DroneDashProject(
            DroneDashProject.CurrentSchemaVersion,
            Guid.NewGuid(),
            name.Trim(),
            NormalizeOptional(description),
            now,
            now,
            []);
    }

    public static void Save(
        string projectPath,
        DroneDashProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            projectPath);
        ArgumentNullException.ThrowIfNull(
            project);

        var fullPath =
            Path.GetFullPath(
                projectPath);

        Validate(
            project,
            fullPath);

        var directory =
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "Projektpfad besitzt keinen gültigen Ordner.");

        Directory.CreateDirectory(
            directory);

        var saved =
            project with
            {
                Name =
                    project.Name.Trim(),
                Description =
                    NormalizeOptional(
                        project.Description),
                SavedAtUtc =
                    DateTimeOffset.UtcNow,
                Artifacts =
                    project.Artifacts.ToArray()
            };

        var tempPath =
            fullPath +
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
                        saved,
                        JsonOptions));

                writer.Flush();
                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                tempPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(
                        tempPath))
                {
                    File.Delete(
                        tempPath);
                }
            }
            catch
            {
                // Cleanup must not hide the project save result.
            }
        }
    }

    public static DroneDashProject Load(
        string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            projectPath);

        var fullPath =
            Path.GetFullPath(
                projectPath);

        var json =
            File.ReadAllText(
                fullPath);

        DroneDashProject project;

        try
        {
            project =
                JsonSerializer.Deserialize<DroneDashProject>(
                    json,
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "DroneDash-Projekt konnte nicht gelesen werden.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "DroneDash-Projekt enthält ungültiges JSON.",
                ex);
        }

        try
        {
            Validate(
                project,
                fullPath);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                "DroneDash-Projekt enthält ungültige Projektdaten.",
                ex);
        }

        return project;
    }

    public static string ResolveArtifactPath(
        string projectPath,
        DroneDashProjectArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(
            artifact);

        return ResolveStoredPath(
            projectPath,
            artifact.StoredPath,
            artifact.IsRelative);
    }

    public static (string StoredPath, bool IsRelative) ToStoredPath(
        string projectPath,
        string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            projectPath);

        var projectDirectory =
            Path.GetDirectoryName(
                Path.GetFullPath(projectPath))
            ?? throw new InvalidOperationException(
                "Projektpfad besitzt keinen gültigen Ordner.");

        ArgumentException.ThrowIfNullOrWhiteSpace(
            targetPath);

        var fullTarget =
            Path.GetFullPath(
                targetPath);

        var relative =
            Path.GetRelativePath(
                projectDirectory,
                fullTarget);

        if (!Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) &&
            !relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return (
                NormalizeRelativePath(relative),
                true);
        }

        return (
            fullTarget,
            false);
    }

    public static DroneDashProject Rebase(
        string oldProjectPath,
        string newProjectPath,
        DroneDashProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            oldProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            newProjectPath);
        ArgumentNullException.ThrowIfNull(
            project);

        Validate(
            project,
            Path.GetFullPath(
                oldProjectPath));

        var artifacts =
            project.Artifacts
                .Select(artifact =>
                {
                    var resolved =
                        ResolveArtifactPath(
                            oldProjectPath,
                            artifact);

                    var stored =
                        ToStoredPath(
                            newProjectPath,
                            resolved);

                    return artifact with
                    {
                        StoredPath = stored.StoredPath,
                        IsRelative = stored.IsRelative
                    };
                })
                .ToArray();

        var rebased =
            project with
            {
                Artifacts =
                    artifacts
            };

        Validate(
            rebased,
            Path.GetFullPath(
                newProjectPath));

        return rebased;
    }

    public static DroneDashProject WithMetadata(
        DroneDashProject project,
        string name,
        string? description)
    {
        ValidateName(name);

        return project with
        {
            Name = name.Trim(),
            Description =
                NormalizeOptional(description)
        };
    }

    private static void Validate(
        DroneDashProject project,
        string? projectPath = null)
    {
        if (project.SchemaVersion !=
            DroneDashProject.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Nicht unterstützte DroneDash-Projektversion {project.SchemaVersion}; erwartet {DroneDashProject.CurrentSchemaVersion}.");
        }

        if (project.ProjectId == Guid.Empty)
        {
            throw new InvalidDataException(
                "DroneDash-Projekt enthält keine gültige Projekt-ID.");
        }

        ValidateName(
            project.Name);

        if (project.Artifacts is null)
        {
            throw new InvalidDataException(
                "DroneDash-Projekt enthält keine Artefaktliste.");
        }

        var duplicate =
            project.Artifacts
                .GroupBy(artifact => artifact.Id)
                .FirstOrDefault(group =>
                    group.Key == Guid.Empty ||
                    group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidDataException(
                "DroneDash-Projekt enthält ungültige oder doppelte Artefakt-IDs.");
        }

        foreach (var artifact in project.Artifacts)
        {
            if (!Enum.IsDefined(
                    artifact.Kind) ||
                !Enum.IsDefined(
                    artifact.ReferenceKind))
            {
                throw new InvalidDataException(
                    "DroneDash-Projekt enthält einen unbekannten Artefakt- oder Referenztyp.");
            }

            if (string.IsNullOrWhiteSpace(
                    artifact.StoredPath))
            {
                throw new InvalidDataException(
                    "DroneDash-Projekt enthält einen leeren Artefaktpfad.");
            }

            if (string.IsNullOrWhiteSpace(
                    artifact.Label))
            {
                throw new InvalidDataException(
                    "DroneDash-Projekt enthält ein Artefakt ohne Bezeichnung.");
            }

            if (artifact.SizeBytes is < 0)
            {
                throw new InvalidDataException(
                    "DroneDash-Projekt enthält eine negative Artefaktgröße.");
            }

            if (!string.IsNullOrWhiteSpace(
                    artifact.Sha256) &&
                !IsSha256(
                    artifact.Sha256))
            {
                throw new InvalidDataException(
                    "DroneDash-Projekt enthält einen ungültigen SHA-256-Wert.");
            }

            if (!string.IsNullOrWhiteSpace(
                    projectPath))
            {
                _ =
                    ResolveStoredPath(
                        projectPath,
                        artifact.StoredPath,
                        artifact.IsRelative);
            }
        }
    }

    private static string ResolveStoredPath(
        string projectPath,
        string storedPath,
        bool isRelative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            projectPath);

        if (string.IsNullOrWhiteSpace(
                storedPath))
        {
            throw new InvalidDataException(
                "Projektartefakt enthält einen leeren Pfad.");
        }

        if (!isRelative)
        {
            if (!Path.IsPathRooted(
                    storedPath))
            {
                throw new InvalidDataException(
                    "Als absolut markierter Artefaktpfad ist nicht absolut.");
            }

            return Path.GetFullPath(
                storedPath);
        }

        if (Path.IsPathRooted(
                storedPath))
        {
            throw new InvalidDataException(
                "Als relativ markierter Artefaktpfad ist absolut.");
        }

        var projectDirectory =
            Path.GetFullPath(
                Path.GetDirectoryName(
                    Path.GetFullPath(
                        projectPath))
                ?? throw new InvalidOperationException(
                    "Projektpfad besitzt keinen gültigen Ordner."));

        var resolved =
            Path.GetFullPath(
                Path.Combine(
                    projectDirectory,
                    storedPath));

        var relative =
            Path.GetRelativePath(
                projectDirectory,
                resolved);

        if (Path.IsPathRooted(
                relative) ||
            relative.Equals(
                "..",
                StringComparison.Ordinal) ||
            relative.StartsWith(
                ".." +
                Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            relative.StartsWith(
                ".." +
                Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Relativer Artefaktpfad verlässt den Projektordner: {storedPath}");
        }

        return resolved;
    }

    private static bool IsSha256(
        string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(
                    character))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateName(
        string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "Projektname darf nicht leer sein.",
                nameof(name));
        }

        if (name.Trim().Length > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                "Projektname darf höchstens 160 Zeichen enthalten.");
        }
    }

    private static string? NormalizeOptional(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string NormalizeRelativePath(
        string path) =>
        path.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
}
