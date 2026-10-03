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

    var sessionRoot =
        Path.Combine(
            root,
            "session");

    Directory.CreateDirectory(
        sessionRoot);

    var sessionProjectPath =
        Path.Combine(
            sessionRoot,
            "integrated.ddproj");

    var sessionProject =
        DroneDashProjectStore.Create(
            "Integrated Workflow");

    DroneDashProjectStore.Save(
        sessionProjectPath,
        sessionProject);

    sessionProject =
        DroneDashProjectStore.Load(
            sessionProjectPath);

    var sessionChangeCount = 0;
    DroneDashProjectSession.Changed += (_, _) =>
        sessionChangeCount++;

    DroneDashProjectSession.Activate(
        sessionProjectPath,
        sessionProject,
        "smoke");

    var workspaceFolders =
        ProjectWorkspaceLayout.EnsureStandardFolders(
            sessionProjectPath);

    foreach (var folder in
             new[]
             {
                 ProjectWorkspaceFolder.Planning,
                 ProjectWorkspaceFolder.PhotogrammetryDataset,
                 ProjectWorkspaceFolder.PvDataset,
                 ProjectWorkspaceFolder.SmartFarmingDataset,
                 ProjectWorkspaceFolder.PhotogrammetryProcessing,
                 ProjectWorkspaceFolder.PvProcessing,
                 ProjectWorkspaceFolder.SmartFarmingProcessing,
                 ProjectWorkspaceFolder.PhotogrammetryResults,
                 ProjectWorkspaceFolder.PvResults,
                 ProjectWorkspaceFolder.SmartFarmingResults,
                 ProjectWorkspaceFolder.Exports
             })
    {
        if (!workspaceFolders.TryGetValue(
                folder,
                out var folderPath) ||
            !Directory.Exists(folderPath))
        {
            throw new InvalidDataException(
                $"Standard project folder was not created: {folder}");
        }
    }

    var sessionPlan =
        Path.Combine(
            sessionRoot,
            "mission.ddplan");

    var sessionDataset =
        Path.Combine(
            sessionRoot,
            "photogrammetry-manifest.json");

    var sessionProcessing =
        Path.Combine(
            sessionRoot,
            "local-processing-plan.json");

    var sessionAnalysis =
        Path.Combine(
            sessionRoot,
            "pv-analysis.json");

    File.WriteAllText(
        sessionPlan,
        """{"schemaVersion":1}""");

    File.WriteAllText(
        sessionDataset,
        """{"schemaVersion":1,"summary":{"imageCount":10,"rtkMetadataCount":10,"rtkPrecisionCount":10,"issueCount":0},"images":[]}""");

    File.WriteAllText(
        sessionProcessing,
        """{"schemaVersion":1}""");

    File.WriteAllText(
        sessionAnalysis,
        """{"schemaVersion":1,"summary":{"thermalImageCount":1,"failedCount":0},"images":[{"rtkFlag":"RTK_FIX","rtkStdLongitudeMeters":0.01,"rtkStdLatitudeMeters":0.01,"rtkStdHeightMeters":0.02}]}""");

    await DroneDashProjectSession.RegisterFileAsync(
        sessionPlan,
        ProjectArtifactKind.FlightPlan);

    await DroneDashProjectSession.RegisterFileAsync(
        sessionDataset,
        ProjectArtifactKind.PhotogrammetryManifest);

    await DroneDashProjectSession.RegisterFileAsync(
        sessionProcessing,
        ProjectArtifactKind.LocalProcessingPlan);

    await DroneDashProjectSession.RegisterFileAsync(
        sessionAnalysis,
        ProjectArtifactKind.PvAnalysis);

    var active =
        DroneDashProjectSession.CurrentProject
        ?? throw new InvalidDataException(
            "Project session lost active project.");

    if (active.Artifacts.Count != 4)
        throw new InvalidDataException(
            $"Expected 4 auto-registered artifacts, got {active.Artifacts.Count}.");

    var workflow =
        ProjectWorkflowAnalyzer.Analyze(
            active);

    if (workflow.PresentStageCount != 4 ||
        !workflow.Planning.Present ||
        !workflow.Dataset.Present ||
        !workflow.Processing.Present ||
        !workflow.Analysis.Present)
    {
        throw new InvalidDataException(
            "Integrated project workflow did not cover all four stages.");
    }

    var dashboard =
        ProjectDashboardAnalyzer.Analyze(
            sessionProjectPath,
            active);

    if (dashboard.Rtk.State != ProjectQaState.Good ||
        dashboard.Dataset.State != ProjectQaState.Good ||
        dashboard.Processing.State != ProjectQaState.Warning ||
        dashboard.Results.State != ProjectQaState.Good)
    {
        throw new InvalidDataException(
            $"Unexpected dashboard states: RTK={dashboard.Rtk.State}, " +
            $"Dataset={dashboard.Dataset.State}, Processing={dashboard.Processing.State}, " +
            $"Results={dashboard.Results.State}");
    }

    ProjectNavigationRequest? navigation = null;

    DroneDashProjectSession.NavigationRequested +=
        (_, request) =>
            navigation = request;

    DroneDashProjectSession.RequestNavigation(
        ProjectNavigationTarget.Photogrammetry,
        reason: "smoke handoff");

    if (navigation is null ||
        navigation.Target !=
            ProjectNavigationTarget.Photogrammetry ||
        !string.Equals(
            Path.GetFullPath(
                navigation.FlightPlanPath ?? ""),
            Path.GetFullPath(
                sessionPlan),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Project workflow navigation did not carry the latest flight plan.");
    }

    var photoProbeFolder =
        Path.Combine(
            sessionRoot,
            "pipeline-photo");

    var pvProbeFolder =
        Path.Combine(
            sessionRoot,
            "pipeline-pv");

    var smartProbeFolder =
        Path.Combine(
            sessionRoot,
            "pipeline-smart");

    var mixedProbeFolder =
        Path.Combine(
            sessionRoot,
            "pipeline-mixed");

    Directory.CreateDirectory(photoProbeFolder);
    Directory.CreateDirectory(pvProbeFolder);
    Directory.CreateDirectory(smartProbeFolder);
    Directory.CreateDirectory(mixedProbeFolder);

    File.WriteAllBytes(
        Path.Combine(
            photoProbeFolder,
            "DJI_0001.JPG"),
        [1]);

    File.WriteAllBytes(
        Path.Combine(
            pvProbeFolder,
            "DJI_20261003000000_0001_T.JPG"),
        [1]);

    foreach (var suffix in
             new[]
             {
                 "MS_G.TIF",
                 "MS_R.TIF",
                 "MS_RE.TIF",
                 "MS_NIR.TIF"
             })
    {
        File.WriteAllBytes(
            Path.Combine(
                smartProbeFolder,
                $"DJI_20261003000000_0001_{suffix}"),
            [1]);

        File.WriteAllBytes(
            Path.Combine(
                mixedProbeFolder,
                $"DJI_20261003000000_0001_{suffix}"),
            [1]);
    }

    File.WriteAllBytes(
        Path.Combine(
            mixedProbeFolder,
            "DJI_20261003000000_0002_T.JPG"),
        [1]);

    var photoProbe =
        ProjectPipelineStore.ProbeDataset(
            photoProbeFolder);

    var pvProbe =
        ProjectPipelineStore.ProbeDataset(
            pvProbeFolder);

    var smartProbe =
        ProjectPipelineStore.ProbeDataset(
            smartProbeFolder);

    var mixedProbe =
        ProjectPipelineStore.ProbeDataset(
            mixedProbeFolder);

    if (photoProbe.Module !=
            ProjectPipelineModule.Photogrammetry ||
        pvProbe.Module !=
            ProjectPipelineModule.PvAnalysis ||
        smartProbe.Module !=
            ProjectPipelineModule.SmartFarming ||
        mixedProbe.Module !=
            ProjectPipelineModule.Unknown ||
        !mixedProbe.Ambiguous)
    {
        throw new InvalidDataException(
            $"Pipeline dataset detection failed: photo={photoProbe.Module}, " +
            $"pv={pvProbe.Module}, smart={smartProbe.Module}, mixed={mixedProbe.Module}/{mixedProbe.Ambiguous}");
    }

    var pipeline =
        ProjectPipelineStore.Start(
            sessionProjectPath,
            photoProbeFolder,
            sessionPlan);

    var loadedPipeline =
        ProjectPipelineStore.Load(
            sessionProjectPath)
        ?? throw new InvalidDataException(
            "Pipeline state was not persisted.");

    if (loadedPipeline.JobId !=
            pipeline.JobId ||
        loadedPipeline.Module !=
            ProjectPipelineModule.Photogrammetry)
    {
        throw new InvalidDataException(
            "Persisted pipeline state mismatch.");
    }

    var pipelineGates =
        ProjectPipelineGateEvaluator.Evaluate(
            sessionProjectPath,
            active,
            loadedPipeline);

    if (pipelineGates.FlightPlan.State !=
            ProjectPipelineGateState.Pass ||
        pipelineGates.Dataset.State !=
            ProjectPipelineGateState.Pass ||
        pipelineGates.Rtk.State !=
            ProjectPipelineGateState.Pass ||
        pipelineGates.Processing.State !=
            ProjectPipelineGateState.Warning ||
        pipelineGates.Results.State !=
            ProjectPipelineGateState.Open)
    {
        throw new InvalidDataException(
            $"Unexpected pipeline gates: plan={pipelineGates.FlightPlan.State}, " +
            $"dataset={pipelineGates.Dataset.State}, rtk={pipelineGates.Rtk.State}, " +
            $"processing={pipelineGates.Processing.State}, results={pipelineGates.Results.State}");
    }

    loadedPipeline =
        ProjectPipelineStore.RefreshStage(
            sessionProjectPath,
            active,
            loadedPipeline);

    if (loadedPipeline.Stage !=
        ProjectPipelineStage.Processing)
    {
        throw new InvalidDataException(
            $"Pipeline stage should be Processing, got {loadedPipeline.Stage}.");
    }

    navigation = null;

    DroneDashProjectSession.RequestNavigation(
        ProjectNavigationTarget.Photogrammetry,
        sessionPlan,
        "pipeline dataset handoff",
        photoProbeFolder);

    if (navigation is null ||
        !string.Equals(
            Path.GetFullPath(
                navigation.DatasetFolder ?? ""),
            Path.GetFullPath(
                photoProbeFolder),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Pipeline navigation did not carry the dataset folder.");
    }

    File.AppendAllText(
        sessionAnalysis,
        Environment.NewLine + """{"refresh":true}""");

    await DroneDashProjectSession.RegisterFileAsync(
        sessionAnalysis,
        ProjectArtifactKind.PvAnalysis);

    active =
        DroneDashProjectSession.CurrentProject
        ?? throw new InvalidDataException(
            "Project session lost project after refresh.");

    if (active.Artifacts.Count != 4)
        throw new InvalidDataException(
            "Re-registering the same artifact created a duplicate.");

    var refreshedAnalysis =
        active.Artifacts.Single(
            artifact =>
                artifact.Kind ==
                ProjectArtifactKind.PvAnalysis);

    var refreshedVerification =
        await ProjectArtifactService.VerifyAsync(
            sessionProjectPath,
            refreshedAnalysis);

    if (refreshedVerification.Integrity !=
        ProjectArtifactIntegrity.Ok)
    {
        throw new InvalidDataException(
            "Re-registered artifact snapshot was not refreshed.");
    }

    await DroneDashProjectSession.SaveMetadataAsync(
        "Integrated Workflow Updated",
        "session smoke");

    if (DroneDashProjectSession.CurrentProject?.Name !=
        "Integrated Workflow Updated")
    {
        throw new InvalidDataException(
            "Project session metadata save failed.");
    }

    if (sessionChangeCount < 6)
        throw new InvalidDataException(
            "Project session change notifications were not emitted.");

    var pipelineSaveAsRoot =
        Path.Combine(
            root,
            "pipeline-save-as");

    Directory.CreateDirectory(
        pipelineSaveAsRoot);

    var pipelineSaveAsPath =
        Path.Combine(
            pipelineSaveAsRoot,
            "integrated-copy.ddproj");

    await DroneDashProjectSession.SaveAsAsync(
        pipelineSaveAsPath,
        "Integrated Workflow Copy",
        "pipeline save-as smoke");

    var rebasedPipeline =
        ProjectPipelineStore.Load(
            pipelineSaveAsPath)
        ?? throw new InvalidDataException(
            "Pipeline state was not copied by project Save As.");

    var rebasedPipelineSource =
        ProjectPipelineStore.ResolveSourceFolder(
            pipelineSaveAsPath,
            rebasedPipeline);

    if (!Path.GetFullPath(
            rebasedPipelineSource)
        .Equals(
            Path.GetFullPath(
                photoProbeFolder),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Pipeline Save As changed the dataset target.");
    }

    var processingRoot =
        Path.Combine(
            root,
            "processing-orchestrator");

    Directory.CreateDirectory(
        processingRoot);

    var processingProjectPath =
        Path.Combine(
            processingRoot,
            "processing.ddproj");

    var processingProject =
        DroneDashProjectStore.Create(
            "Processing Orchestrator");

    DroneDashProjectStore.Save(
        processingProjectPath,
        processingProject);

    DroneDashProjectSession.Activate(
        processingProjectPath,
        DroneDashProjectStore.Load(
            processingProjectPath),
        "processing smoke");

    var processingPipeline =
        ProjectPipelineStore.Start(
            processingProjectPath,
            smartProbeFolder,
            sessionPlan);

    var processingActive =
        DroneDashProjectSession.CurrentProject
        ?? throw new InvalidDataException(
            "Processing smoke project was not activated.");

    var datasetJob =
        ProjectProcessingCoordinator.EnsureForPipeline(
            processingProjectPath,
            processingActive,
            processingPipeline)
        ?? throw new InvalidDataException(
            "Dataset worker job was not created.");

    if (datasetJob.Worker !=
            ProjectProcessingWorkerKind.SmartFarmingDatasetQa ||
        datasetJob.Status !=
            ProjectProcessingJobStatus.Queued)
    {
        throw new InvalidDataException(
            $"Unexpected initial worker: {datasetJob.Worker}/{datasetJob.Status}");
    }

    ProjectProcessingCoordinator.BeginActive(
        ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
        "smoke dataset",
        "scan");

    ProjectProcessingCoordinator.ReportActive(
        ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
        42d,
        "smoke progress",
        "scan",
        appendLog: true);

    ProjectProcessingCoordinator.AwaitOutputActive(
        ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
        "smoke dataset output");

    var smartQaPath =
        Path.Combine(
            processingRoot,
            "smart-farming-dataset.json");

    File.WriteAllText(
        smartQaPath,
        """{"schemaVersion":1,"summary":{"captureCount":1,"completeCaptureCount":1,"issueCount":0},"captures":[]}""");

    await DroneDashProjectSession.RegisterFileAsync(
        smartQaPath,
        ProjectArtifactKind.SmartFarmingDataset);

    ProjectProcessingCoordinator.RecordOutputActive(
        ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
        smartQaPath,
        ProjectArtifactKind.SmartFarmingDataset);

    ProjectProcessingCoordinator.CompleteActive(
        ProjectProcessingWorkerKind.SmartFarmingDatasetQa,
        "smoke dataset complete");

    processingActive =
        DroneDashProjectSession.CurrentProject!;

    var nodeJob =
        ProjectProcessingCoordinator.EnsureForPipeline(
            processingProjectPath,
            processingActive,
            processingPipeline)
        ?? throw new InvalidDataException(
            "NodeODM worker job was not created.");

    if (nodeJob.Worker !=
            ProjectProcessingWorkerKind.SmartFarmingNodeOdm ||
        nodeJob.Status !=
            ProjectProcessingJobStatus.Queued)
    {
        throw new InvalidDataException(
            $"Unexpected NodeODM worker: {nodeJob.Worker}/{nodeJob.Status}");
    }

    ProjectProcessingCoordinator.BeginActive(
        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
        "smoke nodeodm",
        "nodeodm");

    ProjectProcessingCoordinator.ReportActive(
        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
        75d,
        "smoke nodeodm progress",
        "nodeodm");

    ProjectProcessingCoordinator.AwaitOutputActive(
        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
        "smoke nodeodm output");

    var nodeZip =
        Path.Combine(
            processingRoot,
            "all.zip");

    File.WriteAllBytes(
        nodeZip,
        [1, 2, 3]);

    await DroneDashProjectSession.RegisterFileAsync(
        nodeZip,
        ProjectArtifactKind.NodeOdmResultArchive);

    ProjectProcessingCoordinator.RecordOutputActive(
        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
        nodeZip,
        ProjectArtifactKind.NodeOdmResultArchive);

    ProjectProcessingCoordinator.CompleteActive(
        ProjectProcessingWorkerKind.SmartFarmingNodeOdm,
        "smoke nodeodm complete");

    processingActive =
        DroneDashProjectSession.CurrentProject!;

    var fieldJob =
        ProjectProcessingCoordinator.EnsureForPipeline(
            processingProjectPath,
            processingActive,
            processingPipeline)
        ?? throw new InvalidDataException(
            "Field-product worker job was not created.");

    if (fieldJob.Worker !=
            ProjectProcessingWorkerKind.SmartFarmingFieldProducts ||
        fieldJob.Status !=
            ProjectProcessingJobStatus.Queued)
    {
        throw new InvalidDataException(
            $"Unexpected field worker: {fieldJob.Worker}/{fieldJob.Status}");
    }

    ProjectProcessingCoordinator.BeginActive(
        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
        "smoke field products",
        "field-products");

    var fieldManifest =
        Path.Combine(
            processingRoot,
            "field-products.json");

    File.WriteAllText(
        fieldManifest,
        """{"schemaVersion":1}""");

    await DroneDashProjectSession.RegisterFileAsync(
        fieldManifest,
        ProjectArtifactKind.SmartFarmingFieldProducts);

    ProjectProcessingCoordinator.RecordOutputActive(
        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
        fieldManifest,
        ProjectArtifactKind.SmartFarmingFieldProducts);

    ProjectProcessingCoordinator.CompleteActive(
        ProjectProcessingWorkerKind.SmartFarmingFieldProducts,
        "smoke field products complete");

    processingActive =
        DroneDashProjectSession.CurrentProject!;

    var finalJob =
        ProjectProcessingCoordinator.EnsureForPipeline(
            processingProjectPath,
            processingActive,
            processingPipeline)
        ?? throw new InvalidDataException(
            "Final processing job was not retained.");

    var processingLedger =
        ProjectProcessingCoordinator.Load(
            processingProjectPath);

    if (processingLedger.Jobs.Count != 3 ||
        processingLedger.Jobs.Any(job =>
            job.Status !=
            ProjectProcessingJobStatus.Succeeded) ||
        processingLedger.Jobs.Any(job =>
            !File.Exists(
                ProjectProcessingCoordinator.ResolveLogPath(
                    processingProjectPath,
                    job)!)))
    {
        throw new InvalidDataException(
            "Processing ledger did not persist three successful workers with logs.");
    }

    if (processingLedger.Jobs.Sum(job =>
            job.Outputs.Count) < 3)
    {
        throw new InvalidDataException(
            "Processing worker outputs were not associated with jobs.");
    }

    var finalProcessingGates =
        ProjectPipelineGateEvaluator.Evaluate(
            processingProjectPath,
            processingActive,
            processingPipeline);

    if (finalProcessingGates.Dataset.State !=
            ProjectPipelineGateState.Pass ||
        finalProcessingGates.Processing.State !=
            ProjectPipelineGateState.Pass ||
        finalProcessingGates.Results.State !=
            ProjectPipelineGateState.Pass)
    {
        throw new InvalidDataException(
            $"Processing outputs did not release gates: dataset={finalProcessingGates.Dataset.State}, " +
            $"processing={finalProcessingGates.Processing.State}, results={finalProcessingGates.Results.State}");
    }

    var processingCopyRoot =
        Path.Combine(
            root,
            "processing-orchestrator-copy");

    Directory.CreateDirectory(
        processingCopyRoot);

    var processingCopyPath =
        Path.Combine(
            processingCopyRoot,
            "processing-copy.ddproj");

    await DroneDashProjectSession.SaveAsAsync(
        processingCopyPath,
        "Processing Orchestrator Copy",
        "processing ledger save-as smoke");

    var rebasedProcessingLedger =
        ProjectProcessingCoordinator.Load(
            processingCopyPath);

    if (rebasedProcessingLedger.Jobs.Count !=
        processingLedger.Jobs.Count)
    {
        throw new InvalidDataException(
            "Processing ledger was not copied by project Save As.");
    }

    for (var jobIndex = 0;
         jobIndex < processingLedger.Jobs.Count;
         jobIndex++)
    {
        var originalLog =
            ProjectProcessingCoordinator.ResolveLogPath(
                processingProjectPath,
                processingLedger.Jobs[jobIndex]);

        var rebasedLog =
            ProjectProcessingCoordinator.ResolveLogPath(
                processingCopyPath,
                rebasedProcessingLedger.Jobs[jobIndex]);

        if (!string.Equals(
                Path.GetFullPath(
                    originalLog!),
                Path.GetFullPath(
                    rebasedLog!),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Processing Save As changed a worker log target.");
        }
    }

    Console.WriteLine(
        $"PASS project session · workflow={workflow.SummaryText} · " +
        $"dashboard={dashboard.SummaryText} · pipeline={pipelineGates.SummaryText} · " +
        $"processingJobs={processingLedger.Jobs.Count} · events={sessionChangeCount}");

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
