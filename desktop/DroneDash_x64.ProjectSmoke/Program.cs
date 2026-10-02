using System.IO;
using DroneDash_x64.Desktop.Project;

var root =
    Path.Combine(
        Path.GetTempPath(),
        "DroneDash_ProjectSmoke_" +
        Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(root);

try
{
    var projectPath =
        Path.Combine(
            root,
            "farm.ddproj");

    var artifactsDir =
        Path.Combine(
            root,
            "artifacts");

    Directory.CreateDirectory(
        artifactsDir);

    var flightPlan =
        Path.Combine(
            artifactsDir,
            "survey.ddplan");

    var photoManifest =
        Path.Combine(
            artifactsDir,
            "photogrammetry-manifest.json");

    var fieldProducts =
        Path.Combine(
            artifactsDir,
            "field-products.json");

    var nodeOdmZip =
        Path.Combine(
            artifactsDir,
            "nodeodm_test_all.zip");

    File.WriteAllText(
        flightPlan,
        """{"schemaVersion":1,"settings":{"name":"Smoke"}}""");

    File.WriteAllText(
        photoManifest,
        """{"schemaVersion":1,"summary":{"images":10}}""");

    File.WriteAllText(
        fieldProducts,
        """{"schemaVersion":1,"ndviPath":"ndvi.tif"}""");

    File.WriteAllBytes(
        nodeOdmZip,
        [1, 2, 3, 4, 5]);

    var project =
        DroneDashProjectStore.Create(
            "Smoke Farm",
            "Project integration test");

    DroneDashProjectStore.Save(
        projectPath,
        project);

    project =
        DroneDashProjectStore.Load(
            projectPath);

    var added =
        new List<DroneDashProjectArtifact>
        {
            await ProjectArtifactService.CreateFileArtifactAsync(
                projectPath,
                flightPlan),
            await ProjectArtifactService.CreateFileArtifactAsync(
                projectPath,
                photoManifest),
            await ProjectArtifactService.CreateFileArtifactAsync(
                projectPath,
                fieldProducts),
            await ProjectArtifactService.CreateFileArtifactAsync(
                projectPath,
                nodeOdmZip),
            ProjectArtifactService.CreateDirectoryArtifact(
                projectPath,
                artifactsDir)
        };

    project =
        project with
        {
            Artifacts =
                added.ToArray()
        };

    DroneDashProjectStore.Save(
        projectPath,
        project);

    var loaded =
        DroneDashProjectStore.Load(
            projectPath);

    if (loaded.Artifacts.Count != 5)
        throw new InvalidDataException(
            "Project artifact count mismatch.");

    if (!loaded.Artifacts
            .Where(a => a.ReferenceKind == ProjectReferenceKind.File)
            .All(a => a.IsRelative))
    {
        throw new InvalidDataException(
            "Artifacts inside project root must use relative paths.");
    }

    var kinds =
        loaded.Artifacts
            .Select(a => a.Kind)
            .ToHashSet();

    foreach (var expected in new[]
             {
                 ProjectArtifactKind.FlightPlan,
                 ProjectArtifactKind.PhotogrammetryManifest,
                 ProjectArtifactKind.SmartFarmingFieldProducts,
                 ProjectArtifactKind.NodeOdmResultArchive,
                 ProjectArtifactKind.SourceDataFolder
             })
    {
        if (!kinds.Contains(expected))
            throw new InvalidDataException(
                $"Missing project artifact kind: {expected}");
    }

    foreach (var artifact in loaded.Artifacts)
    {
        var verification =
            await ProjectArtifactService.VerifyAsync(
                projectPath,
                artifact);

        if (verification.Integrity != ProjectArtifactIntegrity.Ok)
        {
            throw new InvalidDataException(
                $"Expected OK integrity for {artifact.Label}, got {verification.Integrity}: {verification.Detail}");
        }
    }

    File.AppendAllText(
        fieldProducts,
        Environment.NewLine + "modified");

    var modifiedArtifact =
        loaded.Artifacts.Single(
            a => a.Kind ==
                 ProjectArtifactKind.SmartFarmingFieldProducts);

    var modifiedVerification =
        await ProjectArtifactService.VerifyAsync(
            projectPath,
            modifiedArtifact);

    if (modifiedVerification.Integrity !=
        ProjectArtifactIntegrity.Modified)
    {
        throw new InvalidDataException(
            "Modified artifact was not detected.");
    }

    File.Delete(
        flightPlan);

    var missingArtifact =
        loaded.Artifacts.Single(
            a => a.Kind ==
                 ProjectArtifactKind.FlightPlan);

    var missingVerification =
        await ProjectArtifactService.VerifyAsync(
            projectPath,
            missingArtifact);

    if (missingVerification.Integrity !=
        ProjectArtifactIntegrity.Missing)
    {
        throw new InvalidDataException(
            "Missing artifact was not detected.");
    }

    var discovered =
        ProjectArtifactService.DiscoverKnownArtifacts(
            root);

    if (!discovered.Any(path =>
            Path.GetFileName(path).Equals(
                "photogrammetry-manifest.json",
                StringComparison.OrdinalIgnoreCase)) ||
        !discovered.Any(path =>
            Path.GetFileName(path).Equals(
                "field-products.json",
                StringComparison.OrdinalIgnoreCase)) ||
        !discovered.Any(path =>
            Path.GetFileName(path).Equals(
                "nodeodm_test_all.zip",
                StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidDataException(
            "Known artifact discovery failed.");
    }

    var rebasedDirectory =
        Path.Combine(
            root,
            "rebased");

    Directory.CreateDirectory(
        rebasedDirectory);

    var rebasedPath =
        Path.Combine(
            rebasedDirectory,
            "farm-copy.ddproj");

    var rebased =
        DroneDashProjectStore.Rebase(
            projectPath,
            rebasedPath,
            loaded);

    var photoArtifact =
        rebased.Artifacts.Single(
            a => a.Kind ==
                 ProjectArtifactKind.PhotogrammetryManifest);

    var originalResolved =
        DroneDashProjectStore.ResolveArtifactPath(
            projectPath,
            loaded.Artifacts.Single(
                a => a.Kind ==
                     ProjectArtifactKind.PhotogrammetryManifest));

    var rebasedResolved =
        DroneDashProjectStore.ResolveArtifactPath(
            rebasedPath,
            photoArtifact);

    if (!Path.GetFullPath(originalResolved).Equals(
            Path.GetFullPath(rebasedResolved),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Project rebase changed artifact target.");
    }

    DroneDashProjectStore.Save(
        rebasedPath,
        rebased);

    Console.WriteLine(
        $"PASS DroneDash project · id={loaded.ProjectId} · artifacts={loaded.Artifacts.Count} · discovered={discovered.Count}");
}
finally
{
    try
    {
        Directory.Delete(
            root,
            recursive: true);
    }
    catch
    {
    }
}
