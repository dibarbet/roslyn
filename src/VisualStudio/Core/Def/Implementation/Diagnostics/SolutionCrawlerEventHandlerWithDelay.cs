// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Diagnostics
{
    internal sealed partial class TaskCenterSolutionAnalysisProgressReporter
    {
        /// <summary>
        /// Helper class to pass on solution crawler events with an additional event invoked after the previous event and a delay.
        /// Outputs the resulting events in single-file.
        /// Reason for invoking an additional event with a delay.
        /// When an event stream comes in as below (assuming 200ms minimum interval)
        /// e1 -> (100ms)-> e2 -> (300ms)-> e3 -> (100ms) -> e4
        ///
        /// The actual status shown to users without an additional event will be 
        /// e1 -> e3.
        ///
        /// e2 and e4 will be skipped since the interval was smaller than min interval.
        /// Losing e2 is fine, but losing e4 is problematic as the user sees the wrong status until the next event comes in. 
        /// For example, it could show "Evaluating" when it is actually "Paused".
        /// 
        /// <see cref="_resettableDelay"/> ensures that we invoke an event for e4 if the next event doesn't
        /// arrive within a certain delay.
        /// </summary>
        private class SolutionCrawlerEventHandlerWithDelay
        {
            private readonly int _resettableDelayInterval;
            private readonly object _lock = new object();

            private ProgressData _progressData;
            private ResettableDelay? _resettableDelay;
            private Task _task;

            public EventHandler<ProgressData>? UpdateProgress;

            public SolutionCrawlerEventHandlerWithDelay(ISolutionCrawlerProgressReporter reporter, int resettableDelayInterval)
            {
                _resettableDelayInterval = resettableDelayInterval;
                _task = Task.CompletedTask;

                // no event unsubscription since it will remain alive until VS shutdown
                reporter.ProgressChanged += OnSolutionCrawlerProgressChanged;
            }

            /// <summary>
            /// Invoke a progress updated event and add delay.
            /// Delay is used to invoke the event again at a later point.
            /// The second invocation delay is reset as new events come in.
            /// 
            /// there is no concurrent call to this method since ISolutionCrawlerProgressReporter will serialize all
            /// events to preserve event ordering
            /// </summary>
            /// <param name="progressData"></param>
            public void OnSolutionCrawlerProgressChanged(object sender, ProgressData progressData)
            {
                _progressData = progressData;
                if (_resettableDelay == null || _resettableDelay.Task.IsCompleted)
                {
                    StartDelay();
                }
                else
                {
                    _resettableDelay.Reset();
                }

                InvokeEvent();
            }

            /// <summary>
            /// Serialize output events.
            /// </summary>
            private void InvokeEvent()
            {
                EventHandler<ProgressData>? localUpdateProgress;
                ProgressData progressData;
                lock (_lock)
                {
                    localUpdateProgress = UpdateProgress;
                    progressData = _progressData;
                    _task = _task.SafeContinueWith(_ =>
                    {
                        localUpdateProgress?.Invoke(this, _progressData);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }

            private void StartDelay()
            {
                _resettableDelay = new ResettableDelay(_resettableDelayInterval, AsynchronousOperationListenerProvider.NullListener);
                _resettableDelay.Task.SafeContinueWith(_ =>
                {
                    InvokeEvent();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}

