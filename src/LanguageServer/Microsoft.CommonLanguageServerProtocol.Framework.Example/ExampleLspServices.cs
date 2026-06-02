// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.CommonLanguageServerProtocol.Framework.Example;

internal sealed class ExampleLspServices : ILspServices
{
    private readonly IServiceProvider _serviceProvider;

    public ExampleLspServices(IServiceCollection serviceCollection)
    {
        _ = serviceCollection.AddSingleton<ILspServices>(this);

        var serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider = serviceProvider;
    }

    public T? GetLspService<T>() where T : class
    {
        return TryGetService(typeof(T), out var service)
            ? (T)service
            : null;
    }

    public T GetRequiredLspService<T>() where T : class
    {
        var service = _serviceProvider.GetRequiredService<T>();

        return service;
    }

    public T? GetLspServiceFromInterface<T>() where T : class
    {
        return GetLspServicesFromInterface<T>().SingleOrDefault();
    }

    public T GetRequiredLspServiceFromInterface<T>() where T : class
    {
        return GetLspServicesFromInterface<T>().Single();
    }

    public bool TryGetService(Type type, [NotNullWhen(true)] out object? service)
    {
        service = _serviceProvider.GetService(type);

        return service is not null;
    }

    public IEnumerable<TService> GetServices<TService>()
    {
        return _serviceProvider.GetServices<TService>();
    }

    public void Dispose()
    {
    }

    public IEnumerable<T> GetLspServicesFromInterface<T>() where T : class
    {
        var services = _serviceProvider.GetServices<T>();

        return services;
    }
}
