// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.VisualStudio.LiveShare;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.VisualStudio.LanguageServices.LiveShare.Client
{
    internal sealed partial class RemoteLanguageServiceWorkspace
    {
        /// <summary>
        /// Helper to act as an event sink to subscribe to IVsSolutionEvents and update the remote workspace paths whenever a solution is opened.
        /// TODO - Live share should provide us a proper API to accomplish this.
        /// </summary>
        private class RemoteWorkspacePathsEventSink : ForegroundThreadAffinitizedObject, IVsSolutionEvents, IDisposable
        {
            private readonly IThreadingContext _threadingContext;
            private readonly RemoteLanguageServiceWorkspace _remoteLanguageServiceWorkspace;
            private readonly CollaborationSession _collaborationSession;
            private readonly IVsSolution _solution;
            private readonly uint _solutionEventsCookie;

            /// <summary>
            /// Stores the current base folder path(s) on the client that hold files retrieved from the host workspace(s).
            /// </summary>
            private ImmutableHashSet<string> _remoteWorkspaceRootPaths;

            /// <summary>
            /// Stores the current base folder path on the client that holds registered external files.
            /// </summary>
            private ImmutableHashSet<string> _registeredExternalPaths;

            public RemoteWorkspacePathsEventSink(CollaborationSession session, RemoteLanguageServiceWorkspace remoteLanguageServiceWorkspace, IThreadingContext threadingContext, IVsSolution solution)
                : base(threadingContext, assertIsForeground: true)
            {
                _threadingContext = threadingContext;
                _remoteLanguageServiceWorkspace = remoteLanguageServiceWorkspace;
                _collaborationSession = session;

                _remoteWorkspaceRootPaths = ImmutableHashSet<string>.Empty;
                _registeredExternalPaths = ImmutableHashSet<string>.Empty;

                _solution = solution;
                _solution.AdviseSolutionEvents(this, out _solutionEventsCookie);
            }

            public bool IsExternalLocalUri(string localPath)
                => _registeredExternalPaths.Any(externalPath => localPath.StartsWith(externalPath) && localPath.Length > externalPath.Length + 1);

            public string GetRemoteWorkspaceRoot(string filePath)
                => _remoteWorkspaceRootPaths.SingleOrDefault(remoteWorkspaceRoot => filePath.StartsWith(remoteWorkspaceRoot));

            /// <summary>
            /// Retrieves the base folder paths for files on the client that have been retrieved from the remote host.
            /// Triggers a refresh of all open files so we make sure they are in the correct workspace.
            /// </summary>
            public async Task UpdatePathsToRemoteFiles()
            {
                var roots = await _collaborationSession.ListRootsAsync(CancellationToken.None).ConfigureAwait(false);
                if (roots.Length > 0)
                {
                    var localPathsOfRemoteRoots = roots.Select(root => _collaborationSession.ConvertSharedUriToLocalPath(root)).ToImmutableArray();

                    var remoteRootPaths = new HashSet<string>();
                    var externalPaths = new HashSet<string>();

                    foreach (var localRoot in localPathsOfRemoteRoots)
                    {
                        var remoteRootPath = localRoot.Substring(0, localRoot.Length - 1);
                        var lastSlash = remoteRootPath.LastIndexOf('\\');
                        var externalPath = remoteRootPath.Substring(0, lastSlash + 1);
                        externalPath += "~external";

                        remoteRootPaths.Add(remoteRootPath);
                        externalPaths.Add(externalPath);
                    }

                    _remoteWorkspaceRootPaths = remoteRootPaths.ToImmutableHashSet();
                    _registeredExternalPaths = externalPaths.ToImmutableHashSet();
                    await _remoteLanguageServiceWorkspace.RefreshAllFiles().ConfigureAwait(false);
                }
            }

            public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
            {
                // The remote workspace paths can change any time a solution is opened.
                // So trigger an update to get the new paths any time this happens in a Live Share session.
                if (_remoteLanguageServiceWorkspace.IsRemoteSession)
                {
                    _threadingContext.JoinableTaskFactory.Run(async () => { await UpdatePathsToRemoteFiles().ConfigureAwait(false); });
                }

                return VSConstants.S_OK;
            }

            public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.E_NOTIMPL;

            public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.E_NOTIMPL;

            public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.E_NOTIMPL;

            public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.E_NOTIMPL;

            public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.E_NOTIMPL;

            public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.E_NOTIMPL;

            public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.E_NOTIMPL;

            public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.E_NOTIMPL;

            public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.E_NOTIMPL;

            public void Dispose()
            {
                _solution.UnadviseSolutionEvents(_solutionEventsCookie);
            }
        }
    }
}
