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
        Validate(project);

        var fullPath =
            Path.GetFullPath(projectPath);

        var directory =
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "Projektpfad besitzt keinen gültigen Ordner.");

        Directory.CreateDirectory(directory);

        var saved =
            project with
            {
                Name = project.Name.Trim(),
                Description =
                    NormalizeOptional(project.Description),
                SavedAtUtc = DateTimeOffset.UtcNow,
                Artifacts = project.Artifacts.ToArray()
            };

        var tempPath =
            fullPath + ".tmp";

        File.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(
                saved,
                JsonOptions),
            new UTF8Encoding(false));

        File.Move(
            tempPath,
            fullPath,
            overwrite: true);
    }

    public static DroneDashProject Load(
        string projectPath)
    {
        var fullPath =
            Path.GetFullPath(projectPath);

        var json =
            File.ReadAllText(
                fullPath);

        var project =
            JsonSerializer.Deserialize<DroneDashProject>(
                json,
                JsonOptions)
            ?? throw new InvalidDataException(
                "DroneDash-Projekt konnte nicht gelesen werden.");

        Validate(project);

        return project;
    }

    public static string ResolveArtifactPath(
        string projectPath,
        DroneDashProjectArtifact artifact)
    {
        if (!artifact.IsRelative)
            return Path.GetFullPath(
                artifact.StoredPath);

        var projectDirectory =
            Path.GetDirectoryName(
                Path.GetFullPath(projectPath))
            ?? throw new InvalidOperationException(
                "Projektpfad besitzt keinen gültigen Ordner.");

        return Path.GetFullPath(
            Path.Combine(
                projectDirectory,
                artifact.StoredPath));
    }

    public static (string StoredPath, bool IsRelative) ToStoredPath(
        string projectPath,
        string targetPath)
    {
        var projectDirectory =
            Path.GetDirectoryName(
                Path.GetFullPath(projectPath))
            ?? throw new InvalidOperationException(
                "Projektpfad besitzt keinen gültigen Ordner.");

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

        return project with
        {
            Artifacts = artifacts
        };
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
        DroneDashProject project)
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
        }
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
