// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;

namespace Microsoft.CodeAnalysis.LanguageServer.Daemon;

/// <summary>
/// A named interprocess mutex used to coordinate daemon discovery and startup. Targets .NET Core on
/// all platforms (no Mono / file-mutex fallback). Mirrors the compiler server's
/// <c>ServerNamedMutex</c>.
/// <para>
/// The daemon holds the "server" mutex for its lifetime (its existence signals "daemon is
/// running"). A connecting client briefly holds the "client" mutex to serialize the
/// check-server-then-launch sequence so two clients can't race to start two daemons.
/// </para>
/// <para>
/// <see cref="TryLock"/> uses <see cref="WaitHandle.WaitOne(int)"/> / <see cref="Mutex.ReleaseMutex"/>,
/// which must occur on the same thread. Callers must keep the acquire/release on a single thread
/// (i.e. avoid <c>await</c> between <see cref="TryLock"/> and <see cref="Dispose"/>).
/// </para>
/// </summary>
internal sealed class DaemonMutex : IDisposable
{
    private readonly Mutex _mutex;

    public bool IsDisposed { get; private set; }
    public bool IsLocked { get; private set; }

    public DaemonMutex(string name, out bool createdNew)
    {
        _mutex = new Mutex(initiallyOwned: true, name: name, createdNew: out createdNew);
        if (createdNew)
            IsLocked = true;
    }

    /// <summary>
    /// Returns whether a mutex with the given name currently exists (i.e. is held by some process).
    /// </summary>
    public static bool WasOpen(string mutexName)
    {
        Mutex? mutex = null;
        try
        {
            return Mutex.TryOpenExisting(mutexName, out mutex);
        }
        catch
        {
            // If we failed to open the mutex for any reason, assume it is not open.
            return false;
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>
    /// Attempts to acquire the mutex within the given timeout. Must be called (and later disposed)
    /// on the same thread.
    /// </summary>
    public bool TryLock(int timeoutMs)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(DaemonMutex));
        if (IsLocked)
            throw new InvalidOperationException("Lock already held");

        try
        {
            return IsLocked = _mutex.WaitOne(timeoutMs);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing the mutex; we now own it.
            return IsLocked = true;
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        try
        {
            if (IsLocked)
                _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
            IsLocked = false;
        }
    }
}
