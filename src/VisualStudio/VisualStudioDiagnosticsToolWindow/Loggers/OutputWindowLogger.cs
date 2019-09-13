﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Threading;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.RpcContracts.OutputChannel;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.CodeAnalysis.Internal.Log
{
    /// <summary>
    /// Implementation of <see cref="ILogger"/> that output to output window
    /// </summary>
    internal sealed class OutputWindowLogger : ILogger
    {
        private readonly Func<FunctionId, bool> _loggingChecker;

        private readonly IAsynchronousOperationListener _asyncListener;
        private readonly ServiceBrokerClient _serviceBrokerClient;
        private readonly IThreadingContext _threadingContext;

        public OutputWindowLogger(Func<FunctionId, bool> loggingChecker, IAsynchronousOperationListenerProvider asyncListenerProvider,
            IThreadingContext threadingContext, IBrokeredServiceContainer brokeredServiceContainer)
        {
            _loggingChecker = loggingChecker;

            Assumes.Present(brokeredServiceContainer);
            var serviceBroker = brokeredServiceContainer.GetFullAccessServiceBroker();

            _asyncListener = asyncListenerProvider.GetListener(FeatureAttribute.OutputWindowLogger);
            _threadingContext = threadingContext;
            _serviceBrokerClient = new ServiceBrokerClient(serviceBroker, _threadingContext.JoinableTaskFactory);
        }

        public bool IsEnabled(FunctionId functionId)
        {
            return _loggingChecker == null || _loggingChecker(functionId);
        }

        public void Log(FunctionId functionId, LogMessage logMessage)
        {
            WriteLine(string.Format("[{0}] {1} - {2}", Thread.CurrentThread.ManagedThreadId, functionId.ToString(), logMessage.GetMessage()));
        }

        public void LogBlockStart(FunctionId functionId, LogMessage logMessage, int uniquePairId, CancellationToken cancellationToken)
        {
            WriteLine(string.Format("[{0}] Start({1}) : {2} - {3}", Thread.CurrentThread.ManagedThreadId, uniquePairId, functionId.ToString(), logMessage.GetMessage()));
        }

        public void LogBlockEnd(FunctionId functionId, LogMessage logMessage, int uniquePairId, int delta, CancellationToken cancellationToken)
        {
            var functionString = functionId.ToString() + (cancellationToken.IsCancellationRequested ? " Canceled" : string.Empty);
            WriteLine(string.Format("[{0}] End({1}) : [{2}ms] {3}", Thread.CurrentThread.ManagedThreadId, uniquePairId, delta, functionString));
        }

        private void WriteLine(string value)
        {
            var asyncToken = _asyncListener.BeginAsyncOperation(nameof(WriteLine));
            Task.Run(async () =>
            {
                using var outputChannelStore = await _serviceBrokerClient.GetProxyAsync<IOutputChannelStore>(VisualStudioServices.VS2019_4.OutputChannelStore).ConfigureAwait(false);
                await outputChannelStore.Proxy.WriteLineAsync("Roslyn Logger Output", value).ConfigureAwait(false);
            }).CompletesAsyncOperation(asyncToken);
        }
    }
}
