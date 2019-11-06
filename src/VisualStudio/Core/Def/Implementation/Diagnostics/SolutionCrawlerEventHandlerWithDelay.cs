// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Diagnostics
{
    internal sealed partial class TaskCenterSolutionAnalysisProgressReporter
    {
        /// <summary>
        /// Helper class to pass on solution crawler events throttled with a certain delay.
        /// arrive within a certain delay.
        /// </summary>
        private class SolutionCrawlerEventHandlerWithDelay
        {
            private readonly TimeSpan _publishInterval;
            private readonly object _lock = new object();

            private ProgressData _progressData;
            private ProgressData _lastPublishedProgressData;

            /// <summary>
            /// Task used to invoke events outside the <see cref="_lock"/>
            /// </summary>
            private Task _invokeEventTask;

            /// <summary>
            /// Task used to trigger event invocation on an interval
            /// defined by <see cref="_publishInterval"/>
            /// </summary>
            private Task _intervalTask;

            private bool _isTimerRunning;

            /// <summary>
            /// Solution crawler events throttled to the specified <see cref="_publishInterval"/>
            /// </summary>
            public EventHandler<ProgressData>? UpdateProgress;

            public SolutionCrawlerEventHandlerWithDelay(ISolutionCrawlerProgressReporter reporter, TimeSpan publishInterval)
            {
                _publishInterval = publishInterval;
                _invokeEventTask = Task.CompletedTask;
                _intervalTask = Task.CompletedTask;
                _isTimerRunning = false;

                // no event unsubscription since it will remain alive until VS shutdown
                reporter.ProgressChanged += OnSolutionCrawlerProgressChanged;
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
                    _progressData = progressData;

                    // The publish event timer is already running. If this is a stop event make sure the timer does not get extended.
                    // Otherwise, just update the progress data and continue.
                    if (_isTimerRunning)
                    {
                        if (progressData.Status == ProgressStatus.Stopped)
                        {
                            _isTimerRunning = false;
                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {
                        // Event publishing is not running, so trigger new timer.
                        _isTimerRunning = true;
                        RunOnTimer();
                    }
                }
            }

            private void InvokeEvent()
            {
                _invokeEventTask = _invokeEventTask.SafeContinueWith(_ =>
                {
                    // No need to publish the exact same event over and over.
                    if (!Equals(_progressData, _lastPublishedProgressData))
                    {
                        UpdateProgress?.Invoke(this, _progressData);
                        _lastPublishedProgressData = _progressData;
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            private void RunOnTimer()
            {
                lock (_lock)
                {
                    InvokeEvent();

                    // Only continue the timer if there has not been a stop event.
                    if (_isTimerRunning)
                    {
                        _intervalTask = _intervalTask.ContinueWithAfterDelay(() =>
                        {
                            RunOnTimer();
                        }, CancellationToken.None, _publishInterval, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
        }
    }
}

