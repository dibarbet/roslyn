// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.VisualStudio.TaskStatusCenter;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Diagnostics
{
    [Export(typeof(TaskCenterSolutionAnalysisProgressReporter))]
    internal sealed partial class TaskCenterSolutionAnalysisProgressReporter
    {
        private static readonly TimeSpan s_minimumInterval = TimeSpan.FromMilliseconds(200);

        private readonly IVsTaskStatusCenterService _taskCenterService;
        private readonly TaskHandlerOptions _options;

        // these fields are never accessed concurrently
        private TaskCompletionSource<VoidResult>? _currentTask;

        private readonly object _lock = new object();

        private ProgressData _lastProgressData;

        /// <summary>
        /// Unfortunately, <see cref="ProgressData.PendingItemCount"/> is only reported
        /// when the <see cref="ProgressData.Status"/> is <see cref="ProgressStatus.PendingItemCountUpdated"/>
        /// So we have to store this in addition to <see cref="_lastProgressData"/> so that we
        /// do not overwrite the last reported count with 0.
        /// </summary>
        private int _lastProgressCount;

        /// <summary>
        /// Task used to trigger throttled UI updates in an interval
        /// defined by <see cref="s_minimumInterval"/>
        /// </summary>
        private Task? _intervalTask;

        private ITaskHandler _taskHandler;

        [ImportingConstructor]
        public TaskCenterSolutionAnalysisProgressReporter(
            SVsTaskStatusCenterService taskStatusCenterService,
            IDiagnosticService diagnosticService,
            VisualStudioWorkspace workspace)
        {
            _taskCenterService = (IVsTaskStatusCenterService)taskStatusCenterService;
            _options = new TaskHandlerOptions()
            {
                Title = ServicesVSResources.Running_low_priority_background_processes,
                ActionsAfterCompletion = CompletionActions.None
            };

            _taskHandler = _taskCenterService.PreRegister(_options, data: default);

            var crawlerService = workspace.Services.GetRequiredService<ISolutionCrawlerService>();
            var reporter = crawlerService.GetProgressReporter(workspace);

            reporter.ProgressChanged += OnSolutionCrawlerProgressChanged;

            if (reporter.InProgress)
            {
                Started();
            }
            else
            {
                Stopped();
            }
        }

        /// <summary>
        /// Retrieve and throttle solution crawler events to be sent to the progress reporter UI.
        /// 
        /// there is no concurrent call to this method since ISolutionCrawlerProgressReporter will serialize all
        /// events to preserve event ordering
        /// </summary>
        /// <param name="progressData"></param>
        public void OnSolutionCrawlerProgressChanged(object sender, ProgressData progressData)
        {
            lock (_lock)
            {
                _lastProgressData = progressData;

                // The task is running which will update the progress.
                if (_intervalTask != null)
                {
                    return;
                }

                _intervalTask = Task.CompletedTask.ContinueWithAfterDelay(() =>
                {
                    ReportProgress();
                }, CancellationToken.None, s_minimumInterval, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                // Report progress immediately to ensure we update the UI on the first event.
                ReportProgress();
            }
        }

        private void ReportProgress()
        {
            ProgressData data;
            lock (_lock)
            {
                data = _lastProgressData;
                _intervalTask = null;
            }

            UpdateUI(data);
        }

        private void UpdateUI(ProgressData progressData)
        {
            // there is no concurrent call to this method since SolutionCrawlerEventHandlerWithDelay will serialize all
            // events to preserve event ordering
            switch (progressData.Status)
            {
                case ProgressStatus.Started:
                    Started();
                    break;
                case ProgressStatus.PendingItemCountUpdated:
                    _lastProgressCount = progressData.PendingItemCount ?? 0;
                    ChangeProgress(GetMessage(progressData, _lastProgressCount));
                    break;
                case ProgressStatus.Stopped:
                    Stopped();
                    break;
                case ProgressStatus.Evaluating:
                case ProgressStatus.Paused:
                    ChangeProgress(GetMessage(progressData, _lastProgressCount));
                    break;
                default:
                    throw ExceptionUtilities.UnexpectedValue(progressData.Status);
            }
        }

        private static string GetMessage(ProgressData progressData, int pendingItemCount)
        {
            var statusMessage = (progressData.Status == ProgressStatus.Paused) ? ServicesVSResources.Paused_0_tasks_in_queue : ServicesVSResources.Evaluating_0_tasks_in_queue;
            return string.Format(statusMessage, pendingItemCount);
        }

        private void Started()
        {
            _lastProgressData = default;

            

            _currentTask = new TaskCompletionSource<VoidResult>();
            _taskHandler.RegisterTask(_currentTask.Task);

            // report initial progress
            ChangeProgress(message: null);

            // set handler
            _taskHandler = taskHandler;
        }

        private void Stopped()
        {
            // clear progress message
            ChangeProgress(_taskHandler, message: null);

            // stop progress
            _currentTask?.TrySetResult(default);
            _currentTask = null;

            _taskHandler = null;

            _lastProgressData = default;
        }

        private static void ChangeProgress(string? message)
        {
            var data = new TaskProgressData
            {
                ProgressText = message,
                CanBeCanceled = false,
                PercentComplete = null,
            };

            _taskHandler.Progress.Report(data);
        }
    }
}

