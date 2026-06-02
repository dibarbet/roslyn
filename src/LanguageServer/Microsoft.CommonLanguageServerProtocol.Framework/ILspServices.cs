// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

internal interface ILspServices : IDisposable
{
    /// <summary>
    /// Returns the service registered for the concrete type <typeparamref name="T"/>, or
    /// <see langword="null"/> if no such service is registered.
    /// </summary>
    T? GetLspService<T>() where T : class;

    /// <summary>
    /// Returns the service registered for the concrete type <typeparamref name="T"/>, or
    /// throws if no such service is registered.
    /// </summary>
    T GetRequiredLspService<T>() where T : class;

    /// <summary>
    /// Returns the single service that implements the interface <typeparamref name="T"/>, or
    /// <see langword="null"/> if no such service is registered. Throws if more than one
    /// registered service implements <typeparamref name="T"/>.
    /// </summary>
    T? GetLspServiceFromInterface<T>() where T : class;

    /// <summary>
    /// Returns the single service that implements the interface <typeparamref name="T"/>, or
    /// throws if zero or more than one registered services implement <typeparamref name="T"/>.
    /// </summary>
    T GetRequiredLspServiceFromInterface<T>() where T : class;

    /// <summary>
    /// Returns all services that implement the interface <typeparamref name="T"/>.
    /// May return an empty enumerable if no registered services implement <typeparamref name="T"/>.
    /// </summary>
    IEnumerable<T> GetLspServicesFromInterface<T>() where T : class;

    bool TryGetService(Type type, [NotNullWhen(true)] out object? service);
}
