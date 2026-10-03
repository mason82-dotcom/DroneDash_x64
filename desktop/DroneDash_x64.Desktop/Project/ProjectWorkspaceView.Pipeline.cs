using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DroneDash_x64.Desktop.Planning;
using WinForms = System.Windows.Forms;

namespace DroneDash_x64.Desktop.Project;

public partial class ProjectWorkspaceView
{
    private void RefreshPipelineUi(
        bool refreshStage)
    {
        if (_project is null ||
            string.IsNullOrWhiteSpace(
                _projectPath))
        {
            ResetPipelineUi();
            return;
        }

        try
        {
            var state =
                ProjectPipelineStore.Load(
                    _projectPath);

            if (state is null)
            {
                ResetPipelineUi();
                IngestDatasetButton.IsEnabled = true;
                RefreshPipelineButton.IsEnabled = true;
                return;
            }

            if (refreshStage)
            {
                state =
                    ProjectPipelineStore.RefreshStage(
                        _projectPath,
                        _project,
                        state);
            }

            _pipelineState =
                state;

            var source =
                ProjectPipelineStore.ResolveSourceFolder(
                    _projectPath,
                    state);

            PipelineStateText.Text =
                $"Job {state.JobId} · Ziel {state.Module} · Stufe {state.Stage} · " +
                $"aktualisiert {state.UpdatedAtUtc.LocalDateTime:G}";

            PipelineSourceText.Text =
                $"Datensatz: {source}";

            PipelineProbeText.Text =
                $"Erkennung: {state.Probe.Detail} · Bilder {state.Probe.SupportedImageFiles:N0}/{state.Probe.TotalFiles:N0} Dateien";

            var processingJob =
                ProjectProcessingCoordinator.EnsureForPipeline(
                    _projectPath,
                    _project,
                    state);

            RenderProcessingJob(
                processingJob);

            var gates =
                ProjectPipelineGateEvaluator.Evaluate(
                    _projectPath,
                    _project,
                    state);

            RenderPipelineGate(
                gates.FlightPlan,
                PipelinePlanGateStateText,
                PipelinePlanGateDetailText);

            RenderPipelineGate(
                gates.Dataset,
                PipelineDatasetGateStateText,
                PipelineDatasetGateDetailText);

            RenderPipelineGate(
                gates.Rtk,
                PipelineRtkGateStateText,
                PipelineRtkGateDetailText);

            RenderPipelineGate(
                gates.Processing,
                PipelineProcessingGateStateText,
                PipelineProcessingGateDetailText);

            RenderPipelineGate(
                gates.Results,
                PipelineResultGateStateText,
                PipelineResultGateDetailText);

            ContinuePipelineButton.IsEnabled =
                ProjectPipelineStore.ToNavigationTarget(
                    state.Module)
                .HasValue;

            IngestDatasetButton.IsEnabled = true;
            RefreshPipelineButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _pipelineState = null;

            PipelineStateText.Text =
                $"Pipeline-Status konnte nicht gelesen werden: {ex.Message}";

            PipelineSourceText.Text =
                "Datensatz: —";

            PipelineProbeText.Text =
                "Erkennung: —";

            ContinuePipelineButton.IsEnabled =
                false;

            IngestDatasetButton.IsEnabled =
                true;

            RefreshPipelineButton.IsEnabled =
                true;
        }
    }

    private void RenderProcessingJob(
        ProjectProcessingJob? job)
    {
        if (job is null)
        {
            ProcessingWorkerText.Text =
                "—";

            ProcessingJobStatusText.Text =
                "—";

            ProcessingJobMessageText.Text =
                "Kein Processing-Job.";

            ProcessingJobProgress.Value =
                0;

            ProcessingJobLogText.Text =
                "—";

            return;
        }

        ProcessingWorkerText.Text =
            $"{job.Worker} · {job.Id}";

        ProcessingJobStatusText.Text =
            job.StatusText;

        ProcessingJobMessageText.Text =
            string.IsNullOrWhiteSpace(
                job.CurrentStep)
                ? job.Message
                : $"{job.CurrentStep} · {job.Message}";

        ProcessingJobProgress.Value =
            Math.Clamp(
                job.Percent,
                0d,
                100d);

        ProcessingJobLogText.Text =
            string.IsNullOrWhiteSpace(
                _projectPath)
                ? job.LogPath.StoredPath
                : ProjectProcessingCoordinator.ResolveLogPath(
                      _projectPath,
                      job) ??
                  job.LogPath.StoredPath;
    }

    private static void RenderPipelineGate(
        ProjectPipelineGate gate,
        TextBlock stateText,
        TextBlock detailText)
    {
        stateText.Text =
            gate.StateText;

        detailText.Text =
            gate.Detail;
    }

    private void ResetPipelineUi()
    {
        _pipelineState = null;

        PipelineStateText.Text =
            "Kein aktiver Pipeline-Job.";

        PipelineSourceText.Text =
            "Datensatz: —";

        PipelineProbeText.Text =
            "Erkennung: —";

        RenderProcessingJob(
            null);

        foreach (var pair in
                 new[]
                 {
                     (PipelinePlanGateStateText, PipelinePlanGateDetailText),
                     (PipelineDatasetGateStateText, PipelineDatasetGateDetailText),
                     (PipelineRtkGateStateText, PipelineRtkGateDetailText),
                     (PipelineProcessingGateStateText, PipelineProcessingGateDetailText),
                     (PipelineResultGateStateText, PipelineResultGateDetailText)
                 })
        {
            pair.Item1.Text =
                "OFFEN";

            pair.Item2.Text =
                "—";
        }
    }
}
