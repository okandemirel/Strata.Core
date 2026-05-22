# Changelog

All notable changes to Strada Core are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### BREAKING CHANGES — Phase 2B (v2.0)

**This release drops the legacy reference-based unsubscribe API.** The
following methods are removed from public interfaces and the `EventBus`
/ `ReactiveProperty` classes:

- `IReadOnlyReactiveProperty<T>.Unsubscribe(Action<T>)`
- `IEventPublisher.Unsubscribe<TEvent>(Action<TEvent>)`
- `ISignalBus.UnregisterSignalHandler<TSignal>()`
- `IQueryBus.UnregisterQueryHandler<TQuery, TResult>()`

Interface signatures changed (binary break for external implementers):

- `IReadOnlyReactiveProperty<T>.Subscribe`, `IEventPublisher.Subscribe`,
  `ISignalBus.RegisterSignalHandler` (×2 overloads), and
  `IQueryBus.RegisterQueryHandler` (×2 overloads) now return
  `Strada.Core.SubscriptionToken` instead of `void`.

Migration: replace `bus.Unsubscribe(handler)` / `property.Unsubscribe(h)`
/ `UnregisterSignalHandler<T>()` etc. with `token.Dispose()` where
`token` is the value returned by the matching `Subscribe` /
`RegisterSignalHandler` / `RegisterQueryHandler` call. See
`Documentation~/Messaging.md` and `Documentation~/Sync.md` for the
before/after table.

Internal helpers removed: `ReactivePropertySubscriptionExtensions.SubscribeToken<T>`
extension and all explicit interface implementations in `EventBus` and
`ReactiveProperty<T>` are gone (no longer needed once the interface
returns the token directly).

Package version bumped from `1.0.0-alpha.1` to `2.0.0-alpha.1`.

### Security

#### Phase 2A — test suppression + doc migration tables

- Test/benchmark files that deliberately exercise the legacy
  `Unsubscribe` / `Unregister*` APIs (verifying they still behave
  correctly during the deprecation period) now carry a file-level
  `#pragma warning disable CS0618` with a comment explaining why. The
  build is back to zero CS0618 warnings without losing test coverage
  of the deprecated path. Files: `Tests/Runtime/Communication/MessageBusTests.cs`,
  `BusPropertyTests.cs`, `EventBusThreadSafetyTests.cs`,
  `Tests/Runtime/Sync/ReactivePropertyTests.cs`,
  `Tests/Runtime/Performance/MessageBusPerformanceTests.cs`.
- `Documentation~/Messaging.md` and `Documentation~/Sync.md` gained
  "Migration: legacy Unsubscribe → SubscriptionToken" sections with
  before/after tables and `BindingScope` aggregation examples.

#### Signal/Query token API

Extends the token foundation to the signal and query buses. The
signal/query semantic remains single-slot (1:1, by design — `Send` and
`Query` dispatch to exactly one handler); the token only adds
race-safe per-handler removal, it does not turn signals into events.

- **`EventBus.RegisterSignalHandler<TSignal>`** (both `Action` and
  `ISignalHandler` overloads) now returns a `SubscriptionToken` whose
  disposal removes the registered handler from the slot **only if it
  is still there** (`ReferenceEquals` check). A later
  `RegisterSignalHandler` call that replaced the slot survives a stale
  token disposal.
- **`EventBus.RegisterQueryHandler<TQuery, TResult>`** (both overloads)
  gets the same treatment.
- Both interfaces (`ISignalBus`, `IQueryBus`) keep their `void`
  signatures via explicit interface implementations, so external
  implementers are unaffected.
- **`Patterns/Base`** and **`ECS/SystemBase`** wrappers
  (`RegisterSignalHandler`, `RegisterQueryHandler`) now capture the
  returned tokens into a per-instance `_disposables` list — subclasses
  that register signals or queries get automatic teardown when the
  pattern or system is disposed.
- New `[Obsolete]` markers on `EventBus.UnregisterSignalHandler<T>` and
  `EventBus.UnregisterQueryHandler<TQuery, TResult>` pointing at the
  token-based replacement.

### Roadmap (next major version)

The following items are tracked for the next major release to close out
the F8/F9 deprecation cycle. Items below are NOT in this release — they
describe what the next major version will do once the obsolete-warning
period has elapsed.

- **Remove `[Obsolete]` shims:**
  `IReadOnlyReactiveProperty<T>.Unsubscribe`, `EventBus.Unsubscribe<TEvent>`,
  `EventBus.UnregisterSignalHandler<T>`, `EventBus.UnregisterQueryHandler<TQuery, TResult>`.
- **Tighten interface signatures:** change
  `IReadOnlyReactiveProperty<T>.Subscribe` return type from `void` to
  `SubscriptionToken` (binary break — external implementers of the
  interface will need to update). Same for the equivalents on
  `ISignalBus`, `IQueryBus`, `IEventPublisher` where applicable.
- **Test/benchmark migration:** approximately 10 call sites across
  `Tests/Runtime/Communication/*.cs` and
  `Tests/Runtime/Performance/EventBusSubscribeBenchmarks.cs` still use
  the legacy `Unsubscribe` / `Unregister*` APIs. They must be migrated
  to token disposal before the obsolete methods are removed. Migration
  is mechanical: `Register(...)`/`Subscribe(...)` calls now return a
  token; capture it locally, dispose at end-of-test.
- **Documentation refresh:** `Documentation~/Messaging.md` and
  `Documentation~/Sync.md` need a "Migration from void Subscribe/Unsubscribe"
  section showing the before/after for each affected API.
- **Version bump:** `package.json` from `1.0.0-alpha.1` to the next
  appropriate major (the framework is internally referenced as "v3";
  the public semver should follow when the deprecation cycle closes).

#### F8 + F9 Phase 1 (full caller migration) + Phase 2 prep

Completes the internal caller migration started in the previous Phase 1
PR and adds the first `[Obsolete]` markers as preparation for the next
major version.

- **Sync/ReactiveExtensions:** all eight derived-property types
  (`MappedProperty`, `FilteredProperty`, `CombinedProperty<T1,T2>`,
  `CombinedProperty<T1,T2,T3>`, `ThrottledProperty`, `DistinctProperty`,
  `PropertyBinding`, `ConvertedBinding`) now store the
  `SubscriptionToken` returned by their source(s) and dispose those
  tokens on `Dispose` instead of calling `_source.Unsubscribe(...)`.
- **Sync/ComputedProperty.WatchDependency:** uses the new
  `SubscribeToken` helper, removing the per-dependency
  `DependencySubscription` wrapper class allocation on the fast path
  (the wrapper still backs the reflection-based untyped path).
- **Sync/EntityMediator:** removed the `_unsubscribeActions` Action
  list; the two `Subscribe` wrappers now store the bus's
  `SubscriptionToken` directly in `_disposables`.
- **New extension:**
  `IReadOnlyReactiveProperty<T>.SubscribeToken(handler)` — returns a
  `SubscriptionToken` regardless of whether the static type is the
  interface (whose explicit `Subscribe` returns `void`) or the
  concrete `ReactiveProperty<T>`. Internal helper bridging the gap
  during the deprecation cycle.

Phase 2 prep (`[Obsolete]` markers, warning-level only):
- `IReadOnlyReactiveProperty<T>.Unsubscribe(Action<T>)` — dispose the
  token returned by `Subscribe` instead.
- `EventBus.Unsubscribe<TEvent>(Action<TEvent>)` — same.

These deprecate the legacy reference-based removal API. The methods
remain functional this release; the next major release will remove
them. External callers that rely on `Unsubscribe(handler)` directly
will see a compile-time warning pointing to the token API.

#### F8 + F9 caller migration (Phase 1, partial) + F10 generator hardening

- **Patterns/Base** migrated to the token API: removed the
  `_unsubscribes` Action list, `Subscribe<T>(handler)` now stores the
  returned `SubscriptionToken` in `_disposables`, and `Dispose()` walks
  the disposable list in LIFO order. Behavioural parity is preserved
  for subclasses; this is a code-clarity refactor that exercises the
  Phase 0 API end-to-end.
- **EntityQueryGenerator (F10)** now emits an editor / debug-build
  upper-bound check (`d{i} >= set{i}.Count` → `IndexOutOfRangeException`)
  immediately before the unsafe pointer dereference in generated
  `ForEach`. The check is compiled out under release builds, so the hot
  iteration path is unchanged for shipping code. Define
  `STRADA_ECS_BOUNDS_CHECK` to keep the check on in release.

#### FRAMEWORK DESIGN markers (additional OPEN-BY-DESIGN annotations)

Four more `// FRAMEWORK DESIGN:` comments added to document
already-accepted design trade-offs surfaced by the audit:

- `GameBootstrapper` static globals — single-World deliberate choice.
- `ArchetypeManager.DestroyEntity` — `List<Entity>.Remove` without
  compaction is intentional (dense, append-mostly archetype access
  pattern).
- Module-generator `AssemblyDefStep.WriteAsmdef` — generated `.asmdef`
  emits `allowUnsafeCode: true` because Strada's ECS subsystem requires
  unsafe pointer access.
- `StradaEntityInspectorWindow` — editor-only reflection over runtime
  internals is the canonical debug path.

#### F8 + F9 token foundation (Phase 0)

First step of the deferred subscription-token refactor (see
[`SecurityReports/2026-05-22-major-api-plan-f8-f9.md`](SecurityReports/2026-05-22-major-api-plan-f8-f9.md)).
This release lands the foundation only; internal callers will migrate in
a follow-up sprint and the legacy void overloads stay available for one
more minor version.

- **New type:** `Strada.Core.SubscriptionToken` — disposable handle with
  idempotent, thread-safe `Dispose` (uses `Interlocked.Exchange` on the
  underlying delegate).
- **`EventBus.Subscribe<TEvent>(Action<TEvent>)`** now returns a
  `SubscriptionToken`. Calling code that ignored the previous `void`
  return value continues to compile and behave identically. Callers
  routed through the `IEventPublisher` interface still see the original
  `void` contract via an explicit interface implementation, so external
  implementers of `IEventPublisher` are not broken.
- **`ReactiveProperty<T>.Subscribe(Action<T>)`** and
  **`SubscribeAndInvoke(Action<T>)`** return a `SubscriptionToken` with
  the same source-compatible semantics. The `IReadOnlyReactiveProperty<T>`
  interface keeps its `void Subscribe` shape via an explicit interface
  implementation.
- **`BindingScope.Add(IDisposable)`** is a new public method that
  appends a disposable (typically a `SubscriptionToken`) to the scope's
  disposal list. If the scope is already disposed, the incoming token is
  disposed immediately.

#### Editor tooling & source-generator hardening (Sprint 4)

LOW severity follow-ups for editor and codegen attack surface (see
[`SecurityReports/2026-05-22-low-status-review.md`](SecurityReports/2026-05-22-low-status-review.md)).

- **Editor:** `BusDebuggerWindow.MatchesTypePattern` and
  `BusDataProvider` wildcard filter regex now run with a 100 ms
  `TimeSpan` matchTimeout — closes a ReDoS surface where a malicious
  filter pattern could hang the editor's UI thread.
- **Source generator (DI):** `StradaDISourceGenerator.GetSafeName` now
  maps any non-identifier character to `_` (previously only `.`, `<`,
  `>` were escaped). Prevents invalid C# identifiers from leaking into
  generated method names for nested or generic types.
- **Source generator (ECS):** Factory class names now incorporate the
  service's full namespace (`Game_Player_Foo__Factory` instead of
  `Foo__Factory`) — prevents collisions between same-named services in
  different namespaces from generating duplicate class declarations.

#### LOW severity quick-wins

Sprint 3 — small-effort hardening for LOW findings identified in
[`SecurityReports/2026-05-22-low-status-review.md`](SecurityReports/2026-05-22-low-status-review.md).

- **Bootstrap:** `GameBootstrapperConfig._verboseLogging` now defaults to `false`
  (was `true`). Reduces information disclosure in production builds.
- **DI / Communication / Sync:** integer-overflow guards added to identifier
  allocators that previously wrapped silently on `int.MaxValue`:
  - `TimerService.Schedule` (`Runtime/Services/TimerService.cs`)
  - `EntityHandleRegistry.Register` (`Runtime/Sync/EntityHandleRegistry.cs`)
  - `EventBus` signal / query / event / async-signal / async-query type-id
    counters (`Runtime/Communication/EventBus.cs`) via a shared
    `AllocateAndCheck` helper.
- **Patterns:** `View.UpdateView` rejects `null` model with
  `ArgumentNullException`. `PatternManager.RegisterController` and
  `RegisterService` now reject `null` and throw on duplicate registration.
- **Sync:** `TwoWayBinding<T>` and `TwoWayBinding<TSource, TTarget>` now use
  `try / finally` around the reentrancy guard so a thrown handler does not
  leave `_updating = true`. `MediatorPool` caps the available stack at 256
  to prevent unbounded growth during scene teardown.
- **ECS:** `ECSBuilder.WithSystem` throws when the same system type is
  registered twice, avoiding silently-duplicated update calls.
- **Data:** `ConfigData<T>.Data` setter rejects `null` instead of allowing
  callers to clear the underlying instance.

#### MEDIUM severity (Sprint 1 + 2 — earlier in this release)

- **DI:** `DirectFactory<T>.Delegate` public static field replaced with private
  field plus `Register(factory)` / `Clear()` / `internal Get()` API. External
  code can no longer overwrite the registered factory via direct field
  assignment.
- **DI:** Transient services marked with the new
  `[TrackTransientDisposal]` attribute now have their `IDisposable` instances
  pushed onto the container disposal stack and disposed in LIFO order during
  `Container.Dispose()`. Default behavior unchanged for unmarked transients
  (caller still owns disposal).
- **DI AutoBinding:** New assembly-level attribute
  `[assembly: AutoBindingScope]` opts an assembly into auto-binding
  scanning. Assemblies that match an include pattern but lack the attribute
  now log a one-time-per-session deprecation warning; will become a hard
  error in a future major release. `Strada.*` assemblies are implicitly
  trusted.
- **Modules:** `SerializableType` exposes a new
  `Type AsType<TBase>() where TBase : class` method that resolves the
  serialized type only if it is assignable to `TBase`, rejecting (with an
  error log) tampered or mismatched asset-bundle data.
  `SystemEntry.GetSystemType()` now uses `AsType<ISystem>()` for
  defense-in-depth.
- **ECS:** `EntityCommandBuffer` struct gained explicit XML documentation
  describing its thread-safety, playback, and disposal contract — prevents
  silent misuse in parallel job contexts.
- **ECS:** `SparseSet.EnsureSparseCapacity` growth calculation switched to
  `long` arithmetic to prevent `int` overflow when `_sparse.Length` is near
  `int.MaxValue / 3`; result clamped to `MaxSparseCapacity` (1 048 576).
- **Editor — Module Generator:** `StradaModuleGenerator.SetTargetPath`
  now invokes `ValidateTargetPath()` by default; programmatic callers can
  opt out with `SetTargetPath(path, validate: false)`. Closes a path
  validation bypass available to non-UI code paths.

### Changed

- `DirectFactory<T>.Delegate` field is removed. Use
  `DirectFactory<T>.Register(factory)` / `DirectFactory<T>.Clear()` instead.
  The source-generated `StradaGeneratedInitializer` emits the new API
  automatically; user code that wrote `DirectFactory<T>.Delegate = factory`
  directly must be updated.

### Migration

If your code referenced `DirectFactory<T>.Delegate` directly (uncommon —
this is normally an implementation detail of the source-generated registry):

```csharp
// Before
DirectFactory<MyService>.Delegate = factory;
DirectFactory<MyService>.Delegate = null;

// After
DirectFactory<MyService>.Register(factory);
DirectFactory<MyService>.Clear();
```

To silence the new auto-binding deprecation warning in your assembly:

```csharp
[assembly: Strada.Core.DI.AutoBinding.AutoBindingScope]
```

Place this once anywhere in your assembly (commonly `AssemblyInfo.cs`).

### Documentation

- New `SecurityReports/2026-05-22-status-review.md` — HIGH severity audit
  status (13/15 fixed, 1 partial, 1 open-by-design).
- New `SecurityReports/2026-05-22-medium-status-review.md` — MEDIUM
  severity audit status across 64 unique findings.
- New `SecurityReports/2026-05-22-medium-fix-plans.md` — concrete fix
  plans with code snippets, breaking-change analysis, and sprint ordering.
- New `SecurityReports/2026-05-22-low-status-review.md` — LOW severity
  audit status across 81 unique findings (44% already FIXED, 41 OPEN of
  which 10 are quick-win candidates).
- New `SecurityReports/2026-05-22-major-api-plan-f8-f9.md` — coordinated
  implementation plan for the deferred `EventBus` subscription-token and
  `ReactiveProperty` `BindingScope` API refactors (target: next major
  version). Documents migration phases, affected callers, test strategy,
  and risk assessment.

## Earlier history

Prior security fixes were applied directly to the codebase between
2026-02-21 (`cf55a20`, `ee6ef8f`) and 2026-03-07 (`f586942`,
`164bcb3`, `8db91b4`) — see git log for details. The
2026-03-07 `SecurityReports/unit-*.md` files document the audit that
informed the current sprint.
