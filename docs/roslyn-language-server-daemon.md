# Roslyn language server daemon

This document describes the optional **daemon mode** for the Roslyn language server and the thin
client (`roslyn-language-server`) that drives it. It covers process layout, the discovery/launch
connection flow, pipe/mutex naming, lifecycle, configuration, and known limitations.

## Why a daemon

The Roslyn language server holds expensive state in memory (MEF composition, workspaces, projects,
analyzers). Daemon mode lets a single long-lived server process serve multiple editor sessions,
amortizing startup cost and enabling state reuse across sessions. Daemon mode is **opt-in**; the
default behavior is unchanged (one server process per editor session).

## Process layout

There are two executables, shipped together in the `roslyn-language-server` .NET tool package:

| Executable | Role |
| --- | --- |
| `roslyn-language-server` | The **thin client** and tool entry point. Minimal, dependency-light, AOT/trim-ready. It relays raw LSP bytes; it never parses LSP. |
| `Microsoft.CodeAnalysis.LanguageServer` | The **real server**. Runs the Roslyn LSP server. In daemon mode it listens for many clients; otherwise it hosts a single session. |

The thin-client package **bundles** the server's full published output side-by-side (each app keeps
its own `*.deps.json`/`*.runtimeconfig.json`), so the thin client can launch the server from its own
directory.

```
Editor (LSP client)
  │  stdio or --pipe:<editorPipe>
  ▼
roslyn-language-server  (thin client; tool entry point)
  │   daemon mode:  client mutex → check server mutex → launch daemon if needed → connect
  │   relays raw LSP bytes:  editor transport  ↔  daemon pipe
  ▼  NamedPipeClientStream (PipeOptions.CurrentUserOnly)
Microsoft.CodeAnalysis.LanguageServer --daemon --daemonPipeName:<name>
  │   holds the server mutex; runs the accept loop
  ├── connection A → its own NamedPipeServerStream → JsonRpc → RoslynLanguageServer A
  ├── connection B → its own NamedPipeServerStream → JsonRpc → RoslynLanguageServer B
  └── last client leaves → keepalive timer → release mutex + stop listener + exit
```

In **non-daemon mode** (the default) the thin client instead launches the server as a child process
with `--stdio` and relays bytes between the editor and the child's stdio. This keeps the
editor-monitored process (the thin client) alive for the whole session while adding only a cheap
byte-copy hop.

## Discovery, launch, and single-instance guarantee

This follows the model used by the compiler server (`VBCSCompiler`); see
`src/Compilers/Shared/BuildServerConnection.cs`. The shared pieces live in
`src/LanguageServer/DaemonConnection/` and are source-linked into both the thin client and the
server so both compute identical names.

### Pipe name

`pipeName = base64(SHA-256("{userName}.{isAdmin}.{toolIdentifier}"))` with `/`→`_` and `=` stripped.

- `userName` / `isAdmin` scope the daemon to the current user and elevation level.
- `toolIdentifier` is the full path to the bundled server executable (which lives in a
  version-specific directory), so only version-compatible clients share a daemon.

### Mutexes

Two named mutexes (`.NET` `System.Threading.Mutex`, `Global\` namespace) coordinate startup:

- **Server mutex** `Global\{pipeName}.server` — its existence means a daemon is running. The daemon
  holds an open handle for its lifetime (it does not need to *lock* it; existence is detected via
  `Mutex.TryOpenExisting`).
- **Client mutex** `Global\{pipeName}.client` — briefly held by a connecting client to serialize the
  "check server, launch if absent" sequence so two clients can't race to start two daemons.

### Connection flow (thin client, daemon mode)

1. Compute `pipeName` from the bundled server path.
2. Acquire the **client** mutex (≈20s if a new server may be needed, ≈5s otherwise). If it can't be
   acquired, fall back to non-daemon mode so the user is never blocked.
3. Check whether the **server** mutex exists.
   - Exists → a daemon is running; skip to step 5.
   - Missing → launch `Microsoft.CodeAnalysis.LanguageServer --daemon --daemonPipeName <pipeName>`
     plus any pass-through server arguments. The daemon's stderr is forwarded to the thin client's
     stderr so startup errors are visible.
4. (Daemon acquires the server mutex and starts its pipe listener.)
5. Connect a `NamedPipeClientStream` (`PipeOptions.CurrentUserOnly`) to the daemon.
6. Release the client mutex.
7. Relay raw LSP bytes between the editor transport and the daemon pipe for the session.

## Per-client streams

Each accepted connection gets its own independent `NamedPipeServerStream`. The daemon wraps each
stream in its own `HeaderDelimitedMessageHandler` → `JsonRpc` → `RoslynLanguageServer`, exactly as
the single-client path does. There is no multiplexing at the pipe level. This is orchestrated by
`LanguageServerConnectionManager.RunDaemonAsync`.

## Lifecycle

### Keepalive shutdown

The daemon stays alive while it has connected clients. When the last client disconnects, a keepalive
timer starts; if no new client connects before it elapses, the daemon releases the server mutex,
stops the listener, and exits. Configure it with `--daemonKeepAlive <seconds>` or the
`ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE` environment variable (default 15 minutes; a value `<= 0`
means stay alive indefinitely).

### Per-client teardown and resilience

If a client's connection closes, the daemon tears down only that client's server instance (cancels
in-flight requests, disposes its `LspServices` and its pipe stream) and continues serving other
clients. It relies on pipe disconnect to detect this, so a dead thin-client process is observed via
the broken pipe. The process-global client-PID exit path used by the single-process server
(`RoslynLanguageServer.TryRegisterClientProcessId`, which calls `Environment.Exit`) is **not** used
in daemon mode, so one client exiting can never take down the daemon.

### Thin-client monitoring and exit codes

The thin client monitors both connections and the optional `--clientProcessId`:

- If the **server/daemon** connection dies, the thin client surfaces the error to the editor and
  exits non-zero (it cannot restart — it has no session state).
- If the **editor** connection (or the monitored client process) dies, the thin client closes the
  server connection and exits non-zero.
- A clean child-server exit (code 0) in non-daemon mode is surfaced as exit code 0 so the editor
  does not treat a graceful shutdown as a crash.

## Configuration

There are two distinct concerns:

- **Whether daemon mode is allowed** (a policy/launch decision made by the caller): pass
  `--daemon-mode` to the thin client, or set `ROSLYN_LANGUAGE_SERVER_DAEMON=1`. With neither, the
  thin client runs in non-daemon mode. An editor (e.g. the C# extension) can surface this via a
  user setting.
- **Whether a process is the daemon** (an internal launch argument): `--daemon` /
  `--daemonPipeName`. End users and editors should never pass `--daemon` directly; the thin client
  adds it when launching the daemon.

## Packaging

`roslyn-language-server` is the `PackAsTool` project and tool entry point. It bundles the server's
full published output (apphost, dependencies, `*.deps.json`/`*.runtimeconfig.json`, native SQLite,
`RoslynVersion.txt`, etc.) into its publish directory via the `PublishBundledLanguageServer` target,
so the same package serves both the dotnet-tool and the per-RID publish consumed by editors. The
thin client deliberately has no compile-time dependency on Roslyn/MEF to stay small and AOT-ready.

## Known limitations

- **Concurrent multi-client workspaces** are not fully supported yet. Process-global, `[Shared]`
  workspace event listeners (e.g. `PdbMatchingSourceTextProvider`) currently assume a single Host
  workspace per process and fail when a second is created. The daemon isolates such a failure to the
  affected connection (it does not crash the daemon or other clients), but full concurrency is
  tracked by [dotnet/roslyn#82917](https://github.com/dotnet/roslyn/issues/82917).
- **Native AOT** for the thin client is not yet enabled. The code is kept AOT/trim-clean (analyzers
  on, no reflection, no Roslyn/MEF dependency) so enabling `PublishAot` later is mechanical.
- **Cross-session daemon reuse** depends on the daemon outliving the launching thin client; whether
  it does can be affected by how the host process tree is managed by the editor.
