# Plan: metric handles, and converting the session-wide aggregators onto `IMetricSink`

> **Background.** A prior evaluation asked whether these aggregators could move onto `RoslynTelemetry`
> *without* changing the emitted telemetry. The answer was no, for four independent reasons: the two paths
> use different VS Telemetry transports (`PostEvent` vs `PostMetricEvent`) and therefore different backend
> schemas; the metric path has no place to put a named measurement value (only tags become properties);
> client-side derived statistics are not reproducible from an instrument; and flush-and-remove turns one
> session total into several window deltas.
>
> This plan proceeds under the opposite premise: **a changed shape is acceptable**, provided it is stated
> precisely up front and signed off by the consumers. §3 is that statement.

- **Scope:** Add a declare-once metric handle API (`DefineCounter` / `DefineDistribution`, with
  `RecordBlockTime` on the handle), teach `IMetricSink` about per-metric distribution shape, then convert
  the five bespoke session-wide aggregators to record through it. Delete the shutdown-drain scaffolding
  they required.
- **Non-goals:** The `VSMetricSink` flush/record race (tracked separately). The event (`IEventSink`) path.
  Consolidating separate metric names into tag dimensions — noted as optional follow-up, not done here.
- **Affected areas:** `src/Workspaces/SharedUtilitiesAndExtensions/Compiler/Core/Log`,
  `src/VisualStudio/Core/Def/Telemetry`, `src/VisualStudio/Core/Def/{RoslynPackage.cs,InheritanceMargin}`,
  `src/Features/Core/Portable/{Completion,ChangeSignature,QuickInfo,Common}`,
  `src/EditorFeatures/Core/IntelliSense/AsyncCompletion`,
  `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Telemetry`.
- **This deliberately changes the emitted telemetry shape.** That is the accepted premise. §3 states exactly
  what changes so dashboard owners can sign off before any code lands.

---

## 1. The handle API

`RoslynTelemetry.Metrics.cs` gains three types and two factory methods. Handles are **descriptors, not
instruments** — this is the load-bearing constraint. Instruments are per-sink and per-session, sinks come
and go via `AddMetricSink` (`RoslynTelemetry.Metrics.cs:25-29`), and `Flush()` drops each aggregation as it
posts (`VSMetricSink.cs:164`). A static handle that cached a live instrument would outlive its session.

```csharp
internal readonly struct DistributionShape
{
    public static DistributionShape Default { get; }                    // SDK buckets, min/max on
    public static DistributionShape Linear(int bucketSize, int maxBucketValue,
                                           bool recordMinMax = true, bool recordMedian = false);
    public static DistributionShape Explicit(ImmutableArray<double> bounds, ...);

    public ImmutableArray<double> Bounds { get; }
    public bool RecordMinMax { get; }
    public bool RecordMedian { get; }
}

internal readonly struct CounterMetric        // holds (FunctionId, metricName)
{
    public void Add(long delta = 1);
    public void Add(long delta, KeyValuePair<string, object?> tag);      // + 2- and 3-tag overloads
}

internal readonly struct DistributionMetric   // holds (FunctionId, metricName, DistributionShape)
{
    public void Record(long value);
    public void Record(long value, KeyValuePair<string, object?> tag);   // + 2- and 3-tag overloads
    public IDisposable? RecordBlockTime();
}

internal static partial class RoslynTelemetry
{
    public static CounterMetric      DefineCounter(FunctionId functionId, string metricName);
    public static DistributionMetric DefineDistribution(FunctionId functionId, string metricName,
                                                        DistributionShape shape = default);
}
```

Declaration site carries the shape exactly once, adjacent to the metric:

```csharp
private static readonly DistributionMetric s_sourceInitializationTicks =
    RoslynTelemetry.DefineDistribution(
        FunctionId.Intellisense_AsyncCompletion_Data, "SourceInitializationTicks",
        DistributionShape.Linear(bucketSize: 25, maxBucketValue: 500));

// call site
s_sourceInitializationTicks.Record((long)elapsed.TotalMilliseconds);
```

**`RecordBlockTime` moves onto the handle.** Today it takes no tags, so call sites hand-build compound
metric names — a gap documented deliberately at `RoslynTelemetry.Metrics.cs:120-131`. On a handle it gets
the same tag and shape story as `Record`, closing that gap.

**Sink interface change.** Only `Record` grows a parameter; counters have no shape:

```csharp
void Record(string eventName, string metricName, long value,
            in DistributionShape shape, ReadOnlySpan<KeyValuePair<string, object?>> tags);
```

One implementation exists (`VSMetricSink.cs:25`), so the blast radius is one class.

**`VSMetricSink` change** is confined to `CreateAggregation` (`:187-200`):
`meter.CreateHistogram<long>(name)` → `meter.CreateVSHistogram<long>(name, ToHistogramConfiguration(shape))`.
Verified against the SDK: `IVSHistogram<T> : IInstrument, IHistogram<T>`, so it flows unchanged into the
existing `switch` at `:156` and into `TelemetryHistogramEvent<long>(TelemetryEvent, IHistogram<long>)`.
`AggregationKey` is unchanged — shape is constant per `(eventName, metricName)` by construction. Add a
`Debug.Assert` for a conflicting redeclaration (first-wins at runtime).

Validate shapes eagerly inside `DefineDistribution`. The SDK throws `InvalidBucketConfigurationException`
from `CreateHistogram`; since declarations are static field initializers, an unvalidated bad shape would
surface as a `TypeInitializationException` far from its cause.

---

## 2. Empirically established SDK behavior (basis for §3)

Verified by reflection and live invocation against `Microsoft.VisualStudio.Telemetry` 18.3.124
(`eng/Packages.props:137`):

```
IMeter.CreateHistogram<T>(string, HistogramConfiguration, string, string)
IMeter.CreateVSHistogram<T>(string, HistogramConfiguration, string, string)
HistogramConfiguration(double[] explicitBucketBoundaries, bool recordMinMax, bool recordMedian)
IVSHistogram<T>.Statistics -> Min, Max, Average, Median, Counter, FirstRecorded, LastRecorded
IVSHistogram<T>.Buckets    -> HistogramBuckets<T>.OrderedBuckets : HistogramBucket<T>[]
```

Live results:

```
window1 (5,15,35)            Min=5    Max=35   Avg=18.33   Median=15
window2 fresh (no records)   Min=     Max=     Avg=        Median=
window2 (100)                Min=100  Max=100  Avg=100     Median=100
window1 re-read              Min=5    Max=35   Avg=18.33   Median=15
```

- `CreateHistogram` returns a **new instrument per call** (`ReferenceEquals == False`); no name caching.
- Fresh instruments start empty; instruments are fully isolated.
- ⇒ flush-and-remove yields clean **delta temporality**. Disjoint windows, no double counting, no loss.

Re-aggregation across windows: buckets ✅ sum, count ✅ sum, min ✅ min-of-mins, max ✅ max-of-maxes,
**average ⚠️ must be count-weighted**, **median ❌ not recombinable** (derive approximately from summed
buckets instead). Median is not emitted today either, so nothing regresses.

---

## 3. Exactly what the telemetry looks like, before and after

### 3.1 The structural change, in one line

| | Before | After |
|---|---|---|
| Transport | `TelemetrySession.PostEvent` (`TelemetryLogger.cs:38`) | `TelemetrySession.PostMetricEvent` (`VSMetricSink.cs:44`) |
| Payload | `TelemetryEvent` + property bag | `TelemetryCounterEvent<long>` / `TelemetryHistogramEvent<long>` |
| Granularity | **one** event carrying every metric | **one event per instrument** |
| Cadence | once, at shutdown | every 30 min (`VSMetricSink.cs:97`) + at shutdown |
| Where the value lives | a named property | the instrument payload |
| Event properties | all the data | **tags only** (`VSMetricSink.cs:191-192`); none of these five pass tags |
| Name casing | property names lowercased (`TelemetryNaming.cs:27`) | instrument names **preserved as written**; only tag names lowercased |

The event *name* is unchanged in both worlds — `TelemetryNaming.GetEventName(functionId)` feeds both paths
(`TelemetryLogger.cs:87`, `RoslynTelemetry.Metrics.cs:75`). New in the metric world is a **meter** name,
`eventName.Replace('/','.') + ".meter"` (`VSMetricSink.cs:213-214`).

### 3.2 Worked example — `AsyncCompletionLogger` (has all three aggregator kinds)

**BEFORE** — a single `PostEvent` at shutdown, event `vs/ide/vbcs/intellisense/asynccompletion/data`,
carrying **51 properties** (3 counters + 6 statistic keys × 5 + 6 histogram keys × 3). Abridged:

```
vs.ide.vbcs.intellisense.asynccompletion.data.sessionwithtypeimportcompletionenabled             = 12       int
vs.ide.vbcs.intellisense.asynccompletion.data.sessionwithdelayedimportcompletionincludedinupdate = 3        int
vs.ide.vbcs.intellisense.asynccompletion.data.expanderusagecount                                 = 1        int
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.maximum                  = 480      int
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.minimum                  = 2        int
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.mean                     = 37.4     double
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.range                    = 478      int
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.count                    = 152      int
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.bucketsize               = 25       int
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.maxbucketvalue           = 500      int
vs.ide.vbcs.intellisense.asynccompletion.data.sourceinitializationticks.buckets                  = "[3,0,…]" string
… same 8 rows for sourcegetcontextcompletedticks, sourcegetcontextcanceledticks,
  itemmanagersortticks, itemmanagerupdatecompletedticks, itemmanagerupdatecanceledticks …
```

**AFTER** — **9 metric events per flush window** (3 counters + 6 histograms), all on meter
`vs.ide.vbcs.intellisense.asynccompletion.data.meter`, each with `TelemetryEvent.Name` =
`vs/ide/vbcs/intellisense/asynccompletion/data` and **no properties** (no tags are passed):

```
TelemetryCounterEvent<long>   instrument "SessionWithTypeImportCompletionEnabled"              value 12
TelemetryCounterEvent<long>   instrument "SessionWithDelayedImportCompletionIncludedInUpdate"  value 3
TelemetryCounterEvent<long>   instrument "ExpanderUsageCount"                                  value 1
TelemetryHistogramEvent<long> instrument "SourceInitializationTicks"
        buckets [25,50,…,500]  count 152  min 2  max 480  average 37.4  (median if enabled)
TelemetryHistogramEvent<long> instrument "SourceGetContextCompletedTicks"   …
TelemetryHistogramEvent<long> instrument "SourceGetContextCanceledTicks"    …
TelemetryHistogramEvent<long> instrument "ItemManagerSortTicks"             …
TelemetryHistogramEvent<long> instrument "ItemManagerUpdateCompletedTicks"  …
TelemetryHistogramEvent<long> instrument "ItemManagerUpdateCanceledTicks"   …
```

Field-level consequences:
- `.maximum` / `.minimum` / `.mean` / `.count` → carried natively as histogram Max / Min / Average / Count.
- **`.range` is no longer emitted** — derive `Max - Min` at query time. Only genuinely dropped field.
- `.bucketsize` + `.maxbucketvalue` + `.buckets`-as-string → real bucket boundaries + bucket counts.
  Roslyn's `HistogramLogAggregator(25, 500)` becomes `Linear(25, 500)` ⇒ bounds `[25,50,…,500]`, and the
  clamp-to-last-bucket overflow at `HistogramLogAggregator.cs:99-102` becomes the implicit top bucket.
- The `StatisticLogAggregator` and `HistogramLogAggregator` for the same key **merge into one instrument**
  — today they redundantly describe the same measurements twice.
- **`Median` becomes newly available** (per-window) where `recordMedian: true`. Not derivable today.

### 3.3 `OnTheFlyDocsLogger` — pure counters, the simplest diff

BEFORE: one event `vs/ide/vbcs/copilot/on/the/fly/docs/get/counts` with six `int` properties, e.g.
`vs.ide.vbcs.copilot.on.the.fly.docs.get.counts.hoveredsourcesymbol`.
AFTER: six `TelemetryCounterEvent<long>` on meter `vs.ide.vbcs.copilot.on.the.fly.docs.get.counts.meter`,
instruments `HoveredSourceSymbol`, `HoveredMetadataSymbol`, `ShowedOnTheFlyDocsLink`,
`ShowedOnTheFlyDocsLinkWithDocComments`, `OnTheFlyDocsResultsRequested`,
`OnTheFlyDocsResultsRequestedWithDocComments`. Session total = `sum()` across windows.

### 3.4 `InheritanceMarginLogger` — the double-dot disappears

BEFORE (note the **double dot**, from `InheritanceMarginLogger.cs:37` passing a prefix that already ends in
`.` while `HistogramLogAggregator.cs:114` appends another):

```
vs.ide.vbcs.inheritancemargin.getinheritancememberitems.getinheritancemarginmembers..bucketsize     = 1000
vs.ide.vbcs.inheritancemargin.getinheritancememberitems.getinheritancemarginmembers..maxbucketvalue = 60000
vs.ide.vbcs.inheritancemargin.getinheritancememberitems.getinheritancemarginmembers..buckets        = "[…]"
```

AFTER: one `TelemetryHistogramEvent<long>`, meter
`vs.ide.vbcs.inheritancemargin.getinheritancememberitems.meter`, instrument
`GetInheritanceMarginMembers`, bounds `[1000,2000,…,60000]`.

The malformed name is resolved as a **side effect of the shape change**, not smuggled in as a "fix" —
which is the right way for it to go, since any query keyed on the double-dot string must be rewritten
anyway.

### 3.5 Remaining two

`CompletionProvidersLogger` (`vs/ide/vbcs/intellisense/completionproviders/data`) → 5 counters +
3 histograms (`TypeImportCompletionTicks` merging its statistic+histogram pair, plus
`TypeImportCompletionItemCount`, `TypeImportCompletionReferenceCount`).
`ChangeSignatureLogger` (`vs/ide/vbcs/changesignature/data`) → 21 counters + 1 histogram
(`CommittedSessionCommitElapsedMS`, again merging its statistic+histogram pair).

### 3.6 Two behavioral wins that come for free

1. **The silent-drop bug is fixed.** Today `ReportSessionWideTelemetry` runs from `RoslynPackage.Dispose`
   through *event* sinks that `AbstractWorkspaceTelemetryService.Dispose` unregisters at catalog teardown
   (`:69-79`); if the catalog wins that race, `TryGetEnabledSinks` returns false
   (`RoslynTelemetry.cs:50-61`) and the whole session's data vanishes with no signal. Metrics flush from
   `AbstractWorkspaceTelemetryService.Dispose:73`, *before* teardown — and the 30-minute loop means even a
   lost final window costs at most the last partial window rather than the entire session.
2. **OOP-recorded data is captured.** `RemoteWorkspaceTelemetryService : AbstractWorkspaceTelemetryService`,
   so the ServiceHub process already registers a `VSMetricSink`, whereas nothing there ever calls
   `FeaturesSessionTelemetry.Report()`. All current writers are in-proc so nothing changes today, but a
   future OOP writer would no longer be silently lost.

### 3.7 Migration/rollout

Because §3.1 is a deliberate contract break and **no test anywhere pins these names** (they appear only in
`FunctionId.cs` and the loggers), CI cannot catch a mistake here. Therefore:

- Publish §3.2–3.5 to the dashboard owners and get sign-off **before** Phase 2 lands.
- **Recommended: dual-emit for one release.** Keep the existing `ReportTelemetry` drain behind a feature
  flag while the new metrics flow, so owners can validate new against old on real data, then delete the old
  path. Costs temporary duplicate volume; removes essentially all rollout risk.

---

## 4. Phases

Each phase is independently valid, reviewable and revertible.

**Phase 0 — validation spike (blocking, no product change).**
Prove what `TelemetryHistogramEvent<long>` over an `IVSHistogram<long>` actually puts on the wire: confirm
Min/Max/Average/Median and bucket counts reach the backend, and under what names. Reflection proves the
API surface (§2) but not the serialized payload. **If Statistics do not survive `PostMetricEvent`, §3.2's
claim that `.maximum`/`.minimum`/`.mean` are preserved is false and the plan must be revised** — so this
gate comes first. Deliverable: a short findings note, no committed code.

**Phase 1 — handle API, no aggregator changes.**
Add `DistributionShape` / `CounterMetric` / `DistributionMetric` / `DefineCounter` / `DefineDistribution`;
add `RecordBlockTime` to the handle; extend `IMetricSink.Record` with `in DistributionShape`; update
`VSMetricSink.CreateAggregation` to `CreateVSHistogram`. Migrate the ~25 existing `Count`/`Record`/
`RecordBlockTime` call sites (12 files) to handles. Emitted telemetry is unchanged except that histograms
gain min/max. Extend `VSMetricSinkTests` for shape plumbing and instrument-name preservation.

**Phase 2 — convert the five, one commit each, simplest first.**
`OnTheFlyDocsLogger` (counters only) → `InheritanceMarginLogger` (one histogram) → `ChangeSignatureLogger`
→ `CompletionProvidersLogger` → `AsyncCompletionLogger`. Each commit: replace aggregator fields with
handles, delete that type's `ReportTelemetry`, drop its `ActionInfo` enum.
Fold in the already-identified dead code as it is encountered: `CompletionProvidersLogger`'s
`ExtensionMethodCompletionTicks` / `GetSymbolsTicks` / `CreateItemsTicks` / `RemoteAssetSyncTicks` /
`RemoteTicks` (no writers) and `LogExtensionMethodCompletionMethodsProvidedDataPoint` (`:56`) /
`LogExtensionMethodCompletionPartialResultCount` (`:62`) (no callers); the unused `Maximum`/`Minimum`/
`Mean` consts at `ChangeSignatureTelemetryLogger.cs:12-14`.

**Phase 3 — delete the drain scaffolding.**
Remove `FeaturesSessionTelemetry` (`src/Features/Core/Portable/Common/FeaturesSessionTelemetry.cs`),
`RoslynPackage.ReportSessionWideTelemetry` and its call (`RoslynPackage.cs:184`, `:194-199`), and the
`FeaturesSessionTelemetry.Report()` call in `LanguageServerTelemetry.Dispose` (`:118`) — the adjacent
`RoslynTelemetry.Flush()` at `:119` already does the work.

⚠️ **Do not delete `CountLogAggregator` / `StatisticLogAggregator` / `HistogramLogAggregator` /
`StatisticResult` / `AbstractLogAggregator`.** Six other call sites still use them:
`UnitTestingSolutionCrawlerLogger`, `UnitTestingWorkCoordinator` (×2),
`SourceGeneratorTelemetryCollectorWorkspaceService`, `RemoteSolutionCache`,
`AbstractSemanticModelReuseLanguageService`.

---

## 5. Acceptance

- Every metric from §3.2–3.5 is emitted, with the instrument and meter names stated there.
- `.range` is the only field intentionally dropped; every other field has a stated equivalent.
- No `ReportTelemetry`-style shutdown drain remains for these five; `ReportSessionWideTelemetry` is gone.
- Shutdown no longer depends on package-vs-catalog disposal order for this data.
- The five aggregator helper types remain, still used by their six other consumers.

## 6. Validation

Targeted, single projects (other work is running on this machine — no solution-filter builds):

```
dotnet build src\Workspaces\Core\Portable\Microsoft.CodeAnalysis.Workspaces.csproj
dotnet build src\VisualStudio\Core\Def\Microsoft.VisualStudio.LanguageServices.csproj
dotnet build src\Features\Core\Portable\Microsoft.CodeAnalysis.Features.csproj
dotnet test  src\LanguageServer\Microsoft.CodeAnalysis.LanguageServer.UnitTests\... --filter "FullyQualifiedName~VSMetricSinkTests"
```

Plus the Phase 0 payload evidence, and a manual devenv/LSP smoke check that the new metric events appear.

## 7. Open questions

1. **Phase 0 outcome** — do `IVSHistogram` Statistics survive `PostMetricEvent`? Gates §3.2.
2. **`recordMedian`** — on or off by default? It is per-window only and not recombinable; I would default
   it **off** and enable per metric where a per-window median is genuinely wanted.
3. **Dual-emit or clean cut?** Recommend dual-emit for one release (§3.7).
4. **Tag consolidation** — e.g. `SourceGetContextCompletedTicks` / `…CanceledTicks` are naturally one
   metric with a `canceled` tag. Better modeling, larger shape change. Deferred out of this plan.
