using System.IO;
using System.Text.Json;

namespace DroneDash_x64.Desktop.Project;

public enum ProjectQaState
{
    Open,
    Good,
    Warning,
    Error
}

public sealed record ProjectQaIndicator(
    string Name,
    ProjectQaState State,
    string Detail)
{
    public string StateText => State switch
    {
        ProjectQaState.Good => "OK",
        ProjectQaState.Warning => "Prüfen",
        ProjectQaState.Error => "Fehler",
        _ => "Offen"
    };
}

public sealed record ProjectDashboardSnapshot(
    ProjectQaIndicator Rtk,
    ProjectQaIndicator Dataset,
    ProjectQaIndicator Processing,
    ProjectQaIndicator Results)
{
    public IReadOnlyList<ProjectQaIndicator> Indicators =>
        [Rtk, Dataset, Processing, Results];

    public int GoodCount =>
        Indicators.Count(item =>
            item.State == ProjectQaState.Good);

    public string SummaryText =>
        $"{GoodCount}/4 QA-Bereiche OK";
}

public static class ProjectDashboardAnalyzer
{
    public static ProjectDashboardSnapshot Analyze(
        string? projectPath,
        DroneDashProject? project)
    {
        if (project is null ||
            string.IsNullOrWhiteSpace(projectPath))
        {
            var open =
                new ProjectQaIndicator(
                    "QA",
                    ProjectQaState.Open,
                    "Kein Projekt geladen.");

            return new(
                open with { Name = "RTK" },
                open with { Name = "Datensatz" },
                open with { Name = "Processing" },
                open with { Name = "Ergebnis" });
        }

        return new(
            AnalyzeRtk(projectPath, project),
            AnalyzeDataset(projectPath, project),
            AnalyzeProcessing(project),
            AnalyzeResults(projectPath, project));
    }

    private static ProjectQaIndicator AnalyzeRtk(
        string projectPath,
        DroneDashProject project)
    {
        var samples = 0;
        var withRtk = 0;
        var withPrecision = 0;
        var parseErrors = 0;

        foreach (var artifact in
                 project.Artifacts
                     .Where(item =>
                         item.ReferenceKind ==
                         ProjectReferenceKind.File &&
                         item.Kind is
                             ProjectArtifactKind.PhotogrammetryManifest or
                             ProjectArtifactKind.SmartFarmingDataset or
                             ProjectArtifactKind.PvAnalysis))
        {
            if (!TryOpenJson(
                    projectPath,
                    artifact,
                    out var document))
            {
                parseErrors++;
                continue;
            }

            using (document)
            {
                var root =
                    document.RootElement;

                if (artifact.Kind ==
                    ProjectArtifactKind.PhotogrammetryManifest)
                {
                    if (TryGetSummaryInt(
                            root,
                            "imageCount",
                            out var imageCount))
                    {
                        samples += imageCount;
                    }

                    if (TryGetSummaryInt(
                            root,
                            "rtkMetadataCount",
                            out var rtkCount))
                    {
                        withRtk += rtkCount;
                    }

                    if (TryGetSummaryInt(
                            root,
                            "rtkPrecisionCount",
                            out var precisionCount))
                    {
                        withPrecision += precisionCount;
                    }

                    continue;
                }

                if (!root.TryGetProperty(
                        "images",
                        out var images) &&
                    !root.TryGetProperty(
                        "captures",
                        out images))
                {
                    continue;
                }

                if (images.ValueKind !=
                    JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in
                         images.EnumerateArray())
                {
                    samples++;

                    if (artifact.Kind ==
                        ProjectArtifactKind.PvAnalysis)
                    {
                        if (HasNonEmptyString(
                                item,
                                "rtkFlag"))
                        {
                            withRtk++;
                        }

                        if (HasNumber(
                                item,
                                "rtkStdLongitudeMeters") &&
                            HasNumber(
                                item,
                                "rtkStdLatitudeMeters") &&
                            HasNumber(
                                item,
                                "rtkStdHeightMeters"))
                        {
                            withPrecision++;
                        }

                        continue;
                    }

                    var band =
                        FirstObjectProperty(
                            item,
                            "nir",
                            "red",
                            "redEdge",
                            "green");

                    if (band.HasValue &&
                        HasNonEmptyString(
                            band.Value,
                            "rtkStatus"))
                    {
                        withRtk++;
                    }
                }
            }
        }

        if (samples == 0)
        {
            return new(
                "RTK",
                parseErrors > 0
                    ? ProjectQaState.Error
                    : ProjectQaState.Open,
                parseErrors > 0
                    ? $"{parseErrors} QA-Datei(en) konnten nicht gelesen werden."
                    : "Noch kein auswertbarer Datensatz mit RTK-Metadaten registriert.");
        }

        var detail =
            $"RTK-Metadaten {withRtk:N0}/{samples:N0}" +
            (withPrecision > 0
                ? $" · σ-Werte {withPrecision:N0}/{samples:N0}"
                : " · keine separaten σ-Werte im aktuellen QA-Snapshot");

        return new(
            "RTK",
            withRtk == samples
                ? ProjectQaState.Good
                : ProjectQaState.Warning,
            detail);
    }

    private static ProjectQaIndicator AnalyzeDataset(
        string projectPath,
        DroneDashProject project)
    {
        var datasetArtifacts =
            project.Artifacts
                .Where(item =>
                    item.Kind is
                        ProjectArtifactKind.PhotogrammetryManifest or
                        ProjectArtifactKind.SmartFarmingDataset or
                        ProjectArtifactKind.PvAnalysis)
                .ToArray();

        if (datasetArtifacts.Length == 0)
        {
            var sourceFolders =
                project.Artifacts.Count(item =>
                    item.Kind ==
                    ProjectArtifactKind.SourceDataFolder);

            return new(
                "Datensatz",
                sourceFolders > 0
                    ? ProjectQaState.Warning
                    : ProjectQaState.Open,
                sourceFolders > 0
                    ? $"{sourceFolders} Quellordner registriert, aber noch kein QA-Manifest."
                    : "Noch kein Datensatz-QA-Manifest registriert.");
        }

        var total = 0;
        var issues = 0;
        var parseErrors = 0;
        var details =
            new List<string>();

        foreach (var artifact in datasetArtifacts)
        {
            if (!TryOpenJson(
                    projectPath,
                    artifact,
                    out var document))
            {
                parseErrors++;
                continue;
            }

            using (document)
            {
                var root =
                    document.RootElement;

                if (artifact.Kind ==
                    ProjectArtifactKind.PhotogrammetryManifest)
                {
                    var count =
                        GetSummaryInt(
                            root,
                            "imageCount");

                    var issueCount =
                        GetSummaryInt(
                            root,
                            "issueCount");

                    total += count;
                    issues += issueCount;
                    details.Add(
                        $"Photo {count:N0} Bilder / {issueCount:N0} Hinweise");
                }
                else if (artifact.Kind ==
                         ProjectArtifactKind.SmartFarmingDataset)
                {
                    var count =
                        GetSummaryInt(
                            root,
                            "captureCount");

                    var complete =
                        GetSummaryInt(
                            root,
                            "completeCaptureCount");

                    var issueCount =
                        GetSummaryInt(
                            root,
                            "issueCount");

                    total += count;
                    issues +=
                        issueCount +
                        Math.Max(
                            0,
                            count - complete);

                    details.Add(
                        $"M3M {complete:N0}/{count:N0} komplett / {issueCount:N0} Hinweise");
                }
                else
                {
                    var count =
                        GetSummaryInt(
                            root,
                            "thermalImageCount");

                    var failed =
                        GetSummaryInt(
                            root,
                            "failedCount");

                    total += count;
                    issues += failed;
                    details.Add(
                        $"PV {count:N0} Thermalbilder / {failed:N0} Processing-Fehler");
                }
            }
        }

        if (parseErrors > 0)
        {
            return new(
                "Datensatz",
                ProjectQaState.Error,
                $"{parseErrors} QA-Datei(en) nicht lesbar" +
                (details.Count > 0
                    ? " · " + string.Join(" · ", details)
                    : ""));
        }

        return new(
            "Datensatz",
            total > 0 && issues == 0
                ? ProjectQaState.Good
                : ProjectQaState.Warning,
            details.Count > 0
                ? string.Join(" · ", details)
                : "QA-Dateien vorhanden, aber ohne auswertbare Zähler.");
    }

    private static ProjectQaIndicator AnalyzeProcessing(
        DroneDashProject project)
    {
        var plans =
            project.Artifacts.Count(item =>
                item.Kind ==
                ProjectArtifactKind.LocalProcessingPlan);

        var workspaces =
            project.Artifacts.Count(item =>
                item.Kind ==
                ProjectArtifactKind.ProcessingWorkspace);

        var archives =
            project.Artifacts.Count(item =>
                item.Kind ==
                ProjectArtifactKind.NodeOdmResultArchive);

        if (plans == 0 &&
            workspaces == 0 &&
            archives == 0)
        {
            return new(
                "Processing",
                ProjectQaState.Open,
                "Noch kein Processing-Plan, Workspace oder NodeODM-Ergebnis registriert.");
        }

        return new(
            "Processing",
            archives > 0
                ? ProjectQaState.Good
                : ProjectQaState.Warning,
            $"Pläne {plans:N0} · Workspaces {workspaces:N0} · NodeODM-Ergebnisse {archives:N0}" +
            (archives == 0
                ? " · Ergebnisarchiv noch offen"
                : ""));
    }

    private static ProjectQaIndicator AnalyzeResults(
        string projectPath,
        DroneDashProject project)
    {
        var pv =
            project.Artifacts
                .Where(item =>
                    item.Kind ==
                    ProjectArtifactKind.PvAnalysis)
                .ToArray();

        var fieldProducts =
            project.Artifacts.Count(item =>
                item.Kind ==
                ProjectArtifactKind.SmartFarmingFieldProducts);

        var vegetation =
            project.Artifacts.Count(item =>
                item.Kind ==
                ProjectArtifactKind.VegetationRaster);

        if (pv.Length == 0 &&
            fieldProducts == 0 &&
            vegetation == 0)
        {
            return new(
                "Ergebnis",
                ProjectQaState.Open,
                "Noch keine PV-Analyse, Feldprodukt-Manifest oder Vegetationsraster registriert.");
        }

        var pvFailed = 0;
        var parseErrors = 0;

        foreach (var artifact in pv)
        {
            if (!TryOpenJson(
                    projectPath,
                    artifact,
                    out var document))
            {
                parseErrors++;
                continue;
            }

            using (document)
            {
                pvFailed +=
                    GetSummaryInt(
                        document.RootElement,
                        "failedCount");
            }
        }

        var state =
            parseErrors > 0
                ? ProjectQaState.Error
                : pvFailed > 0
                    ? ProjectQaState.Warning
                    : ProjectQaState.Good;

        return new(
            "Ergebnis",
            state,
            $"PV-QA {pv.Length:N0} · PV-Processingfehler {pvFailed:N0} · " +
            $"Feldprodukt-Manifeste {fieldProducts:N0} · Vegetationsraster {vegetation:N0}" +
            (parseErrors > 0
                ? $" · {parseErrors:N0} Ergebnisdatei(en) nicht lesbar"
                : ""));
    }

    private static bool TryOpenJson(
        string projectPath,
        DroneDashProjectArtifact artifact,
        out JsonDocument document)
    {
        document = null!;

        try
        {
            var path =
                DroneDashProjectStore.ResolveArtifactPath(
                    projectPath,
                    artifact);

            if (!File.Exists(path))
                return false;

            document =
                JsonDocument.Parse(
                    File.ReadAllText(path));

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetSummaryInt(
        JsonElement root,
        string propertyName) =>
        TryGetSummaryInt(
            root,
            propertyName,
            out var value)
            ? value
            : 0;

    private static bool TryGetSummaryInt(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;

        return
            root.TryGetProperty(
                "summary",
                out var summary) &&
            summary.ValueKind ==
                JsonValueKind.Object &&
            summary.TryGetProperty(
                propertyName,
                out var property) &&
            property.TryGetInt32(
                out value);
    }

    private static bool HasNonEmptyString(
        JsonElement element,
        string propertyName) =>
        element.ValueKind ==
            JsonValueKind.Object &&
        element.TryGetProperty(
            propertyName,
            out var property) &&
        property.ValueKind ==
            JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(
            property.GetString());

    private static bool HasNumber(
        JsonElement element,
        string propertyName) =>
        element.ValueKind ==
            JsonValueKind.Object &&
        element.TryGetProperty(
            propertyName,
            out var property) &&
        property.ValueKind ==
            JsonValueKind.Number;

    private static JsonElement? FirstObjectProperty(
        JsonElement element,
        params string[] names)
    {
        if (element.ValueKind !=
            JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(
                    name,
                    out var property) &&
                property.ValueKind ==
                    JsonValueKind.Object)
            {
                return property;
            }
        }

        return null;
    }
}
