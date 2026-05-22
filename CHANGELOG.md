# Changelog

All notable changes to Strada Core are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

Security audit follow-ups for MEDIUM severity findings (see
[`SecurityReports/2026-05-22-medium-status-review.md`](SecurityReports/2026-05-22-medium-status-review.md)).

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

## Earlier history

Prior security fixes were applied directly to the codebase between
2026-02-21 (`cf55a20`, `ee6ef8f`) and 2026-03-07 (`f586942`,
`164bcb3`, `8db91b4`) — see git log for details. The
2026-03-07 `SecurityReports/unit-*.md` files document the audit that
informed the current sprint.
