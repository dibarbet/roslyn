// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.ServiceHub.Framework;

namespace Microsoft.CodeAnalysis.BrokeredServices;

internal interface IWorkspaceServiceBrokerProxy : IWorkspaceService
{
    ValueTask<TResult?> UseProxyAsync<TProxy, TResult>(ServiceRpcDescriptor descriptor, Func<TProxy, CancellationToken, ValueTask<TResult?>> action, CancellationToken cancellationToken) where TProxy : class;
}
