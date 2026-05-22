# F8 + F9 Major API Plan — Subscription Tokens

**Date:** 2026-05-22
**Status:** Plan only — implementation deferred to a major version bump
**Findings addressed:** unit-09 #04 (EventBus handler lifecycle), unit-11 SYNC-02 (ReactiveProperty BindingScope leaks)
**Related docs:** [`2026-05-22-medium-fix-plans.md`](./2026-05-22-medium-fix-plans.md) §F8 and §F9

---

## Goal

Replace Strada's two `void`-returning subscription primitives with an
`IDisposable`-token API so callers can scope subscriptions to a `using`
block or aggregate them into a `BindingScope` that disposes them in
LIFO order. This closes two LOW/MEDIUM-severity classes of memory leak
where the caller forgets to call the matching `Unsubscribe*` method.

The proposal merges F8 (EventBus) and F9 (ReactiveProperty) into a
single coordinated change because they share the token type and the
same `BindingScope` aggregate.

---

## Why one PR per fix is not enough

| Concern | Why it forces a coordinated change |
|---------|-----------------------------------|
| **Shared token type** | Both APIs return the same `SubscriptionToken` so callers can `.AddTo(scope)` regardless of source. Splitting forces two token types, then a merge later. |
| **Shared `BindingScope` aggregate** | `BindingScope` is the natural place to keep tokens; introducing it twice (once with each API) doubles the deprecation cycle. |
| **Callers mix the two APIs** | `Runtime/Patterns/Base.cs` and `Runtime/Sync/EntityMediator.cs` already wrap both `EventBus.Subscribe` and `ReactiveProperty.Subscribe` in the same lifecycle. The wrappers can be updated once instead of twice. |
| **Version bump cost** | Both changes are public-API breaking. Shipping them together amortises the major-version increment over both fixes. |

---

## Goals (non-goals at end)

- Per-handler unsubscribe via `IDisposable` token returned from
  `Subscribe(...)` overloads.
- Aggregate disposal via a `BindingScope` container.
- A 1-version deprecation window so existing callers can migrate without
  immediate breakage.
- Zero throughput regression in the hot dispatch path (`Send` /
  `Publish` / `ReactiveProperty.Value`).

**Non-goals:**

- Weak-reference handlers (separate work item; not in scope).
- Async cancellation semantics for `RegisterAsyncSignalHandler` (the
  existing `CancellationToken` parameter on `SendAsync` already covers
  per-call cancellation).
- Re-architecting Events vs Signals (Events already support per-handler
  unsubscribe — this work harmonises Signals/Queries with Events, not
  the other way around).

---

## API surface — proposed

### Shared token

```csharp
// Runtime/Core/SubscriptionToken.cs (new file)
namespace Strada.Core
{
    /// <summary>
    /// Opaque disposable handle for a subscription created by Strada's
    /// reactive primitives (EventBus, ReactiveProperty, etc.). Disposing
    /// the token removes exactly the handler it represents — no other
    /// subscribers are affected. Idempotent: a second Dispose is a no-op.
    /// </summary>
    public sealed class SubscriptionToken : IDisposable
    {
        private Action _dispose;
        internal SubscriptionToken(Action dispose) { _dispose = dispose; }
        public bool IsActive => _dispose != null;
        public void Dispose()
        {
            var d = System.Threading.Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
    }
}
```

### BindingScope

```csharp
// Runtime/Sync/BindingScope.cs — already exists; extend with token storage
namespace Strada.Core.Sync
{
    public sealed class BindingScope : IDisposable
    {
        private readonly List<IDisposable> _tokens = new();
        private bool _disposed;

        public void Add(IDisposable token)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            if (_disposed) { token.Dispose(); return; }
            _tokens.Add(token);
        }

        public int Count => _tokens.Count;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = _tokens.Count - 1; i >= 0; i--)
                _tokens[i].Dispose();
            _tokens.Clear();
        }
    }

    public static class SubscriptionTokenExtensions
    {
        public static T AddTo<T>(this T token, BindingScope scope)
            where T : IDisposable
        {
            scope.Add(token);
            return token;
        }
    }
}
```

### F8 — EventBus

Add token-returning overloads alongside the existing void methods. Mark
the void methods `[Obsolete]` with a non-error warning.

```csharp
public partial class EventBus
{
    // NEW — replaces RegisterSignalHandler<T>(Action<T>)
    public SubscriptionToken Subscribe<TSignal>(Action<TSignal> handler) where TSignal : struct
    {
        if (_disposed) ThrowDisposed();
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        lock (_lock)
        {
            var id = SignalTypeId<TSignal>.Id;
            EnsureCapacity(ref _signalHandlers, id);
            var previous = _signalHandlers[id] as Action<TSignal>;
            // multicast — append to delegate chain
            Volatile.Write(ref _signalHandlers[id], (Action<TSignal>)Delegate.Combine(previous, handler));
            return new SubscriptionToken(() => RemoveSignalHandlerCore(id, handler));
        }
    }

    private void RemoveSignalHandlerCore<TSignal>(int id, Action<TSignal> handler) where TSignal : struct
    {
        if (_disposed) return;
        lock (_lock)
        {
            var current = _signalHandlers[id] as Action<TSignal>;
            var updated = (Action<TSignal>)Delegate.Remove(current, handler);
            Volatile.Write(ref _signalHandlers[id], updated);
        }
    }

    // Same shape for Query, Event (already has Subscribe, just normalise),
    // AsyncSignal, AsyncQuery.

    [Obsolete("Use Subscribe<TSignal>(handler) which returns a SubscriptionToken. " +
              "This method will be removed in v4.0.", error: false)]
    public void RegisterSignalHandler<TSignal>(Action<TSignal> handler) where TSignal : struct
    {
        // Delegate to Subscribe but discard the token (legacy behaviour).
        // Note: the old "replace previous handler" semantics are abandoned in
        // favour of multicast; callers who relied on replacement must migrate.
        Subscribe(handler);
    }
}
```

**Important semantic change:** the current `RegisterSignalHandler` *replaces*
the previous handler. The new `Subscribe` *appends* to a multicast
delegate. The shim above preserves the visible call but changes
semantics — this is a deliberate behavioural change documented in the
migration guide. Callers who rely on replacement must use the explicit
`UnregisterSignalHandler<T>()` before re-registering, or migrate to the
token API and `Dispose()` the old token.

### F9 — ReactiveProperty

```csharp
public sealed class ReactiveProperty<T> : IReadOnlyReactiveProperty<T>, IDisposable
{
    // ... existing fields ...

    // NEW
    public SubscriptionToken Subscribe(Action<T> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _handlers.Add(handler);
        return new SubscriptionToken(() => RemoveHandler(handler));
    }

    public SubscriptionToken SubscribeAndInvoke(Action<T> handler)
    {
        var token = Subscribe(handler);
        handler(_value);
        return token;
    }

    private void RemoveHandler(Action<T> handler)
    {
        for (int i = _handlers.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_handlers[i], handler))
            {
                _handlers.RemoveAt(i);
                break;
            }
        }
    }

    // OBSOLETE: existing void Subscribe / Unsubscribe stay one version,
    // forwarding to the new methods and ignoring the returned token.
}

public interface IReadOnlyReactiveProperty<T>
{
    T Value { get; }
    SubscriptionToken Subscribe(Action<T> handler);     // signature CHANGE
    [Obsolete("Use Subscribe(handler) and dispose the returned token.")]
    void Unsubscribe(Action<T> handler);
}
```

`IReadOnlyReactiveProperty<T>` is a public interface — changing the
`Subscribe` return type from `void` to `SubscriptionToken` is a binary
breaking change for any external implementation. Acceptable inside a
major version bump.

---

## Affected callers

Found via `grep -rn` over the Runtime/ tree:

### `RegisterSignalHandler` / `UnregisterSignalHandler` (F8)

- `Runtime/Patterns/Base.cs:70-105` — `Subscribe`, `RegisterSignalHandler`, `RegisterQueryHandler` wrappers
- `Runtime/ECS/Systems/SystemBase.cs:146-148` — `RegisterSignalHandler` wrapper
- `Runtime/Sync/EntityMediator.cs:158-161` — `Subscribe` wrappers
- `Runtime/Communication/EventBus.cs:251` — internal `Subscribe` call (event channel; already token-friendly)
- `Tests/Runtime/Communication/MessageBusTests.cs` — 8+ test cases using the old API directly
- `Tests/Runtime/Patterns/ControllerLifecycleTests.cs:132` — `Subscribe<TestEvent>`

### `ReactiveProperty.Subscribe` (F9)

- `Runtime/Sync/BindingScope.cs:20` — `Subscribe(property, handler)` wrapper (this is the natural insertion point for the new pattern)
- `Runtime/Sync/ComputedProperty.cs:183` — internal subscription chain
- `Runtime/Sync/ReactiveExtensions.cs:96, 139, 187-188, 245` — multiple combinator subscriptions (Map, Where, CombineLatest, etc.)
- `Runtime/Sync/EntityMediator.cs:154, 161` — already overlaps with EventBus call sites
- `Runtime/Patterns/Base.cs:72` — `EventBus?.Subscribe(handler)` (Event channel, already token-friendly)

Total external migration surface: **~14 internal callers** + the public
API of `Patterns/Base.cs`, `ECS/SystemBase.cs`, `Sync/EntityMediator.cs`,
which are themselves consumed by user code.

---

## Migration strategy

### Phase 0 — prepare (this PR, no behaviour change)

- Add `Runtime/Core/SubscriptionToken.cs` (new file).
- Extend `Runtime/Sync/BindingScope.cs` with the aggregate behaviour
  described above.
- Add the new token-returning overloads to `EventBus` and
  `ReactiveProperty`.
- Internal Strada callers (Base, SystemBase, EntityMediator, etc.)
  migrate to the token API and store tokens in a per-instance
  `BindingScope` disposed in `OnDispose()`.
- Old void methods are kept and marked `[Obsolete]` (warning only,
  `error: false`).
- Tests get a small new suite for the token API; existing tests stay on
  the old API (we verify both work).

### Phase 1 — encourage migration (next minor release)

- Promote the obsolete warning into the changelog and release notes.
- Update `Documentation~/Messaging.md` and `Documentation~/Sync.md`
  with the new patterns and a migration table.
- Add Roslyn-style suggestion in obsolete messages pointing at the new
  method.

### Phase 2 — remove old API (next major release, v4.0)

- Delete the `[Obsolete]` shims.
- Change `IReadOnlyReactiveProperty<T>.Subscribe` return type to
  `SubscriptionToken` (binary break).
- Bump package version to 4.0.0 in `package.json`.

---

## Test strategy

### Unit tests (added in Phase 0)

- `Subscribe_DisposeToken_RemovesOnlyThatHandler` — multiple handlers,
  dispose one, verify the others fire.
- `Subscribe_DisposeTokenTwice_NoThrow` — idempotency.
- `Subscribe_AfterContainerDispose_TokenIsInert` — token disposal during
  / after `EventBus.Dispose` does not throw or NRE.
- `BindingScope_Dispose_LIFO_Order` — verify reverse order disposal.
- `BindingScope_DisposeIdempotent` — second dispose is no-op.
- `BindingScope_AddAfterDispose_DisposesNewTokenImmediately`.
- `ReactiveProperty_SubscribeAndInvoke_TokenWorks` — `SubscribeAndInvoke`
  returns a token and dispose removes the handler.

### Regression tests (must pass unchanged)

- All existing `MessageBusTests.cs` continue to pass against the
  `[Obsolete]` shims.
- Existing `ControllerLifecycleTests.cs` `Subscribe<TestEvent>` flow.

### Performance bench (must not regress)

- `Tests/Runtime/Performance/MessageBusBenchmarks.*` — `Send` and
  `Publish` paths. Multicast delegate dispatch is ~10ns slower than a
  single-slot direct invoke for one handler; we must verify the gap
  stays within noise and document the trade-off if not.
- `Tests/Runtime/Performance/DIPerformanceTests` — no impact expected,
  but run as a smoke screen.

### Semantic-change test

- `RegisterSignalHandler_ThenAgain_NewBehavior_IsMulticast` —
  explicitly documents that the old "second register replaces first"
  contract is now "second register adds a second handler". This is a
  test that *exists to document the breaking change*, not to enforce a
  property — Phase 2 removes the obsolete method and the test becomes
  irrelevant.

---

## Risk assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------:|-------:|-----------|
| Hot dispatch path regression from multicast delegate | Medium | High | Bench in Phase 0; if regression >10ns/dispatch, use array-backed handler list and skip Delegate.Combine |
| User code relies on RegisterSignalHandler "replace" semantics | Medium | Medium | Documented breaking change in obsolete message; surface in release notes |
| `IReadOnlyReactiveProperty` external implementers break in v4.0 | Low | Medium | Audit external usage in `Documentation~/Sync.md`; provide migration table |
| `BindingScope` re-entrant disposal during `Dispose()` of a token that triggers further `Subscribe` calls | Low | Low | Set `_disposed = true` before iterating; new `Add` during loop disposes immediately |
| Tests/MessageBusTests breaks due to multicast semantics | Low | Low | Tests use unique signal types per test; replacement behaviour rarely exercised |

---

## Sprint breakdown

| Sprint | Scope | Files touched (est.) | Effort |
|--------|-------|---------------------:|-------:|
| **S0** | Add `SubscriptionToken`, extend `BindingScope`, new EventBus + ReactiveProperty overloads, Phase 0 tests | ~10 | 1 day |
| **S1** | Migrate internal Strada callers (Patterns/Base, ECS/SystemBase, Sync/EntityMediator, Sync/ComputedProperty, Sync/ReactiveExtensions); `[Obsolete]` shims for legacy API | ~12 | 1 day |
| **S2** | Documentation: `Documentation~/Messaging.md`, `Documentation~/Sync.md`, CHANGELOG entry, migration guide | ~3 | 0.5 day |
| **S3** | Performance bench audit; tune multicast path if needed | ~2 | 0.5 day |
| **v4 ship** | Remove `[Obsolete]`, change `IReadOnlyReactiveProperty<T>.Subscribe` signature, bump package version | ~6 | 0.25 day |

Total Phase 0 effort: ≈3 days. v4 ship is a small separate PR
when the deprecation window closes.

---

## Open questions

1. **`UnregisterSignalHandler<T>()` semantics post-Phase-0:** the new
   `Subscribe` is multicast, but `UnregisterSignalHandler<T>()` clears
   *all* handlers for the type. Keep, deprecate, or repurpose? Proposal:
   keep as `RemoveAllSignalHandlers<T>()` — clearer name, same intent.

2. **Should `BindingScope` be moved out of `Sync/`?** Tokens originate in
   both `Communication/` (EventBus) and `Sync/` (ReactiveProperty). A
   neutral location like `Runtime/Core/Lifecycle/BindingScope.cs` might
   be cleaner. Tradeoff: breaks any external code that has
   `using Strada.Core.Sync;` and references `BindingScope`.

3. **Should the obsolete shims log a one-time runtime warning?** Helps
   surface the migration during testing, but pollutes logs in projects
   that intentionally stay on the old API.

These decisions can be made at S0 kick-off; none block the plan.

---

## Acceptance criteria for the major version bump

- All public `Subscribe`/`Register*` methods return `SubscriptionToken`.
- `BindingScope` is the documented pattern for grouping subscriptions.
- Internal callers store tokens, never call `Unsubscribe*` by reference.
- `Documentation~/Sync.md` and `Documentation~/Messaging.md` describe
  the token pattern as the primary API and mention the legacy methods
  only in a migration section.
- Performance benches show ≤10ns regression in `EventBus.Send` and
  `ReactiveProperty.Value = ...` paths.
- All existing tests pass against the new API (after a one-time test
  migration in S2).

---

**Author:** Claude (2026-05-22)
**Related work:** Sprint 1+2 (`549d8ff`), Sprint 3 quick-wins (`d44782a`), Sprint 4 editor hardening (current)
