# Replacing `ILspServiceFactory` with `ExportFactory<T>` + a MEF sharing boundary

> **Status:** implemented (mechanism + bulk migration). See [Implementation status](#implementation-status).
> **Question:** Can we remove the `ILspServiceFactory` concept and replace it with "normal-ish" MEF
> exports, using (1) `ExportFactory<T>` for per-LSP-server instances and (2) a MEF sharing boundary
> (scope) so per-server services share one set of instances?

## Implementation status

The sharing-boundary mechanism is implemented and validated end-to-end in the real Roslyn/VS-MEF
composition (standalone server and VS in-proc clients, both contracts):

- **Scope root.** `LspServices` is now `[Export, Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]`.
  It `[ImportMany]`s both the Roslyn and TypeScript service lists and selects the right one at runtime
  from the server kind seeded by `Initialize(serverKind, baseServices, scopeLifetime)`. A **single**
  boundary is used for all contracts (per-`CreateExport()` isolation, not the boundary name, provides
  per-server isolation), which lets every service import the one `LspServices` type without MEF
  cardinality errors.
- **Scope factory.** `LspServiceProvider` (global `[Shared]`) imports
  `[SharingBoundary(...)] ExportFactory<LspServices>` and opens one scope per server in `CreateServices`,
  seeding context via `Initialize` and disposing the scope on server shutdown (re-entrancy-guarded).
- **Deleted plumbing.** `AbstractLspServiceProvider`, `CSharpVisualBasicLspServiceProvider`, and
  `VSTypeScriptLspServiceProvider` are removed; `RoslynLanguageServer`, `CSharpVisualBasicLanguageServerFactory`,
  and `AbstractInProcLanguageClient` (+ subclasses) take `LspServiceProvider`.
- **Migrated services.** ~40 single-contract factory classes were deleted; their services are exported
  directly via the new `ExportLspServiceAttribute` / `ExportCSharpVisualBasicLspServiceAttribute` plus
  `[Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]`, importing `LspServices` when they need
  per-server siblings. (Roslyn core, MS.CodeAnalysis.LanguageServer host, VisualDiagnostics, DevKit.)
- **`ILspServiceFactory` is now `[Obsolete]`** (along with `ExportLspServiceFactoryAttribute`,
  `ExportCSharpVisualBasicLspServiceFactoryAttribute`, and the external-access factory bases below). It is
  still imported and invoked by `LspServices` so existing external-access partners keep working while they
  migrate; `LspServices` and the test helpers that exercise this path suppress the resulting `CS0618`.
- **External-access non-factory replacements were added** so partners can move off `ILspServiceFactory`:
  - **CompilerDeveloperSdk:** new `ExportCompilerDeveloperSdkLspServiceAttribute`; `CompilerDeveloperSdkLspServices`
    became an importable `[Shared(boundary)]` MEF part. `AbstractCompilerDeveloperSdkLspServiceFactory` /
    `ExportCompilerDeveloperSdkLspServiceFactoryAttribute` are `[Obsolete]`.
  - **XAML:** new `ExportXamlLspServiceAttribute`; a handler exported with it reads resolve-data via
    `XamlRequestContext` (no injected `IResolveCachedDataService` needed). `XamlRequestHandlerFactoryBase<,>` /
    `ExportXamlLspServiceFactoryAttribute` are `[Obsolete]`. The in-repo `OnInitializedServiceFactory` was
    migrated to a direct `OnInitializedService` export.
  - `InternalAPI.Unshipped.txt` updated for both external-access assemblies.
- **Cross-contract services migrated.** The five services shared between the Roslyn and TypeScript
  contracts (`LspWorkspaceManager`, `LspWorkspaceRegistrationService`, `RequestTelemetryLogger`,
  `DocumentPullDiagnosticHandler`, `WorkspacePullDiagnosticHandler`) are now exported per contract via thin
  subclasses (`Roslyn*` in Protocol, `VSTypeScript*` in EditorFeatures) — a single class can't carry two
  `ExportLspServiceAttribute`s (`AllowMultiple = false`). `LspServices.ServerKind` was added so
  `RequestTelemetryLogger` can read the server kind. The three Razor cohosting factories are likewise
  migrated to direct `[ExportCSharpVisualBasicLspService, Shared(boundary)]` exports.

This matches the recommendation's framing ("replace ~45 bespoke factories with one scope mechanism")
and the S4 note that external-access compatibility is a per-consumer decision.

## Summary

The replacement is **feasible** with VS-MEF (`Microsoft.VisualStudio.Composition`, the MEF
implementation the language server already uses). Two MEF v2 features cover the two reasons
`ILspServiceFactory` exists today:

1. **Per-server stateful instances** → a sharing-boundary scope created per LSP server via
   `ExportFactory<T>.CreateExport()`; globally `[Shared]` parts remain shared across servers.
2. **A service obtaining other per-server services / server context on construction** → the service
   imports the **scoped `LspServices` facade** and resolves siblings via `GetRequiredService<T>()`.

These two features are **complementary, not interchangeable**: `ExportFactory<T>` opens a fresh
per-server instance + lifetime; the **sharing boundary** is what makes the per-server parts *shared
within that scope* so the facade resolves one instance per type per server.

The per-server runtime context (the `JsonRpc` connection, logger, `HostServices`, `serverKind`, etc.)
does **not** need to be "seeded into MEF." Those are today's "base services," which are not MEF parts;
they are held by `LspServices` and accessed via its `GetService<T>()` facade. The only bootstrap step
is a one-line `Initialize(...)` on the single scoped `LspServices` instance immediately after
`CreateExport()` — essentially identical to today's `new LspServices(mefParts, baseServices, serverKind)`.

**Outcome:** `ILspServiceFactory`, the ~45 `*Factory` classes, and the
`AbstractLspServiceProvider`/provider plumbing can be deleted, replaced by one sharing boundary, an
`ExportFactory<LspServices>` root, and a one-line `Initialize`. This is worth doing for the
simplification, but should be gated behind a de-risking spike (see [Recommendation](#recommendation)).

## How `ILspServiceFactory` works today

### The contract and its two reasons

`ILspServiceFactory` (`src/LanguageServer/Protocol/LspServices/ILspServiceFactory.cs`) exists for
exactly two reasons, per its own doc comment:

```csharp
// Some LSP services need to know the client capabilities on construction or
// need to know about other ILspService instances to be constructed.
ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind);
```

- **Reason A — statefulness / per-server lifetime.** A factory produces a *new* `ILspService` each
  time an LSP server starts. Contrast with `ExportStatelessLspServiceAttribute`, whose services are
  `[Shared]` across *all* server instances in the same MEF container and disposed only with the
  container.
- **Reason B — composition-time access to siblings / server context.** The factory receives the
  per-server `LspServices` and the `serverKind`, so the created service can resolve other per-server
  services or branch on server kind. For example, `SemanticTokensRefreshQueueFactory` resolves
  `IClientLanguageServerManager`, `LspWorkspaceManager`, and `LspWorkspaceRegistrationService` from
  `LspServices` before constructing the queue; `RequestTelemetryLoggerFactory` branches on
  `serverKind` to build a telemetry string.

### Export attributes and metadata

`AbstractExportLspServiceAttribute` is an `ExportAttribute` carrying metadata: `TypeName`,
`InterfaceNames`, `ServerKind`, `IsStateless`, and pre-computed `IMethodHandler` details. Two concrete
attributes derive from it: `ExportStatelessLspServiceAttribute` (contract type `ILspService`,
`isStateless: true`) and `ExportLspServiceFactoryAttribute` (contract type `ILspServiceFactory`,
`isStateless: false`). Both are keyed on a **contract name** (e.g.
`ProtocolConstants.RoslynLspLanguagesContract`).

### Per-server assembly and selection (`LspServices`)

`LspServices` (`src/LanguageServer/Protocol/LspServices/LspServices.cs`) is constructed **once per
server** — the framework holds it as a `Lazy<ILspServices>` per `AbstractLanguageServer`. Its
constructor:

1. Invokes matching factories: `() => lazyServiceFactory.Value.CreateILspService(this, serverKind)`.
2. Adds plain stateless services.
3. Applies a **"specific server kind overrides `Any`"** rule, **keyed by exported `TypeName`**. For
   example, `VSCodeRequestTelemetryLogger` exports under `typeof(RequestTelemetryLogger)` with
   `ServerKind = CSharpVisualBasicLspServer`, overriding the `Any` factory export of the same type
   name.

Other important `LspServices` behaviors to preserve:

- **Service-locator facade.** The CLaSP framework and many services call `GetRequiredService<T>()` /
  `GetRequiredServices<T>()`; `LspServices` itself is offered as a service.
- **Method-handler discovery without instantiation.** `GetMethodHandlers()` reads handler metadata
  from the `Lazy<…, LspServiceMetadataView>` **without forcing `.Value`** — handlers are only
  instantiated when a request arrives. This is a load-bearing performance property.
- **Disposal.** `LspServices` tracks the stateful `IDisposable` services it creates and disposes them
  on server shutdown; stateless/global services are left to the container.

### Runtime-seeded "base services"

`RoslynLanguageServer.GetBaseServices()` builds a map of services that **cannot come from the static
catalog** because they depend on per-server runtime values: `IClientLanguageServerManager` (wraps the
`JsonRpc`), `ILspLogger`/`AbstractLspLogger`, `ServerInfoProvider(serverKind, supportedLanguages)`,
`HostServices`, the `RequestContextFactory`, `TelemetryService`, `HandlerProvider`, the
`InitializeManager`, the server itself as `IOnInitialized`, etc. These are **not MEF parts**; they are
passed into `CreateServices(serverKind, baseServices)` and served by `LspServices` ahead of the MEF
parts.

### Consumers and surface area

- **Providers (one `ImportMany` per contract):** `CSharpVisualBasicLspServiceProvider` and
  `VSTypeScriptLspServiceProvider`, both deriving `AbstractLspServiceProvider`. Contracts:
  `RoslynLspLanguagesContract`, `TypeScriptLanguageContract`.
- **Server kinds:** `WellKnownLspServerKinds` has four real kinds (LiveShare, AlwaysActiveVS,
  CSharpVisualBasic, RoslynTypeScript) plus `Any`; `GetContractName()` maps kinds → contracts.
- **`ILspServiceFactory` implementations:** ~45 across the language-server core, plus external-access
  wrappers: CompilerDeveloperSdk (`AbstractCompilerDeveloperSdkLspServiceFactory`), XAML, Razor
  cohosting (`RazorStartupServiceFactory`, etc.), and DevKit. Several wrap `LspServices` in their own
  context type (e.g. `CompilerDeveloperSdkLspServices`).
- **Hosts:** the standalone VS Code server (`LanguageServerHost` → `ILanguageServerFactory.Create`;
  one server per process today) and **Visual Studio** (in-proc language clients create
  LiveShare/AlwaysActive/TypeScript servers — *multiple servers share one VS-MEF container*, which is
  precisely why per-server instances are needed).
- Composition is VS-MEF with a cached `RuntimeComposition` (`LanguageServerExportProviderBuilder`).

## MEF concepts

### `ExportFactory<T>`

A factory that produces a **new** instance of `T` (and its non-shared dependency graph) per call,
versus `Lazy<T>` which always returns the same value. In MEF v2 / VS-MEF (`System.Composition`),
`CreateExport()` returns an `Export<T>` whose `.Value` is the new instance and whose `Dispose()` tears
down that instance and everything created for it. (The MEF v1 variant lives in
`System.ComponentModel.Composition`; the equivalent v2 type in `System.Composition` is what the
language server uses — see [VS-MEF support](#vs-mef-support-and-allowed-types).)

### Sharing boundaries / scopes (and how CPS uses them)

A **scope** is a set of MEF parts tied to a context's lifetime; scopes nest, and child scopes inherit
(import) from parents but not vice-versa. CPS (the VS project system) defines nested scopes
(`ProjectService` → `UnconfiguredProject` → `ConfiguredProject`); a part automatically "belongs to"
the **innermost scope it imports from**, and CPS "lifts" data across scopes via wrappers such as
`ActiveConfiguredProject<T>`.

In MEF v2 / VS-MEF, scopes are expressed with **sharing boundaries**:

- A part marked `[Export, Shared("BoundaryName")]` has at most one instance **per scope** of that
  boundary.
- A parent part imports `[SharingBoundary("BoundaryName")] ExportFactory<TRoot>`; each
  `CreateExport()` opens a **new** boundary scope. Parts `[Shared("BoundaryName")]` get a fresh
  instance per scope; parts that are globally `[Shared]` (no boundary, in the parent) are shared
  across all scopes. Disposing the returned lifetime context disposes the whole scope.

### VS-MEF support and allowed types

VS-MEF implements MEF v2 attributed parts including `ExportFactory<T>` and sharing boundaries. The
repo's `BannedSymbols.txt` files ban the **MEF v1**
`System.ComponentModel.Composition.SharingBoundaryAttribute` / `SharedAttribute` and direct callers to
"Use types from `System.Composition` instead." The **MEF v2** `System.Composition.SharingBoundaryAttribute`
and `ExportFactory<T>` are **not** banned — they are the recommended set and match the
`[Export]`/`[Shared]`/`[ImportingConstructor]` attributes the language server already uses.

## Proposed design

Introduce one sharing boundary per LSP contract (e.g. `"RoslynLspServer"`, `"TypeScriptLspServer"`).
Services depend on the scoped `LspServices` facade — this is the single model used throughout this
design.

1. **Scope root = the reworked `LspServices`.** A `[Shared("RoslynLspServer")]` part (today's
   `LspServices`, lightly reworked) that `[ImportMany]`s
   `IEnumerable<Lazy<ILspService, LspServiceMetadataView>>` (the per-server services) and holds the
   per-server context. It keeps today's `GetRequiredService<T>` facade and the `Any`/specific
   selection.
2. **Scope factory (global).** A global `[Shared]` part imports
   `[SharingBoundary("RoslynLspServer")] ExportFactory<LspServices>`. Per server it calls
   `CreateExport()` **once**, calls `Initialize(...)` on the scoped root, and holds the returned
   lifetime context for that server's lifetime; disposing it on server shutdown disposes all
   per-server parts (replacing today's manual disposal tracking).
3. **Services** become normal `[Export(typeof(ILspService), contract), Shared("RoslynLspServer")]`
   parts (no factory class). A service that needs siblings or server context imports the scoped
   `LspServices` and resolves them via `GetRequiredService<T>()` / reads `ServerKind`:

   ```csharp
   [Export(typeof(ILspService), RoslynLspLanguagesContract), Shared("RoslynLspServer")]
   internal sealed class SemanticTokensRefreshQueue : ILspService
   {
       [ImportingConstructor]
       public SemanticTokensRefreshQueue(LspServices services)
       {
           _workspaceManager = services.GetRequiredService<LspWorkspaceManager>();
           _kind = services.ServerKind; // seeded per-server context
       }
   }
   ```

4. **Truly shared services** stay globally `[Shared]` (today's "stateless" services) in the parent
   scope, shared across all servers.

This maps the two reasons `ILspServiceFactory` exists onto one mechanism: `ExportFactory` + boundary
provides "new per-server instances tied to the server lifetime" (Reason A); the scoped `LspServices`
facade provides "access this server's siblings/context" (Reason B), while keeping the override logic
exactly where it lives today.

### Why services depend on the `LspServices` facade rather than importing siblings directly

A generic `LspServiceScope<T>` wrapper is **not** needed — that shape is CPS's
`ActiveConfiguredProject<T>`, which exists to *lift across nested scopes*. The LSP services live in a
single, flat per-server scope, so a sibling is in the same scope.

Routing all sibling access through the scoped `LspServices` is the key design choice because it
**preserves the `Any`-vs-specific override**. The override is keyed by exported `TypeName` (e.g.
`VSCodeRequestTelemetryLogger` overriding the `Any` `RequestTelemetryLogger`). If a service imported
such a type *directly* with `[ImportingConstructor]`, MEF would see two exports of the same contract
and throw a cardinality error — it cannot express "specific kind overrides `Any`." Because the only
scoped import is `LspServices` (exactly one per scope, unambiguous), MEF never has to disambiguate at
an import site, and the `TypeName`-keyed selection stays **inside** `LspServices`, driven by the
seeded `serverKind` — identical to today's logic.

There is no construction cycle: `LspServices` imports `[ImportMany] Lazy<ILspService, Metadata>`
(deferred) and each service imports the already-built `LspServices` (eager). The `Lazy` breaks the
cycle, exactly as the factory closure does today, and handler metadata stays enumerable without
instantiation.

This is *service location* rather than constructor injection — slightly less "pure DI" — but it is the
lowest-risk path: service bodies already call `lspServices.GetRequiredService<T>()` today, and it
preserves the override semantics wholesale.

## A sharing boundary is required (not just `ExportFactory<T>`)

`ExportFactory<T>` governs only **instantiation and lifetime**: each `CreateExport()` builds a new `T`
plus its *non-shared* dependency subgraph, and disposing the returned context tears that subgraph down.
It says **nothing** about the *sharing topology* — whether two parts that both import `B` get the same
`B`. That is decided by how `B` is marked with `[Shared]`.

Consider per-server services `A`, `B`, `C`, where `A` imports `B` and `C` imports `B`. We want `A` and
`C` (in the same server) to see the **same** per-server `B`, and a different server to get a different
`B`:

| How `B` is exported | What `A` and `C` get | Per-server? | Correct for LSP? |
|---------------------|----------------------|-------------|------------------|
| **Non-shared** (default in MEF v2) | A **brand-new** `B` at *each* import site — `A` and `C` get *different* `B`s, even inside one `CreateExport()` graph | n/a | No |
| **Globally `[Shared]`** | The **one** process/container-wide `B`, shared across **all** servers | No (cross-server) | No |
| **`[Shared("RoslynLspServer")]`** (named sharing boundary) | The **single per-scope** `B`; all importers in a server's scope resolve the *same* `B`; different servers get different `B` | **Yes** | Yes |

Only a named sharing boundary expresses "one instance per server, shared among that server's
services." Plain `ExportFactory<T>` without a boundary yields only *new-every-time* (non-shared) or
*one-forever* (global `[Shared]`) — neither is the per-server-shared semantics the LSP model depends on
(`LspServices.GetRequiredService<T>()` returns one instance per type per server, and handlers such as
`SemanticTokensRefreshQueue` need *the* server's `LspWorkspaceManager` /
`IClientLanguageServerManager`, not a private copy).

The two features therefore work together: `ExportFactory<T>` decorated with
`[SharingBoundary("RoslynLspServer")]` opens a fresh per-server scope and owns its disposal;
`[Shared("RoslynLspServer")]` on each per-server part makes those parts shared within the scope so the
facade resolves that server's instance. (A service that genuinely needs only a *private* helper it
never shares can use a non-shared import with no boundary, but the core LSP services are per-server
singletons shared by many handlers.)

In MEF v2 / VS-MEF a "scope" is not a separate subsystem — it is exactly the lifetime context of a
sharing boundary, created by `CreateExport()` and ended by disposing it. CPS's named scopes are the
same mechanism (nested boundaries) plus custom seeding layered on top.

## Isolation across concurrent servers in one container

A static (named) boundary is sufficient, and each concurrent server gets a fully isolated instance set.
The boundary **name** is a static *label* (declared in attributes) identifying *which parts are
scoped*; it is **not** the *identity* of a scope. The scope itself is a **runtime object created per
`CreateExport()` call** (think "type vs. instance": the name is like a type; each `CreateExport()`
produces a new instance of that scope).

VS runs several servers at once — e.g. `LiveShareLspServer`, `AlwaysActiveVSLspServer`, and
`RoslynTypeScriptLspServer` — in one VS-MEF container:

- Each server's bootstrap calls `CreateExport()` **once** → **one distinct scope per server**.
- Each scope has its own set of `[Shared("RoslynLspServer")]` singletons. Server A's
  `LspWorkspaceManager` is a *different* object from Server B's, even though both are
  `[Shared("RoslynLspServer")]` with the *same* static name.
- A service in Server A's scope resolves **A's** siblings; it can never see B's. No cross-talk.
- Globally `[Shared]` (no-boundary) parts remain a single instance shared by *all* servers — by
  design (today's "stateless" / cross-server services).

This holds even for two servers that share the **same contract**: `LiveShareLspServer` and
`AlwaysActiveVSLspServer` both use `RoslynLspLanguagesContract`, yet each still calls `CreateExport()`
separately, so each gets its own scope. The seeded `serverKind` then drives the per-scope
`Any`-vs-specific selection independently within each scope.

Requirements to actually get this isolation (easy to get wrong):

- The part importing `[SharingBoundary("RoslynLspServer")] ExportFactory<LspServices>` must be
  **globally `[Shared]`** (one factory), and must call `CreateExport()` **once per server**, holding
  the returned lifetime context for that server's lifetime and disposing it on that server's shutdown —
  i.e. exactly one scope per `AbstractLanguageServer`, matching today's one-`LspServices`-per-server.
- Do **not** create the scope once and reuse it across servers — that would collapse them into shared
  instances.
- The scope root's `[ImportMany(<contract>)]` is contract-filtered, so a server only pulls its own
  contract's parts. Distinct boundary names per contract (e.g. `"RoslynLspServer"`,
  `"TypeScriptLspServer"`) are recommended for clarity, but isolation does **not** depend on the name
  being unique — it depends on the per-`CreateExport()` scope.

## Seeding per-server runtime context

`CreateExport()` accepts no arguments, so the per-server runtime values (`JsonRpc`-wrapping
`IClientLanguageServerManager`, logger, `HostServices`, `serverKind`, …) cannot be passed as
constructor arguments. This is **not** a hard problem, because those values are today's "base
services," which are **not MEF parts** — they are held by `LspServices` and served via its
`GetService<T>()` facade ahead of the MEF parts. No scoped MEF part imports `JsonRpc`/etc. directly;
they all go through the facade.

All that remains is to get the runtime context + base-service map onto the **single scoped
`LspServices` instance**. That is essentially identical to today's
`new LspServices(mefParts, baseServices, serverKind)`; the only difference is *who* constructs
`LspServices`:

- Today: the provider constructs it directly (`new LspServices(...)`).
- Proposed: MEF constructs it as the `[Shared("RoslynLspServer")]` **scope root** (so it can
  `[ImportMany]` the **scoped** `Lazy<ILspService, Metadata>` parts). Because `CreateExport()` cannot
  take constructor args, the bootstrap pushes the context in immediately after:

  ```csharp
  using var export = scopeFactory.CreateExport();        // opens the per-server scope
  var lspServices = export.Value;                          // the scoped LspServices root
  lspServices.Initialize(jsonRpc, logger, hostServices, serverKind, baseServices);
  // ... hold `export` for the server's lifetime; dispose on shutdown to tear down the scope.
  ```

This is safe because MEF is **lazy**: importing `LspServices` does not construct any scoped service,
and no scoped service is built until a request calls `GetRequiredService<T>()` — by which point
`Initialize` has already run.

## Feasibility per concern

| # | Concern | Verdict | Notes |
|---|---------|---------|-------|
| 1 | Per-server instances + cross-server shared parts (Reason A) | Direct fit | `ExportFactory` opens the per-server scope; the sharing boundary provides per-server sharing. Global `[Shared]` parts stay shared across servers. |
| 2 | `Any`-vs-specific override, keyed by `TypeName` | Solved | Routing all sibling access through the scoped `LspServices` keeps the `TypeName`-keyed selection in one place and avoids MEF cardinality errors. |
| 3 | Service-to-service imports (Reason B) | Solved | Services resolve siblings via the scoped `LspServices` facade; `[Shared("RoslynLspServer")]` guarantees one per-server instance. |
| 4 | Seeding runtime context (`JsonRpc`, logger, `HostServices`, kind) | Minor bootstrap | Base services are not MEF parts; just `Initialize(...)` the scoped root after `CreateExport()` — same shape as today's `new LspServices(...)`. |
| 5 | Base services | Stay on the facade | Remain non-MEF, held by the scoped `LspServices` and served via `GetService<T>()` exactly as today (`HandlerProvider`, `RequestContextFactory`, `InitializeManager`, server-as-`IOnInitialized`, etc.). |
| 6 | Method-handler metadata enumeration without instantiation | Preserved | `[ImportMany] Lazy<ILspService, Metadata>` keeps `.Metadata` readable without forcing `.Value`; creating the scope does not eagerly build services. |
| 7 | Disposal | Cleaner | Dispose the scope's lifetime context → disposes all per-server parts; removes manual `_servicesToDispose` tracking. |
| 8 | Multiple contracts/providers (Roslyn, TypeScript) | Multiplies wiring | One boundary + scope root + factory per contract. |
| 9 | External-access wrappers (CompilerDeveloperSdk, XAML, Razor cohosting, DevKit) | Breaking change | They implement `ILspServiceFactory` and wrap `LspServices`. Removing the interface needs a migration/compat story per consumer. |
| 10 | VS host composition | Highest risk | LSP parts live in the large VS-MEF container, not just the standalone server. Must verify sharing boundaries compose there, that multiple in-proc servers each get an isolated scope, and that the cached `RuntimeComposition` round-trips boundaries. |
| 11 | Tests | Update | `LspServicesTests` and `TestLspServices` directly construct the factory + `Any`/specific paths; they would be rewritten against scopes. |

## Recommendation

1. **Feasible — proceed only after a de-risking spike.** The mechanism is sound and would delete a
   large amount of boilerplate (~45 factories plus the `ILspServiceFactory` / `AbstractLspServiceProvider`
   plumbing).
2. **The residual "factory-like" code is small.** It is the scope root (today's `LspServices`, reworked
   as a `[Shared("…")]` part) plus a one-line `Initialize` after `CreateExport()`. Base services stay on
   the facade (non-MEF), so there is no runtime-context seeding into MEF. Frame the goal as "replace ~45
   bespoke factories with one scope mechanism."
3. **Keep `LspServices` as the runtime selector** for the `Any`/specific override and as the
   service-locator facade the CLaSP framework expects.

### Suggested spike

Smallest end-to-end proof, in priority order:

- **S1.** Stand up a `"RoslynLspServer"` sharing boundary with the reworked `LspServices` as the MEF
  scope root, `Initialize`d with the base-service map + runtime context after `CreateExport()`, and
  migrate **one** simple stateful service (e.g. `RequestTelemetryLogger`, which exercises both
  `serverKind` and the `Any`/specific override). Prove standalone server boot + a request, and confirm
  base services are still served off the facade.
- **S2.** Validate the **VS host** (in-proc language client) creates **isolated** scopes for two
  concurrent servers — especially **same-contract** `LiveShareLspServer` + `AlwaysActiveVSLspServer`,
  and also `RoslynTypeScriptLspServer` — in one container; that each resolves only its own per-server
  instances; that disposing one server's scope does not affect another; and that the cached
  `RuntimeComposition` preserves boundaries.
- **S3.** Migrate one **Reason-B** service (e.g. `SemanticTokensRefreshQueue`) to resolve its siblings
  via the scoped `LspServices`; confirm method-handler metadata is still enumerated lazily.
- **S4.** Decide the external-access compatibility story (CompilerDeveloperSdk / XAML / Razor / DevKit)
  before any broad rollout.

## Open questions / risks to resolve in the spike

- Confirm the post-`CreateExport()` `Initialize` reliably runs before any scoped service is constructed
  (expected, due to MEF laziness) and that `LspServices` being the MEF-created scope root composes
  cleanly.
- Does the cached `RuntimeComposition` (standalone server) faithfully round-trip sharing boundaries and
  `ExportFactory` imports? (Cache invalidation / catalog-shape change.)
- How do the non-Roslyn contracts (TypeScript) and external-access wrappers want to consume this — keep
  a thin `ILspServiceFactory`-compatible shim, or migrate fully?
- Performance: does opening a boundary scope per server add measurable startup cost versus today's
  direct factory invocation? (Likely negligible, but measure in the spike.)
- Confirm that all existing `LspServices`/`ILspServices` consumers resolve the **scoped** instance (not
  a stray global one), and that the framework's own `GetLspServices()` returns the per-server scoped
  root.

## Key files

- `src/LanguageServer/Protocol/LspServices/ILspServiceFactory.cs`
- `src/LanguageServer/Protocol/LspServices/LspServices.cs`
- `src/LanguageServer/Protocol/LspServices/AbstractLspServiceProvider.cs`,
  `RoslynLspServiceProvider.cs`
- `src/LanguageServer/Protocol/LspServices/AbstractExportLspServiceAttribute.cs`,
  `ExportStatelessLspServiceAttribute.cs`, `ExportLspServiceFactoryAttribute.cs`,
  `LspServiceMetadataView.cs`, `BaseService.cs`
- `src/LanguageServer/Protocol/RoslynLanguageServer.cs`, `WellKnownLspServerKinds.cs`,
  `ProtocolConstants.cs`, `CSharpVisualBasicLanguageServerFactory.cs`
- `src/LanguageServer/Microsoft.CommonLanguageServerProtocol.Framework/AbstractLanguageServer.cs`,
  `ILspServices.cs`
- `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/LanguageServerExportProviderBuilder.cs`,
  `LanguageServer/LanguageServerHost.cs`, `Telemetry/VSCodeRequestTelemetryLogger.cs`
- Example factories: `src/LanguageServer/Protocol/Handler/Telemetry/RequestTelemetryLoggerFactory.cs`,
  `src/LanguageServer/Protocol/Handler/SemanticTokens/SemanticTokensRefreshQueueFactory.cs`
- External access:
  `src/LanguageServer/ExternalAccess/CompilerDeveloperSDK/LspServices/AbstractCompilerDeveloperSdkLspServiceFactory.cs`
- `src/EditorFeatures/Core/ExternalAccess/VSTypeScript/VSTypeScriptLspServiceProvider.cs`,
  `src/EditorFeatures/Core/LanguageServer/AbstractInProcLanguageClient.cs`
- `src/LanguageServer/BannedSymbols.txt`, `src/Features/BannedSymbols.txt`,
  `src/CodeStyle/BannedSymbols.txt`
