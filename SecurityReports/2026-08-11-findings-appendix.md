# Strada.Core — Bulgu Eki (Findings Appendix)
**Tarih:** 2026-08-11 · **Toplam:** 281 doğrulanmış bulgu · **Triple-confirmed:** 14

Her bulgu, kaynağı yeniden okuyan bir karşı-doğrulayıcı tarafından teyit edildi. `⚑` = üç bağımsız doğrulayıcı (repro + measurement lensleri dahil).

Ana rapor: [2026-08-11-full-system-audit.md](./2026-08-11-full-system-audit.md)


---

# CRITICAL (1)

## `modulebuilder-ambiguousmatch-kills-bootstrap` ⚑

**ModuleBuilder.Register(Type,Type,Lifetime) always throws AmbiguousMatchException — any Inspector-configured service aborts the entire bootstrap**

| | |
|---|---|
| Konum | `Runtime/Modules/ModuleBuilder.cs:47` |
| Kategori | bug · modules-bootstrap |
| Etki | Startup only, but fatal: one throw per Inspector-configured ServiceEntry, and the first throw aborts the entire framework bootstrap (all phases 2-5 never run). |
| Test | NO COVERAGE. There is no test file for ModuleBuilder anywhere under Tests/ (`grep -rn ModuleBuilder Tests/` returns nothing), and no test constructs a ServiceEntry or calls ModuleConfig.Install (`grep -rn 'ServiceEntry|GetEnabledServices' Tests/` returns nothing). Tests/Runtime/Modules/ contains only ModuleRegistryTests.cs and ModulePropertyTests.cs, both of which exercise only the [Obsolete] legacy ModuleRegistry/IModuleInstaller path and never touch ModuleConfig, ModuleBuilder, SystemRunner, SerializableType, or GameBootstrapper. This is precisely the test gap that let a 100%-reproducible bootstrap-killing bug ship. |

```csharp
            var registerMethod = typeof(ContainerBuilder)
                .GetMethod(nameof(ContainerBuilder.Register), new[] { typeof(Lifetime) });
```

**Sorun:** ContainerBuilder declares exactly two `Register` overloads and BOTH have the identical parameter signature `(Lifetime)`: `Register<TInterface,TImplementation>(Lifetime)` (ContainerBuilder.cs:17) and `Register<T>(Lifetime)` (ContainerBuilder.cs:32). `Type.GetMethod(string, Type[])` does not filter by generic arity, so it finds two candidates whose parameter lists match exactly, DefaultBinder.SelectMethod cannot pick a most-specific overload, and it throws System.Reflection.AmbiguousMatchException. This happens unconditionally at the TOP of the method, before either the `interfaceType == implementationType` branch or the arity-2 lookup on line 63 (which is written correctly, using the `GetMethod(name, genericParameterCount, types)` overload). I verified this empirically on the exact runtime this project uses — Unity 6000.5.7f1 MonoBleedingEdge mcs/mono — with a faithful reproduction of the two overloads: `GetMethod(name, types)` THREW `System.Reflection.AmbiguousMatchException: Ambiguous match found.` while `GetMethod(name, 2, types)` correctly returned `ContainerBuilder Register[TInterface,TImplementation](Lifetime)`. The only production caller is ModuleConfig.Install (ModuleConfig.cs:122, `builder.Register(interfaceType, implType, service.Lifetime);`), which is the code path for every service configured through the Inspector `_services` list — a feature the README (line 66-67) and Documentation~/Modules.md ("Service Configuration / ServiceEntry Fields", lines 376-388) both document as supported.

**Senaryo:** Author a ModuleConfig ScriptableObject and add one entry to its `Services` list (Interface = IPlayerService, Implementation = PlayerService, Lifetime = Singleton) — the exact flow Documentation~/Modules.md documents. Press Play. GameBootstrapper.InitializeAsync reaches Phase 2, `BuildContainer()` -> `module.Install(moduleBuilder)` (GameBootstrapper.cs:237) -> `ModuleConfig.Install` -> `builder.Register(interfaceType, implType, ...)` -> AmbiguousMatchException. TryExecute (GameBootstrapper.cs:187) catches it, HandleInitializationError runs DisposeResources, and the framework never initializes: no World, no systems, `GameBootstrapper.Container/Services/World/Systems` all null. The whole game fails to boot, and the console message is the opaque "Container Building failed: Ambiguous match found." with no hint that a Services list entry is responsible.

**Düzeltme:** Replace the arity-agnostic lookup with the arity-aware overload that line 63 already uses, and resolve each branch separately:

```csharp
if (interfaceType == implementationType)
{
    var oneArg = typeof(ContainerBuilder).GetMethod(nameof(ContainerBuilder.Register), 1, new[] { typeof(Lifetime) })
        ?? throw new InvalidOperationException("ContainerBuilder.Register<T>(Lifetime) not found");
    oneArg.MakeGenericMethod(implementationType).Invoke(_containerBuilder, new object[] { lifetime });
}
else
{
    var twoArg = typeof(ContainerBuilder).GetMethod(nameof(ContainerBuilder.Register), 2, new[] { typeof(Lifetime) })
        ?? throw new InvalidOperationException("ContainerBuilder.Register<TI,TImpl>(Lifetime) not found");
    twoArg.MakeGenericMethod(interfaceType, implementationType).Invoke(_containerBuilder, new object[] { lifetime });
}
```

Also cache both MethodInfos in `static readonly` fields (the lookup currently runs per service registration), throw instead of silently doing nothing when the arity-2 lookup returns null (lines 65-70 currently return `this` having registered nothing), and unwrap `TargetInvocationException` so real registration errors (e.g. ContainerBuilder.ValidateType rejecting an abstract implementation type) are not hidden behind a reflection wrapper. Note: `MakeGenericMethod` itself is safe on IL2CPP here because both type parameters are constrained `where T : class`, so IL2CPP's reference-type generic sharing covers all instantiations — the AmbiguousMatchException is the sole blocker.


---

# HIGH (23)

## `ecb-missing-handler-silently-drops-commands` ⚑

**ComponentPlayback silently discards Add/Set/RemoveComponent commands when no handler is registered for the type hash; EntityCommandBuffer never auto-registers, and every test hides this by calling EnsureHandler in SetUp**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:358` |
| Kategori | api-hazard · ecs-jobs |
| Etki | Functional: 100% silent data loss for every component type whose handler was never registered. One extra ConcurrentDictionary lookup per recorded command if fixed at record time (~10-20 ns). |
| Test | ACTIVELY HIDDEN BY TESTS. EntityCommandBufferTests.cs:21-22, ParallelCommandBufferTests.cs:22 and JobSystemPerformanceTests.cs:96 all call `ComponentPlayback.EnsureHandler<T>()` before recording, so the no-handler path is never exercised. Because `_handlers` is a static ConcurrentDictionary, registration also leaks across test fixtures, making the omission even harder to notice. A test asserting that a fresh domain + record + playback without EnsureHandler applies the component would fail today. |

```csharp
        public static unsafe void AddComponent(EntityManager em, Entity entity, ulong typeHash, byte* data, int size)
        {
            if (_handlers.TryGetValue(typeHash, out var handler))
                handler.AddComponent(em, entity, data, size);
        }
```

**Sorun:** All three dispatch methods (lines 358-374) are `if (TryGetValue) ... ` with no `else` — an unregistered type hash is a silent no-op. Nothing in the recording path registers the handler: `EntityCommandBuffer.AddComponent<T>` (line 75) only calls `WriteCommand/WriteEntity/WriteTypeHash<T>/WriteComponent`, never `ComponentPlayback.EnsureHandler<T>()`. A repo-wide grep shows `EnsureHandler` is called from exactly four places, all of them test SetUp methods (EntityCommandBufferTests.cs:21-22, JobSystemPerformanceTests.cs:96, ParallelCommandBufferTests.cs:22) — zero runtime call sites. There is no documented requirement in Documentation~/ECS.md either.

**Senaryo:** A user follows Documentation~/ECS.md, records `ecb.AddComponent(entity, new Position{X=1})` and calls `ecb.Playback(entityManager)` without ever having called `ComponentPlayback.EnsureHandler<Position>()`. `CommandCount` reports the command was recorded, the stream contains its bytes, `Playback` walks over it, `TryGetValue` misses, and the component is never added. No exception, no log, no return value indicating failure. The bug manifests as "my ECB does nothing" with zero diagnostics, and the difference from a working setup is a call the docs never mention.

**Düzeltme:** Register at record time from the generic recording methods, which already have `T`: add `ComponentPlayback.EnsureHandler<T>();` at the top of `AddComponent<T>` (both overloads), `SetComponent<T>` and `RemoveComponent<T>`. Since those methods are already non-Burst-compatible (see the TypeHash finding), this costs nothing extra. Additionally make the three ComponentPlayback dispatchers throw (or `Debug.LogError` in development builds) on a handler miss instead of returning silently.

## `jobs-dangling-storage-pointers-across-frames` ⚑

**EntityJobs.Schedule captures raw SparseSet pointers that a concurrent structural change frees — no safety handle exists to catch it, and JobSystemBase keeps the job alive for a full frame**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityJobs.cs:28` |
| Kategori | concurrency · ecs-jobs |
| Etki | Silent heap corruption whenever a structural change on a job-captured component type races an in-flight job; window is one full frame per JobSystemBase-scheduled job (16 ms at 60 fps). |
| Test | NOT COVERED. Tests/Runtime/Performance/ParallelJobPerformanceTests.cs and JobSystemPerformanceTests.cs create all entities up front and call `.Complete()` synchronously before touching storage again, so the fire-and-forget-across-frames pattern that JobSystemBase actually implements is never tested. |

```csharp
                    EntityIds = set.GetDenseEntityPtr(),
                    Components1 = set.GetDataPtr(),
                    SparseIndex1 = set.GetSparsePtr(),
                    MaxSparse1 = set.SparseCapacity
```

**Sorun:** `GetDenseEntityPtr/GetDataPtr/GetSparsePtr` (SparseSet.cs:117-121) return `GetUnsafePtr()` on the backing NativeArrays. `SparseSet.EnsureSparseCapacity` (SparseSet.cs:200-208) and `EnsureDenseCapacity` (SparseSet.cs:217-225) allocate replacement arrays and `Dispose()` the old ones on any `Add` that outgrows capacity. Because the job holds bare pointers under `[NativeDisableUnsafePtrRestriction]` (ParallelComponentJob.cs:20-22) rather than NativeArray fields, no AtomicSafetyHandle is registered against the job, so Unity's JobsDebugger cannot detect the aliasing. `MaxSparse1` is likewise a snapshot of `SparseCapacity` taken at schedule time. `JobSystemBase.OnUpdate` (JobSystemBase.cs:50) stores the handle and does not complete it until the next frame (line 42), so the exposure window is a full frame during which every other system in the scheduler runs.

**Senaryo:** System A (a BurstSystem<MoveJob, Position, Velocity>) schedules at frame N and returns. System B, later in the same frame's phase list, calls `entityManager.AddComponent(e, new Position())` for an entity whose index exceeds the current sparse capacity, or for the 257th Position (default denseCapacity is 256, ComponentStorage.cs:22). `EnsureDenseCapacity` allocates a new `_data` array and disposes the old one at SparseSet.cs:224 while MoveJob workers are mid-flight writing through `Components1`. The writes land in freed native memory (heap corruption) and the surviving component values are silently taken from the stale array. Under `ENABLE_UNITY_COLLECTIONS_CHECKS` this produces no diagnostic because the job holds no safety handle.

**Düzeltme:** Either (a) hold `NativeArray<T>` fields in ComponentJobParallel instead of raw pointers so the safety system registers read/write dependencies and throws the standard "has been declared as [WriteOnly]/is still being used by a job" error, or (b) have EntityManager/ComponentStore track a per-storage `JobHandle` and call `Complete()` inside `AddComponent`/`RemoveComponent`/`DestroyEntity` before any capacity growth, and add an editor-only structural-change guard that throws when a storage is mutated while a job that captured its pointers is unfinished.

## `jobsystem-commandbuffer-struct-copy` ⚑

**JobSystemBase.CommandBuffer returns the ECB struct BY VALUE — CreateEntity() always returns 0 and every deferred command throws at playback**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/JobSystemBase.cs:21` |
| Kategori | bug · ecs-jobs |
| Etki | Functional break, not a perf cost: 100% of deferred-entity commands recorded through JobSystemBase.CommandBuffer fail; one exception + one Debug.LogException per frame per affected system. |
| Test | NOT COVERED. Repo-wide grep for `JobSystemBase` and `BurstSystem<` returns zero hits outside Runtime/ECS/Systems — there is no test that ever instantiates a JobSystemBase subclass or touches the CommandBuffer property. Tests/Runtime/ECS/Jobs/EntityCommandBufferTests.cs only exercises locally-declared `var ecb = new EntityCommandBuffer(...)` (a local struct variable, so mutations stick), which is exactly the pattern that hides this bug. |

```csharp
        protected EntityCommandBuffer CommandBuffer
        {
            get
            {
                if (!_commandBufferCreated)
                {
                    _commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                    _commandBufferCreated = true;
                }
                return _commandBuffer;
            }
        }
```

**Sorun:** EntityCommandBuffer is a struct (EntityCommandBuffer.cs:38 `public unsafe struct EntityCommandBuffer : IDisposable`). The property getter returns it by value, so every `CommandBuffer.X()` call site operates on a fresh temporary copy. The copy shares `_commandStream`/`_createdEntities` (NativeList is a handle wrapping `UnsafeList<T>*`, so `Add` mutates shared native memory) but does NOT share the plain int fields `_createEntityCount` (EntityCommandBuffer.cs:42) and the auto-property backing field of `CommandCount` (EntityCommandBuffer.cs:46). Those increments are written into the temporary and discarded.

**Senaryo:** A JobSystemBase subclass writes `int idx = CommandBuffer.CreateEntity(); CommandBuffer.AddComponent(idx, new Position());` inside OnSchedule. Each `CreateEntity()` runs on a fresh copy whose `_createEntityCount` is 0, so it returns 0 every time and the system field stays at 0. Next frame OnUpdate (line 46) calls `_commandBuffer.Playback(EntityManager)`; `_commandStream.Length != 0` so it does not early-return, but the loop at EntityCommandBuffer.cs:114 `for (int i = 0; i < _createEntityCount; i++)` runs zero times, leaving `_createdEntities.Length == 0`. The first deferred AddComponent then hits EntityCommandBuffer.cs:251-252 `if (index < 0 || index >= _createdEntities.Length) throw new IndexOutOfRangeException("Invalid deferred entity index")`. Result: zero entities created, an exception every frame (swallowed by SystemBase.Update's try/catch at SystemBase.cs:44-47), and `CommandCount` permanently reports 0. Handing out copies also makes the `_isCreated` double-dispose guard (EntityCommandBuffer.cs:151) useless: `CommandBuffer.Dispose()` frees the shared NativeLists while the system's own `_commandBuffer._isCreated` is still true.

**Düzeltme:** Return by reference: `protected ref EntityCommandBuffer CommandBuffer { get { EnsureCommandBuffer(); return ref _commandBuffer; } }` (C# 7 ref-returning property), or make EntityCommandBuffer a sealed class, or expose method wrappers on JobSystemBase (`protected int EcbCreateEntity() => _commandBuffer.CreateEntity();`) that operate on the field. Additionally move `_createEntityCount`/`CommandCount` into the shared native block (e.g. a `NativeReference<int>` or a header slot in `_commandStream`) so struct copies cannot desynchronise from the stream.

## `jobsystem-no-complete-on-dispose` ⚑

**JobSystemBase.OnDispose never calls _lastJobHandle.Complete(), so World.Dispose frees the SparseSet native arrays that in-flight jobs still hold raw pointers to**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/JobSystemBase.cs:35` |
| Kategori | concurrency · ecs-jobs |
| Etki | Crash/corruption window equal to the job's remaining execution time (0.1-2 ms for 100k entities) on every world teardown that races an in-flight job. |
| Test | NOT COVERED — no test constructs a JobSystemBase or BurstSystem at all, so no test ever disposes a world with a job in flight. |

```csharp
        protected override void OnInitialize() => OnCreate();
        protected override void OnDispose() => OnDestroy();
```

**Sorun:** `OnUpdate` schedules a job and stores the handle (line 50) without completing it — completion happens at the *start of the next* OnUpdate (line 42). So between frames a job is legitimately in flight. The disposal path does not complete it: `OnDispose` only forwards to the empty `OnDestroy()` virtual (line 38). `CompleteAllJobs()` (line 105) exists but is opt-in and nothing calls it. Meanwhile `World.Dispose()` (World.cs:124-125) runs `_scheduler.Dispose()` -> `SystemScheduler.Dispose()` (SystemScheduler.cs:69-70 `_allSystems[i].Dispose()`) and then immediately `_entities.Dispose()` -> `ComponentStore.Dispose()` -> `ComponentStorage<T>.Dispose()` -> `SparseSet<T>.Dispose()` which frees `_sparse`, `_dense` and `_data`. Because the job holds raw `[NativeDisableUnsafePtrRestriction]` pointers (ParallelComponentJob.cs:20-23) rather than NativeArrays, Unity's job safety system has no handle to check and cannot raise the usual "you must call JobHandle.Complete()" error.

**Senaryo:** A scene unload / `world.Dispose()` executes while a `BurstSystem<MoveJob, Position, Velocity>` job scheduled in the previous frame is still running on worker threads. `SparseSet<Position>.Dispose()` (SparseSet.cs:177-183) frees `_data` while `UserJob.Execute(entity, ref Components1[idx1], ...)` (ParallelComponentJob.cs:59) is writing through the stale pointer: use-after-free write into freed native memory, i.e. heap corruption or an editor/player crash that is non-deterministic and will not reproduce under the JobsDebugger.

**Düzeltme:** Override disposal in JobSystemBase: `protected override void OnDispose() { _lastJobHandle.Complete(); _lastJobHandle = default; if (_commandBufferCreated) { _commandBuffer.Dispose(); _commandBufferCreated = false; } OnDestroy(); }`. Independently, SystemScheduler.Dispose should complete all system dependencies before EntityManager.Dispose runs.

## `query-foreach-dangling-native-ptr-on-grow` ⚑

**ForEach hoists raw NativeArray pointers, then the callback can free them via AddComponent — use-after-free on native memory**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQuery.cs:21` |
| Kategori | bug · ecs-query |
| Etki | Per-entity memory corruption for every entity after the reallocation point; unbounded (writes go to freed Allocator.Persistent memory). Not a perf cost — a correctness/memory-safety cost. Triggered once per query invocation in which the callback adds a component of an iterated type. |
| Test | NOT COVERED. Tests/Runtime/ECS/ECSIterationSafetyTests.cs covers only DestroyEntity-during-ForEach with 3 entities (never grows a set). No test in the repo calls AddComponent from inside a ForEach callback (verified by grep over Tests/). Tests/Runtime/ECS/Query/*.cs never mutate structure during iteration. |

```csharp
            ref var sparseSet = ref _storage1.GetSparseSet();
            int count = sparseSet.Count;

            unsafe
            {
                int* entities = sparseSet.GetDenseEntityPtr();
                T1* data = sparseSet.GetDataPtr();

                for (int i = 0; i < count; i++)
                {
                    action(entities[i], ref data[i]);
                }
            }
```

**Sorun:** `entities` and `data` are raw pointers into `SparseSet<T1>._dense` / `._data`, hoisted once before the loop. `action` is an opaque managed delegate that is free to call `EntityManager.AddComponent<T1>` (EntityManager.cs:159-166 -> ComponentStorage.Add -> SparseSet.Add -> SparseSet.EnsureDenseCapacity, SparseSet.cs:211-226). EnsureDenseCapacity allocates NEW NativeArrays and calls `_dense.Dispose()` / `_data.Dispose()` on the old ones. The hoisted pointers then reference freed native memory, and the loop keeps reading `entities[i]` and handing out `ref data[i]` for the remainder of the iteration. There is no iteration guard, version counter, or structural-change check anywhere in the query layer. The same pattern exists in FilteredQuery.cs:74-75 (both pointers hoisted) and in every multi-component ForEach, where `entities` is hoisted (EntityQuery.cs:78, EntityQuery.cs:137/142/147, EntityQueryExtended.cs:51-54, 103, 156, 213, 273) even though the data pointers are re-fetched.

**Senaryo:** ComponentStorage default denseCapacity is 256 (ComponentStorage.cs:22, ComponentStore.cs defaults at 128). Create 256 entities with `Position`. Run `em.ForEach<Position>((int i, ref Position p) => { if (p.X == 0) { var e = em.CreateEntity(); em.AddComponent(e, new Position()); } });`. The 257th Add triggers EnsureDenseCapacity -> the old `_dense`/`_data` NativeArrays are Disposed. Iterations i=1..255 then read `entities[i]` from freed memory and write through `ref data[i]` into freed memory. With ENABLE_UNITY_COLLECTIONS_CHECKS off (release player) there is no diagnostic: the writes silently corrupt whatever the allocator handed the memory to next. The garbage read from `entities[i]` is then fed to `GetDenseIndex` (SparseSet.cs:122) which does `entityIndex < _sparse.Length ? _sparse[entityIndex] : -1` with no `entityIndex >= 0` guard, so a negative garbage value reads out of bounds on `_sparse` in the multi-component variants.

**Düzeltme:** Add a structural-change guard to SparseSet (a `_structuralVersion` incremented in Add/Remove/Clear/EnsureDenseCapacity) and, in every ForEach, capture it before the loop and re-check it after each `action(...)` under `#if UNITY_EDITOR || DEVELOPMENT_BUILD`, throwing `InvalidOperationException("structural change during query iteration; use EntityCommandBuffer")`. For release builds, re-fetch `entities`/`data` from the (live, by-ref) sparse set at the top of every iteration instead of hoisting, so a realloc cannot leave a stale pointer — or, better, make the documented contract explicit and route all structural changes through EntityCommandBuffer (Runtime/ECS/Jobs/EntityCommandBuffer.cs), which the repo already ships.

## `em-ensurecapacity-infinite-loop` ⚑

**EntityManager.EnsureCapacity hangs the main thread: overflow clamp is placed after the doubling loop and is unreachable**

| | |
|---|---|
| Konum | `Runtime/ECS/Core/EntityManager.cs:305` |
| Kategori | bug · ecs-storage |
| Etki | Non-terminating loop on the Unity main thread — full application/Editor hang, no allocation, no exception, no log line. Triggered once, at the first CreateEntity or the first RestoreState with a large index. |
| Test | No coverage. Tests/Runtime/ECS/Core/EntityManagerTests.cs only ever uses `new EntityManager()` (default 1024). EntityPropertyTests caps generated entity counts at 100. Nothing constructs `EntityManager(0)` and nothing calls RestoreState in any test. |

```csharp
            int newCapacity = _versions.Length;
            while (newCapacity < required)
                newCapacity *= 2;

            if (newCapacity < 0 || newCapacity > int.MaxValue / 2)
                newCapacity = int.MaxValue / 2;
```

**Sorun:** The guard on lines 309-310 runs only after the loop terminates, so it cannot prevent anything the loop does. Two distinct inputs make the loop non-terminating. (1) `_versions.Length == 0` seeds `newCapacity = 0`, and `0 *= 2` is 0 forever. (2) Once `newCapacity` reaches 2^30 and `required` is larger, `newCapacity *= 2` wraps unchecked to int.MinValue, the next iteration wraps to exactly 0, and the loop then spins on 0 forever. Prior audit finding unit-04 #6 flagged the overflow; the attempted fix added a post-loop clamp that is dead code, and the item appears in no status review as OPEN, FIXED, or PARTIAL.

**Senaryo:** Case 1: `var m = new EntityManager(0); m.CreateEntity();` -> `_versions.Length` is 0, `CreateEntity` calls `EnsureCapacity(2)`, `newCapacity` stays 0, and the Unity main thread hangs with no exception and no log. `EntityManager(int)` is public and unvalidated. Case 2: `RestoreState(nextEntityIndex: 1_200_000_000, ...)` at line 328 calls `EnsureCapacity(1_200_000_000)`; from the default 1024 the loop reaches 1_073_741_824, then wraps to -2147483648, then to 0, then spins. `Editor/Windows/TimeMachineWindow.cs:930` feeds `NextEntityIndex` straight from a serialized snapshot into this path, so a corrupted or hand-edited snapshot deadlocks the Editor.

**Düzeltme:** Do the growth in `long` and clamp inside the loop: `long cap = Math.Max(_versions.Length, 1); while (cap < required) cap *= 2; if (cap > MaxEntityCapacity) cap = MaxEntityCapacity; if (required > MaxEntityCapacity) throw new ArgumentOutOfRangeException(nameof(required)); int newCapacity = (int)cap;`. Separately, validate `initialCapacity > 0` in both constructors and validate `nextEntityIndex` in `RestoreState` before calling `EnsureCapacity`.

## `ecsbuilder-never-injects-systems` ⚑

**ECSBuilder.Build() never calls SystemBase.Inject — every system built through the builder has a null EntityManager**

| | |
|---|---|
| Konum | `Runtime/ECS/World/ECSBuilder.cs:58` |
| Kategori | bug · ecs-systems |
| Etki | Per-frame: system is a complete no-op plus one `Debug.LogException` (full stack-trace string formatting, typically 1-4 KB of garbage) per system per frame, at 60 fps, indefinitely. Functional impact is total — the builder's system-registration API cannot produce a working system. |
| Test | Zero. `WithSystem` has no call site anywhere in Runtime/, Tests/ or Editor/ (grep for `ECSBuilder` returns only `.Build()` / `.WithInitialEntityCapacity(128).Build()` in BridgeIntegrationTests.cs:30, MassEntityTests.cs:21, ParallelCommandBufferTests.cs:21, ECSBenchmarks.cs:17, BenchmarkRunner.cs:284/311/344, GameBootstrapper.cs:278). There is no test file for SystemBase, SystemScheduler, World or ECSBuilder at all. |

```csharp
            foreach (var (_, phase, factory) in _systemFactories)
            {
                var system = factory(world);
                scheduler.AddSystem(system, phase);
            }
```

**Sorun:** `SystemBase.EntityManager`, `EventBus` and `HandleRegistry` are `{ get; private set; }` properties (SystemBase.cs:19-21) settable only through `public void Inject(...)` (SystemBase.cs:23). A repo-wide grep for `.Inject(` shows exactly one call site on an ISystem: `Runtime/Modules/SystemRunner.cs:276  systemBase.Inject(_entityManager, _eventBus, _handleRegistry);`. `ECSBuilder.Build()` constructs each system via the factory and hands it straight to `scheduler.AddSystem` without injecting anything, so every `SystemBase` produced by the builder has `EntityManager == null` forever.

**Senaryo:** `var world = new ECSBuilder().WithSystem<MovementSystem>().Build(); world.Initialize();` where `MovementSystem : SystemBase<Position, Velocity>`. On the first `world.Update(dt)`, `SystemBase<T1,T2>.OnUpdate` (SystemBase.cs:192) executes `_cachedQuery = EntityManager.Query().Select<T1, T2>();` against a null `EntityManager` → NullReferenceException. `SystemBase.Update` catches it (line 44-47) and calls `Debug.LogException`, so the system silently does nothing and spams one full stack trace per frame forever. The same NRE hits a plain `SystemBase` whose `OnUpdate` calls the inherited `ForEach<T1>` (line 73) or `CreateEntity()` (line 136). If the system touches `EntityManager` from `OnInitialize`, the NRE escapes through `SystemScheduler.Initialize` (which has no try/catch) and aborts `World.Initialize()` for every remaining system.

**Düzeltme:** In `ECSBuilder.Build()`, after `var system = factory(world);` add `if (system is Strada.Core.ECS.Systems.SystemBase sb) sb.Inject(entities, bus, /* handleRegistry */ null);` — mirroring `SystemRunner.InjectSystem` (SystemRunner.cs:274-278). Better: add an `EntityHandleRegistry` to `ECSBuilder` and share one injection helper between `ECSBuilder` and `SystemRunner` so the two construction paths cannot diverge again.

## `hotreload-jsonutility-wipes-component-state`

**Hot reload silently zeroes every live ECS component: JsonUtility round-trip of non-[Serializable] unmanaged structs writes default(T) back into the world**

| | |
|---|---|
| Konum | `Editor/HotReload/EntityStatePreserver.cs:139` |
| Kategori | bug · editor-tools |
| Etki | Per CD_ asset save in Play Mode: O(entities × componentTypes) boxed reads + JSON strings on capture, then the same again on restore, with 100% silent data loss of all component values. At 5,000 entities × 8 component types that is 40,000 JSON strings allocated and 40,000 live components zeroed per Ctrl+S. |
| Test | NONE. Tests/Editor contains only Strada.Core.Editor.Tests.asmdef and its .meta — there is not a single editor test file in the repo. No test pins CaptureState/RestoreState behaviour, which is why the round-trip loss was never caught. |

```csharp
                            var componentValue = JsonUtility.FromJson(componentJson, componentType);
                            if (componentValue != null)
                            {
                                store.SetComponentBoxed(entityIndex, componentType, componentValue);
                            }
```

**Sorun:** CaptureState() at line 58 serializes each boxed component with `var json = JsonUtility.ToJson(componentValue);`. Unity's JsonUtility only serializes types its own serializer supports: plain structs must carry [Serializable], and it silently drops any field it cannot handle. I grepped the whole Runtime tree: `grep -rn "Serializable" Runtime` returns exactly two component-adjacent hits (Runtime/ECS/Entity.cs and Runtime/Modules/*), and every ECS component in the repo is declared as a bare `struct X : IComponent` with no attribute (ComponentStorage<T> is constrained `where T : unmanaged, IComponent`). For such a struct ToJson yields `{}` (or a partial object). RestoreState then feeds that JSON to `JsonUtility.FromJson(componentJson, componentType)`, which produces a *fully default-initialized* struct for every field the JSON did not contain, and unconditionally pushes it into live storage via SetComponentBoxed. There is no round-trip validation anywhere — no check that json != "{}", no field-count comparison, no equality check against the captured value. HotReloadManager.ProcessConfigChange (line 196/216) runs this capture/restore pair around every single config reload.

**Senaryo:** Designer is play-testing. HotReload is enabled (default: `EditorPrefs.GetBool(EnabledPrefKey, true)`). They tweak a value on any CD_*.asset and hit Ctrl+S. ConfigAssetModificationProcessor.OnWillSaveAssets queues the change; HotReloadManager.ProcessConfigChange calls EntityStatePreserver.CaptureState() (all 5,000 entities serialized to "{}"), notifies dependent services, then calls RestoreState(entityState), which loops every entity × every component and writes `default(T)` over it. Every Position, Velocity, Health in the running world resets to zero. Nothing is logged — failedCount stays 0 because no exception is thrown, so RestoreState even returns true and HotReloadManager logs "[HotReload] Successfully reloaded".

**Düzeltme:** Do not use JsonUtility for unmanaged component structs. Keep the boxed object graph directly (as TimeMachineWindow's WorldSnapshot already does via `Dictionary<int, Dictionary<Type, object>>`) instead of serializing to JSON — no domain reload occurs between CaptureState and RestoreState in ProcessConfigChange, so serialization buys nothing. If JSON must be kept for a future domain-reload path, gate it: after ToJson, verify the payload is non-trivial (`json != "{}"`) and that componentType carries [SerializableAttribute]; skip (and warn once per type) otherwise, rather than restoring a default value.

## `dashboard-editmode-full-assetdb-scan-per-ongui`

**StradaDashboardWindow does two project-wide AssetDatabase scans and loads every ScriptableObject in the project on every OnGUI pass in Edit Mode**

| | |
|---|---|
| Konum | `Editor/Windows/StradaDashboardWindow.cs:507` |
| Kategori | performance · editor-tools |
| Etki | 2 project-wide FindAssets queries + N LoadAssetAtPath calls (N = every ScriptableObject in the project) per OnGUI pass; 2 passes per input event. At N=3,000 and 30 events/s that is ~180,000 asset loads/second. |
| Test | NONE — no editor tests exist. |

```csharp
            var configAssets = StradaConfigDataManagerWindow.DiscoverConfigs();
```

**Sorun:** DrawEditModeContent() is called directly from OnGUI (line 287) with no caching and no dirty flag. Line 507 calls DiscoverConfigs(), whose body is `var guids = AssetDatabase.FindAssets("t:ScriptableObject");` (StradaConfigDataManagerWindow.cs:619) followed by `AssetDatabase.LoadAssetAtPath<ScriptableObject>(path)` for EVERY guid — forcing deserialization of every ScriptableObject in the project into memory — plus a `assetType.GetMethod("Validate", ...)` reflection lookup per CD_ asset. Line 532 then runs a second project-wide query, `var moduleGuids = AssetDatabase.FindAssets("t:ModuleConfig");`, with a LoadAssetAtPath per result. Note that StradaConfigDataManagerWindow itself caches this behind `_needsRefresh` (line 158-162) — the dashboard bypasses that guard entirely.

**Senaryo:** A project with 3,000 ScriptableObjects. The developer opens the Dashboard in Edit Mode and moves the mouse over it. IMGUI dispatches Layout + Repaint (2 OnGUI passes) per event, and MouseMove/Repaint events fire continuously. Each pass: 2 full AssetDatabase index queries plus 3,000 LoadAssetAtPath calls. That is ~6,000 asset loads per event, thousands per second, and every ScriptableObject in the project is pinned in memory. The window is visibly frozen and the whole editor stutters.

**Düzeltme:** Cache the discovery result in a field populated in OnEnable and on an explicit Refresh button / AssetPostprocessor callback, exactly as StradaConfigDataManagerWindow already does with `_needsRefresh`. Never call AssetDatabase.FindAssets or LoadAssetAtPath from OnGUI.

## `entity-inspector-quadratic-destroyed-entity-scan`

**StradaEntityInspectorWindow.DetectDestroyedEntities is O(N²) and allocates one List<int> per entity, twice per second**

| | |
|---|---|
| Konum | `Editor/Windows/StradaEntityInspectorWindow.cs:1152` |
| Kategori | performance · editor-tools |
| Etki | 5,000 entities: ~100 MB allocated and 25M comparisons per refresh, at 2 refreshes/s (default `_refreshInterval = 0.5f`). |
| Test | NONE — no editor tests exist. |

```csharp
            var destroyedEntities = _allEntityIds.Where(id => !EntityExists(id)).ToList();
```

**Sorun:** EntityExists delegates to WorldDataProvider.EntityExists, which is:

    var entityIds = World.Current.EntityManager.GetAllEntities();
    return entityIds.Contains(entityId);

(WorldDataProvider.cs:110-111). EntityManager.GetAllEntities (Runtime/ECS/Core/EntityManager.cs:238-247) is `var result = new List<int>(_entityCount); for (...) if (_active[i] == 1) result.Add(i); return result;` — it builds and returns a brand-new List<int> on every call. So each EntityExists call allocates a full copy of the entity-id list and then does an O(N) LINQ Contains scan over it. DetectDestroyedEntities calls it once per tracked entity. OnInspectorUpdate (line 137-143) fires this every `_refreshInterval` seconds, default 0.5f, whenever `_autoRefresh` is on (default true).

**Senaryo:** Play Mode with 5,000 entities and the Entity Inspector open with default settings. Every 0.5 s: 5,000 calls to GetAllEntities, each allocating a 5,000-element List<int> (~20 KB) = ~100 MB of garbage per refresh, plus 25M integer comparisons. At 2 refreshes/s that is ~200 MB/s of allocation and 50M comparisons/s purely to notice that nothing was destroyed. Note that EntityManager already exposes GetActiveEntitiesNonAlloc (EntityManager.cs:253) which this path ignores.

**Düzeltme:** Fetch the live id set once per refresh — `var live = new HashSet<int>(_worldDataProvider.GetEntityIds());` — and test membership against it, instead of calling EntityExists per entity. Add an O(1) `EntityExists(int)` to IWorldDataProvider backed by EntityManager's existing `_active` array rather than a list copy + LINQ Contains.

## `timemachine-lifecycle-markers-quadratic-per-repaint`

**TimeMachineWindow rebuilds 2 HashSets per snapshot pair on every repaint, and repaints unconditionally every editor tick**

| | |
|---|---|
| Konum | `Editor/Windows/TimeMachineWindow.cs:458` |
| Kategori | performance · editor-tools |
| Etki | Per repaint at 600 snapshots × 1,000 entities: 1,198 HashSet allocations + ~2.4M hash ops + 7.2 KB of arrays. Multiplied by the unconditional per-editor-tick Repaint() at line 140. |
| Test | NONE — no editor tests exist (Tests/Editor holds only an asmdef). |

```csharp
            for (int i = 1; i < count; i++)
            {
                var prevIndices = _snapshots[i - 1].ActiveIndices;
                var currIndices = _snapshots[i].ActiveIndices;

                if (prevIndices == null || currIndices == null) continue;

                var prevSet = new HashSet<int>(prevIndices);
                var currSet = new HashSet<int>(currIndices);
```

**Sorun:** DrawEntityLifecycleMarkers is called from DrawTimeline -> OnGUI with no caching and no dirty flag. It allocates two fresh HashSet<int> per adjacent snapshot pair and fills them from the full entity-index arrays, then does two linear membership scans — every single OnGUI pass. `_maxSnapshots` defaults to 600 (line 41). The cost is multiplied by Update() at line 140, which calls `Repaint();` unconditionally whenever `Application.isPlaying`, outside the `_isRecording` / `_isPlayingRecording` guards, so the window repaints at the full editor tick rate even when idle. DrawTimelineBar compounds this with `int[] entityCounts = new int[count]` (line 333) and `Vector3[] points = new Vector3[count]` (line 372) per repaint.

**Senaryo:** Developer records 600 frames of a 1,000-entity world, then leaves the Time Machine window open (still in Play Mode, not recording). Per repaint: 1,198 HashSet<int> allocations, ~1.2M hash inserts, plus 1.2M membership probes, plus a 600-int array and a 600-element Vector3 array (7.2 KB). Update() forces a Repaint every editor tick, so at ~60 ticks/s that is ~72,000 HashSets and ~144M hash operations per second. The editor becomes unusable and the GC thrashes continuously.

**Düzeltme:** Compute the created/destroyed flags once per snapshot at RecordSnapshot time (store two bools on WorldSnapshot, or reuse the existing ComputeDiff/_cachedDiff machinery) and have DrawEntityLifecycleMarkers only read them. Hoist `entityCounts`/`points` into reusable fields sized to _maxSnapshots. Move `Repaint();` at line 140 inside the `if (_isRecording && !_isReplaying)` / `if (_isPlayingRecording && _isReplaying)` blocks so an idle window does not repaint.

## `timemachine-snapshot-boxes-world-every-tick`

**TimeMachineWindow.RecordSnapshot boxes every component of every entity on every editor tick and retains 600 such snapshots**

| | |
|---|---|
| Konum | `Editor/Windows/TimeMachineWindow.cs:908` |
| Kategori | allocation · editor-tools |
| Etki | ~1.5M allocations/second while recording a 2,000-entity/6-component world; multi-GB retained at the 600-snapshot cap. |
| Test | NONE — no editor tests exist. |

```csharp
            foreach (var entityId in ActiveIndices)
            {
                var components = new Dictionary<Type, object>();
                foreach (var type in entityManager.Store.GetComponentTypes())
                {
                    if (entityManager.Store.HasComponent(entityId, type))
                    {
                        var value = entityManager.Store.GetComponentBoxed(entityId, type);
```

**Sorun:** WorldSnapshot.Capture is invoked from Update() -> RecordSnapshot() (line 122), i.e. once per editor tick while recording, with no frame-rate limiter. For each entity it allocates a Dictionary<Type, object> and calls ComponentStore.GetComponentBoxed for every present component type. GetComponentBoxed (Runtime/ECS/Storage/ComponentStorage.cs:197-213) does `storage.GetType().GetMethod("Get")` — an uncached MethodInfo lookup — followed by `method.Invoke(storage, new object[] { entityIndex })`, which allocates an object[1] argument array *and* boxes the returned unmanaged struct. All of it is retained: `_snapshots` grows to `_maxSnapshots` (600 by default) before the oldest is evicted.

**Senaryo:** Developer clicks Start Recording with 2,000 entities × 6 components. Per editor tick: 2,000 Dictionary allocations, 12,000 uncached GetMethod("Get") reflection lookups, 12,000 object[] arg arrays, and 12,000 boxed structs. At ~60 ticks/s that is ~1.5M allocations/second. After 600 ticks (10 s) the retained graph is 1.2M Dictionaries plus 7.2M boxed structs — multiple GB — and the editor OOMs or stalls in GC.

**Düzeltme:** Throttle RecordSnapshot to a fixed sample interval (e.g. reuse the `_lastPlaybackTime` pattern with an explicit capture interval field) rather than every Update. Cache the per-storage `Get` MethodInfo inside ComponentStore instead of resolving it per call, or add a non-reflective boxed accessor. Bound total retained memory rather than only snapshot count.

## `playerloop-shutdown-nukes-global-loop` ⚑

**PlayerLoop.Shutdown() resets Unity's ENTIRE global player loop to default and clears the shared static callback lists, permanently silencing ECSAdapter/PatternManager and any third-party loop insertions**

| | |
|---|---|
| Konum | `Runtime/Core/PlayerLoop.cs:52` |
| Kategori | api-hazard · modules-bootstrap |
| Etki | Not a per-frame cost — a permanent, silent loss of all PlayerLoop-driven ticking for the remainder of the process. |
| Test | NO COVERAGE. There is no test file for Runtime/Core/PlayerLoop.cs anywhere under Tests/. Nothing exercises the Initialize -> register -> Shutdown -> re-Initialize sequence, which is exactly the sequence that breaks. |

```csharp
            _updateCallbacks.Clear();
            _lateUpdateCallbacks.Clear();
            _fixedUpdateCallbacks.Clear();
            _initCallbacks.Clear();

            var defaultLoop = UnityEngine.LowLevel.PlayerLoop.GetDefaultPlayerLoop();
            UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(defaultLoop);
```

**Sorun:** `Shutdown()` does two globally destructive things instead of undoing only what `Initialize()` did. (a) `SetPlayerLoop(GetDefaultPlayerLoop())` discards the *current* loop wholesale, so every other package's player-loop insertion — UniTask's PlayerLoopHelper, Unity's own Addressables/DOTS/Input System subsystems added after startup, any user code — is silently removed. `Initialize()` was careful to do a surgical insert (`InsertStradaSystems` walks the current loop and prepends a single subsystem), but Shutdown is a sledgehammer. (b) It clears the four *shared* static callback lists, which are not Strada-bootstrapper-owned: `Runtime/Sync/ECSAdapter.cs:46-48` and `Runtime/Patterns/PatternManager.cs:104-106` both register into them. Both of those classes guard re-registration with `if (_registeredWithLoop) return;` and set the flag to true — so once Shutdown wipes the list, they believe they are still registered and will NEVER re-register, even if `PlayerLoop.Initialize()` runs again. The only caller of Shutdown is GameBootstrapper.cs:383.

**Senaryo:** Additive scene setup: Scene Boot holds the GameBootstrapper; Scene Gameplay holds a DontDestroyOnLoad ECSAdapter and a PatternManager, both of which have called RegisterWithPlayerLoop(). The player returns to the main menu and the app unloads Scene Boot (or the bootstrapper GameObject is destroyed for a soft restart). GameBootstrapper.OnDestroy -> Shutdown() -> PlayerLoop.Shutdown(): all four callback lists are cleared and the global loop is reset to Unity's default. The ECSAdapter and PatternManager objects are still alive and still have `_registeredWithLoop == true`, so their OnUpdate/OnLateUpdate/OnFixedUpdate are never invoked again for the rest of the process, and calling RegisterWithPlayerLoop() again is a no-op. There is no error, no warning — the ECS world and all pattern tickables just stop ticking. A second GameBootstrapper spun up afterwards re-inserts the Strada loop systems but cannot restore the lost callbacks.

**Düzeltme:** Make Shutdown the exact inverse of Initialize. Remove the callback-list clearing entirely (the lists are shared, not owned — registrants unregister themselves via UnregisterUpdate/UnregisterLateUpdate/UnregisterFixedUpdate). Replace the default-loop reset with a surgical removal: walk `GetCurrentPlayerLoop()`, and for each parent whose subSystemList[0].type is one of `StradaInitialization`/`StradaUpdate`/`StradaLateUpdate`/`StradaFixedUpdate`, rebuild that parent's array without that element, then SetPlayerLoop. Separately, add reference counting or an explicit ownership check so Shutdown is a no-op while any callbacks remain registered. Also note `PatternManager.UnregisterFromPlayerLoop` and `ECSAdapter.UnregisterFromPlayerLoop` call `UnregisterUpdate` on a list that Shutdown may already have cleared — List.Remove on a missing item is harmless, but the `_registeredWithLoop` bookkeeping is what breaks.

## `timer-callback-exception-wedges-frame-loop` ⚑

**A throwing timer callback permanently wedges TimerService.Update AND kills the ECS SystemRunner for the rest of the session**

| | |
|---|---|
| Konum | `Runtime/Services/TimerService.cs:81` |
| Kategori | bug · patterns-utils |
| Etki | Per-frame, permanent: one exception + full ECS update skipped every frame for the rest of the session (60/s at 60fps). Not transient — it never self-heals. |
| Test | No coverage. Tests/Runtime/Services/TimerServiceTests.cs has 9 tests, none of which uses a throwing callback. Tests/Runtime/Performance/TimerServicePerformanceTests.cs uses only `() => { }` and `() => count++`. |

```csharp
                timer.Callback?.Invoke();
```

**Sorun:** `Update` invokes user callbacks with no try/catch. If a callback throws, the exception propagates out of the for-loop before the timer's bookkeeping runs: `RemainingRepeats` is never decremented (lines 83-84) and `RemoveAt(i)` is never reached (line 88), so the timer stays in `_timers` with `RemainingTime <= 0` and re-fires — and re-throws — every subsequent frame. The exception also escapes `TimerService.Update`, and its only production caller is `GameBootstrapper.Update()` which is likewise unguarded:

    private void Update()
    {
        if (!_isInitialized) return;
        _timerService?.Update(Time.deltaTime);
        _systemRunner?.Update(Time.deltaTime);
    }

(GameBootstrapper.cs:131-136). Because line 134 throws, line 135 never executes.

**Senaryo:** `timerService.Every(1f, () => _target.DoThing())` where `_target` is destroyed. Frame N: callback throws NullReferenceException -> escapes Update -> `_systemRunner.Update(Time.deltaTime)` is skipped, so every ECS system stops running. Frame N+1: `timer.RemainingTime -= deltaTime` makes it more negative, `> 0` is false, callback invoked again, throws again. The entire ECS simulation is dead from frame N onward while Unity logs one exception per frame. Also, because iteration runs downward from `_timers.Count - 1`, every timer at an index below the faulting one is starved forever too.

**Düzeltme:** Wrap the invocation: `try { timer.Callback?.Invoke(); } catch (Exception ex) { UnityEngine.Debug.LogException(ex); }` — and, critically, keep going with the bookkeeping (decrement/RemoveAt) so a poison timer is retired rather than replayed. Consider auto-cancelling a timer whose callback throws. Independently, wrap the two calls in `GameBootstrapper.Update` so one subsystem cannot starve the other.

## `template-systemorder-editor-only-attribute`

**Every generated ECS System file references [SystemOrder], which only exists in the Editor-only assembly — generated runtime code never compiles**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/Pipeline/Steps/FileGenerationStep.cs:294` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only — but it is a guaranteed, first-use compile break on the tool's primary happy path (System template + module generator with ECS System selected). |
| Test | No coverage at all — Tests/Editor/ contains only Strada.Core.Editor.Tests.asmdef and zero .cs files. A single test asserting the generated System source compiles would have caught this. |

```csharp
                sb.AppendLine("    [SystemOrder(0)]");
```

**Sorun:** `SystemOrderAttribute` is declared in exactly one place: Editor/CodeGen/SystemRegistryGenerator.cs:206 `public class SystemOrderAttribute : Attribute` inside `namespace Strada.Core.Editor.CodeGen`, which lives in Strada.Core.Editor (`"includePlatforms": ["Editor"]`). Three separate code paths emit `[SystemOrder(...)]` into files that land in normal (runtime) Assets folders: (a) this line, whose WrapInNamespace usings are only `using Strada.Core.ECS;` and `using Strada.Core.ECS.Systems;` (line 291) — neither namespace contains the attribute; (b) Editor/ModuleGenerator/Utilities/TemplateProcessor.cs:225 `sb.AppendLine("    [SystemOrder(0)]");` with the same two usings (line 219-220); (c) Editor/Templates/StradaTemplates.cs:39 `sb.AppendLine($"    [SystemOrder({systemOrder})]");`, whose using block comes from Editor/Templates/UsingStatementGenerator.cs:46 `"Strada.Core.Editor.CodeGen"` — an Editor-only namespace that a runtime assembly cannot reference at all.

**Senaryo:** User opens Strada > Module Generator, ticks 'System' under ECS Components, and clicks Create Module. Assets/Modules/PlayerModule/Scripts/Systems/PlayerSystem.cs is written containing `[SystemOrder(0)]` with no resolvable attribute -> CS0246 'The type or namespace name SystemOrderAttribute could not be found'. Separately, Assets > Create > Strada > System writes a file whose first using block contains `using Strada.Core.Editor.CodeGen;` -> CS0234 'The type or namespace name Editor does not exist in the namespace Strada.Core' in Assembly-CSharp. In both cases the project stops compiling immediately after the tool reports success.

**Düzeltme:** Move SystemOrderAttribute out of Editor/CodeGen/SystemRegistryGenerator.cs into a runtime file under Runtime/ECS/Systems (namespace Strada.Core.ECS.Systems), keeping an Editor-side type-forward if needed. Then remove `"Strada.Core.Editor.CodeGen"` from UsingStatementGenerator.cs:46 and replace it with `"Strada.Core.ECS.Systems"`.

## `entityquerygen-not-shipped` ⚑

**EntityQueryGenerator is not in the shipped analyzer DLL — README's "9-16 source-generated" queries are undelivered**

| | |
|---|---|
| Konum | `SourceGenerationECS~/EntityQueryGenerator.cs:12` |
| Kategori | bug · sourcegen |
| Etki | Ships-or-not, not a runtime cost: the entire advertised 9-16 component query surface (8 arities x 3 emitted members = 24 public types/methods) is absent from every consumer build. |
| Test | No coverage. Tests/Runtime/ECS/Query contains EntityQueryTests.cs, FilteredQueryTests.cs, QueryPropertyTests.cs; none reference `GeneratedQueryExtensions` or any 9+ arity ForEach. A single compile-time test using a 9-component query would have caught this. |

```csharp
    public sealed class EntityQueryGenerator : IIncrementalGenerator
```

**Sorun:** The only analyzer binary the package ships is Editor/Analyzers/Strada.SourceGeneration.dll (labeled `RoslynAnalyzer` in its .meta). `strings` on that DLL yields `StradaFactoryGenerator`, `IIncrementalGenerator`, `GeneratorAttribute` — and zero occurrences of `EntityQuery` / `EntityQueryGenerator` / `GenerateAllCode` / `RegisterPostInitializationOutput`. The DLL is also a *different, older build* than the one in the repo: md5 of Editor/Analyzers/Strada.SourceGeneration.dll = 288b72775d6612365cb7b61e58a32851, while SourceGenerationECS~/bin/Release/netstandard2.0/Strada.SourceGeneration.dll = 40a8a67c6081c3b38a6036cc3e73252f, and only the latter contains `EntityQueryGenerator`/`GenerateAllCode`. The source folders end in `~`, so Unity ignores them entirely, and no .csproj exists anywhere in the repo (obj/ metadata points at an absolute path on another machine: /Users/okan/Desktop/Other Projects/Cases/plinko/...), so the generator cannot even be rebuilt from this repo. Consequently the hand-written arity ceiling is the real ceiling: `grep` finds `public static void ForEach<T1..T8>` at Runtime/ECS/Query/QueryBuilder.cs:221 as the maximum, and `GeneratedQueryExtensions` is referenced by zero files in Runtime/ or Tests/.

**Senaryo:** A user installs com.strada.core 2.0.0-alpha.1, reads README.md:42 ("Query System: ForEach<T1...T16>() - up to 8 hand-written, 9-16 source-generated") and README.md:144 ("// Query 9-16 components (source-generated)"), then writes `entityManager.ForEach<A,B,C,D,E,F,G,H,I>(...)`. Compilation fails with CS1501/CS0308 — no 9-argument overload exists and none is ever generated, because the generator that would emit it is not in any shipped binary.

**Düzeltme:** Add a checked-in Strada.SourceGeneration.csproj under a non-`~` folder, build both generators into one assembly, and replace Editor/Analyzers/Strada.SourceGeneration.dll with that build (plus a CI step that rebuilds and diffs it). Until then, remove the "9-16 source-generated" claims from README.md:42/46/144 and Documentation~/ECS.md.

## `markdirty-never-called-dirtyonly-dead` ⚑

**MarkDirty has zero callers, so EntityView.SyncBindings and the whole DirtyOnly sync mode silently sync nothing**

| | |
|---|---|
| Konum | `Runtime/Sync/EntityView.cs:72` |
| Kategori | bug · sync-reactive |
| Etki | Correctness, not cost — the intended optimisation path produces zero syncs instead of N. Startup-configurable, permanent for the session. |
| Test | NOT COVERED. No test in Tests/Runtime/Sync/ calls EntityView.SyncBindings, ViewSyncRunner, or ViewSyncSystem at all; only ViewRegistry.SyncAll is touched (Tests/Runtime/Performance/BridgePerformanceTests.cs:116) and its assertions are timing-only. Prior audit filed this as SYNC-05/LOW (unit-11) and 2026-05-22-low-status-review.md:78 left it OPEN with 'explicit doc gerekir' — documentation cannot fix a dead code path. |

```csharp
                if (_bindings[i].IsDirty)
                    _bindings[i].Sync();
```

**Sorun:** `ComponentBinding<T>` (the EntityView.cs one, used by every `BindComponent<T>()` call at lines 93-106) initialises `_dirty` to false, sets it to false again in Sync() (line 251), and sets it true only in `MarkDirty()` (line 264-267). `grep -rn 'MarkDirty' --include='*.cs' .` over the entire repository returns exactly one hit: the declaration at EntityView.cs:264. Nothing in the ECS layer, the mediator layer, or the sync runner ever marks a binding dirty. Therefore `IsDirty` is permanently false and the loop above never calls Sync() on anything. This makes `ViewSyncMode.DirtyOnly` (ViewSyncRunner.cs:39-42), `ViewSyncRunner.SyncDirty()` (line 58-61) and `ViewSyncSystem.OnUpdate` (ViewSyncSystem.cs:17-20) all silent no-ops — a user who selects the documented 'reactive, better performance' mode gets a view layer that never updates.

**Senaryo:** Developer sets `syncRunner.SyncMode = ViewSyncMode.DirtyOnly` after reading the tooltip at ViewSyncRunner.cs:14 ('DirtyOnly (reactive)') and the comment at line 40 ('Only sync views with dirty bindings (better performance)'). Every health bar, nameplate and position indicator freezes at its bind-time value, with no error, no warning, and no log line. Same outcome for anyone wiring ViewSyncSystem into their SystemScheduler, since it hard-codes SyncAll().

**Düzeltme:** Either (a) drive the flag: have EntityManager.SetComponent publish ComponentChanged<T> (the struct already exists in SyncEvents.cs:5-17, currently published by nobody) and have ViewRegistry route it to MarkDirty on the matching binding; or (b) delete the flag and the mode. If DirtyOnly must remain until (a) lands, make ViewSyncRunner log an error when DirtyOnly is selected, and make EntityView.SyncBindings fall through to Sync() so it is at least correct.

## `vp-double-despawn-duplicate` ⚑

**ViewPool.Despawn pushes the same view into the free stack twice on double-despawn; two entities then share one view**

| | |
|---|---|
| Konum | `Runtime/Sync/ViewPool.cs:133` |
| Kategori | bug · sync-reactive |
| Etki | Not a perf issue — silent state corruption. One duplicated pool entry per erroneous despawn, persisting for the lifetime of the pool. |
| Test | NOT COVERED. Tests/Runtime/Sync/ViewPoolTests.cs covers single Spawn/Despawn (line 80-94), respawn reuse (line 113-130) and DespawnAll (line 146-164) but never despawns the same view twice. Tests/Benchmarks/ViewPoolBenchmarks.cs:64-75 despawns then respawns in warmup, then despawns again — but each despawn is preceded by a spawn, so it misses it. |

```csharp
            if (_available.Count < _maxSize)
            {
                view.gameObject.SetActive(false);
                if (_poolRoot != null)
                    view.transform.SetParent(_poolRoot, false);
                _available.Push(view);
            }
```

**Sorun:** `_available.Push(view)` is unconditional — it is not gated on the `_entityToActiveIndex.TryGetValue` branch above it (line 113) that actually establishes the view was active. On a second Despawn of the same view, line 106 `var entity = view.Entity;` reads `default(Entity)` (Unbind at line 110 set `_entity = default` on the first call), so `GetEntityKey` yields 0, the TryGetValue lookup misses (EntityManager.Exists requires Index > 0, so key 0 is never a live entity), the active-list bookkeeping is correctly skipped — and then the view is pushed onto `_available` a second time anyway. Runtime/Pooling/ObjectPool.cs got a `HashSet<T> _inPool` guard for exactly this (prior finding POOL-03, marked FIXED); ViewPool never did.

**Senaryo:** `pool.Despawn(view); pool.Despawn(view);` (trivially reachable: a death handler despawns, then a cleanup pass despawns again). `_available` now contains the same TView twice. `var a = pool.Spawn(e1); var b = pool.Spawn(e2);` returns the SAME instance for a and b. Worse, the second `view.Bind(...)` at ViewPool.cs:82 hits `EntityView.Bind`'s `if (_bound) return;` (EntityView.cs:38) and silently does nothing, so `b.Entity` is e1 while `_registry` maps e2 -> b and `_active` contains the instance twice. One GameObject now renders and syncs for two entities; despawning e2 leaves e1's map entry dangling.

**Düzeltme:** Track pooled membership: add `private readonly HashSet<TView> _inPool = new(ReferenceEqualityComparer<TView>.Instance);` mirroring ObjectPool.cs:32, and in Despawn return early if `!_inPool.Add(view)` succeeded — i.e. `if (_inPool.Contains(view)) { StradaLog.LogWarning(...); return; }` before line 106, adding on push and removing on pop at line 65. Additionally move the `_available.Push` into the `if (_entityToActiveIndex.TryGetValue(...))` success branch so a view that was never active cannot be pooled.

## `vr-syncall-mutation-during-enumeration` ⚑

**ViewRegistry.SyncAll/ForceSyncAll enumerate the _allViews HashSet while sync handlers can spawn or despawn views**

| | |
|---|---|
| Konum | `Runtime/Sync/ViewRegistry.cs:129` |
| Kategori | bug · sync-reactive |
| Etki | Zero steady-state cost with the fix (the cache is only rebuilt on register/unregister). Without it: a hard InvalidOperationException every frame in which a sync handler spawns or despawns, aborting the rest of that frame's sync. |
| Test | NOT COVERED. Tests/Runtime/Performance/BridgePerformanceTests.cs:96-116 calls `_registry.SyncAll()` but with views whose bindings have no handlers that touch the registry. No test in Tests/Runtime/Sync/ registers or unregisters a view from inside a sync handler. |

```csharp
        public void SyncAll()
        {
            foreach (var view in _allViews)
            {
                view.SyncBindings();
            }
        }

        public void ForceSyncAll()
        {
            foreach (var view in _allViews)
            {
                view.ForceSyncBindings();
            }
        }
```

**Sorun:** `view.ForceSyncBindings()` -> `binding.Sync()` -> `OnChanged?.Invoke(current)` (EntityView.cs:254) runs arbitrary game code inside the live `foreach` over `_allViews`. Any handler that spawns a view calls `ViewPool.Spawn` -> `_registry.Register(view, entity)` -> `_allViews.Add(view)` (ViewRegistry.cs:55). `HashSet<T>.Add` increments the collection version in every BCL implementation, so the next `MoveNext()` throws `InvalidOperationException: Collection was modified`. The symmetric despawn case hits `_allViews.Remove(view)` at line 70. The class already builds a defensive snapshot list for exactly this (`AllViews` / `_allViewsCache`, lines 21-32) and neither sync method uses it. ReactiveProperty.Notify was hardened with ToArray for the identical hazard; this was not.

**Senaryo:** 200 enemy views registered. Enemy A's health binding fires OnChanged with health <= 0; the handler calls `poolManager.Spawn<ExplosionView>(entity)`. ViewPool.Spawn -> ViewRegistry.Register -> `_allViews.Add` bumps the HashSet version. Control returns to ForceSyncAll's foreach, `MoveNext()` throws InvalidOperationException, the exception propagates out of ViewSyncRunner.LateUpdate, and the remaining ~150 views are not synced this frame — every frame, for as long as anything dies during sync.

**Düzeltme:** Iterate the existing snapshot instead of the live set: `var views = AllViews; for (int i = 0; i < views.Count; i++) views[i].SyncBindings();`. `_cacheInvalid` is already maintained by Register/Unregister (lines 56, 72, 87), so the snapshot is rebuilt only when membership actually changes, not per frame.

## `fscheck-nonthrowing-runner`

**All 71 FsCheck property tests use the non-throwing default runner — a falsified property never fails the test**

| | |
|---|---|
| Konum | `Tests/Runtime/Generators/StradaArbitraries.cs:58` |
| Kategori | test-gap · tests-bench |
| Etki | 71 of 556 test methods (12.8% of the suite, including every DI/ECS/Sync invariant property) currently cannot fail. Zero runtime cost; pure test-integrity loss. |
| Test | This IS the coverage. No test asserts that a deliberately-falsified property causes a red test. |

```csharp
            return new Configuration { MaxNbOfTest = maxTest };
```

**Sorun:** `PropertyTestConfig.CreateConfig()` builds a bare `FsCheck.Configuration`. In FsCheck 2.16.6 (the DLL shipped at Tests/Runtime/Plugins/FsCheck.dll — `strings` reports version 2.16.6 and the type exports `get_QuickThrowOnFailure` / `get_VerboseThrowOnFailure` / `consoleRunner`), the parameterless `Configuration` inherits `Config.Quick`'s runner, which is `Runner.consoleRunner`: it prints "Falsifiable, after N tests" to stdout and returns normally. Only `Configuration.QuickThrowOnFailure` / `Configuration.VerboseThrowOnFailure` install `throwingRunner`. `grep -rn 'ThrowOnFailure' Tests/` returns ZERO hits. All 71 property tests (`grep -c 'property.Check(config)' == 71` across 9 files: ContainerPropertyTests 10, ReactivePropertyPropertyTests 12, ComponentPropertyTests 9, BindingPropertyTests 9, EntityPropertyTests 7, QueryPropertyTests 7, BusPropertyTests 7, AutoBindingPropertyTests 5, ModulePropertyTests 5) call `property.Check(config)` with this configuration.

**Senaryo:** Introduce a regression that breaks singleton identity — e.g. make `Container.Resolve<T>()` return a new instance every 50th call. `ContainerPropertyTests.SingletonIdentity_AllResolutionsReturnSameInstance` runs 100 FsCheck cases, the property returns `false`, FsCheck writes "Falsifiable, after 3 tests" to the Unity console, and the NUnit test reports PASS. 71 of the 556 test methods (12.8% of the suite) are structurally incapable of failing.

**Düzeltme:** Change line 58 to `return new Configuration { MaxNbOfTest = maxTest, Runner = Configuration.QuickThrowOnFailure.Runner };` (or return `Configuration.QuickThrowOnFailure` and set `MaxNbOfTest` on it). Then re-run the suite — expect real failures to surface for the first time.

## `gc-benchmarks-measure-retained-not-allocated`

**The two GC-allocation benchmarks force a full collection before sampling — they measure retained heap, not allocation, so they cannot detect any transient garbage**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/DIPerformanceTests.cs:413` |
| Kategori | test-gap · tests-bench |
| Etki | Backs two published claims (0 bytes singleton resolve, 0 bytes scoped resolve) with a measurement that reports 0 for arbitrarily large transient allocation. Per-resolve allocation on a hot path is exactly the metric a Unity framework is sold on. |
| Test | Benchmark_GCAllocation_Transient (DIPerformanceTests.cs:392) and Benchmark_GCAllocation_Singleton (DIPerformanceTests.cs:426) are the only GC tests; both are affected. |

```csharp
            long memAfter = GC.GetTotalMemory(true);
```

**Sorun:** `GC.GetTotalMemory(forceFullCollection: true)` runs a blocking full GC and then reports the *live* heap. In `Benchmark_GCAllocation_Transient` (lines 392-423) the measured loop creates 10,000 x 4 = 40,000 unrooted ServiceA/B/C/D instances; every one of them is reclaimed by the very GC that produces `memAfter`, so `allocated` is ~0 (often negative) no matter how much the resolve path allocated. The log at line 420 even prints "(Expected: ~96 bytes for 4 objects)" — a value this measurement structurally cannot produce. `Assert.Less(bytesPerOp, 200)` at line 422 and `Assert.Less(bytesPerOp, 1)` at line 455 are both unfalsifiable. These two tests are the only GC measurements in the entire repo: `grep -rn 'GetTotalMemory|Measure.Allocation|ProfilerRecorder|GC.Alloc' Tests/` returns exactly 4 GetTotalMemory call sites (DIPerformanceTests 408/413/441/446 and ECSPerformanceTests 488/497) and nothing else.

**Senaryo:** Add a `new object[parameters.Length]` boxing array to the singleton resolve fast path (100k allocations of 24+ bytes in the measured loop). `Benchmark_GCAllocation_Singleton` still reports ~0 bytes/op and PASSES its `Assert.Less(bytesPerOp, 1)`, because the arrays are dead by the time `GC.GetTotalMemory(true)` collects. The README claim "GC Allocation (Singleton resolve) 0 bytes" (README.md line 233) is therefore backed by a measurement that would report 0 bytes for a path allocating 10 KB per call.

**Düzeltme:** Replace with an allocation *recorder*: either Unity.PerformanceTesting's `Measure.Method(...).GC().Run()` (records GC.Alloc samples), or `var r = new ProfilerRecorder(ProfilerCategory.Memory, "GC Allocated In Frame")`, or at minimum `GC.GetAllocatedBytesForCurrentThread()` deltas (monotonic, counts collected garbage). Do NOT pass `forceFullCollection: true` around an allocation measurement.

## `single-sample-no-median`

**Every Stopwatch benchmark takes exactly ONE sample — no repetition, median, or outlier rejection; Documentation~/Benchmarks.md claims the opposite**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ECSPerformanceTests.cs:157` |
| Kategori | test-gap · tests-bench |
| Etki | All ~72 Stopwatch benchmarks, and therefore every headline number in README.md lines 205-235 and Documentation~/Benchmarks.md, rest on n=1. Timer resolution is NOT the problem (loops of 10k-100k with division give ample headroom over Stopwatch's ~40 ns resolution on Apple Silicon); sample count is. |
| Test | No test in the repo records or asserts on measurement variance. |

```csharp
            var sw = Stopwatch.StartNew();
            _entityManager.ForEach<Position>((int idx, ref Position p) =>
            {
                iterCount++;
                sum += p.X + p.Y + p.Z;
            });
            sw.Stop();
```

**Sorun:** The measured region is entered exactly once. There is no repeat loop, no median/min/trimmed-mean, no variance reporting, and no `GC.Collect()` before the window (only the two GC tests collect at all). This pattern is used by every Stopwatch benchmark in the repo — DIPerformanceTests (13 tests), ECSPerformanceTests (16), MessageBusPerformanceTests (14), MVCSPerformanceTests (9), BridgePerformanceTests (5), AsyncPerformanceTests (5), JobSystemPerformanceTests (4), ParallelJobPerformanceTests (4), RealisticSimulationTests (2), plus EventBusSubscribeBenchmarks/ComputedPropertyBenchmarks/ViewPoolBenchmarks. Documentation~/Benchmarks.md line 51 states "- Multiple runs averaged for stability" — that is false for every one of these. The 27 `[Test, Performance]` benchmarks that DO use `Measure.Method(...).MeasurementCount(n)` (and therefore get a real median) are the ones that do NOT produce any published number.

**Senaryo:** A single gen-0 collection or OS scheduling preemption inside the ~0.66 ms window of `Benchmark_Query_SingleComponent_100k` shifts the reported per-entity cost by tens of percent. The published "6.6ns/entity" (README.md:221) is one draw from an unmeasured distribution; nothing in the repo records the spread, so a genuine 30% regression is indistinguishable from run-to-run noise.

**Düzeltme:** Wrap each measured region in a repeat loop (>= 15 samples), discard the first 3, and report the median plus interquartile range — or simply port these to `Measure.Method(...).WarmupCount(n).MeasurementCount(n).Run()`, which the repo already uses correctly in PoolingPerformanceTests/ReactiveSystemPerformanceTests. Add `GC.Collect(); GC.WaitForPendingFinalizers();` immediately before each measurement window.

## `assert-thresholds-decoupled-from-published-numbers`

**Assertion thresholds are 1.5x-24x looser than the published numbers, so the suite cannot detect the regressions it exists to prevent**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ECSPerformanceTests.cs:173` |
| Kategori | test-gap · tests-bench |
| Etki | Every headline claim in README.md lines 205-235 is guarded by a threshold that tolerates a 6x-24x regression. The performance suite provides essentially no regression protection for the numbers it is cited to support. |
| Test | These ARE the guarding assertions; there is no separate baseline-comparison mechanism anywhere in Tests/. |

```csharp
            Assert.Less(usPerEntity, 0.1, "Single component query should be under 0.1μs per entity");
```

**Sorun:** Published value vs. the assertion that guards it: single-component query 6.6ns/entity (README:221) vs `< 0.1μs` = 100ns → 15x headroom (this line). Simple transient 0.11μs (README:207) vs `Assert.Less(usPerOp, 1.0)` (DIPerformanceTests:110) → 9x. Singleton lookup 61ns (README:210) vs `Assert.Less(usPerOp, 0.5)` = 500ns (DIPerformanceTests:210) → 8x. Scoped lookup 21ns (README:211) vs `Assert.Less(usPerOp, 0.5)` (DIPerformanceTests:320) → 24x. Simulation 1.62ms/frame (README:225) vs `Assert.Less(msPerFrame, 10)` (ECSPerformanceTests:286) → 6x. vs-manual 1.56x (README:213) vs `Assert.Less(overhead, 20)` (DIPerformanceTests:498) → 12x. Parallel 17x (README:226) vs `Assert.Greater(speedup, 1.5f)` (JobSystemPerformanceTests:90). Only GetComponent/SetComponent/HasComponent are within ~1.5x of their published figures.

**Senaryo:** A change that makes single-component query iteration 10x slower — say, replacing the dense-array walk with a per-entity `Dictionary<Type, ...>` lookup taking 66ns/entity — leaves `usPerEntity == 0.066` and the assertion at line 173 still passes. The README's advertised 6.6ns silently becomes 66ns with a green suite. Same for a 8x singleton-resolve regression and a 9x transient regression.

**Düzeltme:** Tie each assertion to the published number with a defensible margin (e.g. 2x): `Assert.Less(nsPerEntity, 15.0)` for the 6.6ns claim, `Assert.Less(usPerOp, 0.25)` for the 0.11μs claim, `Assert.Greater(speedup, 12.0)` for the 17x claim. Better: record baselines with Unity.PerformanceTesting SampleGroups and fail on regression against a checked-in baseline file.


---

# MEDIUM (139)

## `icommand-file-defines-no-documented-command-api`

**Runtime/Commands/ICommand.cs defines no ICommand; README and Messaging.md publish six command APIs that do not exist anywhere in the package**

| | |
|---|---|
| Konum | `Runtime/Commands/ICommand.cs:9` |
| Kategori | api-hazard · communication |
| Etki | Documentation/API only; no runtime cost. |
| Test | No test references ICommand, IAsyncCommand, IPooledCommand or ICommandHandler — the tests use the real surface (ISignalHandler in MessageBusTests.cs:359, IAsyncSignalHandler in AsyncEventBusTests.cs:299), which is exactly why the docs drifted without CI noticing. |

```csharp
    public interface ISignalHandler<in TSignal> where TSignal : struct
    {
        void Handle(TSignal signal);
    }
```

**Sorun:** The file named ICommand.cs contains only `ISignalHandler<in TSignal>` and `IAsyncSignalHandler<in TSignal>`; `Runtime/Commands/` holds no other source file. `grep -rn "interface ICommand|interface IAsyncCommand|interface IPooledCommand|interface ICommandHandler|IAsyncAwaitCommand" --include=*.cs .` over the whole repo returns zero matches, and `grep -rn "ICommand\b" Runtime/ Tests/` returns zero usages. Yet README.md lines 454-456 publish `void Execute(ICommand command);` / `void ExecuteAsync(IAsyncCommand command, Action onComplete = null);`, README.md line 447 publishes `void Unsubscribe<TEvent>(Action<TEvent> handler)` (removed in commit 556a3e9 "remove legacy Unsubscribe API"), README.md line 451 publishes `RegisterCommandHandler<TCommand>` (renamed to `RegisterSignalHandler`), Documentation~/Messaging.md lines 454-470 declare `ICommand`, `IAsyncCommand`, `IPooledCommand` and `ICommandHandler<TCommand>` as public interfaces, and Messaging.md line 481 states the legacy Unsubscribe methods "are marked [Obsolete] (warning-only) and remain functional" when they no longer exist at all.

**Senaryo:** A user follows the README MessageBus API reference and writes `public class SpawnCommand : ICommand { public void Execute() {...} }` then `bus.Execute(cmd)` — CS0246 (type or namespace 'ICommand' not found) and CS1061 (EventBus has no method 'Execute'). Following the documented `bus.Unsubscribe(handler)` migration table in Messaging.md produces CS1061 as well. For a v2.0.0-alpha package this is the primary API contract being wrong in six places.

**Düzeltme:** Rename Runtime/Commands/ICommand.cs to ISignalHandler.cs to match its contents, and update README.md lines 445-460 and Documentation~/Messaging.md lines 270-285 / 452-495 to the actual surface: `SubscriptionToken Subscribe<TEvent>(Action<TEvent>)`, `SubscriptionToken RegisterSignalHandler<TSignal>(...)`, `SubscriptionToken RegisterQueryHandler<TQuery,TResult>(...)`, `void RegisterAsyncSignalHandler<TSignal>(...)`, `Publish`/`Send`/`Query`/`SendAsync`/`QueryAsync`. Delete the ICommand/IAsyncCommand/IPooledCommand/ICommandHandler and legacy-Unsubscribe sections entirely.

## `eventbus-async-handlers-have-no-unregister-path`

**Async signal/query handlers return no SubscriptionToken and have no unregister API — permanent handler retention**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:326` |
| Kategori | api-hazard · communication |
| Etki | Unbounded retention: one closure + captured handler graph per registered async signal/query type, held for the process lifetime of the shared EventBus. Registration is startup-only, but the leaked object graph is not. |
| Test | Tests/Runtime/Communication/AsyncEventBusTests.cs registers async handlers 10 times and never attempts to remove one; the only removal test is Clear_RemovesAsyncSignalHandlers (line 247), which pins the coarse Clear() behaviour. No test asserts a token is returned. |

```csharp
        public void RegisterAsyncSignalHandler<TSignal>(IAsyncSignalHandler<TSignal> handler) where TSignal : struct
        {
            RegisterAsyncSignalHandler<TSignal>((signal, ct) => handler.HandleAsync(signal, ct));
        }
```

**Sorun:** `RegisterAsyncSignalHandler` (326, 331) and `RegisterAsyncQueryHandler` (357, 363) return `void`, and the interfaces declare them `void` (lines 41-42, 55-56). `grep -rn "Unregister" Runtime/ Tests/` finds no `UnregisterAsyncSignalHandler`/`UnregisterAsyncQueryHandler` anywhere in the repo. The only removal path is `Clear()`/`Dispose()`, which wipes every handler of every kind. Worse, the interface overload wraps the caller's handler in a **new closure** `(signal, ct) => handler.HandleAsync(signal, ct)`, so the caller does not even hold a reference to the delegate that was stored. The v2.0 token migration plan (SecurityReports/2026-05-22-major-api-plan-f8-f9.md line 165: "Same shape for Query, Event (already has Subscribe, just normalise), AsyncSignal, AsyncQuery.") explicitly scoped async into F8, and Documentation~/Messaging.md line 481 announces that "EventBus now returns a Strada.Core.SubscriptionToken from every Subscribe / RegisterSignalHandler / RegisterQueryHandler overload" — the async half was never implemented. Prior finding unit-09 #04 (MEDIUM) is therefore only partially closed.

**Senaryo:** A `SystemBase`/`MonoBehaviour` calls `bus.RegisterAsyncSignalHandler<LoadLevel>(this)` in OnEnable. On teardown it disposes its `BindingScope`, which releases every sync token — but the async slot still holds the closure capturing `this`. The object is rooted for the lifetime of the bus (created once in GameBootstrapper.cs:224 and shared through DI), never garbage-collected, and the next `SendAsync<LoadLevel>` invokes a destroyed object -> MissingReferenceException. The only mitigations are `bus.Clear()` (nukes unrelated subsystems) or re-registering a dummy handler for that exact closed generic type.

**Düzeltme:** Change both async register overloads to return `Strada.Core.SubscriptionToken` (matching lines 163-189 / 213-237), using the same `ReferenceEquals(arr[id], handler)` guard so a stale token cannot clear a replaced slot. For the interface overloads, hoist the wrapper delegate into a local so the token can compare against the delegate that was actually stored. Update `ISignalBus`/`IQueryBus` (lines 41-42, 55-56) accordingly.

## `query-ref-aliasing-differs-by-registration-overload`

**Query(ref TQuery) mutation semantics silently differ between the IQueryHandler and Func<> registration paths**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:501` |
| Kategori | api-hazard · communication |
| Etki | Correctness/API only; no allocation. The `ref`-to-by-value copy at line 501 also copies sizeof(TQuery) bytes per query, which for a large query struct is the cost the `ref` overload was supposed to avoid. |
| Test | Tests/Runtime/Communication/MessageBusTests.cs:98-107 `Query_ByRef_ReturnsResult` and :110-117 `Query_WithDelegateHandler_ReturnsResult` both only assert the return value; neither reads the query struct after the call, so no test pins the aliasing behaviour in either direction. |

```csharp
            public TResult Handle(ref TQuery query) => _handler(query);
```

**Sorun:** `IQueryBus.Query<TQuery,TResult>(ref TQuery query)` (line 50) forwards the caller's struct by reference straight into `IQueryHandler<TQuery,TResult>.Handle(ref TQuery)` (line 125), so a handler registered via `RegisterQueryHandler(IQueryHandler<,>)` can write through to the *caller's* local. But `RegisterQueryHandler(Func<TQuery,TResult>)` (line 239) wraps the delegate in `DelegateQueryHandler`, whose `Handle(ref TQuery query) => _handler(query)` passes a **copy**. Two public registration overloads for the same query type therefore have opposite aliasing semantics, and nothing in the signature or docs distinguishes them. Separately, `ISignalBus.Send<TSignal>(ref TSignal signal)` (line 35) advertises by-ref but line 104 invokes `Action<TSignal>` by value, so no signal handler can ever write back — the `ref` on Send is purely decorative while the `ref` on Query is load-bearing.

**Senaryo:** A pathfinding query `struct FindPathQuery : IQuery<bool> { public Vector3[] Buffer; public int Count; }` is answered by `class PathHandler : IQueryHandler<FindPathQuery,bool> { public bool Handle(ref FindPathQuery q) { q.Count = n; return true; } }` — the caller reads `query.Count` correctly. A developer later refactors the handler to a lambda: `bus.RegisterQueryHandler<FindPathQuery,bool>(q => { q.Count = n; return true; })`. It still compiles, still returns true, and `query.Count` is now silently always 0 — the mutation went into `DelegateQueryHandler`'s stack copy.

**Düzeltme:** Make the two paths agree. Either change `Func<TQuery,TResult>` to a by-ref delegate (`public delegate TResult QueryHandler<TQuery,TResult>(ref TQuery query);`) so `DelegateQueryHandler.Handle` forwards `ref query`, or document `IQueryHandler.Handle(ref TQuery)`'s `ref` as read-only-for-performance and defensively copy in `Query` so no handler can mutate the caller's struct. Also drop the `ref` from `Send<TSignal>(ref TSignal)` or route it through a by-ref delegate, since the current signature promises something `Action<TSignal>` cannot deliver.

## `signalsequence-async-entry-fire-and-forget`

**AsyncActionEntry.Execute is fire-and-forget, silently breaking sequence ordering and dropping the ValueTask on the success path**

| | |
|---|---|
| Konum | `Runtime/Communication/SignalSequence.cs:308` |
| Kategori | bug · communication |
| Etki | Ordering break on every `Execute()` of a sequence containing a `ThenAsync` entry. Allocation on the incomplete path: `AsTask()` materializes a Task plus `ContinueWith` allocates a continuation Task — 2 Task objects per async entry per Execute. |
| Test | Tests/Runtime/Communication/SignalSequenceTests.cs:294-309 `ThenAsync_ExecutesAsyncAction` awaits `ExecuteAsync()` only. No test calls the synchronous `Execute()` on a sequence containing a `ThenAsync` entry, so neither the ordering break nor the dropped ValueTask is covered. |

```csharp
                var task = _asyncAction.Invoke(CancellationToken.None);
                if (!task.IsCompleted)
                {
                    task.AsTask().ContinueWith(t =>
```

**Sorun:** Two distinct defects. (1) Ordering: `Execute` starts the async action and returns immediately, so every entry after a `ThenAsync(...)` runs *before* the async action completes. The XML doc on `Execute` (line 115-117) says "Executes all signals in the sequence synchronously", and the whole point of the type is ordered chaining — that contract is silently violated with no warning and no way for the caller to detect it. (2) ValueTask misuse: on the `task.IsCompleted && !task.IsFaulted` path the ValueTask is never consumed — neither `GetResult()` nor `AsTask()` is called. For a ValueTask backed by an `IValueTaskSource` (a pooled async method builder, or `ManualResetValueTaskSourceCore`, both common in Unity async libraries), never consuming the ValueTask leaves the source token un-advanced and the source object never recycled. Note the earlier audit's FIND-09-03 (`.AsTask().Wait()` deadlock) *was* fixed — the fix replaced a deadlock with a silent ordering break.

**Senaryo:** `seq.Then(() => uiState = Loading).ThenAsync(ct => assets.LoadAsync(ct)).Then(new LevelReady()); seq.Execute();` — `LevelReady` is sent while `LoadAsync` is still in flight, so downstream systems read half-loaded assets. Additionally, if `LoadAsync` returns a pooled `ValueTask` that completes synchronously, the value-task source is never returned to its pool, leaking one source object per Execute.

**Düzeltme:** Give the entry an explicit contract: either have `Execute` throw `InvalidOperationException("Sequence contains async entries; use ExecuteAsync")` (the sequence already knows at build time — set a `_hasAsyncEntries` flag in `ThenAsync`), or block deliberately only when the caller opts in. Independently, always consume the ValueTask: replace the `if (!task.IsCompleted)` branch structure with an unconditional `task.AsTask().ContinueWith(...)` (AsTask consumes it in all states), or call `task.GetAwaiter().GetResult()` on the completed-successfully path.

## `asyncscope-reinitializes`

**AsyncContainerScope.ResolveAsync re-runs InitializeAsync on every call, including on already-initialized cached scoped/singleton instances and on PreWarm'd instances**

| | |
|---|---|
| Konum | `Runtime/DI/AsyncContainerScope.cs:62` |
| Kategori | bug · di-core |
| Etki | Per ResolveAsync of any IAsyncInitializable: one full re-initialization. With the repo's own ExpensiveService fixture that is 50ms of Task.Delay per redundant call. |
| Test | NOT COVERED. AsyncContainerTests.cs:75-83 calls ResolveAsync exactly once and asserts Initialized == true. AsyncContainerTests.cs:120-138 (CreateScopeWithPreWarmAsync_*) uses the SYNC `scope.Resolve<T>()`, deliberately avoiding the double-init path. No test calls ResolveAsync twice or asserts an init call count. |

```csharp
            var instance = _innerScope.Resolve<T>();

            if (instance is IAsyncInitializable asyncInit)
            {
                await _initLock.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    await asyncInit.InitializeAsync(cancellation).ConfigureAwait(false);
                }
```

**Sorun:** There is no memo of which instances have already been initialized. `_innerScope.Resolve<T>()` returns the cached Scoped instance (ContainerScope.cs:86-88) or the cached parent Singleton (ContainerScope.cs:77-79), and InitializeAsync is invoked unconditionally on it every time. _initLock (a SemaphoreSlim) provides mutual exclusion but no idempotency. AsyncScopeBuilder.BuildAsync already initializes PreWarm'd types (AsyncScopeBuilder.cs:52-53), so the first ResolveAsync of a PreWarm'd type is guaranteed to double-initialize. The non-generic overload has the identical defect at AsyncContainerScope.cs:96-107.

**Senaryo:** await using var scope = await container.CreateAsyncScopeBuilder().PreWarm<ExpensiveService>().BuildAsync();
// AsyncScopeBuilder.cs:53 ran InitializeAsync -> Value = 42
var a = await scope.ResolveAsync<ExpensiveService>(); // InitializeAsync runs AGAIN (Task.Delay(50) in the test fixture)
var b = await scope.ResolveAsync<ExpensiveService>(); // and AGAIN — same instance, 3 initializations total
For a real IAsyncInitializable that opens a socket, subscribes to a message bus, or downloads a manifest, each redundant call re-opens/re-subscribes/re-downloads: duplicate event subscriptions, doubled network traffic, leaked connections. For a Singleton IAsyncInitializable resolved from N scopes, it initializes N times.

**Düzeltme:** Track initialized instances per scope (a HashSet with reference equality, or a ConditionalWeakTable<object, object>) under _initLock and skip InitializeAsync when already present. Also record PreWarm'd instances from AsyncScopeBuilder.BuildAsync into that set when constructing the AsyncContainerScope.

## `async-factory-not-cached-not-disposed`

**Async factory registrations are re-invoked on every ResolveAsync and their instances are never disposed by the scope**

| | |
|---|---|
| Konum | `Runtime/DI/AsyncContainerScope.cs:54` |
| Kategori | bug · di-core |
| Etki | One extra instance construction plus one leaked resource per repeat ResolveAsync of an async-registered type. |
| Test | NOT COVERED. AsyncContainerTests.cs:99-117 resolves the async-registered service exactly once and asserts factoryCalls == 1; a second ResolveAsync would make it 2. No test disposes a scope with async-registered instances and checks they were released. |

```csharp
                int asyncIndex = _typeIdToAsyncIndex[typeId];
                if (asyncIndex >= 0)
                {
                    var result = await _asyncFactories[asyncIndex](typeof(T), cancellation).ConfigureAwait(false);
                    return (T)result;
                }
```

**Sorun:** The async-factory branch returns the factory result directly with no caching array (contrast ContainerScope._scopedInstances) and no disposal registration. AsyncContainerScope.Dispose()/DisposeAsync() (lines 112-131) dispose only _innerScope and _initLock. So a service registered via AsyncScopeBuilder.RegisterAsync — an API whose whole purpose is scope construction — behaves as an untracked transient: N calls produce N instances, none of which the scope owns. The non-generic overload has the same shape at line 89-91.

**Senaryo:** await using var scope = await container.CreateAsyncScopeBuilder()
    .RegisterAsync<IDbSession>(async (c, ct) => await DbSession.OpenAsync(ct))
    .BuildAsync();
var s1 = await scope.ResolveAsync<IDbSession>();  // opens connection #1
var s2 = await scope.ResolveAsync<IDbSession>();  // opens connection #2 — s1 != s2
// scope disposal closes neither: both connections leak.

**Düzeltme:** Add an `object[] _asyncInstances` parallel to _asyncFactories, cache the awaited result under _initLock (double-checked), and dispose any IDisposable/IAsyncDisposable entries in Dispose()/DisposeAsync().

## `async-factory-gets-root-container`

**AsyncScopeBuilder passes the root container, not the inner scope, to async factories**

| | |
|---|---|
| Konum | `Runtime/DI/AsyncScopeBuilder.cs:75` |
| Kategori | bug · di-core |
| Etki | Per async factory invocation; correctness. |
| Test | NOT COVERED. AsyncContainerTests.cs:105-110 registers an async factory whose delegate never touches the container parameter, so the wiring is untested. |

```csharp
                factories[i] = (t, ct) => factory(_container, ct);
```

**Sorun:** BuildAsync has innerScope in hand (line 45) but wires the factory adapters to `_container`, the root. An async factory registered on a *scope* builder therefore cannot resolve any Scoped dependency: `c.Resolve<TScoped>()` inside the delegate hits Container.cs:328's throwing lambda. This mirrors the same defect in RegisterFactory (Container.cs:294) and is inconsistent with CreateDirectFactoryWrapper (Container.cs:374), which does forward the scope.

**Senaryo:** builder.Register<IRequestContext, RequestContext>(Lifetime.Scoped);
await container.CreateAsyncScopeBuilder()
    .RegisterAsync<IUploader>(async (c, ct) => new Uploader(c.Resolve<IRequestContext>(), await Api.ConnectAsync(ct)))
    .BuildAsync();
await scope.ResolveAsync<IUploader>(); // InvalidOperationException: "Cannot resolve scoped type from root container. Use CreateScope() first." — inside a scope.

**Düzeltme:** Capture innerScope and write `factories[i] = (t, ct) => factory(innerScope, ct);` (innerScope is an IContainerScope, which is an IContainer).

## `registerinstance-double-dispose`

**RegisterInstance<T>() of an IDisposable pushes the instance onto the disposal stack twice — Dispose() is called twice on it**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:284` |
| Kategori | bug · di-core |
| Etki | Once per RegisterInstance'd IDisposable per container lifetime; correctness (double Dispose), not throughput. |
| Test | NOT COVERED. Tests/Runtime/DI/ContainerDisposalTests.cs:41 does `builder.RegisterInstance(tracker)` but Tracker (line 11-14) is not IDisposable. Tests/Runtime/DI/ContainerTests.cs:261-286 register a non-disposable TestService instance. ContainerTests.cs:362 (Dispose_DisposesAllSingletons) uses Register<>, not RegisterInstance. No test asserts a single Dispose() call on a registered instance. |

```csharp
                if (reg.Instance != null)
                {
                    if (reg.Instance is IDisposable d)
                    {
                        lock (_lock) _disposalStack.Push(d);
                    }
                    rawFactory = _ => reg.Instance;
                }
```

**Sorun:** Registration.FromInstance hardcodes Lifetime.Singleton (Registration.cs:42), so an instance registration takes BOTH the reg.Instance branch at line 284 (which pushes the instance onto _disposalStack at build time) AND the Lifetime.Singleton wrapper at line 302, whose body pushes the very same object a second time on first resolve: `if (instance is IDisposable disposable) { lock (_lock) _disposalStack.Push(disposable); }` (lines 318-321). There is no de-duplication anywhere in _disposalStack.

**Senaryo:** var db = new SqlConnectionWrapper(); // IDisposable
builder.RegisterInstance<IDb>(db);
var c = builder.Build();   // push #1 at Container.cs:288
c.Resolve<IDb>();          // CAS installs db into _singletons, then push #2 at Container.cs:320
c.Dispose();               // pops db twice -> db.Dispose() runs twice
For a non-idempotent Dispose (ref-counted native handle, NativeArray wrapper, a pooled object returned to its pool, a socket) the second call corrupts state or throws; the throw is then swallowed by the catch at Container.cs:196 and only logged.

**Düzeltme:** In the Lifetime.Singleton wrapper (Container.cs:302-325), skip the disposal-stack push when the registration came from an instance — e.g. capture `bool ownsInstance = reg.Instance == null;` outside the lambda and guard line 318 with it. Alternatively pre-seed `_singletons[index] = reg.Instance` in the reg.Instance branch so the wrapper's slow path is never entered.

## `tryresolve-missing-disposed-check`

**Container.TryResolve<T>() never checks _disposed — it resurrects singletons on a disposed container and leaks them permanently**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:112` |
| Kategori | bug · di-core |
| Etki | Per post-dispose TryResolve call: one leaked service instance plus everything it transitively holds, retained for the process lifetime. |
| Test | NOT COVERED. ContainerTests.cs:375 asserts Resolve<T>() throws after disposal but there is no TryResolve-after-disposal test. ContainerThreadSafetyTests.cs:87 exercises TryResolve concurrently on a live container only. |

```csharp
        public bool TryResolve<T>(out T instance) where T : class
        {
            var typeId = TypeId<T>.Id;
            if (typeId <= _maxTypeId)
```

**Sorun:** Resolve<T>() (line 80) and Resolve(Type) (line 102) and CreateScope() (line 150) all start with `if (_disposed) ThrowDisposed();`. TryResolve<T> does not. After Dispose() has drained _disposalStack and nulled every _singletons[i] (lines 187-212), TryResolve<T> for a Singleton finds `_singletons[index] == null`, runs rawFactory, CAS-installs a brand-new instance into the disposed container, and pushes it onto _disposalStack at line 320. Dispose() cannot be re-entered (line 180 `if (_disposed) return;`), so that instance is never disposed.

**Senaryo:** container.Dispose();            // drains stack, nulls _singletons
container.TryResolve<IFileLogger>(out var log); // returns TRUE, constructs a NEW IFileLogger on a dead container, opens a file handle
// log is pushed to _disposalStack which will never be drained again -> file handle leaked for process lifetime, and callers now hold a service whose dependencies were already disposed.

**Düzeltme:** Add `if (_disposed) { instance = null; return false; }` as the first statement of TryResolve<T> (mirroring ContainerScope.TryResolve, ContainerScope.cs:107-111), and re-check `_disposed` under `_lock` inside the singleton wrapper at Container.cs:304 the way the tracked-transient wrapper already does at Container.cs:347.

## `resolvebyindex-no-disposed-check`

**Container.ResolveByIndex has no _disposed check, so a live ContainerScope keeps constructing singletons on a disposed container**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:215` |
| Kategori | bug · di-core |
| Etki | One leaked instance per distinct singleton type per post-dispose scope resolve; retained for process lifetime. |
| Test | NOT COVERED. ContainerScopeTests.cs:210 (Dispose_DoesNotDisposeParentSingletons) disposes the SCOPE, not the container. No test disposes the container while a scope is alive. |

```csharp
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal object ResolveByIndex(int index)
        {
            return _factories[index](this);
        }
```

**Sorun:** ContainerScope.ResolveByIndex falls through to `return _parent.ResolveByIndex(index);` (ContainerScope.cs:81) whenever the parent's singleton slot is null. Container.Dispose() nulls every _singletons[i] (Container.cs:211-212) but does not track or invalidate outstanding scopes. A scope that is still alive therefore sees null, calls into the disposed container, and re-creates the singleton — which is then pushed onto the already-drained _disposalStack (Container.cs:320) and never disposed. Every expression-compiled factory also reaches this method via IIndexResolver.ResolveByIndex (Container.cs:222), so entire object graphs get rebuilt on a dead container.

**Senaryo:** var scope = container.CreateScope();
container.Dispose();            // _singletons all nulled, stack drained
scope.Resolve<IAudioService>(); // ContainerScope.cs:77 Volatile.Read -> null -> _parent.ResolveByIndex(index) -> ctor runs, new AudioService leaks
No ObjectDisposedException is raised anywhere on this path; the caller silently gets a second, untracked singleton.

**Düzeltme:** Add `if (_disposed) ThrowDisposed();` to Container.ResolveByIndex, and have Container.Dispose() invalidate outstanding scopes (see finding container-does-not-dispose-scopes).

## `dispose-wipes-global-directfactory`

**Container.Dispose() wipes the process-global DirectFactory<T> statics, permanently disabling source-generated factories for every later container**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:208` |
| Kategori | api-hazard · di-core |
| Etki | Disposal cost is also non-trivial: MakeGenericType + GetMethod + Invoke per registered type (~2-10us each on Mono) => ~0.4-2ms of reflection per Dispose for a 200-registration container, on the main thread during scene teardown. The permanent loss of direct factories is the larger cost. |
| Test | NOT COVERED for cross-container contamination. Tests/Runtime/Performance/DIPerformanceTests.cs:68-82 manually calls DirectFactory<T>.Clear() for 15 types in teardown, which shows the maintainers already know this global state leaks across fixtures, but no test asserts that container A's disposal must not break container B. |

```csharp
            for (int i = 0; i < _registeredCount; i++)
                ClearFactory(_registeredTypes[i]);
```

**Sorun:** ClearFactory (line 434-435) is `typeof(DirectFactory<>).MakeGenericType(type).GetMethod(nameof(DirectFactory<object>.Clear)).Invoke(null, null);` — it nulls a static field on a process-global generic class (IStradaFactory.cs:17 `private static Func<IContainer, T> _delegate;`). The source generator installs those delegates exactly once per app run: SourceGenerationECS~/StradaFactoryGenerator.cs:280-291 emits `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)] Initialize()` guarded by `if (_initialized) return;`. So the first container disposal (scene teardown, a test's [TearDown], a soft restart) permanently erases the direct factories, and every container built afterwards silently falls back to CompileFactory/Expression.Compile. Disposing container A also corrupts the state that container B (built later) depends on — this is per-process global state mutated from an instance-scoped operation.

**Senaryo:** Level 1 builds container A over [AutoRegister] services (direct factories in use). Player returns to menu -> A.Dispose() -> DirectFactory<PlayerService>.Clear() etc. Level 2 builds container B: TryGetDirectFactory returns null for every type, so B runs Expression.Compile for the whole graph. On IL2CPP the "zero reflection" path the docs promise (Documentation~/DI.md:142,199) is gone for the rest of the session, silently.

**Düzeltme:** Do not clear global DirectFactory state from an instance Dispose(). Either drop lines 208-209 entirely, or move the clear to an explicit test-only/editor-only reset hook (the generator already emits `StradaGeneratedInitializer.Reset()` at StradaFactoryGenerator.cs:300-311 for exactly this purpose).

## `directfactory-register-public-override`

**DirectFactory<T>.Register is public and silently overrides explicitly registered implementations (prior DI-08 is NOT fixed)**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:296` |
| Kategori | security · di-core |
| Etki | Startup only (Build time), but changes which implementation every subsequent resolve returns. |
| Test | NOT COVERED. No test asserts that an explicit Register<TInterface,TImplementation>() wins over a pre-installed DirectFactory<TInterface>. |

```csharp
                else
                {
                    var directFactory = TryGetDirectFactory(kvp.Key);
                    rawFactory = directFactory ?? CompileFactory(reg.ImplementationType, registrations, typeIdMap);
                }
```

**Sorun:** The direct factory is consulted BEFORE the user's explicit `Register<TInterface, TImplementation>()` implementation type, and `DirectFactory<T>.Register` is public (IStradaFactory.cs:27-31: `public static void Register(Func<IContainer, T> factory)`), writable by any loaded assembly, with no container-scoped ownership. SecurityReports/2026-05-22-medium-status-review.md:118 recorded the DI-08 remediation as "DirectFactory<T>.Delegate public set'i private yapip Register() static method'u ile expose et" — that change was made (the field is now private), but the tampering vector was preserved verbatim as a public method. The class's own XML doc (IStradaFactory.cs:11-13) concedes "direct registration bypasses container lifetime tracking".

**Senaryo:** Any code in the process (a mod assembly, an asset-bundle-loaded DLL, a stray test helper) executes `DirectFactory<IAuthService>.Register(c => new SpoofedAuthService());` before builder.Build(). The application's explicit `builder.Register<IAuthService, RealAuthService>(Lifetime.Singleton)` is silently ignored — line 299's null-coalesce takes the tampered factory — and container.Resolve<IAuthService>() returns the attacker's object. No warning, no exception, no way for the app to detect it. The same shape occurs non-maliciously: a stale DirectFactory registration left over from a previous scene silently overrides new explicit registrations.

**Düzeltme:** Make DirectFactory<T>.Register `internal` and add `[assembly: InternalsVisibleTo]` for the generated assembly only, or move the direct-factory table off a static generic onto a per-container `Dictionary<Type, Delegate>` populated at Build() from an explicitly passed registry. At minimum invert the precedence at line 299 so an explicit ImplementationType registration wins over the ambient direct factory.

## `reflection-only-methods-strippable`

**CreateDirectFactoryWrapper and DirectFactory<T>.Clear are reachable only through GetMethod(nameof(...)) — no [Preserve], no link.xml, and no null check before dereference**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:362` |
| Kategori | aot-il2cpp · di-core |
| Etki | Startup-only, but it is a hard crash: container construction fails on the shipped configuration while working in the editor. |
| Test | NOT COVERED. No stripping/AOT test exists in Tests/. |

```csharp
        private Func<IIndexResolver, object> TryGetDirectFactory(Type serviceType)
        {
            var method = typeof(Container).GetMethod(nameof(CreateDirectFactoryWrapper), BindingFlags.NonPublic | BindingFlags.Static);
            var genericMethod = method.MakeGenericMethod(serviceType);
            return (Func<IIndexResolver, object>)genericMethod.Invoke(null, new object[] { this });
        }
```

**Sorun:** `nameof(CreateDirectFactoryWrapper)` compiles to a string literal — it emits no IL reference to the method. CreateDirectFactoryWrapper (line 369) is private static and is never called directly anywhere, so UnityLinker sees it as unreachable. The package contains no link.xml (verified: `find . -name link.xml` returns nothing) and no [Preserve] attribute anywhere in Runtime/. The same pattern appears at line 435 for DirectFactory<T>.Clear. Neither call site null-checks the MethodInfo, so a strip produces a NullReferenceException rather than a diagnosable failure.

**Senaryo:** Build for iOS/Android with Managed Stripping Level = Low or higher (routine for mobile size budgets). UnityLinker removes Container.CreateDirectFactoryWrapper. At runtime `builder.Build()` -> BuildFactories -> TryGetDirectFactory -> `method` is null -> `method.MakeGenericMethod(serviceType)` throws NullReferenceException. The app cannot construct its container at all, and the stack trace points at a reflection helper rather than at stripping. Identical failure in Container.Dispose() via ClearFactory when DirectFactory<T>.Clear is stripped (no generated code referencing it).

**Düzeltme:** Annotate CreateDirectFactoryWrapper and DirectFactory<T>.Clear with [UnityEngine.Scripting.Preserve], ship a link.xml preserving Strada.Core.DI.Container and Strada.Core.DI.DirectFactory`1, and guard both call sites with a null check that throws a descriptive InvalidOperationException naming the stripping cause.

## `stradalog-defeats-release-redaction`

**StradaLog.LogError unconditionally interpolates the full exception, defeating the #if release redaction two lines above it (prior finding still open)**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:196` |
| Kategori | security · di-core |
| Etki | Per swallowed disposal exception; also allocates a full ToString() of the exception on the disposal path in release builds. |
| Test | NOT COVERED. PRIOR FINDING NOT FIXED: SecurityReports/2026-05-22-medium-status-review.md:56 records this exact defect as PARTIAL — "Debug.LogError build-gated ({e.Message} prod), ama StradaLog.LogError($\"...: {e}\") her zaman full exception object'i interpolate ediyor". It is still present verbatim. |

```csharp
                    catch (Exception e)
                    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        UnityEngine.Debug.LogError($"Error disposing service: {e}");
#else
                        UnityEngine.Debug.LogError($"Error disposing service: {e.Message}");
#endif
                        StradaLog.LogError($"Error disposing service: {e}", LogModule.DI);
```

**Sorun:** The #if/#else pair deliberately reduces the release-build message to `e.Message`, then line 203 immediately logs `{e}` — the full exception with type name, stack trace, and inner-exception chain — unconditionally, in every build configuration. StradaLog (Runtime/Logging/StradaLog.cs) buffers entries in a static _logBuffer and raises OnLogAdded, so the un-redacted text is retained in memory and forwarded to any subscriber (crash reporters, in-game consoles). The redaction is therefore inert.

**Senaryo:** Release iOS build; a service's Dispose() throws on scene teardown. Debug.LogError prints only the message (as intended), but StradaLog.LogError captures `Strada.Game.Net.SessionTokenStore.Dispose() ... at /Users/<dev>/Projects/... :line 88` including internal class names and absolute source paths, which is then surfaced by the in-game debug console and any crash-report uploader.

**Düzeltme:** Wrap line 203 in the same conditional, or pass `e.Message` in release: move the interpolation inside the existing #if block so both sinks receive the same redaction level.

## `singleton-scoped-captive-dependency`

**A Singleton that depends on a Scoped service fails at first resolve with a misleading "use CreateScope() first" message; Build() performs no lifetime validation**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:328` |
| Kategori | api-hazard · di-core |
| Etki | Startup/first-resolve only; correctness and diagnosability, not throughput. |
| Test | NOT COVERED. ContainerScopeTests.cs:141-153 tests Scoped-depends-on-Singleton (the safe direction). No test covers Singleton-depends-on-Scoped. |

```csharp
                    _factories[index] = _ => throw new InvalidOperationException("Cannot resolve scoped type from root container. Use CreateScope() first.");
```

**Sorun:** BuildDependencyExpr (Container.cs:423-432) emits `resolver.ResolveByIndex(index)` for every ctor parameter without consulting the dependency's Lifetime. ContainerScope.ResolveByIndex routes an uncached Singleton through `_parent.ResolveByIndex(index)` (ContainerScope.cs:81), which invokes the factory with the *Container* as IIndexResolver (Container.cs:218). Any Scoped dependency of that singleton then hits the throwing lambda at line 328. ContainerBuilder.DetectCircularDependencies (ContainerBuilder.cs:87-133) never compares lifetimes, so nothing is caught at Build().

**Senaryo:** builder.Register<IStatsUploader, StatsUploader>(Lifetime.Singleton); // ctor takes IRequestContext
builder.Register<IRequestContext, RequestContext>(Lifetime.Scoped);
builder.Build();                       // succeeds, no diagnostic
using var scope = container.CreateScope();
scope.Resolve<IStatsUploader>();       // InvalidOperationException: "Cannot resolve scoped type from root container. Use CreateScope() first."
The developer DID create a scope; the message sends them down a wrong path, and the real defect (a captive dependency) is never named. Failure surfaces at runtime, potentially only on a code path reached late in a session.

**Düzeltme:** Validate lifetimes in ContainerBuilder.Build(): reject Singleton->Scoped edges with "Singleton 'X' cannot depend on Scoped 'Y' (captive dependency)". Separately, make the message at line 328 distinguish "root container" from "reached from a scope via a singleton".

## `registerfactory-ignores-scope`

**RegisterFactory delegates always receive the root container, never the resolving scope — scoped factory registrations cannot resolve scoped dependencies**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:292` |
| Kategori | bug · di-core |
| Etki | Per resolve of any factory-registered service from a scope; correctness. |
| Test | NOT COVERED. ContainerTests.cs:221-257 tests RegisterFactory only on the root container. No test registers a factory with Lifetime.Scoped or resolves a factory registration through a scope. |

```csharp
                else if (reg.Factory != null)
                {
                    rawFactory = _ => reg.Factory(this);
                }
```

**Sorun:** The lambda discards its IIndexResolver parameter and closes over `this` (the Container). For Lifetime.Scoped this rawFactory is stored in _scopedFactories (line 329) and later invoked by ContainerScope with the scope as resolver (ContainerScope.cs:90 `_scopedFactories[index](this)`), but the resolver argument is thrown away and the user's `Func<IContainer, T>` still receives the root container. The direct-factory path is inconsistent with this and does the right thing — CreateDirectFactoryWrapper (Container.cs:374) writes `factory(resolver is IContainer c ? c : container)`, honouring the scope.

**Senaryo:** builder.Register<IRequestContext, RequestContext>(Lifetime.Scoped);
builder.RegisterFactory<IHandler>(c => new Handler(c.Resolve<IRequestContext>()), Lifetime.Scoped);
using var scope = container.CreateScope();
scope.Resolve<IHandler>(); // c is the ROOT container -> c.Resolve<IRequestContext>() hits the Container.cs:328 throwing lambda -> InvalidOperationException("Cannot resolve scoped type from root container")
A Transient factory registration has the milder version of the same bug: c.Resolve<IScopedDep>() also throws, and any transient sub-graph resolved through c bypasses the scope entirely.

**Düzeltme:** Mirror CreateDirectFactoryWrapper: `rawFactory = resolver => reg.Factory(resolver as IContainer ?? this);` so factory delegates see the scope when invoked from one.

## `lock-held-across-user-code`

**ResolveByType holds _lock across arbitrary user constructors and Dispose() holds it across arbitrary user Dispose() — serialization plus a deadlock window; contradicts the "lock-free singleton resolution" doc**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:233` |
| Kategori | concurrency · di-core |
| Etki | Per Resolve(Type)/TryResolve call on the cached singleton path: one uncontended monitor acquire/release plus full cross-thread serialization; deadlock is unbounded. |
| Test | PARTIAL/MISLEADING. ContainerThreadSafetyTests.cs:87 (TryResolve_ConcurrentAccess_DoesNotThrow) passes precisely because the global lock serializes everything — it pins the slow behaviour rather than exposing it. No test resolves concurrently with Dispose(). |

```csharp
                    var lifetime = _lifetimes[index];
                    if (lifetime == Lifetime.Singleton || lifetime == Lifetime.Scoped)
                    {
                        lock (_lock)
                        {
                            return _factories[index](this);
                        }
                    }
                    return _factories[index](this);
```

**Sorun:** Container.cs:20-21 documents "Singleton resolution uses lock-free patterns with Interlocked.CompareExchange for optimal performance". That is true only for the generic Resolve<T>() (line 78-90). The non-generic Resolve(Type) path (this quote) and TryResolve<T> (lines 121-127) take the global _lock even for an already-cached singleton, and hold it while the factory runs user constructor code. Container.Dispose() (lines 182-206) holds the same _lock while calling user Dispose() methods. Two threads can therefore deadlock, and every Resolve(Type) is globally serialized.

**Senaryo:** Deadlock: thread T1 calls container.Resolve(typeof(IAssetService)); the AssetService ctor blocks on a job/handle completed by T2. T2 calls container.Dispose(), which blocks on `lock (_lock)` at line 182. Neither proceeds. Performance: on a worker-thread system that resolves through the non-generic API, every cached-singleton lookup pays an uncontended Monitor.Enter/Exit (~20-25ns on ARM64 Mono, i.e. ~40% on top of the documented 61ns) and all such lookups serialize across threads.

**Düzeltme:** Do not hold _lock across factory invocation — the singleton wrapper at line 304 is already CAS-safe and needs no outer lock. Make ResolveByType and TryResolve use the same lock-free path as Resolve<T>(). In Dispose(), snapshot the stack into a local array under the lock and dispose outside it.

## `tryresolve-throws-for-scoped`

**Container.TryResolve<T>() throws InvalidOperationException for Scoped registrations, violating the documented try-pattern contract**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:119` |
| Kategori | api-hazard · di-core |
| Etki | Per call; correctness of the public API contract. |
| Test | NOT COVERED. ContainerTests.cs:290-312 covers TryResolve for a registered Singleton and an unregistered type only. ContainerScopeTests.cs:168 covers TryResolve from a scope. No test calls TryResolve for a Scoped type on the root container. |

```csharp
                {
                    var lifetime = _lifetimes[index];
                    if (lifetime == Lifetime.Singleton || lifetime == Lifetime.Scoped)
                    {
                        lock (_lock)
                        {
                            instance = (T)_factories[index](this);
                        }
                    }
```

**Sorun:** For Lifetime.Scoped, _factories[index] is the throwing lambda installed at Container.cs:328. TryResolve therefore propagates InvalidOperationException instead of returning false, contradicting its own XML doc at Container.cs:107 ("Attempts to resolve ... without throwing an exception") and the IContainer contract (IContainer.cs:9). The tracked-transient wrapper adds a second violation: it throws ObjectDisposedException from line 347 after a Dispose. IsRegistered<T>() (line 160) meanwhile returns true for the same Scoped type, so the natural guard pattern `if (c.IsRegistered<T>()) c.TryResolve<T>(out var x)` does not protect the caller.

**Senaryo:** builder.Register<IRequestContext, RequestContext>(Lifetime.Scoped);
var container = builder.Build();
if (container.TryResolve<IRequestContext>(out var ctx)) { ... }  // throws InvalidOperationException instead of returning false
Callers written against the try-pattern have no try/catch and the exception escapes to the frame loop.

**Düzeltme:** In TryResolve<T>, return false when `_lifetimes[index] == Lifetime.Scoped` (and when _disposed), before invoking the factory.

## `no-runtime-cycle-guard`

**Factory and instance registrations are excluded from cycle detection and there is no runtime resolution guard — a factory cycle is an uncatchable StackOverflowException**

| | |
|---|---|
| Konum | `Runtime/DI/ContainerBuilder.cs:96` |
| Kategori | bug · di-core |
| Etki | Process termination, no diagnostic. A DEVELOPMENT_BUILD-only guard costs one array push/pop per resolve in dev builds and zero in release. |
| Test | NOT COVERED. CircularDependencyTests.cs has exactly one test (lines 23-31) covering the ctor-only, type-registration-only case. PRIOR FINDING STILL OPEN: SecurityReports/2026-05-22-low-status-review.md:63 lists DI-12 as unresolved ("runtime crash riski; ek graph traversal gerekir"). |

```csharp
                if (registration.Factory != null || registration.Instance != null)
                    continue;
```

**Sorun:** DetectCircularDependencies skips factory/instance registrations, and DetectCycle likewise refuses to recurse into them (ContainerBuilder.cs:123-125 requires `depReg.Factory == null && depReg.Instance == null`). Nothing in Container adds a runtime resolution-stack guard, so a cycle introduced through a factory delegate recurses until the stack is exhausted. In .NET/Mono StackOverflowException cannot be caught — the process is killed immediately, with no Unity log entry and no crash handler.

**Senaryo:** builder.RegisterFactory<IA>(c => new A(c.Resolve<IB>()));
builder.Register<IB, B>();   // B's ctor takes IA
builder.Build();             // passes: IA is skipped at ContainerBuilder.cs:96, IB's dependency IA is skipped at line 123
container.Resolve<IA>();     // factory -> Resolve<IB> -> compiled expr -> ResolveByIndex(IA) -> factory -> ... -> stack overflow, process terminated with no diagnostic
The same holds for the property/method-injection cycles handled by InjectionProcessor, which DetectCycle never inspects (it reads constructor parameters only, line 116-117).

**Düzeltme:** Add a [ThreadStatic] resolution stack in Container (push the index in ResolveByIndex/ResolveByType, throw InvalidOperationException with the full chain if the index is already present, pop in a finally). Gate it behind UNITY_EDITOR || DEVELOPMENT_BUILD if the per-resolve cost matters for the 61ns claim.

## `scope-resolve-dispose-race`

**ContainerScope resolve path never takes _disposeLock — an instance created concurrently with Dispose() is stored in the disposed scope and never disposed (prior DI-03 reported FIXED, race is intact)**

| | |
|---|---|
| Konum | `Runtime/DI/ContainerScope.cs:84` |
| Kategori | concurrency · di-core |
| Etki | Per concurrent resolve/dispose pair: one leaked IDisposable plus a use-after-dispose hand-off. |
| Test | NOT COVERED, AND PRIOR FINDING MISREPORTED. SecurityReports/2026-05-22-status-review.md:33 lists DI-03 as fixed: "ContainerScope.cs:17 volatile _disposed, Dispose() (146-161) double-check pattern + Volatile.Read(ref _scopedInstances[i]) her bir instance icin". Those changes are present but address visibility only, not the TOCTOU. ContainerScopeTests.cs has no concurrency test; ContainerThreadSafetyTests.cs never touches scopes. |

```csharp
            if (lifetime == Lifetime.Scoped)
            {
                var existing = Volatile.Read(ref _scopedInstances[index]);
                if (existing != null)
                    return existing;

                var instance = _scopedFactories[index](this);

                var prev = Interlocked.CompareExchange(ref _scopedInstances[index], instance, null);
```

**Sorun:** Dispose() (lines 146-165) takes _disposeLock, sets the volatile _disposed, and walks _scopedInstances nulling each slot. ResolveById checks _disposed at line 57 but never acquires _disposeLock, and ResolveByIndex checks nothing at all. Making _disposed volatile fixed only the visibility half of DI-03; the time-of-check/time-of-use window between line 57 and the CAS at line 92 is unchanged, so a resolve that started before the flag was set can CAS a fresh instance into a slot the disposer has already passed.

**Senaryo:** Thread A: scope.Resolve<IDbSession>() passes `if (_disposed)` (false) and enters the ctor.
Thread B: scope.Dispose() -> takes _disposeLock, sets _disposed, walks all slots (index i is currently null), nulls them, returns.
Thread A: CompareExchange(ref _scopedInstances[i], session, null) succeeds against the freshly-nulled slot and returns the session to its caller.
Result: an IDisposable DB session lives in a disposed scope, is never disposed (the drain already ran and cannot re-run), and its caller uses a session belonging to a torn-down unit of work. Symmetrically, thread A can be handed an instance that thread B disposed one instruction earlier.

**Düzeltme:** Take _disposeLock around the Scoped creation branch (lines 84-100), or use a sentinel: after setting _disposed, CAS a poison object into each slot so a late CAS fails and the resolver can dispose its own instance and throw ObjectDisposedException.

## `build-benchmark-registers-one-type`

**Benchmark_ContainerBuild_100Registrations actually builds a 2-registration container — the README "Container Build (100 types) 0.05ms" figure is not measuring 100 types**

| | |
|---|---|
| Konum | `Tests/Runtime/DI/ContainerPerformanceTests.cs:144` |
| Kategori | test-gap · di-core |
| Etki | Startup-only, but the published number understates real build cost by roughly the registration count (50x for a 100-type container). |
| Test | THIS IS THE TEST GAP. Tests/Runtime/DI/ContainerPerformanceTests.cs:138-156 is the only build benchmark and it is invalid as written. |

```csharp
                for (int i = 0; i < 100; i++)
                {
                    builder.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                }
```

**Sorun:** ContainerBuilder.Register<TInterface, TImplementation> stores into a Dictionary via the indexer — `_registrations[typeof(TInterface)] = Registration.FromType(` (ContainerBuilder.cs:22) — so 100 identical calls produce ONE entry. Build() then adds IContainer via autoRegisterSelf (ContainerBuilder.cs:77), giving 2 registrations total. The measured work is 1 Expression.Compile + 1 TryGetDirectFactory, not 100. README.md:212 reports this as "Container Build (100 types) | 0.05ms | ~0.5us per registration" and Documentation~/DI.md repeats the perf framing; the real per-registration cost is dominated by Expression.Lambda(...).Compile() (Container.cs:384/394) plus a MakeGenericMethod+Invoke per type (Container.cs:364-366), which is orders of magnitude above 0.5us.

**Senaryo:** A developer sizes their startup budget from the published "~0.5us per registration" and registers 300 real services. Actual Build() cost is 300 x (Expression.Compile + reflection Invoke), which is tens to hundreds of milliseconds of main-thread hitch at app start — a regression the benchmark suite cannot detect because it never builds more than 2 registrations.

**Düzeltme:** Generate 100 distinct service types (e.g. a generic `Svc<T0..T99>` marker set or 100 declared classes) so the loop produces 100 dictionary entries, then re-measure and correct README.md:212 and the Documentation~/DI.md table.

## `service-attribute-inherited-by-every-subclass`

**[Service] is Inherited=true and is read with inherit:true, so every subclass silently auto-registers against the base's InterfaceType**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:175` |
| Kategori | bug · di-injection |
| Etki | Correctness/determinism. Startup-only in cost terms, but the wrong implementation is then resolved for the whole session. |
| Test | Not covered. `Tests/Runtime/DI/AutoBindingTests.cs` and `AutoBindingPropertyTests.cs` exercise only `[AutoRegisterSingleton]`/`[AutoRegisterTransient]` (both `Inherited = false`); `[Service]` is never used in any test or anywhere else in the repo (`grep -rn "\[Service\]\|\[Service("` returns nothing), so the entire `ServiceAttribute` branch at lines 175-186 is dead in CI. |

```csharp
            var serviceAttr = type.GetCustomAttribute<ServiceAttribute>();
            if (serviceAttr != null)
            {
                return new AutoBindingEntry
                {
                    ImplementationType = type,
                    ServiceType = serviceAttr.InterfaceType ?? type,
                    Lifetime = serviceAttr.Lifetime,
                    Priority = 0,
                    RegisterSelf = false
                };
            }
```

**Sorun:** Two defaults compound. (1) `ServiceAttribute` (Runtime/DI/Attributes/ServiceAttribute.cs:5) is declared `[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false)]` with no `Inherited` argument, and `AttributeUsageAttribute.Inherited` defaults to **true**. (2) `CustomAttributeExtensions.GetCustomAttribute<T>(MemberInfo)` — the single-argument overload used on line 175 — forwards to `Attribute.GetCustomAttribute(element, type, inherit: true)`. So for every subclass of a `[Service]`-annotated class, line 175 returns the BASE's attribute instance, and lines 180-182 build an entry whose `ImplementationType` is the subclass but whose `ServiceType` and `Lifetime` are copied from the base. The author clearly knew about this hazard: line 162 explicitly passes `inherit: false` for `AutoRegisterBaseAttribute`, and `AutoRegisterAttribute` (Attributes/AutoRegisterAttribute.cs:5) explicitly declares `Inherited = false`. `ServiceAttribute` is the one that was missed on both axes. Because all `[Service]` entries get hard-coded `Priority = 0` (line 183), the resulting duplicates cannot even be disambiguated by priority.

**Senaryo:** ```csharp
[Service(Lifetime.Singleton, InterfaceType = typeof(IWeapon))]
public class WeaponBase : IWeapon { }
public class Sword   : WeaponBase { }
public class Bow     : WeaponBase { }
```
The scanner produces THREE entries, all with `ServiceType == typeof(IWeapon)` and `Priority == 0`. `RegisterAll` then calls `ContainerBuilder.Register<IWeapon, T>` three times, and `ContainerBuilder.cs:22` is a last-write-wins dictionary assignment (`_registrations[typeof(TInterface)] = ...`). Which of Sword/Bow/WeaponBase wins is decided by `Assembly.GetTypes()` enumeration order (unspecified) fed through the unstable `List<T>.Sort` on line 37. `container.Resolve<IWeapon>()` returns a `Bow` on one machine and a `Sword` on another, with no warning ever logged. Adding a new subclass to an unrelated file can flip the binding of a shipped service.

**Düzeltme:** Add `Inherited = false` to `ServiceAttribute`'s `[AttributeUsage]` (Attributes/ServiceAttribute.cs:5) AND change line 175 to `type.GetCustomAttribute<ServiceAttribute>(inherit: false)` so the fix holds even for consumers compiled against the old attribute. Both changes, not one — the `inherit: false` argument is what line 162 already does correctly.

## `scanner-typeload-escapes-recovery-path`

**TypeLoadException from GetCustomAttribute aborts the entire scan, and the ReflectionTypeLoadException recovery path re-enters the same unguarded call**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:93` |
| Kategori | bug · di-injection |
| Etki | Startup-only; failure mode is a hard boot failure (zero registrations) rather than degraded operation. |
| Test | Not covered. No test in `Tests/Runtime/DI/` constructs an assembly with unresolvable types or attribute arguments; the `ReflectionTypeLoadException` branch (lines 83-98) is never executed by CI. |

```csharp
                            var entry = TryCreateEntry(type);
                            if (entry != null)
                                entries.Add(entry);
```

**Sorun:** The only handler on the scan loop is `catch (ReflectionTypeLoadException ex)` (line 83). But `TryCreateEntry` calls `type.GetCustomAttribute<...>()` on lines 149, 162 and 175, and `Attribute.GetCustomAttribute` is documented to throw `TypeLoadException` when a custom attribute's type — or a `Type` value baked into its named arguments, such as `As = typeof(IFoo)` or `InterfaceType = typeof(IBar)` — cannot be loaded, and `CustomAttributeFormatException` when the blob is malformed. Neither is a `ReflectionTypeLoadException`, so both escape past line 83, out of `ScanAssemblies`, out of `RegisterAll`, and abort container construction with zero auto-bindings registered. The recovery path is worse than useless: lines 89-96 run INSIDE the catch block and call the very same `TryCreateEntry` on types recovered from `ex.Types` — types from an assembly that is by definition in a broken load state, i.e. the population most likely to throw `TypeLoadException` on attribute materialisation — with no handler at all, since the catch clause is already executing.

**Senaryo:** A `Game.Modules` assembly references an optional plugin DLL that is absent in a particular build configuration. It contains `[AutoRegisterSingleton(As = typeof(IOptionalFeature))]` where `IOptionalFeature` lives in the missing DLL. `assembly.GetTypes()` on line 113 throws `ReflectionTypeLoadException`; the handler on line 83 logs a warning and enters the recovery loop; line 93 calls `TryCreateEntry` on the annotated type; line 149's `GetCustomAttribute<AutoRegisterAttribute>()` must materialise the `As` argument, fails to load `IOptionalFeature`, and throws `TypeLoadException` from inside the catch block. It propagates out of `ScanAssemblies`, out of `RegisterAll`, out of `builder.RegisterAutoBindings()`, and the app fails to boot — from one optional plugin being absent. A tampered or truncated DLL produces the same result, which is a trivially reachable denial of boot.

**Düzeltme:** Wrap the body of `ScanAssembly`'s per-type loop and the recovery loop in `try { var entry = TryCreateEntry(type); ... } catch (Exception ex) { Debug.LogWarning($"[Strada AutoBinding] Skipping type in {assemblyName}: {ex.GetType().Name}"); }` so one broken type skips itself rather than killing the scan. Also broaden the outer handler on line 83 to catch `TypeLoadException`, `FileNotFoundException`, `FileLoadException` and `BadImageFormatException` per assembly, so one broken assembly skips itself rather than killing the container.

## `inject-skips-private-base-class-members`

**[Inject] on private fields/properties/methods declared in a base class is silently ignored — the exact pattern the code comment advertises**

| | |
|---|---|
| Konum | `Runtime/DI/InjectionProcessor.cs:70` |
| Kategori | bug · di-injection |
| Etki | Correctness, not cost. Zero runtime cost to fix (the hierarchy walk happens once per type inside the `ConcurrentDictionary.GetOrAdd` factory). |
| Test | Not covered. `Tests/Runtime/Patterns/ServiceInjectionTests.cs` and `ControllerLifecycleTests.cs` only ever inject through `Base.Construct` (Runtime/Patterns/Base.cs:31-32, PUBLIC) and `Controller<TModel>.InjectModel` (Runtime/Patterns/Controller.cs:20-21, PUBLIC) — public base members are returned by GetMethods, so the bug is invisible. A test with a `[Inject] private` field on an abstract base and a derived concrete type would fail today. |

```csharp
            foreach (var field in type.GetFields(flags))
            {
                if (field.GetCustomAttribute<InjectAttribute>() == null)
                    continue;

                fields.Add(field);
            }
```

**Sorun:** `BuildInjectionInfo` calls `type.GetMethods(flags)` (47), `type.GetProperties(flags)` (61) and `type.GetFields(flags)` (70) exactly once on the concrete runtime type, and never walks `type.BaseType`. The .NET reflection contract for `BindingFlags.Instance | BindingFlags.NonPublic` is explicit: "Only protected and internal fields on base classes are returned; private fields on base classes are not returned" (identical wording in the GetMethods and GetProperties docs). So a `private` `[Inject]` member declared on a base class is invisible when injecting a derived instance — no exception, no warning, the member just stays null. `protected`/`internal` base members DO work, which makes the failure mode maximally confusing: changing one keyword from `protected` to `private` silently breaks injection. This directly contradicts the FRAMEWORK DESIGN comment on lines 40-44 of this same file, which justifies `BindingFlags.NonPublic` by saying `[Inject]` targets "private/protected fields, properties, and methods so services can keep their dependencies out of their public API". Real DI containers walk the hierarchy explicitly for this reason (Zenject's `AllFields`, VContainer's `InjectTypeInfo` builder).

**Senaryo:** ```csharp
public abstract class GameServiceBase : Service {
    [Inject] private ILogger _logger;      // declared private on the base
    protected void Log(string m) => _logger.Log(m);
}
public sealed class SaveService : GameServiceBase { ... }
```
`InjectionProcessor.Inject(new SaveService(), container)` calls `typeof(SaveService).GetFields(Instance|Public|NonPublic)`, which does not return `GameServiceBase._logger`. `fields` is empty for that member, nothing is assigned, `Inject` returns successfully, and the first call to `Log(...)` throws `NullReferenceException` inside `GameServiceBase` — a stack frame with no visible connection to the container. Widening the field to `protected ILogger _logger;` makes it work, which is the opposite of the encapsulation guidance in lines 40-44.

**Düzeltme:** Walk the hierarchy in `BuildInjectionInfo`: loop `for (var t = type; t != null && t != typeof(object); t = t.BaseType)` calling `t.GetFields(flags | BindingFlags.DeclaredOnly)` / `GetProperties` / `GetMethods`, and de-duplicate overridden virtuals by `MethodInfo.GetBaseDefinition()` and shadowed fields by declaring-type. Build the list base-first so base dependencies land before derived ones. Cost is startup/first-touch only because the result is cached in `_cache`.

## `resolve-by-type-takes-global-container-lock-per-member`

**Injection resolves by System.Type, which takes the container's global lock and a ConcurrentDictionary<Type,int> lookup for every injected member**

| | |
|---|---|
| Konum | `Runtime/DI/InjectionProcessor.cs:118` |
| Kategori | performance · di-injection |
| Etki | ~35-65 ns per injected member (Type dictionary lookup + Monitor round trip) on top of the reflection cost, per `Inject()` call; plus serialisation of all container resolution across threads while injecting. |
| Test | Not covered. `Tests/Runtime/DI/ContainerPerformanceTests.cs` benchmarks only `container.Resolve<IServiceA>()` (the generic, lock-free path) — the `Resolve(Type)` path that injection actually uses is never benchmarked. `ContainerThreadSafetyTests.cs` does not combine injection with concurrent resolution. |

```csharp
                var value = container.Resolve(field.FieldType);
```

**Sorun:** The injection path can only use the non-generic `IContainer.Resolve(Type)` (used here and at lines 98 and 140). That overload resolves very differently from `Resolve<T>()`: `Container.ResolveByType` (Container.cs:225-245) calls `TypeRegistry.GetId(type)` — a `ConcurrentDictionary<Type,int>` lookup with `RuntimeType` hashing/equality (TypeRegistry.cs:19) — and then, for Singleton and Scoped lifetimes, wraps the factory invocation in `lock (_lock)` on the CONTAINER-WIDE lock object. `Resolve<T>()` (Container.cs:78-90) does neither: it reads a `static readonly int` and indexes an array with no lock at all. So the reflection path is both slower per member and serialises across threads. Worse, that lock is held while `_factories[index](this)` runs arbitrary user constructor code (Container.cs:238), so any constructor that blocks waiting on another thread which itself needs the container deadlocks the whole container. The brief's rule that "Dictionary lookups by System.Type in hot paths are not [fine]" applies exactly here.

**Senaryo:** A service with five `[Inject]` singleton dependencies costs five `ConcurrentDictionary<Type,int>` lookups (~15-25 ns each) and five uncontended `Monitor.Enter`/`Exit` pairs (~20-40 ns each) = ~175-325 ns of pure lookup/lock overhead per injection, versus ~10 ns had the same five been constructor parameters resolved through the compiled factory. For the deadlock: a `[PostConstruct]`-style singleton whose constructor does `someTask.Wait()` while a worker thread calls `container.Resolve(typeof(IOther))` — the worker blocks on `_lock` held by the main thread, and the main thread blocks on the worker: hard hang, no timeout, no diagnostic.

**Düzeltme:** Give the container an internal index-based resolve keyed at cache-build time: in `BuildInjectionInfo`, resolve each member's `Type` to a registration index once (via `IIndexResolver`, already present at Container.cs:216-222) and store the int in `TypeInjectionInfo`, so injection becomes an array index instead of a Type lookup + lock. Independently, narrow `Container.ResolveByType`'s lock so it does not span user factory execution (the singleton factory at Container.cs:304-324 already does its own `Interlocked.CompareExchange`, so the outer lock is redundant for Singleton).

## `inject-methods-run-before-fields-and-in-undefined-order`

**[Inject] methods are invoked before [Inject] fields/properties are assigned, and the order among multiple [Inject] methods is unspecified**

| | |
|---|---|
| Konum | `Runtime/DI/InjectionProcessor.cs:20` |
| Kategori | api-hazard · di-injection |
| Etki | Correctness/determinism; no runtime cost (sorting happens once per type inside the cache factory). |
| Test | Not covered. Every `[Inject]` member reached by `ServiceInjectionTests.cs` / `ControllerLifecycleTests.cs` is a method with no dependency on injected fields, and `Controller_WithModel_InjectsModel` (ControllerLifecycleTests.cs:92-106) happens to pass regardless of whether `Construct` or `InjectModel` ran first. No test asserts injection ordering. |

```csharp
            InjectMethods(target, info.Methods, container);
            InjectProperties(target, info.Properties, container);
            InjectFields(target, info.Fields, container);
```

**Sorun:** Two ordering hazards. (1) Method injection runs FIRST, so an `[Inject]` method can never observe an `[Inject]` field or property — they are still null when it executes. This inverts the convention of every mainstream C# DI container (Zenject and VContainer both inject fields, then properties, then methods, precisely so method injection can use the injected state). Nothing in `Documentation~/DI.md` documents Strada's inverted order. (2) Within `InjectMethods`, the array order comes from `type.GetMethods(flags)` on line 47, and `Type.GetMethods` is explicitly documented not to return methods in any particular order (declaration order is not guaranteed and differs between Mono and IL2CPP). The framework's own hierarchy has two `[Inject]` methods on a single object — `Base.Construct(IContainer)` at Runtime/Patterns/Base.cs:31 and `Controller<TModel>.InjectModel(TModel)` at Runtime/Patterns/Controller.cs:20 — so any user `[Inject]` method added to that chain has an undefined position relative to `Construct`, which is what populates `Container`, `World`, `EntityManager` and `EventBus`.

**Senaryo:** ```csharp
public class HudController : Controller<HudModel> {
    [Inject] private IScoreService _score;   // (also see inject-skips-private-base-class-members)
    [Inject] public void Wire() { _score.OnChanged += Redraw; }   // NRE
}
```
`Inject` calls `InjectMethods` first, so `Wire()` runs while `_score` is still null -> `NullReferenceException` inside `Wire`. Second scenario: a user `[Inject] public void Setup() { EventBus.Subscribe<Foo>(OnFoo); }` on a Controller — whether `EventBus` is non-null depends on whether `GetMethods` happened to return `Base.Construct` before `Setup`. Mono in the Editor typically returns derived-then-base, IL2CPP need not, so this can work in play mode and silently no-op (`EventBus?.Subscribe` on line 74 of Base.cs swallows a null bus) in the player.

**Düzeltme:** Reorder line 20-22 to fields -> properties -> methods, matching Zenject/VContainer, and give `Base.Construct` a guaranteed-first position via an explicit ordering key (e.g. an `Order` property on `InjectAttribute`, defaulting `Construct` to int.MinValue) with a stable secondary sort on declaring-type depth so base methods precede derived ones deterministically. Sort once inside `BuildInjectionInfo` so the cost stays on the cached build path. Document the final contract in `Documentation~/DI.md`.

## `lifecycle-cache-unlocked-dictionary-read`

**LifecycleProcessor reads a plain Dictionary outside the lock that guards its writes (prior report falsely marks this FIXED)**

| | |
|---|---|
| Konum | `Runtime/DI/LifecycleProcessor.cs:59` |
| Kategori | concurrency · di-injection |
| Etki | Per first-touch of each new type from a second thread; corruption is permanent for the process lifetime once it happens (the cache never self-heals). No per-frame cost when single-threaded. |
| Test | ZERO. `grep -rl "LifecycleProcessor\|PostConstruct\|DeConstruct" Tests/` returns no files — the whole class is untested. `Tests/Runtime/DI/ContainerThreadSafetyTests.cs` exercises Container only and never touches LifecycleProcessor. Needs a test that hammers `InvokePostConstruct` from N threads over M distinct types. |

```csharp
            if (cache.TryGetValue(type, out var methods))
                return methods;

            lock (_lock)
            {
                if (cache.TryGetValue(type, out methods))
                    return methods;

                methods = FindMethodsWithAttribute(type, attributeType);
                cache[type] = methods;
```

**Sorun:** `PostConstructCache` and `DeConstructCache` are plain `System.Collections.Generic.Dictionary<Type, MethodInfo[]>` (lines 10-11). The write on line 68 is inside `lock (_lock)`, but the fast-path read on line 59 is completely unsynchronised. `Dictionary<K,V>` is explicitly documented as unsafe for concurrent read-while-write: a write that triggers `Resize()` reassigns the `_buckets` and `_entries` arrays non-atomically, so a concurrent reader can observe a new `_buckets` against a stale `_entries` (or vice versa). This is not a benign stale-read — it produces `IndexOutOfRangeException`, an infinite loop walking a corrupted collision chain (hangs the calling thread forever, which in Unity is usually the main thread), or a silently wrong `MethodInfo[]` for the wrong Type. `ClearCache()` (lines 87-94) takes the lock but readers on line 59 do not, so `Clear()` races the same way. Note the contrast: `InjectionProcessor` (line 12) uses `ConcurrentDictionary`, and `EntityCommandBuffer.cs:330` was already migrated Dictionary->ConcurrentDictionary for exactly this reason.

**Senaryo:** Two threads warm up pooled objects concurrently (e.g. an addressables/async load worker plus the main thread), each calling `LifecycleProcessor.InvokePostConstruct(obj)` for a type not yet cached. Thread A is inside the lock at line 68 executing `cache[type] = methods`, which grows the dictionary from 3 to 7 buckets: it allocates new `_buckets`/`_entries`, writes `_buckets` first, then copies entries. Thread B is simultaneously at line 59 with a DIFFERENT already-cached type; its `FindEntry` reads the NEW `_buckets` (length 7) and indexes the OLD `_entries` (length 3) -> `IndexOutOfRangeException` thrown out of `InvokePostConstruct`, aborting object initialisation. The alternate interleaving where B reads a bucket head pointing into a half-copied entry chain yields an infinite `for (i = buckets[hash]; i >= 0; i = entries[i].next)` loop that hard-hangs the thread with no exception and no log.

**Düzeltme:** Replace both dictionaries with `ConcurrentDictionary<Type, MethodInfo[]>` and use `GetOrAdd` with a cached static factory, matching `InjectionProcessor.cs:12`. Delete `_lock` and make `ClearCache()` call `.Clear()` on both. If the Dictionary must be kept, the line-59 read has to move inside `lock (_lock)`. Then correct `SecurityReports/2026-05-22-status-review.md:34` and `Documentation~/DI.md:389`, both of which currently assert this is already synchronised.

## `il2cpp-reflection-with-no-preserve-or-linkxml`

**Entire DI/lifecycle contract is reflection-only, yet the package ships no link.xml and no [Preserve] — members are stripped in IL2CPP player builds**

| | |
|---|---|
| Konum | `Runtime/DI/LifecycleProcessor.cs:26` |
| Kategori | aot-il2cpp · di-injection |
| Etki | Zero runtime cost; correctness divergence between Editor (Mono, unstripped) and IL2CPP player builds. Manifests at Managed Stripping Level Low and above. |
| Test | Untestable in the current suite — all tests run in the Editor on Mono where nothing is stripped. There is no IL2CPP smoke build in CI and no `link.xml` fixture. Nothing in `Tests/` would catch this. |

```csharp
                    method.Invoke(target, null);
```

**Sorun:** `[PostConstruct]`/`[DeConstruct]` methods (invoked here and at line 47), `[Inject]` fields/properties/methods (InjectionProcessor.cs:88, 108, 128), and auto-registered implementation constructors reached only through `MakeGenericMethod` (RuntimeAutoBindingScanner.cs:215, 220, 226) have ZERO static callers anywhere in the compiled IL. UnityLinker/Mono.Linker decides what to keep by static reachability. `find . -iname "*link*.xml"` over the whole repo returns nothing, and `grep -rn "Preserve" --include="*.cs" Runtime Editor` returns only unrelated identifiers (`EntityStatePreserver`, doc comments) — there is not a single `[UnityEngine.Scripting.Preserve]` in the package, nor any `link.xml`, nor any guidance in `Documentation~/DI.md`. Because Unity's Editor play mode runs on Mono with no stripping, this diverges only in the shipped player build, which is the worst place to discover it. Per note D in domainNotes, the source-generator escape hatch that would avoid reflection entirely is non-functional, so reflection is the only path.

**Senaryo:** Project ships iOS/Android with Player Settings > Managed Stripping Level = Medium (routine for mobile size budgets). `public class SaveService { [PostConstruct] private void OpenDb() { _db = Sqlite.Open(...); } }` — `OpenDb` has no static caller, so UnityLinker removes the method body and metadata. In the player, `FindMethodsWithAttribute` (line 73-85) calls `type.GetMethods(MethodFlags)`, the method is not in the list, `methods` is an empty array, the `foreach` on line 22 iterates zero times, and `InvokePostConstruct` returns successfully having done nothing. No exception, no log. `_db` stays null and the game NREs later at an unrelated call site — a bug that reproduces only in release builds on device and never in the Editor. Same mechanism silently drops `[DeConstruct]` cleanup (leaking event subscriptions and native handles) and, at higher stripping levels, the public constructor of a service that is only ever instantiated via `Register<IFoo, Foo>`.

**Düzeltme:** Ship a `link.xml` at the package root (`<linker><assembly fullname="Strada.Core" preserve="all"/></linker>`) — Unity honours `link.xml` inside UPM packages. That covers Strada's own types but NOT user assemblies, which is where `[Inject]`/`[PostConstruct]` actually live, so additionally: (a) document in `Documentation~/DI.md` that consumer assemblies need a link.xml entry or `[Preserve]` on every `[Inject]`/`[PostConstruct]`/`[DeConstruct]` member; (b) have `LifecycleProcessor` log an error when `type` declares no matching method but a base/interface suggests one should exist; (c) fix the source generator (domainNotes D) so the compile-time path emits real static registrations and link.xml entries, removing the dependency on reflection survival.

## `ecb-byte-at-a-time-native-list-writes`

**Every ECB field is appended one byte at a time via NativeList<byte>.Add — 22+sizeof(T) safety-checked calls per AddComponent instead of one AddRange memcpy**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:189` |
| Kategori | performance · ecs-jobs |
| Etki | ~34x more NativeList.Add calls (and safety-handle checks) than necessary per AddComponent<Position>; on a 5,000-command frame that is ~165,000 redundant calls/frame on the main thread. Recording only — playback is unaffected. |
| Test | Partially measured but not constrained: JobSystemPerformanceTests.cs:128-148 (`Benchmark_EntityCommandBuffer_Recording_Performance`) times 100k `CreateEntity()` calls, which is the 1-Add case and therefore never exercises the 34-Add path. No benchmark measures AddComponent recording throughput in isolation. |

```csharp
            int size = sizeof(T);
            WriteInt(size);
            var ptr = (byte*)&component;
            for (int i = 0; i < size; i++)
                _commandStream.Add(ptr[i]);
```

**Sorun:** `WriteComponent` (lines 186-194), `WriteTypeHash` (lines 177-184, an 8-iteration Add loop) and `WriteInt` (lines 196-204, four separate Adds) all push single bytes. `NativeList<T>.Add` is not free: under `ENABLE_UNITY_COLLECTIONS_CHECKS` (Editor and development builds) it performs an `AtomicSafetyHandle.CheckWriteAndThrow` before delegating to `UnsafeList<T>.Add`, which itself does a capacity compare-and-branch. Recording one `AddComponent<T>(Entity, T)` costs 1 (command) + 9 (WriteEntity = 4+4+1) + 8 (type hash) + 4 (size) + sizeof(T) = 22 + sizeof(T) Add calls. For the 12-byte `Position` in the tests that is 34 calls, i.e. 34 safety-handle checks and 34 capacity branches to move 34 bytes.

**Senaryo:** Tests/Stress/ParallelCommandBufferTests.cs records 20 threads x 1000 iterations of `CreateEntity()` (1 Add) + `AddComponent(index, TestComponentA)` (22 + 4 = 26 Adds) = 540,000 NativeList.Add calls, ~540,000 AtomicSafetyHandle.CheckWriteAndThrow invocations in the Editor, to write ~540 KB. A game recording 5,000 structural commands per frame pays ~170,000 Add calls/frame on the main thread. The existing benchmark's own budget acknowledges the cost: JobSystemPerformanceTests.cs:145 asserts only `< 500 ns/command` for the cheapest possible command (bare `CreateEntity`, a single Add).

**Düzeltme:** Use the bulk overloads: `_commandStream.AddRange((void*)ptr, size)` for the payload and type hash, and a single `AddRange` for the packed entity header. Best is one `EnsureCapacity(22 + sizeof(T))` followed by one `UnsafeUtility.MemCpy` into `_commandStream.GetUnsafePtr() + oldLength` and a single length bump — 1 safety check and 1 memcpy per command instead of 34.

## `jobs-generic-job-not-registered-for-burst-aot`

**ComponentJobParallel<> is only ever instantiated from a generic method and no [RegisterGenericJobType] exists — Burst silently does not compile it in AOT/IL2CPP builds, invalidating the README's "17x Burst" claim on device**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityJobs.cs:23` |
| Kategori | aot-il2cpp · ecs-jobs |
| Etki | Whole-of-domain: 100% of parallel jobs lose Burst codegen in AOT builds. Burst-vs-IL2CPP on a Position+=Velocity*dt loop is typically 3-8x on its own, so the advertised 17x collapses toward the core-count-only speedup on device. |
| Test | NOT COVERED and NOT COVERABLE by the current suite: Tests/Runtime/Performance/ParallelJobPerformanceTests.cs and JobSystemPerformanceTests.cs run in the Editor, where Burst's JIT compiles generic instantiations on demand — the exact configuration in which this bug is invisible. There is no player/AOT benchmark and no test asserting that the scheduled job was actually Burst-compiled. |

```csharp
            unsafe
            {
                return new ComponentJobParallel<TJob, T1>
                {
                    UserJob = job,
                    EntityIds = set.GetDenseEntityPtr(),
                    Components1 = set.GetDataPtr(),
                    SparseIndex1 = set.GetSparsePtr(),
                    MaxSparse1 = set.SparseCapacity
                }.Schedule(set.Count, batchSize, dependency);
            }
```

**Sorun:** The job struct carries `[BurstCompile]` (ParallelComponentJob.cs:14, 36, 63, 95), but it is a *generic* job whose only construction site is inside the generic method `EntityJobs.Schedule<TJob, T1>` (and the T1..T4 overloads at lines 55, 94, 140), which is in turn reached only through more generic methods (EntityManagerJobExtensions.ScheduleParallel<...>, JobSystemBase.ScheduleParallel<...>, BurstSystem<...>.OnSchedule). Unity's Burst AOT compiler discovers generic job instantiations by static analysis of concrete `new Job<A,B>()` expressions; instantiations that exist only through generic-method type parameters are not discoverable and must be declared with `[assembly: RegisterGenericJobType(typeof(ComponentJobParallel<MoveJob, Position, Velocity>))]`. A repo-wide grep for `RegisterGenericJobType` returns zero hits, and there is no link.xml anywhere in the package.

**Senaryo:** Ship an IL2CPP player build. Every `ScheduleParallel`/`RunParallel` call runs `ComponentJobParallel<...>.Execute` as ordinary IL2CPP-generated C++ instead of Burst-generated vectorised native code — silently, with no compile error and no runtime warning. Measured throughput on device is a small multiple of the managed path (thread count only), not the 17x the README advertises. The framework also gives users no way to fix it: Documentation~/ECS.md's Parallel Jobs section (lines 369-410) shows only `[BurstCompile]` on the user's `IJobComponent` struct — which is itself inert, because `MoveJob : IJobComponent<Position, Velocity>` is not a Unity job type; Burst compiles the outer `ComponentJobParallel` and only inlines `UserJob.Execute`.

**Düzeltme:** (1) Document and require `[assembly: RegisterGenericJobType(typeof(ComponentJobParallel<MyJob, MyComp1, MyComp2>))]` per concrete job in Documentation~/ECS.md, ideally emitted automatically by the existing source generator alongside the query codegen. (2) Add a runtime diagnostic in editor/development builds (`BurstCompiler.IsEnabled` + `[BurstDiscard]`-guarded probe) that logs a warning when a scheduled ComponentJobParallel instantiation is not Burst-compiled. (3) Correct README.md:44/226, Documentation~/ECS.md:412 and Documentation~/Benchmarks.md:337-339 to state that the 17x figure is an Editor/Mono measurement of Burst+parallel vs a managed delegate loop.

## `jobsystem-ecb-tempjob-never-disposed`

**JobSystemBase allocates its EntityCommandBuffer with Allocator.TempJob and never disposes it — permanent native leak plus a TempJob leak warning every 4 frames**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/JobSystemBase.cs:27` |
| Kategori | allocation · ecs-jobs |
| Etki | Leaks 1024 bytes (stream) + 64*8 = 512 bytes (created-entity list) of native memory per JobSystemBase instance, growing with the stream's capacity; plus a repeating editor/player console warning starting at frame 5. |
| Test | NOT COVERED — no test constructs a JobSystemBase. Tests/Runtime/ECS/Jobs/EntityCommandBufferTests.cs disposes its own TempJob buffers inside the same test method, so the long-lived-TempJob pattern is never exercised. |

```csharp
                    _commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
```

**Sorun:** The ECB is created once, lazily, and lives for the whole lifetime of the system (only `Clear()`ed each frame at line 47). `Allocator.TempJob` is Unity's 4-frame allocator. `JobSystemBase` also never calls `_commandBuffer.Dispose()`: the only teardown hook is `protected override void OnDispose() => OnDestroy();` (line 35) and `OnDestroy()` is an empty virtual (line 38). Grep of the file confirms `Dispose` appears only at line 35 and never on `_commandBuffer`.

**Senaryo:** Any JobSystemBase subclass that touches `CommandBuffer` once allocates two TempJob NativeLists (1024-byte stream + 64-Entity list) that are still alive on frame 5. Unity's allocator then emits `Internal: JobTempAlloc has allocations that are more than 4 frames old - this is not allowed and likely a leak` to the console every few frames for the rest of the session. When the World is torn down (`World.Dispose()` -> `_scheduler.Dispose()` -> `system.Dispose()` -> `SystemBase.Dispose()` -> `OnDispose()` -> `OnDestroy()` no-op) the native memory is never freed; with leak detection enabled Unity reports the leaked allocation with its stack trace.

**Düzeltme:** Use `Allocator.Persistent` for a buffer with system lifetime, and override teardown: `protected override void OnDispose() { _lastJobHandle.Complete(); if (_commandBufferCreated) { _commandBuffer.Dispose(); _commandBufferCreated = false; } OnDestroy(); }`.

## `jobsystem-playback-exception-skips-clear`

**An exception thrown inside Playback skips the following Clear(), so the command stream is replayed and grows without bound every subsequent frame**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/JobSystemBase.cs:44` |
| Kategori | bug · ecs-jobs |
| Etki | Once triggered: one full re-replay of all N buffered commands per frame forever, and the stream grows by the per-frame recorded byte count each frame (e.g. 1000 AddComponent<Position> commands = ~34 KB/frame = ~2 MB/s at 60 fps). |
| Test | NOT COVERED. No test drives JobSystemBase. Tests/Runtime/ECS/Jobs/EntityCommandBufferTests.cs::MultiplePlaybacks_WorkCorrectly (line 161) explicitly calls `ecb.Clear()` between playbacks, pinning only the happy path; no test plays back a buffer containing a command that throws. |

```csharp
            if (_commandBufferCreated)
            {
                _commandBuffer.Playback(EntityManager);
                _commandBuffer.Clear();
            }
```

**Sorun:** `Playback` and `Clear` are two separate statements with no try/finally. `Playback` has several reachable throw sites: EntityCommandBuffer.cs:252 (`IndexOutOfRangeException` for a bad deferred index), EntityCommandBuffer.cs:277/285/293/307/319 (`InvalidOperationException` stream-overflow guards), EntityCommandBuffer.cs:424 (component size mismatch), and — most reachably — `ComponentPlaybackHandler<T>.SetComponent` -> `EntityManager.SetComponent` -> `SparseSet.Set` which throws `InvalidOperationException($"Entity {entityIndex} does not exist in sparse set")` (SparseSet.cs:112-113) whenever the target entity lost that component between recording and playback. `EntityCommandBuffer.Playback` itself never clears the stream on the way out (EntityCommandBuffer.cs:109-139 contains no Clear).

**Senaryo:** A system records `CommandBuffer.SetComponent(e, new Health{...})` in frame N; another system removes Health from `e` before frame N+1. In frame N+1 `Playback` throws at SparseSet.cs:113. SystemBase.Update catches and logs it (SystemBase.cs:44-47) so the game keeps running, but `_commandBuffer.Clear()` at line 47 is never reached. Frame N+2 replays the same failing command plus everything recorded in N+1, throws again, and skips Clear again. The NativeList command stream therefore grows monotonically (never freed, never cleared) and every already-succeeded command before the throw point is re-executed on every subsequent frame — duplicate DestroyEntity/AddComponent side effects plus unbounded native memory growth until OOM.

**Düzeltme:** Wrap in try/finally: `try { _commandBuffer.Playback(EntityManager); } finally { _commandBuffer.Clear(); }` — and do the same in `FlushCommandBuffer()` (lines 111-118, which has the identical pattern). Better still, make `EntityCommandBuffer.Playback` clear its own stream in a finally block so no caller can get this wrong.

## `query-stale-count-silent-write-loss`

**Cached `count` plus swap-remove means writes made during iteration after a structural removal are silently discarded**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQuery.cs:22` |
| Kategori | bug · ecs-query |
| Etki | One duplicated callback invocation and one silently-discarded component write per component removed during a single ForEach pass; scales linearly with removals per frame. |
| Test | PARTIALLY PINNED, INCOMPLETELY. Tests/Runtime/ECS/ECSIterationSafetyTests.cs:42 asserts `Assert.AreEqual(3, processedCount, "Legacy ForEach iterates original count, processing moved entities at old indices (stale but counted).")` — the test knowingly pins the duplicate-visit count but never asserts anything about the component DATA, so the silent write-loss half of the defect is entirely untested. |

```csharp
            int count = sparseSet.Count;
```

**Sorun:** `count` is snapshotted before the loop. `SparseSet.Remove` (SparseSet.cs:49-68) does a swap-remove: the entity at `lastIndex` is copied down into the removed slot and `_count--`. The loop keeps running to the ORIGINAL count, so the dense slot at `lastIndex` is visited again as a stale duplicate. The callback receives `ref data[lastIndex]` — a dead slot that is no longer the entity's live storage (its live copy now sits at the lower index the loop already passed). Any component mutation the callback writes through that `ref` is silently thrown away, and the callback observes the same entity index twice.

**Senaryo:** 3 entities e1,e2,e3 with `TestComponent`, dense order [e1,e2,e3]. `em.ForEach<TestComponent>((int i, ref TestComponent c) => { if (c.Value == 1) em.DestroyEntity(em.GetEntity(i)); c.Value += 100; });` At i=0 the destroy fires: dense becomes [e3,e2], _count=2, but slot 2 still holds e3's stale bytes. i=1 processes e2 correctly. i=2 processes e3's STALE copy at slot 2 and writes `Value += 100` there. e3's live component (now at dense slot 0) never receives the +100. Result: e3's update is silently lost and the callback ran 3 times for 2 live entities.

**Düzeltme:** Re-read `sparseSet.Count` at the top of every iteration (`for (int i = 0; i < sparseSet.Count; i++)`) so the loop shrinks with the set — this makes removal-during-iteration skip rather than duplicate, which is the standard sparse-set contract — and add the structural-version assert from finding `query-foreach-dangling-native-ptr-on-grow` so the skip is loud in the Editor. Same change needed at EntityQuery.cs:79, EntityQuery.cs:151, FilteredQuery.cs:70/134/226, and every `minCount`/`min` in EntityQueryExtended.cs.

## `query-redundant-driving-set-sparse-probe`

**The join probes the driving set's own sparse array every entity, although the answer is provably the loop index**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQuery.cs:81` |
| Kategori | performance · ecs-query |
| Etki | Eliminates 1 of N sparse probes + 1 of N sign branches per entity per query. For the 2-component query that is 50% of the join's probe work — 100k redundant random reads per invocation of Benchmark_Query_TwoComponents_100k (README claims 18ns/entity, so ~1.8ms total for that pass). For the 8-component query it is 12.5%. |
| Test | Correctness is covered (Tests/Runtime/ECS/Query/QueryPropertyTests.cs:99-170 property-checks 2-component join completeness, EntityQueryTests.cs:96-145 covers 2/3-component). No test would break from this optimization. No test asserts the probe count. |

```csharp
                for (int i = 0; i < count; i++)
                {
                    int entityIndex = entities[i];

                    int idx1 = set1.GetDenseIndex(entityIndex);
                    int idx2 = set2.GetDenseIndex(entityIndex);

                    if (idx1 < 0 || idx2 < 0)
                        continue;
```

**Sorun:** The code correctly picks the smaller set to drive iteration (line 74/78-79), so `entities` IS `setK._dense`. The sparse-set invariant is `_sparse[_dense[k]] == k` for all k < _count, therefore `setK.GetDenseIndex(entities[i])` is identically `i` and `idxK < 0` is identically false. The driving set's probe is a provably-redundant random-access read into a `NativeArray<int>` that is up to 1,048,576 entries long (SparseSet.MaxSparseCapacity), plus a never-taken branch, executed once per entity. The same redundancy is present in EntityQuery.cs:155-160 (1 of 3 probes wasted), EntityQueryExtended.cs:59-60 (1 of 4), :108-109 (1 of 5), :161-162 (1 of 6), :218-219 (1 of 7), :278-280 (1 of 8).

**Senaryo:** Not a wrong-answer bug — a pure waste. Tests/Runtime/Performance/ECSPerformanceTests.cs:177-209 (`Benchmark_Query_TwoComponents_100k`) performs 100,000 redundant `_sparse[]` loads and 100,000 dead branch evaluations per invocation. The repo's own `Benchmark_Comparison_ManualVsECS` (ECSPerformanceTests.cs:559) accepts up to 10x overhead vs. a plain array loop; this is one of the contributors.

**Düzeltme:** Specialize the loop on which set drives, so the driving index is `i` and only the other N-1 sets are probed. E.g. for the 2-component case emit two loop bodies: `if (useSet1) { for (i..) { int i2 = set2.GetDenseIndex(entities[i]); if (i2 < 0) continue; action(entities[i], ref d1[i], ref *(d2 + i2)); } } else { ... }`. For arity 3-8 this is exactly what the existing SourceGenerationECS~/EntityQueryGenerator should emit rather than the hand-written symmetric form.

## `query-managed-delegate-per-entity`

**Every entity costs one uninlinable managed delegate invocation, which also blocks hoisting of everything else in the loop**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQuery.cs:31` |
| Kategori | performance · ecs-query |
| Etki | ~2-5ns per entity for the indirect call itself on Mono/IL2CPP, plus the blocked LICM of finding `query-getdataptr-not-hoisted` (N extra loads per entity). At 100k entities that is 0.2-0.5ms per query pass from the call alone. Plus ~88 bytes of Gen0 garbage per frame per SystemBase<...> subclass (not startup-only — every frame). |
| Test | Behaviour covered by Tests/Runtime/ECS/Query/EntityQueryTests.cs and QueryPropertyTests.cs. NOT covered: no test or benchmark asserts zero GC allocation on any query path — Tests/Runtime/Performance/ECSPerformanceTests.cs uses bare Stopwatch with no `.GC()` measurement, and Tests/Benchmarks/ECSBenchmarks.cs has `.GC()` nowhere (only Tests/Runtime/DI/ContainerPerformanceTests.cs uses it). Nothing would catch a regression to the "0 bytes" claim. |

```csharp
                    action(entities[i], ref data[i]);
```

**Sorun:** `action` is a `QueryDelegate<T1>` — a managed MulticastDelegate (declared EntityQuery.cs:172-177, EntityQueryExtended.cs:7-20). `action(...)` compiles to `callvirt Delegate.Invoke`, an indirect call through the delegate's method pointer plus a target load. Neither Mono nor IL2CPP can inline through it, so (a) the callback body never fuses with the loop and never vectorizes, and (b) — more costly — the call is an opaque memory clobber, which forbids the compiler from hoisting the loop-invariant `GetDataPtr()` reloads (EntityQuery.cs:91-92) or from keeping the base pointers in registers across iterations. This is the structural reason the README's per-component scaling is superlinear rather than flat. It also forces every caller into a heap allocation: SystemBase.cs:175/195/216/237/258/279/301/323 all pass `(int entity, ref T1 c1) => OnUpdateEntity(entity, ref c1, deltaTime)`, which captures `deltaTime` and `this` and therefore allocates a display class + a delegate object EVERY FRAME, per system. Burst is impossible on this path for the same reason (managed ref in the job body).

**Senaryo:** README.md:221-223 claims 6.6ns / 18ns / 28ns per entity for 1/2/3 components over 100k entities. A `p.X += v.X; p.Y += v.Y; p.Z += v.Z` over two contiguous 12-byte arrays is sub-nanosecond per element when inlined; the repo's own ECSPerformanceTests.cs:559 `Benchmark_Comparison_ManualVsECS` asserts only `overhead < 10.0` vs. the manual array loop, i.e. it explicitly tolerates a 10x delegate tax. Separately, SystemBase<Position,Velocity> at 60fps allocates one `<>c__DisplayClass` (~24B) + one `QueryDelegate<T1,T2>` (~64B) per frame = ~5.3KB/s per system, ~105KB/s across 20 systems — which directly contradicts Documentation~/Benchmarks.md:401 ("Query Iteration | 0 bytes").

**Düzeltme:** Add a struct-generic overload alongside the delegate one, reusing the interface the repo ALREADY ships at Runtime/ECS/Jobs/IJobComponent.cs (its `void Execute(int entity, ref T1 c1)` signature is byte-for-byte the delegate's): `public void ForEach<TJob>(ref TJob job) where TJob : struct, IJobComponent<T1> { ... job.Execute(entities[i], ref data[i]); ... }`. The `struct` constraint makes the runtime emit a specialized instantiation per TJob, `job.Execute` devirtualizes to a direct call and inlines, the closure disappears (state lives in TJob fields — `deltaTime` becomes a field, not a capture), and the body becomes Burst-eligible. Then change SystemBase<T...> to hold a TJob struct instead of allocating a lambda per frame. Keep the delegate overload for source compatibility.

## `query-getdataptr-not-hoisted`

**GetDataPtr() is re-invoked once per component per entity inside the inner loop instead of once per query**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQuery.cs:91` |
| Kategori | performance · ecs-query |
| Etki | N_components-1 redundant `GetUnsafePtr` calls per entity. In Editor/Development builds each is an AtomicSafetyHandle version check; at 100k entities x 3 components that is 200,000 avoidable checked calls per query pass. In release players it degrades to a redundant field load, still N-1 per entity. |
| Test | No test measures this. Tests/Runtime/Performance/ECSPerformanceTests.cs:208/243 only assert coarse ceilings (`< 0.2μs`, `< 0.3μs` per entity) — roughly 10x the actual figures, so a large regression would pass. |

```csharp
                    T1* ptr1 = set1.GetDataPtr() + idx1;
                    T2* ptr2 = set2.GetDataPtr() + idx2;
```

**Sorun:** `SparseSet.GetDataPtr()` is `(T*)_data.GetUnsafePtr()` (SparseSet.cs:118). `NativeArray<T>.GetUnsafePtr()` is `NativeArrayUnsafeUtility.GetUnsafePtr`, which under ENABLE_UNITY_COLLECTIONS_CHECKS (defined in the Editor and in Development Builds) executes `AtomicSafetyHandle.CheckWriteAndThrow(m_Safety)` before returning `m_Buffer`. The value is loop-invariant, but because the loop body ends with an opaque delegate call the compiler cannot hoist it. So the safety check runs N_components times per entity. Same pattern at EntityQuery.cs:162-164 (3x), EntityQueryExtended.cs:61 (4x), :110 (5x), :163 (6x), :220 (7x), :281-282 (8x). The single-component path (EntityQuery.cs:27) does hoist it correctly — which is exactly why 1-component measures 6.6ns while 2-component measures 18ns.

**Senaryo:** Tests/Runtime/Performance/ECSPerformanceTests.cs:177-209 and :212-244 are plain NUnit tests run under the Editor Test Runner, where ENABLE_UNITY_COLLECTIONS_CHECKS is defined. `Benchmark_Query_TwoComponents_100k` therefore performs 200,000 AtomicSafetyHandle checks; `Benchmark_Query_ThreeComponents_100k` performs 300,000. The published 18ns and 28ns figures in README.md:222-223 and Documentation~/Benchmarks.md:174-175 include this cost; the ~10ns-per-added-component delta in that table is exactly one extra safety check + one extra sparse probe per component.

**Düzeltme:** Hoist all data base pointers out of the loop alongside `entities` — `T1* d1 = set1.GetDataPtr(); T2* d2 = set2.GetDataPtr();` before `for` — and index them as `d1[idx1]`, `d2[idx2]`. (This is only safe together with the structural-change guard from finding `query-foreach-dangling-native-ptr-on-grow`; today the code is neither hoisted NOR safe, so it pays the cost without buying the safety.)

## `query-test-gap-structural-change-and-allocation`

**The query test suite has no add-during-iteration test and no allocation assertion, hiding the two highest-severity findings**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQuery.cs:19` |
| Kategori | test-gap · ecs-query |
| Etki | Test-only. The missing add-during-iteration test is the single highest-value addition — it is the only thing that would surface a memory-corruption bug that is currently invisible in Editor runs with small entity counts. |
| Test | Tests/Runtime/ECS/ECSIterationSafetyTests.cs (2 tests, removal-only, 3 entities); Tests/Runtime/ECS/Query/EntityQueryTests.cs (13 tests, all structurally static); QueryPropertyTests.cs (7 FsCheck properties, all structurally static); FilteredQueryTests.cs (7 tests, max 4 entities, inline chains only); Tests/Benchmarks/ECSBenchmarks.cs (never calls ForEach). |

```csharp
        public void ForEach(QueryDelegate<T1> action)
```

**Sorun:** Two whole classes of defect in this file are structurally untestable by the current suite. (1) Structural growth during iteration: Tests/Runtime/ECS/ECSIterationSafetyTests.cs contains exactly two tests, both removal-only, both with 3 entities — never enough to cross the 256-element default dense capacity that triggers the reallocation in finding `query-foreach-dangling-native-ptr-on-grow`. No test anywhere under Tests/ calls AddComponent from inside a ForEach callback. (2) Allocation: no query test or benchmark asserts GC bytes. Tests/Runtime/Performance/ECSPerformanceTests.cs uses bare `Stopwatch` (lines 157, 190, 226) with no allocation measurement, and Tests/Benchmarks/ECSBenchmarks.cs (85 lines) benchmarks entity creation, AddComponent, and a `GetAllEntities`+`HasComponent`+`GetComponent` loop — it never invokes the ForEach query path at all, and never calls `.GC()`.

**Senaryo:** The dangling-pointer bug ships undetected: a green test suite plus a green benchmark suite plus the published 6.6/18/28 ns table all pass while `em.ForEach<Position>(cb)` where `cb` adds a Position corrupts Allocator.Persistent memory in release players. Likewise, the "Query Iteration | 0 bytes" claim at Documentation~/Benchmarks.md:401 is contradicted by both FilteredQueryBuilder's per-frame Lists and SystemBase's per-frame closures, and nothing in CI can catch either.

**Düzeltme:** Add to Tests/Runtime/ECS/ECSIterationSafetyTests.cs: (a) `ForEach_AddComponentDuringIteration_DoesNotCorrupt` — create 300 entities with T, add a 301st T from inside the callback (crosses the 256 dense capacity), assert every surviving entity's data is intact; (b) `ForEach_RemoveDuringIteration_WritesAreNotLost` — assert component VALUES after a destroy-during-iteration, not just the callback count (today only the count is asserted, at line 42). Add to Tests/Runtime/Performance/ECSPerformanceTests.cs a `Measure.Method(...).GC()` variant of `Benchmark_Query_TwoComponents_100k` and of a `.Filter<>().Also<>().None<>()` query, asserting 0 GC allocations for the former and pinning the current allocation for the latter until it is fixed.

## `filtered-builder-shared-mutable-list-aliasing`

**FilteredQueryBuilder is a struct whose copies share one mutable List — divergent query chains silently contaminate each other**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/FilteredQuery.cs:51` |
| Kategori | api-hazard · ecs-query |
| Etki | Case 1: silently wrong query results, zero diagnostics. Case 2: filter list grows by 1 entry per frame per reused builder; per-entity filter cost grows linearly with frame count (3,600 interface calls/entity after one minute at 60fps). |
| Test | NOT COVERED. All seven tests in Tests/Runtime/ECS/Query/FilteredQueryTests.cs build the whole chain inline in a single expression (lines 42-49, 73-80, 108-116, 136-143, 165-171, 197-204, 227-234) and never store an intermediate builder or reuse one — the exact usage pattern that breaks is the one pattern never exercised. |

```csharp
        public FilteredQueryBuilder<T1> Also<TFilter>() where TFilter : unmanaged, IComponent
        {
            _withFilters ??= new List<IComponentStorage>(4);
            _withFilters.Add(_manager.Store.GetOrCreateStorage<TFilter>());
            return this;
        }
```

**Sorun:** `FilteredQueryBuilder<T1>` is declared `public struct` (line 35) but its filter state is a `List<IComponentStorage>` reference field (lines 39-40). `Also`/`None` are non-readonly: they mutate the RECEIVER in place and then `return this` — a bitwise copy that shares the same List instance. So (a) two builders derived from a common base alias one filter list, and (b) invoking `Also` on a stored builder mutates that stored builder permanently. This is not a theoretical struct-copy nit: the mutation is on the referenced heap object, so every copy that ever existed observes it. Identical in the 2-component (lines 111-113, 119-121) and 3-component (lines 181-183, 189-191) builders.

**Senaryo:** Case 1 — contamination: `var baseQ = em.Query().Filter<Position>(); var alive = baseQ.Also<Alive>(); var dead = baseQ.Also<Dead>();` The first call sets `baseQ._withFilters` to a new List [Alive]; the second sees a non-null `_withFilters` (the SAME list) and appends, giving [Alive, Dead]. `alive.ForEach(...)` now silently requires BOTH Alive and Dead and returns the wrong entity set, with no error. Case 2 — unbounded growth: caching a filtered query the way SystemBase caches an EntityQuery — `private FilteredQueryBuilder<Position> _q;` assigned once in OnInitialize, then `_q.Also<Alive>().ForEach(...)` in OnUpdate — appends one storage to the same List every frame. After 60 seconds at 60fps the list holds 3,600 entries and `PassesFilters` performs 3,600 interface calls PER ENTITY PER FRAME; memory and per-frame cost grow without bound.

**Düzeltme:** Make the builder immutable-by-copy: `Also`/`None` should return a NEW builder with a freshly copied list (`var next = this; next._withFilters = _withFilters == null ? new List<IComponentStorage>(4) : new List<IComponentStorage>(_withFilters); next._withFilters.Add(...); return next;`), or — better for the allocation problem too — replace the two Lists with a fixed inline buffer (`FilterSet4` struct with 4 `IComponentStorage` slots + a count) so copies are genuinely value-typed. Mark the type `readonly struct` afterwards so the compiler enforces it.

## `filtered-builder-list-alloc-per-construction`

**Every Also()/None() allocates a List + backing array, so building a filtered query per frame is per-frame GC garbage**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/FilteredQuery.cs:61` |
| Kategori | allocation · ecs-query |
| Etki | 88 bytes per `Also`-chain and 88 bytes per `None`-chain, per query construction. Per-frame, per-system — 176 B/frame for a typical Also+None query, ~10.6 KB/s per system at 60fps, feeding Unity's non-generational Boehm GC and producing periodic collection spikes. |
| Test | NOT COVERED. No test in Tests/ asserts allocation on the query path; `.GC()` appears only in Tests/Runtime/DI/ContainerPerformanceTests.cs, never for ECS. Tests/Runtime/Performance/ECSPerformanceTests.cs benchmarks no filtered query at all. |

```csharp
            _withoutFilters ??= new List<IComponentStorage>(4);
            _withoutFilters.Add(_manager.Store.GetOrCreateStorage<TExclude>());
```

**Sorun:** The filter state is heap-allocated on first `Also` and again on first `None`, and both are garbage the moment `ForEach` returns. `FilteredQueryBuilder` cannot be safely cached (see finding `filtered-builder-shared-mutable-list-aliasing`), so the only correct usage is to rebuild the chain each frame — which means the allocation is per-frame, not startup-only. Six allocation sites: lines 53, 61 (T1), 111, 119 (T1,T2), 181, 189 (T1,T2,T3).

**Senaryo:** A system doing `em.Query().Filter<Position>().Also<Alive>().None<Dead>().ForEach(...)` in OnUpdate allocates, per frame: one `List<IComponentStorage>` object (16B header + 8B _items + 4B _size + 4B _version = 32B) plus one `IComponentStorage[4]` (16B header + 8B length + 32B payload = 56B) for `_withFilters`, and the same 88B again for `_withoutFilters` = 176 bytes/frame. At 60fps that is 10.6 KB/s for one system. Documentation~/Benchmarks.md:401 states "| Query Iteration | 0 bytes |" and line 403 states "All hot paths are allocation-free after initialization" — both are false for every filtered query.

**Düzeltme:** Replace the two `List<IComponentStorage>` fields with a fixed inline buffer struct (4 or 8 `IComponentStorage` slots + a byte count) held by value in the builder. That removes both allocations AND the aliasing bug in one change, since the filter set then copies with the struct. Cap the filter count and throw at build time if exceeded.

## `filtered-query-ignores-filter-cardinality`

**Also<T> filters never participate in driving-set selection, so `.Filter<A>().Also<B>()` is O(|A|) where the equivalent `.Select<A,B>()` is O(min(|A|,|B|))**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/FilteredQuery.cs:69` |
| Kategori | performance · ecs-query |
| Etki | O(n_primary) vs O(n_min). With a 100k primary set and a 10-entity Also filter that is 100,000 iterations + 100,000 interface calls instead of 10 — roughly 1-2 ms/frame wasted for a query that should cost microseconds. |
| Test | Correctness covered by Tests/Runtime/ECS/Query/FilteredQueryTests.cs:26-53 (`Filter_Also_OnlyMatchesEntitiesWithBothComponents`) with 3 entities — far too small to expose the asymptotics. No performance test covers filtered queries at all (Tests/Runtime/Performance/ECSPerformanceTests.cs has none). |

```csharp
            ref var set = ref _storage.GetSparseSet();
            int count = set.Count;

            unsafe
            {
                int* entities = set.GetDenseEntityPtr();
                T1* data = set.GetDataPtr();

                for (int i = 0; i < count; i++)
                {
                    int entity = entities[i];
                    if (!QueryFilterHelper.PassesFilters(entity, _withFilters, _withoutFilters))
```

**Sorun:** `.Filter<A>().Also<B>()` and `.Select<A, B>()` select the identical entity set, but they have different complexity. `EntityQuery<T1,T2>.ForEach` (EntityQuery.cs:74) explicitly compares `set1.Count <= set2.Count` and drives from the smaller set. `FilteredQueryBuilder<T1>.ForEach` unconditionally drives from `_storage`'s set and treats every `Also` storage as a per-entity predicate — the filter sets' cardinalities are never consulted. Same in the 2-component (line 129, only the two Select storages are compared) and 3-component (lines 210-224) filtered builders. This makes the more expressive API asymptotically worse than the less expressive one for the same query.

**Senaryo:** 100,000 entities have `Position`; 10 of them have `Boss`. `em.Query().Filter<Position>().Also<Boss>().ForEach(...)` walks all 100,000 Position entities and calls `Contains` 100,000 times to find 10 matches — O(100,000). `em.Query().Select<Position, Boss>().ForEach(...)` on the same data drives from the 10-element Boss set — O(10). A 10,000x difference for a query the user reasonably expects to be equivalent, and the more readable form is the slow one. Documentation~/ECS.md:197-207 actively steers users toward the filtered form.

**Düzeltme:** In `FilteredQueryBuilder<...>.ForEach`, fold the `_withFilters` storages into the driving-set selection: compute `min` over {primary Select storages} ∪ {Also storages} by `IComponentStorage.Count`, drive from that set (using `GetEntityIndices`-style dense access or an added `IComponentStorage.GetDenseEntityPtr`), and demote whichever storage was chosen from the filter list to a probe. `None` storages must stay as predicates (they are negations), which is correct today.

## `filter-predicate-interface-dispatch-per-entity`

**PassesFilters runs a List enumerator + interface dispatch per filter per entity in the innermost loop**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/FilteredQuery.cs:13` |
| Kategori | performance · ecs-query |
| Etki | ~5-10ns per filter per entity (enumerator + 2 chained non-inlined calls). For 100k entities x 2 filters that is roughly 1-2 ms per query pass; hoisting the sparse pointers would reduce it to ~1ns per filter per entity. |
| Test | Correctness covered by Tests/Runtime/ECS/Query/FilteredQueryTests.cs (7 tests, max 4 entities each). No performance test exercises PassesFilters. |

```csharp
            if (withFilters != null)
            {
                foreach (var storage in withFilters)
                {
                    if (!storage.Contains(entity))
                        return false;
                }
            }
```

**Sorun:** Per entity, per filter, this executes: a `List<T>.Enumerator` construction, a `_version != _list._version` check inside `MoveNext`, an interface dispatch on `IComponentStorage.Contains` (ComponentStorage.cs:37) which is a second non-inlined call into `SparseSet.Contains` (SparseSet.cs:70), and a null check on the list. The `[MethodImpl(AggressiveInlining)]` at line 10 cannot take effect — the method contains two loops and interface calls and is far past any inliner budget on Mono/IL2CPP, so the attribute is inert. (Note: the `foreach` does NOT box, because the static type is `List<T>` and its struct enumerator is used — the enumerator-boxing hazard does not apply here.)

**Senaryo:** A filtered query over 100,000 Position entities with `.Also<Alive>().None<Dead>()` performs 200,000 interface dispatches plus 200,000 enumerator version checks plus 300,000 null/bounds checks per frame, none of which can inline or be hoisted, on top of the 100,000 delegate invocations from `action`.

**Düzeltme:** Store the filters as `IComponentStorage[]` (or the fixed inline buffer proposed in `filtered-builder-list-alloc-per-construction`) and index with a plain `for` loop, which removes the enumerator and version check. Better: keep the filter storages as `ComponentStorage<T>`-typed generic fields for the common 1-2-filter case so `Contains` devirtualizes, or hoist each filter's `GetSparsePtr()` + `_sparse.Length` out of the loop and inline the check as `entity < len && sparse[entity] >= 0`, which is 2 instructions with no call at all.

## `archetype-tracking-list-keeps-dead-entities`

**Entities destroyed through EntityManager/World are never removed from the archetype tracking list**

| | |
|---|---|
| Konum | `Runtime/ECS/Archetypes/ArchetypeManager.cs:50` |
| Kategori | bug · ecs-storage |
| Etki | 8 bytes of managed heap per destroyed-but-tracked entity, retained for the life of the ArchetypeManager; plus a linearly growing iteration cost for any consumer of GetEntities<T>. 60k dead entries = 480 KB and a 1200x iteration blow-up in the bullet example. |
| Test | Test gap that hides the bug: Tests/Runtime/ECS/ArchetypeTests.cs:72 `DestroyEntity_RemovesFromTracking` destroys via `_archetypes.DestroyEntity<PlayerDescriptor>(entity)` — the one path that does maintain the list. No test destroys an archetype-created entity via `_entities.DestroyEntity` and then checks `GetEntityCount<T>()`. |

```csharp
            _entitiesByArchetype[typeof(T)].Add(entity);
            return entity;
```

**Sorun:** The only code that removes an entity from `_entitiesByArchetype` is `ArchetypeManager.DestroyEntity<T>` (line 87). `EntityManager.DestroyEntity` (Runtime/ECS/Core/EntityManager.cs:108) and `World.DestroyEntity` (Runtime/ECS/World/World.cs:97) — the two most obvious ways to destroy an entity, and the ones every non-archetype example uses — know nothing about the ArchetypeManager. An entity spawned by `CreateEntity<T>()` and destroyed by either of those stays in the list forever. `GetEntityCount<T>()` (line 99) then reports the number of entities ever created rather than the number alive, and `GetEntities<T>()` (line 94) hands callers stale handles whose `Exists()` is false. The list grows without bound in a spawn/despawn loop.

**Senaryo:** A bullet system spawns via `archetypes.CreateEntity<BulletDescriptor>()` at 100 bullets/second and despawns via `world.DestroyEntity(bullet)` (the API the World facade exposes). After 10 minutes the tracking list holds 60,000 entries of which ~50 are alive: `GetEntityCount<BulletDescriptor>()` returns 60,000, any system iterating `GetEntities<BulletDescriptor>()` walks 60,000 handles to find 50 live ones, and the List has consumed 480 KB of managed heap it will never release. `ArchetypeManager.Clear()` at line 111-112 iterates all 60,000 and calls `DestroyEntity` on each, which no-ops for the 59,950 already dead.

**Düzeltme:** Stop keeping a second source of truth. Either (a) route destruction through the ArchetypeManager by having `EntityManager` raise a destroy callback the ArchetypeManager subscribes to, or (b) drop `_entitiesByArchetype` entirely and derive membership from the component set — add a zero-size `ArchetypeTag<T>` component in `InitializeComponents` and let the existing query machinery enumerate it, which is O(1) to remove because `EntityManager.DestroyEntity` already calls `_store.RemoveEntity`. Option (b) also fixes the O(n^2) teardown. As a minimum stopgap, have `GetEntities<T>`/`GetEntityCount<T>` compact the list by filtering on `_entities.Exists(e)`.

## `em-clear-resets-versions-stale-handle-alias`

**EntityManager.Clear() zeroes the version array, so pre-Clear Entity handles alias post-Clear entities**

| | |
|---|---|
| Konum | `Runtime/ECS/Core/EntityManager.cs:273` |
| Kategori | bug · ecs-storage |
| Etki | Correctness, not performance: one aliased handle per entity index reused after each Clear(). Cost of the fix is O(_nextEntityIndex) integer increments once per Clear() — negligible versus the MemClear it replaces. |
| Test | Tests/Runtime/ECS/Core/EntityManagerTests.cs:208 `Clear_RemovesAllEntities` asserts only `EntityCount == 0`; it never re-checks `Exists()` on a pre-Clear handle. Tests/Runtime/Performance/JobSystemPerformanceTests.cs:124 calls `_entityManager.Clear()` between measurements without checking handle validity. No test covers the alias. |

```csharp
                UnsafeUtility.MemClear(_versions.GetUnsafePtr(), _versions.Length * sizeof(int));
                UnsafeUtility.MemClear(_active.GetUnsafePtr(), _active.Length * sizeof(byte));
            }

            _recycledIndices.Clear();
            _nextEntityIndex = 1;
```

**Sorun:** The version counter is the only thing distinguishing a recycled index from the handle that previously occupied it. `Clear()` destroys that counter (memclear to 0) and simultaneously resets `_nextEntityIndex` to 1, so the very next `CreateEntity()` re-issues `(index=1, version=1)` — byte-identical to the first handle ever issued. `Exists()` at line 136 compares exactly those two fields, so every stale handle from before the Clear silently validates against a completely unrelated new entity. This defeats the entire purpose of the generation field, which `EntityPropertyTests` otherwise pins carefully for the destroy/recreate path.

**Senaryo:** ```
var e = m.CreateEntity();            // (1, 1)
m.AddComponent(e, new Health{Value=100});
m.Clear();                            // scene teardown / level reload
var other = m.CreateEntity();         // (1, 1) — identical handle
m.AddComponent(other, new Health{Value=5});
m.Exists(e);                          // true  (wrong)
m.GetComponent<Health>(e).Value;      // 5     — reads `other`'s data
m.DestroyEntity(e);                   // destroys `other`
```
Any gameplay object, UI binding, or cached list holding an Entity across a level reload now silently mutates a different entity. Secondary defect in the same statement: `_versions.Length * sizeof(int)` is an int multiply feeding `MemClear`'s `long size` parameter, so a capacity above 536,870,911 produces a negative byte count.

**Düzeltme:** Do not clear versions. Increment them instead, so no old handle can ever be re-issued: replace the `_versions` MemClear with `for (int i = 1; i < _nextEntityIndex; i++) if (_active[i] == 1) _versions[i]++;` (or unconditionally `_versions[i]++` across the used range), keep the `_active` MemClear, and keep `_nextEntityIndex = 1`. Also cast the MemClear sizes to `long`: `(long)_versions.Length * sizeof(int)`.

## `em-restorestate-negative-index-oob-write`

**EntityManager.RestoreState writes _active[idx] with only an upper-bound check on caller-supplied indices**

| | |
|---|---|
| Konum | `Runtime/ECS/Core/EntityManager.cs:331` |
| Kategori | security · ecs-storage |
| Etki | Save/load path only, not per-frame. One extra compare per restored index. Without it: one arbitrary-offset 1-byte heap write per malformed array element, plus a permanently wrong EntityCount. |
| Test | Zero. No test in Tests/ calls RestoreState or CaptureState; the only caller is Editor/Windows/TimeMachineWindow.cs. |

```csharp
            for (int i = 0; i < activeIndices.Length; i++)
            {
                int idx = activeIndices[i];
                if (idx < _active.Length)
                {
                    _active[idx] = 1;
                    _entityCount++;
                }
            }
```

**Sorun:** `idx` comes from a caller-supplied `int[]` and is checked only against the upper bound. A negative value passes, and `_active[idx] = 1` becomes an unchecked single-byte write at `activeBase + idx` in release builds (no ENABLE_UNITY_COLLECTIONS_CHECKS). Because `idx` is a full 32-bit int, the write offset is attacker-chosen over a +/-2 GB window from the `_active` allocation. Three further validation gaps in the same method: `idx == 0` is accepted even though index 0 is reserved for `Entity.Null`; duplicate indices each bump `_entityCount`, desynchronizing `EntityCount` from reality; and the separate loop at 341-347 writes `_versions[i] = versions[i]` with no check that an active index ends up with a non-zero version, so a truncated `versions` array leaves every restored entity failing `Exists()` (version 0 after the `Clear()` memclear at line 273). SecurityReports/2026-05-22-medium-status-review.md row 5 records this as PARTIAL with "Bounds check var" — the bound that exists is only the upper one.

**Senaryo:** `Editor/Windows/TimeMachineWindow.cs:930` calls `entityManager.RestoreState(NextEntityIndex, ActiveIndices, Versions)` with arrays taken from a serialized snapshot. A snapshot containing `ActiveIndices = [-100000]` writes the byte 1 at `activeBase - 100000` and increments `_entityCount` to 1 for a nonexistent entity. `RestoreState` is `public` on a Runtime type, so any save/load or replay system that round-trips this state through untrusted data (cloud save, mod, level file) has an arbitrary-offset byte-write primitive.

**Düzeltme:** Validate up front and reject rather than skip: `if (nextEntityIndex < 1) throw new ArgumentOutOfRangeException(nameof(nextEntityIndex));` then inside the loop `if (idx <= 0 || idx >= _active.Length) throw new ArgumentException($"activeIndices[{i}] = {idx} is out of range");`, and skip indices already marked active so `_entityCount` cannot double-count. Also assert `versions.Length >= nextEntityIndex` and that `versions[idx] > 0` for every active idx.

## `getorcreate-storage-on-read-paths`

**HasComponent/RemoveComponent/GetComponent permanently allocate a Persistent ComponentStorage for types that were never added**

| | |
|---|---|
| Konum | `Runtime/ECS/Core/EntityManager.cs:184` |
| Kategori | allocation · ecs-storage |
| Etki | ~5.2 KB of Allocator.Persistent native memory per probe-only component type, allocated once and never freed; plus one extra SparseSet.Remove probe per phantom type on every DestroyEntity, forever. |
| Test | None. Tests/Runtime/ECS/Core/EntityManagerTests.cs:81 calls `HasComponent<Position>` only after `AddComponent<Position>`; no test probes a never-added type or asserts on `Store.GetComponentTypes()` content. |

```csharp
            var storage = _store.GetOrCreateStorage<T>();
            return storage.Contains(entity.Index);
```

**Sorun:** `GetOrCreateStorage<T>()` is a mutating call used from three read-only or remove-only paths: `HasComponent` (line 184), `RemoveComponent` (line 174), and `GetComponent` (line 194). Probing for a component type that no entity has therefore constructs `new ComponentStorage<T>(1024, 256)` (ComponentStorage.cs:140), which allocates three `Allocator.Persistent` NativeArrays in the SparseSet constructor (lines 23-25): 1024 ints = 4 KB sparse, 256 ints = 1 KB dense, 256 * sizeof(T) data. Nothing ever frees it before `Dispose()`. Three knock-on effects: (1) the phantom type is returned by `ComponentStore.GetComponentTypes()` (line 176-179), which `Editor/HotReload/EntityStatePreserver.cs:38` and `Editor/Windows/TimeMachineWindow.cs:911` enumerate into snapshots; (2) `ComponentStore.RemoveEntity` (line 151-157) walks *every* registered storage on *every* `DestroyEntity`, so each phantom type adds a permanent `SparseSet.Remove` call to every entity destruction for the life of the world; (3) `Runtime/ECS/Query/FilteredQuery.cs:62` calls `GetOrCreateStorage<TExclude>()` for exclusion filters, so a `Without<Dead>()` query registers `Dead` even if nothing ever adds it.

**Senaryo:** A system does `if (!em.HasComponent<Stunned>(e)) { ... }` on entities in a world where `Stunned` is never added. The first call allocates ~5.2 KB of Persistent native memory that is never reclaimed, `Stunned` now appears in every editor snapshot as a component type with zero entities, and every subsequent `DestroyEntity` in the world does one extra sparse-set probe. With 20 such probe-only or exclusion-only types that is ~104 KB of dead native memory and 20 extra probes per entity destruction — at the README's 100k-entity scale and a 10%/frame churn rate, 200,000 wasted probes per frame.

**Düzeltme:** Add `public bool TryGetStorage<T>(out ComponentStorage<T> storage) where T : unmanaged, IComponent` to `ComponentStore` (a plain `TryGetValue` with no create) and use it in `HasComponent` (return false when absent), `RemoveComponent` (return when absent), and `GetComponent` (throw `ThrowComponentNotFound<T>` when absent, which is the correct behaviour today anyway since `SparseSet.Get` throws). Keep `GetOrCreateStorage` for `AddComponent` and `SetComponent` only. `FilteredQuery.Without<TExclude>()` should hold a nullable storage and treat absent as "nothing excluded".

## `getcomponent-typeof-dictionary-per-call`

**Every component operation resolves storage through a Dictionary<Type, IComponentStorage> lookup plus a castclass**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/ComponentStorage.cs:137` |
| Kategori | performance · ecs-storage |
| Etki | ~20-40ns per component operation on IL2CPP, per call. At 10k random GetComponent/HasComponent calls per frame that is 0.2-0.4 ms/frame; the fix reduces it to a static-field read plus an array index (~1-2ns). |
| Test | Tests/Benchmarks/ECSBenchmarks.cs:64-77 `Iteration_10k_Entities_Benchmark` measures the whole loop (GetAllEntities + GetEntity + HasComponent + GetComponent) as one number, so the resolve cost is never isolated and a regression or improvement in it is invisible. No microbenchmark exists for storage resolution alone. |

```csharp
            Type type = typeof(T);
            if (!_storages.TryGetValue(type, out var storage))
            {
                storage = new ComponentStorage<T>(_defaultSparseCapacity, _defaultDenseCapacity);
                _storages[type] = storage;
            }
            return (ComponentStorage<T>)storage;
```

**Sorun:** `GetOrCreateStorage<T>()` is called on every single component operation: `AddComponent` (EntityManager.cs:154 and 164), `RemoveComponent` (174), `HasComponent` (184), `GetComponent` (194), `GetComponentRef` (209), `SetComponent` (230). Each call performs a `Dictionary<Type, IComponentStorage>` lookup whose comparer is `ObjectEqualityComparer<Type>` — a virtual `RuntimeType.GetHashCode()`, a bucket probe, and a virtual `Equals` — followed by a `castclass` to `ComponentStorage<T>` on line 143. None of that varies with the entity: it is per-call overhead on a value that is fixed at JIT/AOT time for each closed generic. This is precisely why README.md:224 reports GetComponent at 67ns random access while README.md:221 reports the query path at 6.6ns/entity — the query resolves the storage once (FilteredQuery.cs:69) and then walks raw pointers, while the random-access path pays the dictionary every time.

**Senaryo:** Not a crash — a measurable throughput ceiling. A gameplay system doing random-access component reads on 10,000 entities per frame executes 10,000 `Dictionary<Type,V>` lookups + 10,000 castclass checks per frame. At the ~20-40ns a Type-keyed dictionary lookup costs on IL2CPP, that is 0.2-0.4 ms/frame spent resolving a constant, roughly half of the documented 67ns per GetComponent. Tests/Benchmarks/ECSBenchmarks.cs:66-72 shows the intended usage pattern doing exactly this in a loop.

**Düzeltme:** Give each closed generic a resolved slot instead of hashing a Type. Assign a dense int id once per component type (`internal static class ComponentTypeId<T> { internal static readonly int Value = Interlocked.Increment(ref s_next) - 1; }`) and back `ComponentStore` with an `IComponentStorage[]` indexed by that id, growing on registration. Resolution becomes a static field read plus an array index — no hashing, no virtual dispatch, no castclass (store `ComponentStorage<T>` in a per-store, per-T cache so the cast is also eliminated). This is a purely additive change; the Dictionary can stay for the `Type`-keyed reflection APIs at lines 192-232.

## `sparseset-add-negative-index-oob-write`

**SparseSet.Add performs an unchecked out-of-bounds read and write for a negative entityIndex (prior finding unit-04 #14 marked FIXED but never fixed)**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:33` |
| Kategori | security · ecs-storage |
| Etki | One predictable compare-and-branch per Add (sub-nanosecond, ~1 instruction). Without it: unbounded 4-byte heap write per malformed call, in release builds only. |
| Test | Tests/Runtime/ECS/Storage/SparseSetTests.cs has 12 tests and passes only non-negative indices (0, 1, 2, 5, and loop counters). No negative-index test exists for any SparseSet method. |

```csharp
            EnsureSparseCapacity(entityIndex + 1);

            if (_sparse[entityIndex] >= 0)
            {
                _data[_sparse[entityIndex]] = component;
                return;
            }
```

**Sorun:** There is no `entityIndex < 0` guard. For a negative `entityIndex`, `EnsureSparseCapacity(entityIndex + 1)` returns immediately (`required <= _sparse.Length`), and line 35 then evaluates `_sparse[entityIndex]`. In a release player `ENABLE_UNITY_COLLECTIONS_CHECKS` is undefined, so `NativeArray<int>.this[int]` compiles to an unchecked `UnsafeUtility.ReadArrayElement` — this is a raw read at `sparseBase + entityIndex*4`, arbitrarily far before the allocation. If that garbage word happens to be >= 0 it is then used as a dense index at line 37 for an unchecked *write* into `_data`. If it is < 0, control reaches line 45 `_sparse[entityIndex] = _count;` which is an unconditional 4-byte write at `sparseBase + entityIndex*4`. `Documentation~/ECS.md` itself states "Bounds checking enabled with ENABLE_UNITY_COLLECTIONS_CHECKS", confirming there is no release-build check. The same statement also overflows: `entityIndex == int.MaxValue` makes `entityIndex + 1` negative, `EnsureSparseCapacity` returns, and line 35 reads far past the end.

**Senaryo:** `ComponentStorage<T>.Add(int, T)` and `SparseSet<T>.Add(int, T)` are both public. A caller that derives an entity index from a network packet, a save file, a `-1` sentinel returned by `GetDenseIndex`, or arithmetic that underflows calls `storage.Add(-4096, cmp)`. In the Editor this throws IndexOutOfRangeException; in the shipped player it writes `_count` into heap memory 16 KB before the sparse array — silent heap corruption with a crash at an unrelated site later. Prior audit finding unit-04 #14 named exactly this and SecurityReports/2026-05-22-low-status-review.md:125 lists it under FIXED ("HIGH'da FIXED, dup"), deduping the unchecked *write* path into the `Get` finding, which itself only received an upper-bound check.

**Düzeltme:** Add a single guard at the top of `Add`: `if (entityIndex < 0) throw new ArgumentOutOfRangeException(nameof(entityIndex), "Entity index must be non-negative");`. A branch on a value already in a register costs well under a nanosecond and is fully predicted on the hot path. Then remove the `[low-status-review] FIXED` claim for unit-04 #14.

## `sparseset-query-family-negative-index-oob-read`

**Remove/Contains/Get/Set/TryGet/GetDenseIndex check only the upper bound of entityIndex, not the lower (prior finding unit-04 #1 claimed FIXED, only half-fixed)**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:51` |
| Kategori | security · ecs-storage |
| Etki | Zero measurable cost — `(uint)i < (uint)len` is the same single `cmp`/`jae` pair the current `i < len` emits. Without it: unbounded OOB read on every query method and an OOB write in Remove, release builds only. |
| Test | Tests/Runtime/ECS/Storage/SparseSetTests.cs never passes a negative index. `Remove_NonExistingElement_ReturnsFalse` (line 66) tests index 5 on an empty set, which exercises the upper-bound path only. |

```csharp
            if (entityIndex >= _sparse.Length || _sparse[entityIndex] < 0)
                return false;

            int denseIndex = _sparse[entityIndex];
            int lastIndex = _count - 1;
```

**Sorun:** Six methods use the identical half-guard `entityIndex >= _sparse.Length` and then index `_sparse[entityIndex]` in the same expression: `Remove` (line 51), `Contains` (line 72 `return entityIndex < _sparse.Length && _sparse[entityIndex] >= 0;`), `Get` (line 77), `TryGet` (line 96 `if (entityIndex < _sparse.Length)`), `Set` (line 112), and `GetDenseIndex` (line 122 `public int GetDenseIndex(int entityIndex) => entityIndex < _sparse.Length ? _sparse[entityIndex] : -1;`). A negative index satisfies every one of these conditions and reaches an unchecked `_sparse[negative]` in release builds. `GetRef` (line 84) is the only method in the file with the correct two-sided check `if (entityIndex < 0 || entityIndex >= _sparse.Length)`, which proves the omission elsewhere is an oversight rather than a convention. `Remove` compounds it: after the OOB read, line 65 `_sparse[entityIndex] = -1;` is an unconditional OOB *write*.

**Senaryo:** `ComponentStorage<T>.Contains(-1)` / `.Remove(-1)` / `.Get(-1)` are all public and all reachable from `EntityManager` if an `Entity.Index` is ever negative — which `Entity`'s public constructor `new Entity(-5, 1)` permits, and which `RestoreState` can install. In a release player, `set.Remove(-1000)` reads `sparseBase - 4000` and, if that word is >= 0, writes -1 to `sparseBase - 4000` and then executes the swap-and-pop against `_dense`/`_data` using the garbage dense index — corrupting the live component array. SecurityReports/2026-05-22-status-review.md row 6 marks this FIXED, citing only the `entityIndex >= _sparse.Length` half.

**Düzeltme:** Change the guard in all six methods to the form already used by `GetRef`: `if (entityIndex < 0 || entityIndex >= _sparse.Length)`. For `Contains`/`GetDenseIndex` the unsigned trick is free: `(uint)entityIndex < (uint)_sparse.Length` collapses both bounds into one compare with no extra instruction.

## `sparseset-struct-copy-double-free`

**SparseSet is a public mutable IDisposable struct with no ownership sentinel — a silent copy causes use-after-free and double-free**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:177` |
| Kategori | security · ecs-storage |
| Etki | One byte of struct size and one predicted branch per Dispose (called once per storage teardown, not on any hot path). Without it: double free of three Persistent native allocations, or dereference of freed memory once per query iteration after a copy grows. |
| Test | No coverage. Tests/Runtime/ECS/Storage/SparseSetTests.cs disposes each set exactly once from the original variable; nothing copies a SparseSet, nothing disposes twice. |

```csharp
        public void Dispose()
        {
            if (_sparse.IsCreated) _sparse.Dispose();
            if (_dense.IsCreated) _dense.Dispose();
            if (_data.IsCreated) _data.Dispose();
            _count = 0;
        }
```

**Sorun:** `Dispose()` guards on `NativeArray.IsCreated`, which is per-instance state. `NativeArray.Dispose()` nulls `m_Buffer` on the instance it is called on; every *copy* of that NativeArray retains the freed pointer and still reports `IsCreated == true`. Because `SparseSet<T>` is declared `public unsafe struct` (line 7), every by-value copy is an independent owner of the same three native allocations. There is no `_isCreated`/`_disposeSentinel` field. This is notable because the sibling public disposable struct in this codebase, `EntityCommandBuffer`, was given exactly that guard during the same prior audit round (`private bool _isCreated;` at Runtime/ECS/Jobs/EntityCommandBuffer.cs:44, checked at line 151) — SparseSet was skipped.

**Senaryo:** `ComponentStorage<T>.GetSparseSet()` returns `ref SparseSet<T>`, but C# lets the caller drop the ref: `var set = storage.GetSparseSet();` compiles with no warning and yields a copy (Runtime/ECS/Query/FilteredQuery.cs:69 and Runtime/ECS/Jobs/EntityJobs.cs:20 correctly write `ref var set = ref ...`, so the correct form is one keyword away from the broken one). Then: (1) `set.Add(e, c)` grows past capacity -> `EnsureDenseCapacity` calls `_dense.Dispose()` and `_data.Dispose()` on the shared buffers and assigns the new arrays to the *copy*; the owning `ComponentStorage` now holds dangling pointers, and the next `FilteredQuery.ForEach` dereferences `set.GetDataPtr()` on freed memory. (2) `set.Dispose()` followed by the owner's `ComponentStorage.Dispose()` at line 118 double-frees all three allocations. In release builds neither is detected. The public `SparseSet<T>` constructor makes the same trap available directly to user code.

**Düzeltme:** Add an ownership sentinel: `private byte _isCreated;` set to 1 in the constructor, and make `Dispose()` early-return `if (_isCreated == 0) return;` then set it to 0 before freeing. That makes double-Dispose a no-op (it does not fix the grow-on-a-copy aliasing). To close the aliasing too, either stop exposing the struct — make `GetSparseSet()` return a `ref readonly` and add mutating wrappers on `ComponentStorage<T>` — or mark the type `[Obsolete]` for direct construction and hand out only the pointer/slice accessors.

## `sparseset-1m-cap-not-enforced-at-entity-creation`

**The 1,048,576 sparse capacity ceiling is undocumented and unenforced at entity creation, so it surfaces as a mid-initialization throw**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:191` |
| Kategori | api-hazard · ecs-storage |
| Etki | Hard ceiling at 1,048,575 entity indices. Zero runtime cost to enforce it correctly (one compare in CreateEntity, already on a cold-ish path). Currently the failure mode is a leaked half-built entity per occurrence. |
| Test | None. The largest entity count in any test is 10,000 (Tests/Stress/MassEntityTests.cs:33, Tests/Runtime/ECS/ArchetypeTests.cs:119). Nothing approaches the cap and nothing asserts what happens at it. |

```csharp
            if (required > MaxSparseCapacity)
                throw new InvalidOperationException(
                    $"Entity index requires sparse capacity {required} which exceeds maximum {MaxSparseCapacity}");
```

**Sorun:** `MaxSparseCapacity` is 1_048_576 (line 185), but `EntityManager` has no matching limit: `EnsureCapacity` grows `_versions`/`_active` without bound and `CreateEntity` will happily hand out `Entity` handles with `Index >= 1_048_576`. The ceiling then materializes at the first `AddComponent` on such an entity, as an `InvalidOperationException` thrown from inside `SparseSet.Add` with a message about "sparse capacity" rather than about an entity limit. The limit appears nowhere in README.md, Documentation~/ECS.md, or Documentation~/Benchmarks.md (grepped for 1_048_576 / 1048576 / "million" / "max entit" — no hits), so a project that provisions for 2 M entities discovers it in production.

**Senaryo:** A simulation reaches entity index 1_048_576 (achievable at ~1 M concurrently-live entities, since indices are only recycled through `_recycledIndices`). `em.AddComponent(e, pos)` throws `InvalidOperationException: Entity index requires sparse capacity 1048577 which exceeds maximum 1048576`. Through `ArchetypeManager.CreateEntity<T>` (lines 48-50) the failure is worse than a plain throw: `_entities.CreateEntity()` on line 48 has already succeeded, `descriptor.InitializeComponents` on line 49 throws partway through the component list, and line 50 `_entitiesByArchetype[typeof(T)].Add(entity)` never executes — so the world is left with a live, half-initialized entity that the ArchetypeManager does not track and that `Clear()`/`Dispose()` will therefore never destroy.

**Düzeltme:** Enforce the ceiling where entities are minted, with a message that names the real constraint: in `EntityManager.CreateEntity`/`CreateEntities`, `if (index >= SparseSet<int>.MaxSparseCapacity) throw new InvalidOperationException($"Entity index limit ({MaxEntityIndex}) reached");` (promote the constant to a shared public `EcsLimits.MaxEntityIndex`). Document the limit in README.md's ECS section and Documentation~/ECS.md. Separately, make `ArchetypeManager.CreateEntity<T>` exception-safe: wrap lines 49-50 in try/catch and call `_entities.DestroyEntity(entity)` before rethrowing.

## `reactive-notify-array-alloc-per-write`

**ReactiveComponentStorage allocates a fresh callback array on every Add/Set/Remove notification**

| | |
|---|---|
| Konum | `Runtime/ECS/Reactive/ReactiveComponentStorage.cs:147` |
| Kategori | allocation · ecs-systems |
| Etki | 32 bytes + one `Array.Copy` per component write with 1 subscriber; 40 B with 2 subscribers, 48 B with 3, etc. At 1,000 reactive writes/frame @60 fps ≈ 1.9 MB/s. Directly contradicts `Documentation~/Benchmarks.md:403` "All hot paths are allocation-free after initialization." |
| Test | ReactiveSystemPerformanceTests.cs measures wall-clock only via `Measure.Method(...)` (lines 25, 57, 83) with no `Measure.ProfilerMarkers`, no `GC.GetTotalMemory` delta, and no `Assert` on allocation. `Benchmark_NonReactive_Baseline_10k` (line 103) exists to compare against but nothing asserts the reactive/raw ratio, so this regression is invisible to CI. |

```csharp
                var snapshot = _onChangeCallbacks.ToArray();
                foreach (var callback in snapshot)
                {
                    try { callback(entityIndex, oldValue, newValue); }
                    catch (Exception ex) { Debug.LogError($"[ReactiveComponentStorage<{typeof(T).Name}>] Exception in OnChange callback: {ex}"); }
                }
```

**Sorun:** `NotifyChange` (line 147), `NotifyAdd` (line 105 `var snapshot = _onAddCallbacks.ToArray();`) and `NotifyRemove` (line 126 `var snapshot = _onRemoveCallbacks.ToArray();`) each call `List<T>.ToArray()` on every single notification. This was added to fix prior finding unit-06 #2 (collection-modified-during-enumeration), but it converted a reentrancy bug into a per-write heap allocation on the layer the README markets as the perf-critical MVCS+ECS seam. The raw path it wraps — `ComponentStorage<T>.Set` → `SparseSet<T>.Set` (SparseSet.cs:110-115) — is a bounds check plus one indexed native write with zero allocations.

**Senaryo:** A view-sync system calls `rem.SetReactiveComponent(e, newHealth)` for 1,000 entities per frame with one OnChange subscriber registered. Each call runs `_onChangeCallbacks.ToArray()` → one `Action<int,T,T>[1]` (16 B header + 8 B length + 8 B element = 32 B on 64-bit Mono/IL2CPP). That is 1,000 arrays/frame = 60,000 arrays/s ≈ 1.9 MB/s of pure garbage, on a code path whose underlying storage write costs ~5 ns and 0 bytes. `Benchmark_ReactiveChange_10k` (ReactiveSystemPerformanceTests.cs:57-66) drives exactly this: 10,000 Sets × 10 measurement iterations = 100,000 array allocations ≈ 3.2 MB of garbage, and the test asserts nothing about allocation.

**Düzeltme:** Keep a reusable `private Action<int,T,T>[] _changeSnapshot;` field, grow it only when `_onChangeCallbacks.Count` exceeds its length, and `CopyTo` into it before iterating (same for add/remove). Or drop the snapshot entirely and iterate by index with a `for (int i = 0; i < list.Count; i++)` loop plus a deferred add/remove queue processed when `_notifyDepth` returns to 0 — the depth guard at lines 96/117/138 already tracks that.

## `reactive-add-overwrites-without-notification`

**ReactiveComponentStorage.Add on an existing entity overwrites the stored value and fires no callback at all**

| | |
|---|---|
| Konum | `Runtime/ECS/Reactive/ReactiveComponentStorage.cs:51` |
| Kategori | bug · ecs-systems |
| Etki | Correctness, not perf. 100% of duplicate-Add writes are invisible to subscribers. |
| Test | `ReactiveComponentStorageTests.Add_DoesNotTrigger_WhenEntityAlreadyExists` (lines 36-50) pins HALF of this behaviour — it asserts `addCount == 1` after two Adds — but it registers no OnChange subscriber and never asserts the stored value, so the missing change notification is unpinned and untested. The passing test makes the suppressed OnAdd look intentional while hiding the real bug. |

```csharp
        public void Add(int entityIndex, T component)
        {
            bool isNew = !_storage.Contains(entityIndex);
            _storage.Add(entityIndex, component);

            if (isNew)
                NotifyAdd(entityIndex, component);
        }
```

**Sorun:** `_storage.Add` bottoms out in `SparseSet<T>.Add` (SparseSet.cs:31-46), whose first branch is `if (_sparse[entityIndex] >= 0) { _data[_sparse[entityIndex]] = component; return; }` — it silently OVERWRITES an existing value. `ReactiveComponentStorage.Add` correctly suppresses `OnAdd` in that case but never routes to `NotifyChange`, so a genuine value mutation is committed to storage with zero notifications. The reactive contract has a hole: not every write raises an event.

**Senaryo:** `storage.Add(5, new Health{Value=100})` fires OnAdd(5,100). Then `storage.Add(5, new Health{Value=200})` — stored value becomes 200, `storage.Get(5).Value == 200`, but no OnAdd and no OnChange fires. Any view/UI bound through `OnChange` keeps rendering 100 indefinitely. This is not exotic: `ReactiveEntityManager.AddReactiveComponent` (ReactiveEntityManager.cs:52-56) forwards straight to this method, and re-adding a component is the normal idempotent spawn pattern.

**Düzeltme:** Make Add symmetric with Set: `if (_storage.Contains(entityIndex)) { var old = _storage.Get(entityIndex); _storage.Set(entityIndex, component); NotifyChange(entityIndex, old, component); return; } _storage.Add(entityIndex, component); NotifyAdd(entityIndex, component);` — or throw on duplicate add so the silent divergence is impossible.

## `reactive-no-unsubscribe-token-closure-leak`

**Reactive storage still has no SubscriptionToken and no way to unsubscribe a closure — prior finding unit-06 #7 is listed as FIXED but is not**

| | |
|---|---|
| Konum | `Runtime/ECS/Reactive/ReactiveComponentStorage.cs:33` |
| Kategori | api-hazard · ecs-systems |
| Etki | Unbounded: one leaked object graph per never-unsubscribed view, plus one `Debug.LogError` (full exception string, typically 1-4 KB) per component write per dead subscriber once the target is destroyed. |
| Test | `ReactiveComponentStorageTests.Unsubscribe_StopsCallbacks` (lines 133-150) passes only because it uses a *local function* `void OnAdd(int entity, HealthComponent component)` — which the compiler caches as a single delegate instance — deliberately sidestepping the closure case that actually occurs in application code. No test attempts to unsubscribe a capturing lambda. |

```csharp
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SubscribeOnAdd(Action<int, T> callback) => _onAddCallbacks.Add(callback);
```

**Sorun:** Subscribe returns `void`; the only removal path is `UnsubscribeOnAdd(Action<int,T>)` (line 42), which uses `List<T>.Remove` → `EqualityComparer<Action<int,T>>.Default` → `Delegate.Equals`, comparing `Target` by reference. A lambda that captures anything gets a fresh display-class `Target` per call site evaluation, so the caller must retain the exact delegate instance. `ReactiveEntityManager` is worse: it exposes `OnAdd`/`OnRemove`/`OnChange` (lines 79-95) and NO unsubscribe API at all — the only escape hatch is reaching through `GetReactiveStorage<T>()`. Meanwhile the rest of v2.0 migrated to `SubscriptionToken` (EventBus.cs:163-189, ReactiveProperty.cs:63-67); the reactive ECS layer was left behind even though `SubscriptionToken`'s constructor is `internal` (SubscriptionToken.cs:22) and therefore already reachable from this assembly. STALE-CLAIM FLAG: `SecurityReports/2026-05-22-low-status-review.md:129` lists "unit-06 #7 Callback list snapshot" under "## FIXED (13 adet)". unit-06 #7 is "Memory Leak from Unsubscribed Handlers" whose recommendation was "return an `IDisposable` subscription token that auto-unsubscribes" (unit-06-ecs-reactive-world.md:118-127) — the reviewer conflated it with finding #2 ("Collection Modified During Enumeration", whose fix WAS the snapshot). #7 is not fixed.

**Senaryo:** A `MonoBehaviour` view does `rem.OnChange<Health>((e, o, n) => _bar.fillAmount = n.Value / 100f);` in `OnEnable`. There is no returned handle and `ReactiveEntityManager` has no `Off`/`Unsubscribe` method, so `OnDisable`/`OnDestroy` cannot remove it. The `List<Action<int,Health,Health>>` in the storage holds the closure → the closure holds `this` → the destroyed MonoBehaviour is never collected, and every subsequent `SetReactiveComponent<Health>` invokes it, hitting Unity's fake-null on `_bar` and throwing — caught and logged by line 151, producing one `Debug.LogError` with a full exception string per write per dead subscriber, forever.

**Düzeltme:** Change `SubscribeOnAdd/OnRemove/OnChange` to return `Strada.Core.SubscriptionToken` built over the removal action (mirroring ReactiveProperty.cs:63-67), and surface the tokens from `ReactiveEntityManager.OnAdd/OnRemove/OnChange` so callers can enroll them in a `BindingScope` or `SystemBase._disposables`. Keep the `Action`-based Unsubscribe overloads for one deprecation cycle.

## `reactive-destroy-stale-handle-strips-live-entity`

**ReactiveEntityManager.DestroyEntity ignores entity version — a stale handle strips the recycled entity's reactive components**

| | |
|---|---|
| Konum | `Runtime/ECS/Reactive/ReactiveEntityManager.cs:41` |
| Kategori | bug · ecs-systems |
| Etki | Correctness/data-corruption. One recycled index is enough; no allocation or perf cost involved. |
| Test | None. `ReactiveEntityManagerTests` never destroys an entity (no `DestroyEntity` call in the whole file), never recycles an index, and never uses a stale handle. `Dispose_CleansUpAllStorages` (line 126) is the only teardown test and it only checks a fresh manager's storage is empty. |

```csharp
        public void DestroyEntity(Entity entity)
        {
            foreach (var storage in _reactiveStorages.Values)
            {
                if (storage is IReactiveStorage reactive)
                    reactive.Remove(entity.Index);
            }
            _entityManager.DestroyEntity(entity);
        }
```

**Sorun:** The reactive cleanup loop keys purely on `entity.Index` with no liveness/version check, while the very next line, `_entityManager.DestroyEntity(entity)`, does validate: it starts with `if (!Exists(entity)) return;` (EntityManager.cs:110-111), and `Exists` compares `_versions[entity.Index] == entity.Version` (EntityManager.cs:136). `EntityManager` recycles indices (`_recycledIndices`, EntityManager.cs:51-56), so a stale `Entity` struct can carry a live index with a dead version. The two halves of this method disagree about which entity they are operating on.

**Senaryo:** `var e1 = rem.CreateEntity();` → Index 1, Version 1. `rem.AddReactiveComponent(e1, health)`. `rem.DestroyEntity(e1)` → index 1 recycled. `var e2 = rem.CreateEntity();` → Index 1, Version 2. `rem.AddReactiveComponent(e2, new Health{100})`. Now a stale copy of the handle is destroyed again — `rem.DestroyEntity(e1)` (a double-destroy, or a handle cached in a view/controller). The loop removes index 1 from every reactive storage, firing `OnRemove(1, e2's health)` on all subscribers and wiping e2's live component; `_entityManager.DestroyEntity(e1)` then correctly no-ops. e2 is now half-destroyed: alive in the EntityManager, gone from every reactive storage, and its subscribers were told it was removed.

**Düzeltme:** Guard the whole method: `if (!_entityManager.Exists(entity)) return;` as the first statement, matching `EntityManager.DestroyEntity`. Apply the same guard to `AddReactiveComponent`, `SetReactiveComponent`, `RemoveReactiveComponent` and `GetReactiveComponent` (lines 52-77), all of which currently accept an arbitrary `entity.Index` with no validation while their `EntityManager` counterparts (`AddComponent` EntityManager.cs:161, `SetComponent` EntityManager.cs:227, `GetComponent` EntityManager.cs:191) all check `Exists`.

## `reactive-em-disposes-borrowed-entitymanager`

**ReactiveEntityManager.Dispose() disposes an externally-owned EntityManager it did not create**

| | |
|---|---|
| Konum | `Runtime/ECS/Reactive/ReactiveEntityManager.cs:20` |
| Kategori | api-hazard · ecs-systems |
| Etki | Correctness/native-memory-safety. One misuse of the public injecting constructor corrupts the whole World. |
| Test | None. `ReactiveEntityManagerTests` uses only the parameterless constructor (lines 25, 46, 61, 82, 103, 117, 128, 136). The `ReactiveEntityManager(EntityManager)` overload has zero call sites in the entire repo. |

```csharp
        public ReactiveEntityManager(EntityManager entityManager)
        {
            _entityManager = entityManager;
        }
```

**Sorun:** There are two constructors — line 15 creates and owns an `EntityManager`, line 20 borrows a caller-supplied one — but `Dispose()` (line 97-106) unconditionally ends with `_entityManager.Dispose();`. There is no ownership flag. `EntityManager.Dispose` releases `Allocator.Persistent` `NativeArray<int> _versions`, `NativeArray<byte> _active`, `NativeList<int> _recycledIndices` and the whole `ComponentStore` (EntityManager.cs:282-292), so disposing a borrowed manager tears down native memory that the real owner is still using.

**Senaryo:** `var rem = new ReactiveEntityManager(world.EntityManager);` — the documented reason the overload exists — used inside a scoped feature and disposed at scene teardown. `rem.Dispose()` calls `world.EntityManager.Dispose()`, deallocating the World's native arrays. Every subsequent `world.CreateEntity()` / `ForEach` touches deallocated `NativeArray` memory: with Collections safety checks on (Editor) this throws `ObjectDisposedException: The NativeArray has been deallocated`; in an IL2CPP player build with safety checks compiled out, it is a use-after-free read/write of released native memory. `World.Dispose()` later calls `_entities.Dispose()` again, which is a no-op only because of `EntityManager`'s `_disposed` flag (line 284).

**Düzeltme:** Add `private readonly bool _ownsEntityManager;` set to `true` in the parameterless ctor and `false` in the injecting ctor, then guard: `if (_ownsEntityManager) _entityManager.Dispose();`. Also null-check the ctor argument (`?? throw new ArgumentNullException`).

## `systembase-disposed-flag-set-after-ondispose`

**SystemBase.Dispose sets _disposed last, so a throwing OnDispose leaves the system undisposed, still subscribed, and re-disposable**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/SystemBase.cs:50` |
| Kategori | bug · ecs-systems |
| Etki | Teardown-only, but the leaked system keeps running `OnUpdate` at 60 fps for the remaining process lifetime and keeps its EventBus slot occupied, blocking any replacement system from registering (EventBus.cs:172-173 logs "Signal handler ... is being replaced"). |
| Test | None. There is no test file for SystemBase anywhere under Tests/ (grep for `SystemBase` in Tests/ returns nothing). |

```csharp
        public void Dispose()
        {
            if (_disposed) return;
            OnDispose();
            // Release any tokens captured by the RegisterSignalHandler / RegisterQueryHandler
            // wrappers below so the EventBus slots do not retain references to this disposed
            // system. LIFO disposal matches Patterns/Base.
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();
            _disposed = true;
        }
```

**Sorun:** `OnDispose()` is user-overridable and is called on line 53 before any of the framework's own cleanup. If it throws, the `_disposables` loop on lines 57-58 never runs — so the very `SubscriptionToken`s this method exists to release stay alive and the `EventBus` signal slot keeps a strong reference to the dead system (EventBus.cs:175 `Volatile.Write(ref _signalHandlers[id], handler);`, cleared only by the token's dispose action at EventBus.cs:184-186). `_disposed = true` on line 60 is also skipped, so `Update` (line 39 `if (!_initialized || _disposed) return;`) keeps running this system every frame, and a second `Dispose()` call re-enters `OnDispose()` on an object that already partially tore itself down.

**Senaryo:** `MySystem.OnDispose()` calls `_someService.Unregister()` where `_someService` is already null after scene unload → NullReferenceException. Result: (1) the system's signal handler stays registered on the EventBus, so the next `Send<TSignal>` invokes a method on a half-destroyed system; (2) the system keeps executing `OnUpdate` every frame because `_disposed` is still false; (3) the exception escapes into `SystemScheduler.Dispose`, which has no try/catch (see finding scheduler-dispose-no-exception-isolation), cascading the leak to every other system.

**Düzeltme:** Set the flag first and use try/finally: `if (_disposed) return; _disposed = true; try { OnDispose(); } finally { for (int i = _disposables.Count - 1; i >= 0; i--) { try { _disposables[i].Dispose(); } catch (Exception ex) { Debug.LogException(ex); } } _disposables.Clear(); }`. Note `SubscriptionToken.Dispose` is already idempotent (SubscriptionToken.cs:33-37) so double-disposal there is safe.

## `systembase-generic-closure-per-frame`

**SystemBase<T1..T8>.OnUpdate allocates a display class + delegate every frame because the lambda captures deltaTime**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/SystemBase.cs:175` |
| Kategori | allocation · ecs-systems |
| Etki | 2 allocations ≈ 96 bytes per system per frame (per phase-run). 30 systems @60 fps ≈ 173 KB/s. Startup-only for the `_cachedQuery` itself (correctly guarded by `_queryInitialized`), so the query caching is fine — only the delegate is the leak. |
| Test | None. No test instantiates any `SystemBase<...>` subclass. `ECSPerformanceTests` (Benchmark_Query_* / Benchmark_SimulationLoop_10Frames_100k, lines 141-287) benchmarks `EntityManager.ForEach` directly with a single hoisted lambda, so it never exercises — and cannot detect — the per-frame delegate allocation the generic SystemBase adds on top. |

```csharp
            _cachedQuery.ForEach((int entity, ref T1 c1) => OnUpdateEntity(entity, ref c1, deltaTime));
```

**Sorun:** The lambda captures both `this` and the `deltaTime` parameter. Because `deltaTime` is a per-call local, Roslyn cannot cache the delegate: it must emit `new <>c__DisplayClass{ <>4__this = this, deltaTime = deltaTime }` and `new QueryDelegate<T1>(displayClass.<OnUpdate>b__0)` on every single invocation. `QueryDelegate<T1>` is a managed delegate (EntityQuery.cs:172), not a struct callback, so there is no allocation-free path. The identical pattern is repeated in every arity: line 195 (`SystemBase<T1,T2>`), 216, 237, 258, 279, 301, 323. `Documentation~/ECS.md:226-232` teaches the same capturing-`deltaTime` lambda as the recommended `ForEach` idiom for plain `SystemBase`.

**Senaryo:** A project with 30 systems derived from `SystemBase<...>` running at 60 fps. Each frame each system allocates one display class (16 B header + 8 B `this` + 4 B float, padded → 32 B) and one `QueryDelegate` (~64 B on 64-bit Mono/IL2CPP) ≈ 96 B. 30 systems × 60 fps × 96 B ≈ 173 KB/s of continuous garbage, triggering periodic incremental-GC steps and frame-time spikes on mobile — on the exact code path README.md:41 advertises as "Cache-friendly component iteration (6-28ns per entity)" and Documentation~/Benchmarks.md:401-403 claims is "Query Iteration | 0 bytes ... All hot paths are allocation-free after initialization."

**Düzeltme:** Hoist `deltaTime` into a private field and cache the delegate alongside `_cachedQuery`: add `private float _dt; private QueryDelegate<T1> _cachedDelegate;` and in `OnUpdate` do `_dt = deltaTime; _cachedDelegate ??= (int e, ref T1 c1) => OnUpdateEntity(e, ref c1, _dt); _cachedQuery.ForEach(_cachedDelegate);` — the lambda then captures only `this`, so it is allocated exactly once per system. Apply to all eight arities.

## `systembase-disposables-private-no-subscribe-wrapper`

**SystemBase._disposables is private with no protected enrollment API, and the promised RegisterQueryHandler/Subscribe wrappers do not exist**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/SystemBase.cs:54` |
| Kategori | api-hazard · ecs-systems |
| Etki | Teardown-only leak, but the leaked reference keeps the whole system object graph (including its EntityManager reference) alive for the process lifetime and delivers events to a disposed system. |
| Test | None. No test file for SystemBase exists; no test calls `SystemBase.RegisterSignalHandler` or `SystemBase.Dispose`. |

```csharp
            // Release any tokens captured by the RegisterSignalHandler / RegisterQueryHandler
            // wrappers below so the EventBus slots do not retain references to this disposed
            // system. LIFO disposal matches Patterns/Base.
```

**Sorun:** The comment names two wrappers, but only `RegisterSignalHandler` exists (line 153-159). There is no `RegisterQueryHandler` wrapper and — more importantly — no `Subscribe<T>` wrapper, even though the class provides `Publish<T>` (line 142) whose natural counterpart is `EventBus.Subscribe<TEvent>` (EventBus.cs:251). `_disposables` is `private readonly` (line 15) with no `protected void AddDisposable(IDisposable)` helper, so a subclass that calls `EventBus.Subscribe<Foo>(OnFoo)` or `EventBus.RegisterQueryHandler<Q,R>(...)` receives a `SubscriptionToken` it has no supported way to enroll in the framework's automatic teardown. The v2.0 token migration (commits e3d8292, 556a3e9) covered the signal path and stopped there.

**Senaryo:** `class ScoreSystem : SystemBase { protected override void OnInitialize() { EventBus.Subscribe<ScoreChanged>(OnScoreChanged); } }`. The token is discarded. On `Dispose()`, `_disposables` is empty, so the EventBus subscriber list (EventBus.cs:251-275) keeps a strong reference to the disposed system; `Publish<ScoreChanged>` then invokes `OnScoreChanged` on a system whose `_disposed` is true and whose `EntityManager` may already be disposed, dereferencing freed `NativeArray` memory. The subclass author's only workarounds are to hand-roll their own disposable list or to override `OnDispose` — the exact boilerplate `_disposables` was introduced to remove.

**Düzeltme:** Add `protected void AddDisposable(IDisposable d) { if (d != null) _disposables.Add(d); }`, plus symmetric wrappers `protected void Subscribe<T>(Action<T> h) where T : struct` and `protected void RegisterQueryHandler<TQuery,TResult>(Func<TQuery,TResult> h)` that capture their tokens the same way `RegisterSignalHandler` does (line 157-158). Then the comment on line 54 becomes accurate.

## `updatephase-initialization-never-executes`

**UpdatePhase.Initialization systems are Initialize()'d but never Update()'d — the phase has no driver**

| | |
|---|---|
| Konum | `Runtime/ECS/World/SystemScheduler.cs:41` |
| Kategori | bug · ecs-systems |
| Etki | Functional, not perf: 100% of systems registered into the Initialization phase never run. Startup-only cost otherwise. |
| Test | None. No test file exists for SystemScheduler or World; no test in the repo calls `SystemScheduler.AddSystem`, `World.Update`, `World.LateUpdate` or `World.FixedUpdate`. |

```csharp
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(float deltaTime) => RunPhase(UpdatePhase.Update, deltaTime);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void LateUpdate(float deltaTime) => RunPhase(UpdatePhase.LateUpdate, deltaTime);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FixedUpdate(float fixedDeltaTime) => RunPhase(UpdatePhase.FixedUpdate, fixedDeltaTime);
```

**Sorun:** `RunPhase` is private and has exactly these three callers. `UpdatePhase.Initialization = 0` (UpdatePhase.cs:5) is therefore never passed to `RunPhase`. The scheduler still allocates `_systemsByPhase[0]` (line 16-18), `AddSystem` happily accepts it (line 22-26), and `Initialize()` loops `phase = 0..Length` so those systems DO get `Initialize()` called (line 33-38) — which makes the failure look like a live, working registration. The identical hole exists in the other runner: `SystemRunner.Update/LateUpdate/FixedUpdate` (SystemRunner.cs:181/191/202) index only `UpdatePhase.Update/LateUpdate/FixedUpdate`.

**Senaryo:** `builder.WithSystem<SpawnSystem>(UpdatePhase.Initialization)` — or, on the SystemRunner path, `[StradaSystem(Phase = UpdatePhase.Initialization)]` which `Documentation~/ECS.md:351` presents as a configurable field — registers the system, logs no warning, reports it in `SystemCount`, calls its `OnInitialize()`, and then never calls `OnUpdate` for the entire lifetime of the process. There is no diagnostic; the user sees a system that exists in the profiler window's system list but produces no work.

**Düzeltme:** Either (a) drive the phase: add `public void InitializationUpdate(float dt) => RunPhase(UpdatePhase.Initialization, dt);` and call it from `World` + `ECSAdapter` at the start of the Update tick (Unity's `PlayerLoop` `Initialization` category), or (b) if `Initialization` is meant to be an init-only marker, throw in `AddSystem` when `phase == UpdatePhase.Initialization` is combined with an `ISystem` whose `Update` is meaningful, or remove the enum member. Silently accepting it is the only unacceptable option.

## `scheduler-dispose-no-exception-isolation`

**SystemScheduler.Dispose has no per-system exception isolation — one throwing system leaks every remaining system's subscriptions and the World's native memory**

| | |
|---|---|
| Konum | `Runtime/ECS/World/SystemScheduler.cs:67` |
| Kategori | bug · ecs-systems |
| Etki | Teardown-only, but leaks are unbounded: per leaked ComponentStorage, `sparseCapacity*4 + denseCapacity*4 + denseCapacity*sizeof(T)` bytes of Allocator.Persistent memory (≥ 4 KB + 1 KB + data at defaults) never returned, plus the EntityManager's own `initialCapacity*5` bytes. |
| Test | None. No test disposes a `SystemScheduler` or a `World` containing systems. `BridgeIntegrationTests.TearDown` (line 51) calls `_world?.Dispose()` but the world has zero registered systems. |

```csharp
        public void Dispose()
        {
            for (int i = _allSystems.Count - 1; i >= 0; i--)
                _allSystems[i].Dispose();

            _allSystems.Clear();
            for (int i = 0; i < _systemsByPhase.Length; i++)
                _systemsByPhase[i].Clear();
        }
```

**Sorun:** `RunPhase` protects the update path (SystemBase.cs:40-48 wraps `OnUpdate` in try/catch), but the teardown path is unprotected in both directions. If any `ISystem.Dispose()` throws, the loop aborts: every system with a lower index is never disposed (so its `SubscriptionToken`s are never released and the `EventBus` slots keep hard references to it — exactly what SystemBase.cs:54-56 was written to prevent), `_allSystems` and `_systemsByPhase` are never cleared, and the exception propagates out of `World.Dispose()` (World.cs:124) BEFORE `_entities.Dispose()` and `_bus?.Dispose()` on lines 125-126 ever run.

**Senaryo:** A user system's `OnDispose()` dereferences a Unity object already destroyed by scene unload and throws `NullReferenceException`. `world.Dispose()` → `_scheduler.Dispose()` → the loop throws at index 5 of 12. Systems 0-4 stay subscribed to a disposed EventBus; `_entities.Dispose()` never runs so `EntityManager`'s `Allocator.Persistent` `NativeArray<int> _versions`, `NativeArray<byte> _active` and `NativeList<int> _recycledIndices` plus every `ComponentStorage`'s `SparseSet` (3 NativeArrays each) leak, and Unity logs "A Native Collection has not been disposed, resulting in a memory leak" at domain reload. In a player build these are permanent leaks. `World._disposed` was already set to `true` on line 119, so `Dispose()` can never be retried.

**Düzeltme:** Wrap each iteration: `for (int i = _allSystems.Count - 1; i >= 0; i--) { try { _allSystems[i].Dispose(); } catch (Exception ex) { UnityEngine.Debug.LogException(ex); } }`. Independently, make `World.Dispose` fault-tolerant: put `_scheduler.Dispose()` in its own try/catch (or a try/finally) so `_entities.Dispose()` and `_bus?.Dispose()` always run.

## `scheduler-initialize-no-exception-isolation`

**SystemScheduler.Initialize has no per-system try/catch and pre-sets _initialized, so one throwing OnInitialize permanently blocks all later systems**

| | |
|---|---|
| Konum | `Runtime/ECS/World/SystemScheduler.cs:28` |
| Kategori | bug · ecs-systems |
| Etki | Startup-only, but the failure is permanent for the process: N-k systems never run. |
| Test | None. No test calls `SystemScheduler.Initialize` with a registered system; `BridgeIntegrationTests.cs:44` calls `_world.Initialize()` on a world with zero systems. |

```csharp
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            for (int phase = 0; phase < _systemsByPhase.Length; phase++)
            {
                var systems = _systemsByPhase[phase];
                for (int i = 0; i < systems.Count; i++)
                    systems[i].Initialize();
            }
        }
```

**Sorun:** `Documentation~/ECS.md:238` states "`SystemBase.OnUpdate` is wrapped in exception isolation — if an update throws, the error is logged but does not crash other systems." That isolation exists only for `Update` (SystemBase.cs:40-47); `SystemBase.Initialize` (SystemBase.cs:30-35) calls `OnInitialize()` bare, and the scheduler loop here is bare too. Worse, `_initialized = true` is set on line 31 BEFORE the loop, and `World.Initialize` (World.cs:59-61) does the same, so after a throw neither object can be retried — the remaining systems are stuck with `_initialized == false`, which makes `SystemBase.Update` return early at line 39 forever.

**Senaryo:** System #3 of 12 has an `OnInitialize` that resolves a service which is not yet registered and throws. The exception propagates out of `SystemScheduler.Initialize` → `World.Initialize` → `GameBootstrapper`. Systems 4-11 never get `Initialize()`, so their `_initialized` stays false and `SystemBase.Update` silently returns on every frame — eight systems dead, with a single stack trace pointing at system #3 as the only clue. Calling `world.Initialize()` again is a no-op because `_initialized` is already true on both objects.

**Düzeltme:** Wrap each `systems[i].Initialize()` in try/catch and log, matching the Update path; and set `_initialized = true` after the loop in both `SystemScheduler.Initialize` and `World.Initialize` (World.cs:59-61 currently sets it before `_scheduler.Initialize()`, so `World.IsInitialized` reports true even when initialization aborted).

## `benchmark-no-warmup-per-iteration-stopwatch`

**BenchmarkRunner has no warmup and starts/stops a Stopwatch inside every iteration, contradicting the documented methodology the README's competitive claims rest on**

| | |
|---|---|
| Konum | `Editor/Benchmarking/BenchmarkRunner.cs:259` |
| Kategori | bug · editor-tools |
| Etki | Startup-only in editor cost terms, but it invalidates the numbers: a ~2 ms first-resolve outlier is folded into a mean whose pass threshold is 0.002 ms, and per-iteration Stopwatch overhead is comparable to the README's claimed 61ns singleton lookup. |
| Test | NONE for the harness itself. Tests/Benchmarks/*.cs (ContainerBenchmarks, ECSBenchmarks, EventBusBenchmarks, …) are separate runtime tests that do not exercise Editor/Benchmarking/BenchmarkRunner.cs at all, so the harness's statistical validity is untested. |

```csharp
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                var _ = container.Resolve<ITestService>();
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }
```

**Sorun:** Three compounding defects: (1) No warmup. The very first Resolve pays JIT of the resolution path and, for a container advertising 'expression-tree compiled factories', the Expression.Compile cost — potentially milliseconds — and that sample is folded straight into AverageTimeMs, MaxTimeMs and StandardDeviation by BenchmarkResult.Calculate (BenchmarkModels.cs:44-64), which does a plain mean with no trimming, no median, and no outlier rejection. (2) Per-iteration Stopwatch.Restart()/Stop()/Elapsed. Start+Stop costs on the order of tens of nanoseconds, and `sw.Elapsed` allocates and normalises a TimeSpan each iteration; the README claims a 61ns singleton lookup, so measurement overhead is the same order as the quantity being measured. (3) No GC settling between iterations — a collection landing mid-loop is attributed wholesale to one iteration, inflating Max and StdDev. Every one of the seven benchmarks uses this identical shape (lines 259, 286, 319, 352, 384, 414). Documentation~/Benchmarks.md line 50 states 'Warm-up iterations performed before measurement' and its own sample code at lines 497-509 shows the correct shape (explicit `// Warmup` loop, then a single `Stopwatch.StartNew()` around the whole loop) — the shipped harness does neither.

**Senaryo:** A developer runs DI_SingletonResolve (10,000 iterations, threshold MaxAverageTimeMs = 0.002 ms). The first iteration includes expression-tree compilation at ~2 ms. That single sample alone contributes 2ms/10000 = 0.0002 ms to the mean — 10% of the entire threshold budget — and dominates StandardDeviation. Meanwhile the per-iteration Stopwatch overhead adds a constant floor to every sample. The reported 'ops/s' is a measurement artifact, and the threshold pass/fail in BenchmarkThreshold.CheckPassed is decided by it.

**Düzeltme:** Add an explicit warmup phase (e.g. 1,000 untimed iterations) before the measured loop, matching Documentation~/Benchmarks.md's own sample. Time the whole loop with one Stopwatch and divide, or batch iterations into groups of ~1,000 per timing sample so the timer overhead is amortised below 1%. Call GC.Collect()/WaitForPendingFinalizers() once before the measured loop. Report median and a trimmed mean (drop the top/bottom 5%) in BenchmarkResult.Calculate alongside the raw mean.

## `benchmark-memory-window-includes-setup`

**BenchmarkRunner's memory delta attributes container/world construction to the measured operation, and compares a post-collection baseline against a pre-collection reading**

| | |
|---|---|
| Konum | `Editor/Benchmarking/BenchmarkRunner.cs:253` |
| Kategori | bug · editor-tools |
| Etki | Reported per-benchmark memory is off by the full cost of world/container setup — for ECS_ComponentQuery that is a whole ECSBuilder world plus 1,000 entities and components. |
| Test | NONE — no editor tests exist. |

```csharp
            long memoryBefore = GC.GetTotalMemory(true);

            var container = new ContainerBuilder()
                .Register<ITestService, TestServiceImpl>(lifetime)
                .Build();
```

**Sorun:** `memoryBefore` is taken with forceFullCollection:true, then the entire subject-under-test setup happens inside the measurement window: the ContainerBuilder + Build() here, and `new ECSBuilder().Build()` plus `entities = new Entity[iterations]` plus 10,000 CreateEntity calls in RunECSComponentAddBenchmark (lines 311-317), plus a 1,000-entity/1,000-component world in RunECSQueryBenchmark (lines 344-350). `memoryAfter = GC.GetTotalMemory(false)` (line 267) is then taken *without* collection. So MemoryAllocatedBytes = (setup allocations + measured allocations + whatever garbage happens to be uncollected), reported as the memory cost of the operation, and it is never divided by iteration count.

**Senaryo:** ECS_ComponentQuery reports MemoryAllocatedBytes covering an entire ECSBuilder world plus 1,000 entities plus 1,000 components — none of which the query allocated. A developer reading the Results tab (`Memory: {FormatBytes(result.MemoryAllocatedBytes)}`, BenchmarkRunnerWindow.cs:344) concludes queries allocate megabytes. Conversely BenchmarkThreshold.MaxMemoryBytes checks (BenchmarkModels.cs:167) would fire on setup cost, not on the operation.

**Düzeltme:** Move all setup (container/world construction, entity pre-creation, timings array) above the `GC.GetTotalMemory(true)` baseline. Take `memoryAfter` with GC.GetTotalMemory(false) immediately after the loop and before any teardown, and report bytes-per-iteration ((after-before)/iterations) rather than a raw total.

## `bus-debugger-log-never-populated`

**BusDataProvider.LogMessage/LogEvent/LogCommand/LogQuery have zero callers — the Bus Debugger and the Dashboard Bus tab can never show a message**

| | |
|---|---|
| Konum | `Editor/DataProviders/BusDataProvider.cs:191` |
| Kategori | bug · editor-tools |
| Etki | Not a cost issue — a whole documented feature (Bus Debugger log, statistics, export, breakpoints) is non-functional. |
| Test | NONE. BusDebuggerWindow exposes internal test seams (DisplayedEntriesCount, Breakpoints, SetTypeFilter, GetDisplayedEntries, MatchesTypePattern) that were clearly written for tests that do not exist — Tests/Editor contains only an asmdef. |

```csharp
        public void LogMessage(MessageLogEntry entry)
        {
            if (!_isLogging) return;
```

**Sorun:** `grep -rn "LogEvent\|LogCommand\|LogQuery\|LogMessage" Runtime Editor` returns hits only inside BusDataProvider.cs itself (the four definitions plus LogEvent/LogCommand/LogQuery calling LogMessage). Nothing in Runtime/ ever calls into these — and it cannot: BusDataProvider lives in the Strada.Core.Editor assembly, which Runtime does not (and must not) reference, and EventBus contains no interception hook that would reach it. `_logEntries` is therefore always empty. BusDebuggerWindow and StradaDashboardWindow's Bus tab both bind to this provider (BusDebuggerWindow.cs:95, StradaDashboardWindow.cs:132) and their StartLogging buttons flip `_isLogging` on a provider that nothing ever feeds.

**Senaryo:** Developer follows Documentation~/Debugging.md ('Bus Debugger ... Features: Event type list, Subscriber counts, Message flow visualization'), enters Play Mode, opens the Bus Debugger, clicks the Log button, and publishes hundreds of events. The panel shows 'Waiting for messages...' forever. All downstream UI — breakpoints, bookmarks, Statistics tab, JSON export, message-chain tracking, the Dashboard's Events/Commands/Queries counters — is unreachable dead code.

**Düzeltme:** Add an interception hook to EventBus in Runtime (e.g. a conditional `[Conditional("UNITY_EDITOR")]` static Action<object,MessageKind,int> that the editor assembly subscribes to via [InitializeOnLoad]), or expose an IMessageInterceptor the editor can register. Until wired, the Bus Debugger's log UI should be disabled with an explicit 'not instrumented' message rather than a misleading 'Waiting for messages...'.

## `worlddataprovider-entityversions-field-does-not-exist`

**Entity 'Version' is hardcoded to 0 everywhere: the reflected field _entityVersions does not exist on EntityManager**

| | |
|---|---|
| Konum | `Editor/DataProviders/WorldDataProvider.cs:180` |
| Kategori | bug · editor-tools |
| Etki | Correctness, not cost. A displayed diagnostic value is permanently and silently wrong. |
| Test | NONE. Prior audit unit-15 Finding 5 and unit-20 Finding 10 flagged the hardcoded-field-name *pattern* on these exact lines, but neither verified that `_entityVersions` is already absent today — i.e. the risk they described has already materialised. |

```csharp
                var versionsField = typeof(EntityManager).GetField("_entityVersions", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (versionsField?.GetValue(entityManager) is Dictionary<int, int> versions)
```

**Sorun:** I enumerated EntityManager's fields: it declares `private NativeArray<int> _versions;` and `private NativeArray<byte> _active;` (Runtime/ECS/Core/EntityManager.cs:22-23). There is no `_entityVersions`, and the type sought (Dictionary<int,int>) does not match NativeArray<int> either. GetField returns null, the null-conditional short-circuits, the pattern match fails, and the method falls through to `return 0;` — with the failure swallowed by the bare `catch { }` at line 188. StradaEntityInspectorWindow duplicates the identical broken lookup (line 1190-1196, cached into `_cachedEntityVersionsField`) behind its own `catch { }` at line 1199.

**Senaryo:** Developer is chasing a stale-entity-reference bug and follows Documentation~/Debugging.md's guidance to compare entity versions ('Stale entity reference. Expected v{entity.Version}, got v{current.Version}'). They open the Entity Inspector, which renders `EditorGUILayout.LabelField($"Version: {version}")` (StradaEntityInspectorWindow.cs:567). It shows 'Version: 0' for every entity, always — including entities whose index has been recycled and whose real version is non-zero. The developer concludes no recycling has occurred and looks in the wrong place.

**Düzeltme:** Read `_versions` (NativeArray<int>) and index it, or better, add a public `int GetVersion(int index)` to EntityManager and delete the reflection. Replace the bare `catch { }` at WorldDataProvider.cs:188 and StradaEntityInspectorWindow.cs:1199 with a one-shot Debug.LogWarning so a renamed field surfaces instead of silently degrading to zero.

## `worlddataprovider-systems-field-does-not-exist`

**WorldSnapshot.SystemCount is always 0: the reflected field _systems does not exist on SystemScheduler**

| | |
|---|---|
| Konum | `Editor/DataProviders/WorldDataProvider.cs:225` |
| Kategori | bug · editor-tools |
| Etki | Correctness, not cost. Systems count and the systems list are permanently empty. |
| Test | NONE — no editor tests exist. |

```csharp
                    var systemsField = scheduler.GetType().GetField("_systems", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    if (systemsField?.GetValue(scheduler) is IEnumerable<object> systemList)
```

**Sorun:** SystemScheduler declares `private readonly List<ISystem>[] _systemsByPhase;` (Runtime/ECS/World/SystemScheduler.cs:9) — there is no `_systems` field. SystemProfilerWindow.RefreshSystemList (line 998) and SystemProfilerHook.RegisterSystemsFromWorld (line 116) both correctly reflect `_systemsByPhase`; only WorldDataProvider uses the wrong name. GetField returns null, the pattern match fails, ExtractSystemInfo returns an empty list, and the empty `catch { }` at line 243 hides it. FetchData then sets `snapshot.SystemCount = snapshot.Systems.Count;` = 0.

**Senaryo:** Developer opens the Dashboard's ECS World tab during Play Mode with 20 registered systems. DrawECSStatsPanel renders `GUILayout.Label($"{snapshot.SystemCount}")` (StradaDashboardWindow.cs:1011) as 'Systems: 0' regardless of how many systems the scheduler holds. The developer believes system registration failed and starts debugging bootstrap.

**Düzeltme:** Reflect `_systemsByPhase` (List<ISystem>[]) and flatten it, mirroring SystemProfilerWindow.RefreshSystemList lines 998-1020 — including the phase mapping so SystemInfo.Phase stops being hardcoded to Models.UpdatePhase.Update at line 236. Better, expose a public read-only system enumeration on SystemScheduler. Replace the empty `catch { }` at line 243 with a logged warning.

## `hotreload-duplicate-queue-per-save`

**Saving one CD_ asset queues the same hot-reload twice, doubling the full-world capture/restore cycle**

| | |
|---|---|
| Konum | `Editor/HotReload/ConfigAssetPostprocessor.cs:76` |
| Kategori | bug · editor-tools |
| Etki | Exactly 2× the intended work per CD_ save: two full world capture/restore passes and two OnConfigReloaded notifications per dependent service. |
| Test | NONE — no editor tests exist. |

```csharp
                    EditorApplication.delayCall += () =>
                    {
                        if (Application.isPlaying && HotReloadManager.IsEnabled)
                        {
                            HotReloadManager.QueueConfigChange(path, config);
                        }
                    };
```

**Sorun:** ConfigAssetModificationProcessor.OnWillSaveAssets queues a change via delayCall for every CD_ asset being saved. The save then completes and triggers the import pipeline, so ConfigAssetPostprocessor.OnPostprocessAllAssets fires for the same path and calls ProcessAssetChange -> QueueConfigChange again (lines 25-28). Nothing de-duplicates: `_pendingChanges` is a plain Queue<ConfigChangeInfo> and ProcessPendingChanges drains it entirely with `while (_pendingChanges.Count > 0)` (HotReloadManager.cs:173-177). Each dequeued change runs a full EntityStatePreserver.CaptureState + RestoreState round trip (lines 196 and 216).

**Senaryo:** Designer saves one CD_ asset in Play Mode with 3,000 entities. Two identical ConfigChangeInfo entries are queued. Every registered IConfigDependentService.OnConfigReloaded is invoked twice (services that accumulate rather than replace state — spawn counts, subscription lists — double), and the entire world is captured-and-restored twice. Combined with the JsonUtility defect above, the state is zeroed twice per save.

**Düzeltme:** De-duplicate on enqueue in QueueConfigChange: skip if a pending ConfigChangeInfo with the same AssetPath (or Config.Guid) is already queued, or replace it. Also pick one detection mechanism — OnPostprocessAllAssets is sufficient and safer than calling AssetDatabase.LoadAssetAtPath from inside OnWillSaveAssets.

## `entity-mediator-inspector-reflection-every-frame`

**EntityMediatorInspector forces constant repaint and re-resolves its entire reflection chain (Type.GetType + 5 member lookups + MethodInfo.Invoke) on every repaint**

| | |
|---|---|
| Konum | `Editor/Inspectors/EntityMediatorInspector.cs:79` |
| Kategori | performance · editor-tools |
| Etki | ~360 reflection member lookups + ~780 GUIStyle allocations + 60 MethodInfo.Invoke calls per second per selected View, for the whole Play Mode session. |
| Test | NONE — no editor tests exist. |

```csharp
            var registryType = Type.GetType("Strada.Core.Sync.MediatorRegistry, Strada.Core");
```

**Sorun:** RequiresConstantRepaint() returns `Application.isPlaying && _mediator != null` (line 219-222), so in Play Mode the Inspector repaints every editor frame. OnInspectorGUI calls RefreshMediatorReference() unconditionally on each of those repaints (line 43), which performs: Type.GetType by string, GetProperty("Instance"), GetValue(null), GetMethod("GetMediatorForView"), `getMediatorMethod.Invoke(registry, new object[] { view })` (a fresh object[1] per repaint), then GetProperty("Bindings"), GetMethod("SyncBindings"), GetMethod("PushBindings") — none cached. DrawMediatorSection adds `GetProperty("IsBound")` + a boxing GetValue per repaint (lines 120-122), and DrawColoredMiniLabel allocates `new GUIStyle(EditorStyles.miniLabel)` per call (line 203), invoked at least twice per binding per repaint. Because `[CustomEditor(typeof(View), true)]` uses inherit:true, this applies to every View subclass in the project.

**Senaryo:** Developer selects any View-derived GameObject during Play Mode with 6 component bindings. Per editor frame (~60/s): 1 Type.GetType by string, 6 uncached reflection member lookups, 1 object[1] allocation, 1 MethodInfo.Invoke, 1 boxed bool, and ~13 GUIStyle allocations. That is ~360 reflection member lookups and ~780 GUIStyle allocations per second for a read-only status panel — the Inspector visibly stutters and the profiler shows constant GC.Alloc from the editor.

**Düzeltme:** Resolve registryType / instanceProperty / getMediatorMethod / bindingsProperty / _syncMethod / _pushMethod / isBoundProperty once in OnEnable (or lazily into static readonly fields) and reuse them. Cache the object[1] argument buffer. Hoist the mini-label GUIStyle into a static readonly field. Throttle RequiresConstantRepaint to a timer rather than every frame.

## `reactivepropertydrawer-path-fails-for-collection-elements`

**ReactivePropertyDrawer.GetFieldByPath cannot resolve ReactiveProperty inside arrays/lists, so subscriber count reads 0 and Notify silently no-ops**

| | |
|---|---|
| Konum | `Editor/PropertyDrawers/ReactivePropertyDrawer.cs:181` |
| Kategori | bug · editor-tools |
| Etki | Correctness, not cost. Subscriber count and Notify are permanently non-functional for any ReactiveProperty stored in an array or List. |
| Test | NONE — no editor tests exist. |

```csharp
                if (part == "Array" || part.StartsWith("data[")) continue;
```

**Sorun:** Unity's propertyPath for a collection element is `_props.Array.data[3]._value`. The code skips the 'Array' and 'data[3]' segments but never unwraps the collection element type — `currentType` remains `List<ReactiveProperty<int>>` (or `ReactiveProperty<int>[]`). The next iteration calls `currentType.GetField("_value", ...)` on the List/array type, which returns null, and the method returns null at line 184. GetReactivePropertyInstance therefore returns null for every collection-hosted ReactiveProperty, and both GetSubscriberCount (line 156) and TryNotifyProperty (line 167) bail out.

**Senaryo:** A MonoBehaviour declares `[SerializeField] private List<ReactiveProperty<float>> _stats;` with three live subscribers on element 0. In Play Mode the inspector draws the live indicator as amber 'No subscribers' and the count as 0 for every element, and clicking Notify does nothing at all — no error, no log. The developer concludes the reactive plumbing is broken.

**Düzeltme:** When a path segment is `Array` followed by `data[i]`, parse the index, set `currentType` to the element type (`currentType.GetElementType()` for arrays, `currentType.GetGenericArguments()[0]` for List<T>), and carry the resolved *instance* along with the FieldInfo so the element can actually be indexed. Simpler and more robust: replace the whole hand-rolled path walk with a helper that resolves propertyPath to the boxed object by walking the object graph (fields + array indices) rather than returning a FieldInfo.

## `serviceentry-drawer-uncached-full-appdomain-type-menu`

**ServiceEntryDrawer builds a GenericMenu containing every class and interface in every user assembly, uncached, on each dropdown click**

| | |
|---|---|
| Konum | `Editor/PropertyDrawers/SystemEntryDrawer.cs:220` |
| Kategori | performance · editor-tools |
| Etki | One full AppDomain type scan plus ~3 allocations per discovered type, per dropdown click; ~24,000 allocations and a multi-second stall at 8,000 types. |
| Test | NONE — no editor tests exist. |

```csharp
                ShowTypeMenu(rect, assemblyQualifiedNameProp, typeof(object));
```

**Sorun:** ShowTypeMenu is called with baseType = typeof(object), so the filter `baseType.IsAssignableFrom(type)` at line 257 matches every non-abstract class, and the `else if` at line 261 additionally admits every interface outside the System namespace. The full AppDomain scan (lines 237-283) runs on every dropdown click with no caching whatsoever — unlike SerializableTypeDrawer, which at least memoises into `_cachedTypes`. The result is then grouped into a Dictionary and walked twice (interfaces at 298-311, classes at 316-329), calling `menu.AddItem(new GUIContent(menuPath), isSelected, () => {...})` per type — a GUIContent plus a capturing closure plus a delegate per type.

**Senaryo:** Developer opens a ModuleConfig inspector and clicks the interface dropdown on a service entry. In a project with Assembly-CSharp plus a handful of plugin asmdefs, the scan yields 8,000+ types. Per click: 8,000 GUIContent allocations, 8,000 closures, 8,000 delegates, plus building an 8,000-item GenericMenu — Unity's GenericMenu is not virtualised, so the editor freezes for seconds and the resulting menu is unnavigable.

**Düzeltme:** Constrain the type set: for the interface slot show only interfaces, for the implementation slot only types assignable to the currently selected interface. Cache the scan result in a static Dictionary<Type, List<Type>> as SerializableTypeDrawer does. Replace GenericMenu with UnityEditor.IMGUI.Controls.AdvancedDropdown, which is designed for large searchable type lists. Note SerializableTypeDrawer has the same typeof(object) fallback at GetBaseTypeConstraint line 56 and inherits the same explosion, just cached after the first hit.

## `modulevalidation-hascycle-visiting-set-leak`

**ModuleValidationService.HasCycle never unwinds its shared `visiting` set on the cycle path, producing spurious circular-dependency errors for unrelated modules**

| | |
|---|---|
| Konum | `Editor/Validation/ModuleValidationService.cs:288` |
| Kategori | bug · editor-tools |
| Etki | Correctness, not cost. Up to N-1 spurious 'Circular dependency detected' errors for N modules once a single real cycle exists. |
| Test | NONE — no editor tests exist. This is exactly the kind of graph-state bug a two-cycle unit test would catch immediately. |

```csharp
                    if (HasCycle(dep, visited, visiting, enabledSet, out cyclePath))
                    {
                        cyclePath.Insert(0, current);
                        return true;
                    }
```

**Sorun:** When a cycle is detected the recursion unwinds via `return true` at every level, so neither `visiting.Remove(current)` nor `visited.Add(current)` (lines 296-297) ever executes for any node on the cycle path. ValidateDependencyGraph shares one `visited` and one `visiting` set across the entire `foreach (var module in modules)` loop (lines 94-109), so those nodes remain permanently in `visiting`. Every subsequent top-level module whose DFS reaches any of them immediately trips `if (visiting.Contains(current))` at line 273 and reports another cycle.

**Senaryo:** Six enabled modules where A→B→A form a real cycle, and unrelated modules C, D, E each depend on B. Iteration 1 finds A→B→A and correctly reports it, leaving {A,B} stuck in `visiting`. Iterations for C, D and E each descend into B, hit `visiting.Contains(B)`, and each emit 'Circular dependency detected: C -> B' etc. The developer sees four circular-dependency errors instead of one and cannot tell which is real. The same stale state also makes CheckForAlerts in StradaDashboardWindow (line 1918) raise one critical alert per bogus error.

**Düzeltme:** Reset `visited`/`visiting` per top-level iteration, or (better) unwind correctly: on the cycle-detected path still remove `current` from `visiting` before returning, and de-duplicate reported cycles by their node set so one cycle is reported once.

## `benchmarkrunnerwindow-directory-scan-in-ongui`

**BenchmarkRunnerWindow enumerates the results directory from disk on every OnGUI pass while the History tab is open**

| | |
|---|---|
| Konum | `Editor/Windows/BenchmarkRunnerWindow.cs:395` |
| Kategori | performance · editor-tools |
| Etki | ≥2 synchronous directory enumerations + sorts + List allocations per input event while the History tab is visible. |
| Test | NONE — no editor tests exist. |

```csharp
            var sessions = BenchmarkPersistence.GetSavedSessions();
```

**Sorun:** DrawHistoryTab is called straight from OnGUI (line 127) with no caching. GetSavedSessions performs `Directory.GetFiles(directory, SessionFilePattern)` followed by `.OrderByDescending(f => f).ToList()` (BenchmarkPersistence.cs:100-102) — a synchronous filesystem enumeration plus a LINQ sort and List allocation. IMGUI dispatches Layout and Repaint separately, so this is at least two directory enumerations per input event, and continuous while the mouse hovers the window.

**Senaryo:** Developer has 400 saved benchmark sessions and leaves the History tab open. Every mouse-move event triggers two Directory.GetFiles calls, each returning a 400-element string[], each followed by an OrderByDescending sort and a ToList. On a network-mounted or virus-scanned project directory this is tens of milliseconds of blocking I/O per repaint and the tab becomes unresponsive.

**Düzeltme:** Cache the session list in a field, populated in OnEnable, when the tab is first selected, and after Save/Delete operations. Add an explicit Refresh button. Never call Directory.GetFiles from OnGUI.

## `editor-domain-zero-test-coverage`

**The entire Editor domain has zero tests despite widespread internal test seams**

| | |
|---|---|
| Konum | `Editor/Windows/BusDebuggerWindow.cs:1488` |
| Kategori | test-gap · editor-tools |
| Etki | Zero coverage across ~44 editor source files, including the hot-reload state preservation path that mutates live world data. |
| Test | This finding IS the coverage gap: Tests/Editor/ contains only Strada.Core.Editor.Tests.asmdef and its .meta file. |

```csharp
        internal int DisplayedEntriesCount => _displayedEntries.Count;
```

**Sorun:** `ls Tests/Editor` returns exactly two entries: Strada.Core.Editor.Tests.asmdef and its .meta. There is not a single test source file. Yet the production code is riddled with internal seams written explicitly for tests that were never authored: BusDebuggerWindow exposes DisplayedEntriesCount, IsPaused, TypeFilterPattern, KindFilter, Breakpoints, ShowBookmarkedOnly, SetTypeFilter, SetKindFilter, AddBreakpoint, RemoveBreakpoint, ToggleBookmarkAt, IsBookmarkedAt, GetDisplayedEntries and a static MatchesTypePattern documented 'Exposed for testing purposes'; SystemProfilerWindow exposes GetThresholdLevel/GetThresholdConfiguration/SetThresholdConfiguration with the same comment; StradaConfigDataManagerWindow exposes public static FilterConfigs/DetermineCategory/DiscoverConfigs. Worse, BusDebuggerWindow.MatchesTypePattern (line 1451) is dead in production — the real filtering runs through BusDataProvider.MatchesTypePattern (line 141) — and the two implementations disagree: the window's builds an anchored `^pattern$` regex for *all* patterns (exact match), while the provider falls back to a substring IndexOf when the pattern contains no wildcard. Any test written against the window's copy would pin behaviour the shipped path does not have.

**Senaryo:** Every defect in this report — the JsonUtility state wipe, the non-existent _entityVersions/_systems fields, the HasCycle visiting-set leak, the always-empty bus log, the warmup-free benchmark harness, the collection-path failure in ReactivePropertyDrawer — is trivially detectable by a unit test and none was caught, because the test assembly is empty.

**Düzeltme:** Populate Tests/Editor. Highest value first, since they need no EditorWindow instance: (1) EntityStatePreserver capture/restore round-trip asserting component values survive; (2) ModuleValidationService.ValidateDependencyGraph with one real cycle plus unrelated dependents, asserting exactly one issue; (3) BenchmarkResult.Calculate and BenchmarkPersistence.ValidatePath (including the sibling-directory bypass); (4) StradaConfigDataManagerWindow.FilterConfigs/DetermineCategory. Then delete BusDebuggerWindow.MatchesTypePattern and test BusDataProvider.MatchesTypePattern instead, so the tested code is the executed code.

## `poolmonitor-triggers-arbitrary-type-initializers`

**PoolMonitorWindow reads every static field named *pool* across all user assemblies, forcing arbitrary type initializers to run**

| | |
|---|---|
| Konum | `Editor/Windows/PoolMonitorWindow.cs:560` |
| Kategori | api-hazard · editor-tools |
| Etki | One `GetValue(null)` per matching static field across every type in every user assembly, on each Rescan and on OnEnable — potentially thousands of type initializers executed. |
| Test | NONE — no editor tests exist. |

```csharp
                        var poolObj = field.GetValue(null);
```

**Sorun:** DiscoverPoolsFromStaticFields enumerates every non-system assembly and calls ScanStaticFieldsForPools on EVERY type (lines 532-542 — note both the `if` and the `else` branch call the same method, so the static-class filter is inert). ScanStaticFieldsForPools then calls `field.GetValue(null)` for every static field passing IsPoolField. IsPoolField (line 583-594) matches on `fieldName.Contains("pool")` — any name containing the substring, e.g. `_poolPrefab`, `s_poolRoot`, `PoolManagerInstance`. Reading a static field forces the declaring type's static constructor to execute. Doing that across every type in the project runs arbitrary user and third-party type initializers inside an editor window refresh, with all side effects swallowed by the bare `catch { }` at lines 572-575.

**Senaryo:** A project contains `static class AudioPoolBootstrap { static readonly ObjectPool<AudioSource> _pool = CreateAndWarm(); }` whose initializer instantiates GameObjects, or a third-party type whose cctor opens a file/socket or calls Application.Quit-adjacent APIs. The developer clicks Rescan in the Pool Monitor (or simply opens it in Play Mode, since OnEnable calls DiscoverPools at line 65). Every such initializer fires. Objects appear in the scene, resources are opened, or an initializer throws — and the exception is silently eaten by `catch { }`, so the developer sees unexplained side effects with no diagnostic.

**Düzeltme:** Restrict the static scan to an opt-in allowlist: require the field type to pass IsPoolType (the strong check) rather than the `Contains("pool")` name heuristic, and skip types whose TypeInitializer is non-null unless already initialized (`RuntimeHelpers.RunClassConstructor` is explicitly *not* what you want here). Better, have pools register themselves with a static editor-visible registry instead of being discovered by reflection. At minimum, log rather than swallow initializer exceptions.

## `config-manager-stale-selection-and-double-filter`

**StradaConfigDataManagerWindow filters the config list twice per OnGUI and orphans its selection and validation dictionaries on refresh**

| | |
|---|---|
| Konum | `Editor/Windows/StradaConfigDataManagerWindow.cs:226` |
| Kategori | bug · editor-tools |
| Etki | Two full filter passes plus a grouping Dictionary and per-category Lists per OnGUI pass; plus silently stale selection/validation state after any refresh. |
| Test | NONE. FilterConfigs, DetermineCategory and DiscoverConfigs are public statics that look designed for tests, but Tests/Editor contains only an asmdef. |

```csharp
            var filteredCount = GetFilteredConfigs().Count;
```

**Sorun:** Two problems. (1) GetFilteredConfigs allocates a fresh List and re-runs FilterConfigs over the whole cached set; it is called from DrawConfigStats (line 226) and again from DrawConfigList (line 405) in the same OnGUI pass, then DrawConfigList builds a grouping Dictionary plus one List per category plus a sorted copy per category (lines 417-482) — all per repaint. (2) `_selectedConfigs` (HashSet<ConfigAsset>) and `_validationResults` (Dictionary<ConfigAsset, ValidationResult>) key on ConfigAsset *wrapper* references, but RefreshConfigList replaces `_cachedConfigs` with brand-new wrapper objects (line 608). The Refresh toolbar button clears _validationResults (line 215) but never _selectedConfigs, and CreateNewConfig sets `_needsRefresh = true` (line 396) while clearing neither.

**Senaryo:** Developer selects 12 configs, then creates a new config. _needsRefresh triggers RefreshConfigList, producing 12 new ConfigAsset wrappers for the same assets. The toolbar still reads 'Selected: 12' and the bulk bar still offers 'Validate Selected (12)', but none of the 12 rows render as selected (DrawConfigItem's `_selectedConfigs.Contains(config)` compares against the new wrappers), and clicking Validate Selected validates 12 detached wrapper objects that are no longer in the list. Stale _validationResults entries also keep inflating the 'Errors: N' counter at lines 229-233.

**Düzeltme:** Give ConfigAsset value semantics keyed on the underlying asset (override Equals/GetHashCode on `Asset.GetInstanceID()`), or key both collections on the instance ID rather than the wrapper. Clear _selectedConfigs and _validationResults inside RefreshConfigList so every refresh path is consistent. Cache the filtered list in a field, recomputed only when the filter fields or _cachedConfigs change, instead of calling GetFilteredConfigs twice per OnGUI.

## `container-dependency-graph-rebuilt-per-repaint`

**StradaDashboardWindow rebuilds the whole DI dependency graph and runs an O(V·E) LINQ cycle detection on every OnGUI pass**

| | |
|---|---|
| Konum | `Editor/Windows/StradaDashboardWindow.cs:801` |
| Kategori | performance · editor-tools |
| Etki | At 150 registrations / 300 edges: ~450 object allocations + 45,000 predicate calls + 150 LINQ closures per OnGUI pass, at ≥2 passes/s and far more while the mouse is over the window. |
| Test | NONE — no editor tests exist. |

```csharp
            if (_containerProvider.HasCircularDependency(out var cycle))
```

**Sorun:** DrawDIStatsPanel is called from DrawDIContainerTab -> OnGUI with no caching. HasCircularDependency (ContainerDataProvider.cs:54-64) calls BuildDependencyGraph -> BuildDependencyGraphFromRegistrations, which allocates a fresh DependencyGraph, a Dictionary<Type,DependencyNode>, one DependencyNode per registration and one DependencyEdge per dependency, then calls graph.DetectCycles(). DetectCycles (DataModels.cs:185-238) runs a DFS in which every node does `var outgoingEdges = Edges.Where(e => e.Source == current);` — a full linear scan of the entire edge list, allocating a LINQ iterator and a closure per node — giving O(V·E). CheckForAlerts (line 1903) calls HasCircularDependency a second time on every RefreshAllData. Only the registration *snapshot* is cached (0.5 s via EditorDataProviderBase); the graph build and cycle detection are not cached at all.

**Senaryo:** A game with 150 DI registrations averaging 2 dependencies each (300 edges). Per OnGUI pass: 1 Dictionary + 150 DependencyNode + 300 DependencyEdge allocations, plus a DFS doing 150 × 300 = 45,000 predicate invocations with 150 LINQ iterator/closure allocations. IMGUI runs Layout + Repaint per event and OnInspectorUpdate forces a Repaint every 0.5 s minimum, more on mouse movement — so this fires many times per second while the DI tab is visible.

**Düzeltme:** Cache the DependencyGraph alongside the ContainerSnapshot inside ContainerDataProvider (invalidate in the same place the snapshot is invalidated) so it is built at most once per RefreshInterval. Inside DetectCyclesDFS, precompute an adjacency Dictionary<Type, List<DependencyEdge>> once instead of re-scanning `Edges` with LINQ per node, turning O(V·E) into O(V+E).

## `stradalogwindow-unbounded-entry-growth`

**StradaLogWindow._allEntries grows without bound: the window appends every incoming log but never mirrors the provider's ring-buffer eviction**

| | |
|---|---|
| Konum | `Editor/Windows/StradaLogWindow.cs:1232` |
| Kategori | bug · editor-tools |
| Etki | Unbounded: hundreds of MB retained over a long session, plus an O(total) filter pass inside OnGUI on every filter change. |
| Test | NONE — no editor tests exist. |

```csharp
            _allEntries.Add(entry);
```

**Sorun:** _allEntries is seeded from `_dataProvider.GetEntries()` (line 1148), which returns a defensive *copy*. StradaLogDataProvider enforces its cap by evicting from its own list — `if (_entries.Count >= maxEntries) { _entries.RemoveAt(0); }` (lines 287-290 and 321-324) — but that eviction is invisible to the window's copy. OnLogReceived only ever appends; nothing trims _allEntries or _filteredEntries. RefreshLogs (which would re-sync) is called only from OnEnable and ClearLogs. So MaxLogEntries (configurable up to 10,000 via StradaLogSettingsProvider line 83) bounds the provider but not the window.

**Senaryo:** Long play session emitting 500 log lines/second for 20 minutes = 600,000 entries. The provider correctly holds 10,000; the window holds all 600,000 LogEntry objects (each with Message, StackTrace, FilePath strings) — hundreds of MB retained until the window is closed. DrawLogList's `viewRect` height becomes 600,000 × 22px = 13.2M pixels, and RefreshFilteredEntries (triggered by any filter toggle) walks all 600,000 entries synchronously inside OnGUI, freezing the editor.

**Düzeltme:** Mirror the provider's cap in OnLogReceived: after appending, trim from the front to StradaLogSettings.Instance.MaxLogEntries (and remove the corresponding head entries from _filteredEntries, fixing up _selectedIndex). Better, have the window read through to the provider's list rather than maintaining a divergent copy.

## `stradalogwindow-regex-ignored-for-incremental-entries`

**StradaLogWindow applies substring matching to newly arriving logs even when Regex Search is enabled, so the visible list mixes two filter semantics**

| | |
|---|---|
| Konum | `Editor/Windows/StradaLogWindow.cs:1263` |
| Kategori | bug · editor-tools |
| Etki | Correctness, not cost. With regex search active, zero new log lines are ever displayed. |
| Test | NONE — no editor tests exist. |

```csharp
                bool matches = entry.Message.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
```

**Sorun:** RefreshFilteredEntries honours `_regexSearch` and uses Regex.IsMatch when it is on (lines 1173-1187). The incremental path in OnLogReceived, which is what actually populates the list during a live session, ignores `_regexSearch` entirely and always uses IndexOf. The two code paths therefore disagree about what 'matches'.

**Senaryo:** Developer enables Regex Search and enters `^\[Combat\].*damage=\d+$`. Existing entries are filtered correctly by the regex. Every log line that arrives afterwards is tested with `IndexOf("^\\[Combat\\].*damage=\\d+$")` — a literal substring search that matches nothing — so no new lines ever appear. The developer concludes logging stopped. Conversely a plain-text query behaves as substring in the incremental path but as an unanchored regex in the refresh path, so toggling any filter checkbox silently changes which rows are shown.

**Düzeltme:** Extract the per-entry predicate from RefreshFilteredEntries into a single `bool PassesFilter(LogEntry)` helper that reads `_regexSearch`, and call it from both RefreshFilteredEntries and OnLogReceived. Pre-compile the Regex once when `_searchText`/`_regexSearch` change rather than per entry, and reuse the 100ms match-timeout pattern already used in BusDataProvider.MatchesTypePattern.

## `sysprofiler-getsamples-tolist-per-repaint`

**SystemProfilerWindow copies every system's full 1000-sample circular buffer to a new List on every repaint, and recomputes all metrics twice per OnGUI**

| | |
|---|---|
| Konum | `Editor/Windows/SystemProfilerWindow.cs:646` |
| Kategori | allocation · editor-tools |
| Etki | ~600 KB allocated + ~100,000 sample reads per repaint at 25 systems × 1,000 samples; ×10 repaints/s from the default `_updateInterval = 0.1f`. |
| Test | NONE — SystemProfilerWindow exposes internal GetThresholdLevel/GetThresholdConfiguration/SetThresholdConfiguration seams for tests that do not exist. |

```csharp
            var samples = _profiler.GetSamples(systemType);
```

**Sorun:** SystemProfiler.GetSamples returns `buffer.ToList()` (SystemProfiler.cs:153), and CircularBuffer<T>.ToList allocates `new List<T>(_count)` and copies every element (lines 389-397). DrawSparkline calls it once per system row per repaint; DrawTimelineView calls it once per system in its `foreach (var m in allMetrics)` loop (line 747). The buffer capacity is DefaultBufferSize = 1000 and SystemTimingSample is a 4-field struct (Type ref + enum + double + long ≈ 24 bytes), so a full buffer copy is ~24 KB per system per repaint. Separately, GetMetricsByPhase() is invoked twice per OnGUI — once from DrawSummaryStatisticsPanel (line 375) and again from DrawSystemsByPhase (line 525) — and each call runs GetAllMetrics, which re-walks every sample of every buffer twice (mean pass then std-dev pass, SystemProfiler.cs:255-270) and allocates a Dictionary plus a List per phase plus a SystemMetrics per system.

**Senaryo:** Recording 25 systems with full 1000-sample buffers, System Profiler window open. Per repaint: 25 List<SystemTimingSample> allocations of 1,000 elements each ≈ 600 KB, plus 2 × (25 × 2,000) = 100,000 sample reads for metrics, plus 2 Dictionaries and 50 SystemMetrics objects. OnInspectorUpdate Repaints every `_updateInterval` (default 0.1f = 10 Hz), so ~6 MB/s of garbage and 1M sample reads/s just to draw sparklines.

**Düzeltme:** Add an index-based read API to CircularBuffer/SystemProfiler (e.g. `TryGetSample(Type, int index, out SystemTimingSample)` or expose Count + indexer) so the sparkline and timeline can read in place without copying. Call GetMetricsByPhase once at the top of OnGUI and pass the result to both DrawSummaryStatisticsPanel and DrawSystemsByPhase. Cache metrics between sample collections rather than recomputing per repaint.

## `serializabletype-il2cpp-stripping-silent-failure`

**Inspector-configured systems/services are managed-stripping unsafe on IL2CPP: no link.xml, no [Preserve], type identity exists only as a serialized string**

| | |
|---|---|
| Konum | `Runtime/Modules/SerializableType.cs:28` |
| Kategori | aot-il2cpp · modules-bootstrap |
| Etki | Startup only, but the failure is silent and build-configuration-dependent — it does not reproduce in the Editor, which is where all testing happens. |
| Test | NO COVERAGE, and no coverage is possible from EditMode/PlayMode tests — the failure only manifests in an IL2CPP player build. No build-time validation exists either: GameBootstrapperConfig.Validate (lines 81-136) never resolves any SerializableType. This is the highest-value missing check in the domain. |

```csharp
                    _cachedType = Type.GetType(_assemblyQualifiedName);
                    if (_cachedType == null)
                        Debug.LogWarning($"[SerializableType] Failed to resolve type: {_assemblyQualifiedName}");
```

**Sorun:** A system or service that is only ever referenced from a ModuleConfig's serialized `_assemblyQualifiedName` string has NO static reference anywhere in IL. Unity's IL2CPP managed code stripping (Medium is the default for IL2CPP in Unity 6) removes such types and their constructors. The package ships no link.xml — `find . -name link.xml` returns nothing — and no `[Preserve]` attribute appears anywhere in Runtime/ (`grep -rn 'Preserve]' Runtime/` returns nothing). SerializableType's failure mode is a `Debug.LogWarning` and a null return; from there `SystemEntry.IsValid` becomes false, `SystemRunner.AddSystemsFromConfig` line 97 `continue`s past the entry, and the system is simply absent. Nothing throws, nothing fails validation (GameBootstrapperConfig.Validate at lines 81-136 checks null entries, duplicates, cycles, and missing module dependencies — it never checks that a SystemEntry or ServiceEntry type resolves), and `CompleteInitialization()` reports success. The same stripping hazard applies to `Activator.CreateInstance(systemType)` at SystemRunner.cs:269, which additionally needs the parameterless constructor preserved. This directly undermines the two headline features in README lines 65-67 ("Inspector Systems: Configure ECS systems via drag-and-drop").

**Senaryo:** Ship an IL2CPP player with default (Medium) managed stripping. A gameplay system `EnemySpawnSystem` is registered only via a ModuleConfig `Systems` list entry — never `new`'d or referenced in code. IL2CPP strips it. At runtime `Type.GetType("Game.EnemySpawnSystem, Game.Runtime, Version=0.0.0.0, ...")` returns null, `SerializableType` logs one warning, `entry.IsValid` is false, and SystemRunner skips it. The game boots "successfully" with enemies never spawning. It works perfectly in the Editor and in a Mono build, so the bug reproduces only in the shipped IL2CPP artifact. The same class of silent failure occurs on any asmdef rename or type namespace move, because the stored AQN embeds the assembly simple name.

**Düzeltme:** Three parts. (1) Ship a `link.xml` in the package root (and document that consumers need one for their own assemblies) preserving `ISystem` implementors, or add `[UnityEngine.Scripting.Preserve]` to `SystemBase` — note Preserve is NOT inherited, so the reliable option is link.xml with `<type fullname="*" preserve="all"/>` scoped to the game assemblies, or an editor build callback that emits a link.xml from the set of types actually referenced by every ModuleConfig asset in the project. (2) Make the failure loud instead of silent: `GameBootstrapperConfig.Validate` should resolve every SystemEntry and ServiceEntry type and add an error when resolution fails, so `FailOnValidationError` (default true) turns a stripped type into a hard startup failure rather than a missing system. (3) Escalate `Debug.LogWarning` at SerializableType.cs:30 to `Debug.LogError`.

## `systemrunner-no-exception-isolation`

**SystemRunner phase loops have no per-system exception isolation — one throwing system silently stops every later system in that phase, every frame, forever**

| | |
|---|---|
| Konum | `Runtime/Modules/SystemRunner.cs:179` |
| Kategori | bug · modules-bootstrap |
| Etki | Per-frame: every system after the throwing one in that phase is skipped on every frame for the remainder of the run. A try/catch that is never entered costs zero on the happy path in both Mono and IL2CPP, so the fix is free. |
| Test | NO COVERAGE. There is no SystemRunnerTests.cs. Nothing verifies that a throwing system does not take down its phase, and nothing verifies that Dispose drains fully when one Dispose throws. |

```csharp
        public void Update(float deltaTime)
        {
            var systems = _systemsByPhase[(int)UpdatePhase.Update];
            for (int i = 0; i < systems.Count; i++)
                systems[i].System.Update(deltaTime);
        }
```

**Sorun:** None of `Update` (179-184), `LateUpdate` (190-195), `FixedUpdate` (201-206), `Initialize` (153-173) or `Dispose` (211-222) wraps the per-system call. The callers do not compensate either: GameBootstrapper.Update/LateUpdate/FixedUpdate (lines 131-148) call straight through with no try/catch, so the exception escapes to Unity's MonoBehaviour boundary, which logs it and moves on to the next frame. The framework is internally inconsistent about this — `PlayerLoop.InvokeCallbacks` (PlayerLoop.cs:183-196) wraps each callback in try/catch + Debug.LogException, `PlayerLoop.RunInitialization` does the same, `PatternManager.OnUpdate` wraps each tickable in try/catch, and `Container.Dispose` explicitly keeps draining its disposal stack when one Dispose throws (Container.cs:194 comment). Only SystemRunner, the per-frame hot path that runs user-authored systems, has no isolation. `Dispose` is the worst case: one system throwing in `Dispose` skips the disposal of every system registered before it AND skips `_allSystems.Clear()` and the phase-list clears at lines 219-221.

**Senaryo:** A game has 12 systems in the Update phase, ordered 0..11. `InventorySystem` (order 4) hits a null reference on a specific item type. From that frame onward, systems 5-11 — combat, AI, animation driving, audio — never run again while the offending entity exists. Unity logs one NullReferenceException per frame from GameBootstrapper.Update; the player sees the world freeze from the waist down with no crash. On teardown, `SystemRunner.Dispose` (line 216-217) iterates in reverse; if the outermost system's Dispose throws, `_disposed` is already true (line 214) so a retry is impossible, every earlier system leaks its subscriptions and native allocations, and `_allSystems`/`_systemsByPhase` are never cleared.

**Düzeltme:** Wrap the per-system call in try/catch in all five loops, logging via `StradaLog.LogError` with the `SystemInstance.Name` already stored for exactly this purpose (SystemRunner.cs:44). For the update loops, add an opt-out or a strike counter that disables a system after N consecutive throws so the log does not become a per-frame flood — e.g. store a mutable `Faulted` flag alongside Order/Name and skip faulted systems. For `Dispose`, use try/catch per system unconditionally so the loop always drains, matching the Container.Dispose precedent at Container.cs:191-194.

## `assetdb-getall-misses-derived-types`

**RuntimeAssetDatabase indexes by concrete runtime type but queries by the static type parameter, so GetAll<TBase>() always returns nothing**

| | |
|---|---|
| Konum | `Runtime/Data/AssetDatabase.cs:42` |
| Kategori | bug · patterns-utils |
| Etki | Silent empty result on every base-typed query; correctness, not perf. |
| Test | No coverage — there is no Tests/Runtime/Data directory and no test anywhere references RuntimeAssetDatabase, AssetRegistry, or AssetContainer. |

```csharp
        public IEnumerable<T> GetAll<T>() where T : AssetContainer
        {
            var type = typeof(T);

            if (!_assetsByType.TryGetValue(type, out var list))
                yield break;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is T typed)
                    yield return typed;
            }
        }
```

**Sorun:** `Register` buckets by the concrete runtime type — `var type = asset.GetType();` (line 64) — while `GetAll<T>` looks up `typeof(T)` (line 44). The two only agree when the caller names the exact leaf class. The `if (list[i] is T typed)` filter on line 51 is dead code: every element of `_assetsByType[typeof(X)]` is already exactly an `X`, which proves the author intended polymorphic buckets. Any query by a base type — including the natural `GetAll<AssetContainer>()` — silently yields an empty sequence rather than failing loudly.

**Senaryo:** `class WeaponAsset : AssetContainer {}` / `class SwordAsset : WeaponAsset {}` / `class AxeAsset : WeaponAsset {}`. `AssetRegistry.PopulateDatabase` (lines 101-108) registers 30 swords and 20 axes under `typeof(SwordAsset)` and `typeof(AxeAsset)`. `foreach (var w in db.GetAll<WeaponAsset>())` to build the weapon-selection UI iterates zero times — no exception, no warning, just an empty menu.

**Düzeltme:** Register into a bucket per assignable type: walk `asset.GetType()` up the base chain (and its interfaces) to `AssetContainer`, adding the instance to each bucket; or keep one flat list and filter with `is T` (which is what the dead line-51 filter already expects). `Unregister` must mirror whichever choice is made — it currently removes only from the concrete-type bucket (lines 82-84).

## `log-stacktrace-format-mismatch-dead-ide-navigation`

**LogEntry.ParseStackTrace and the log window both parse Unity's ExtractStackTrace format, but StradaLog feeds them BCL Environment.StackTrace — source navigation never resolves**

| | |
|---|---|
| Konum | `Runtime/Logging/LogEntry.cs:92` |
| Kategori | bug · patterns-utils |
| Etki | Feature is 100% non-functional; the parse cost (1 string[] + N substring scans per log call) is pure waste. |
| Test | No coverage — no test exists for LogEntry, StradaLog, or any Runtime/Logging type; there is no Tests/Runtime/Logging directory at all. A single assertion that `new LogEntry(..., Environment.StackTrace, ...).FilePath != string.Empty` would have caught this. |

```csharp
                int atIndex = line.IndexOf(" (at ", StringComparison.Ordinal);
                if (atIndex < 0)
                    continue;
```

**Sorun:** The `" (at "` token is Unity's own format, produced by `UnityEngine.StackTraceUtility.ExtractStackTrace()` (`Method () (at Assets/Foo.cs:42)`). The producer, `StradaLog.LogInternal` line 222, uses `Environment.StackTrace`, whose Mono/BCL rendering is `  at Ns.Type.Method (System.String s) [0x00000] in /path/Foo.cs:42` — the character following `(` is the first parameter type, never `at `. Every line therefore hits the `continue` on line 94, so `FilePath` stays `string.Empty` and `LineNumber` stays `0` (set on lines 73-74). The Editor consumes exactly those fields: `Editor/Windows/StradaLogWindow.cs:803` does `GUI.enabled = !string.IsNullOrEmpty(_selectedEntry.FilePath);` and `Editor/DataProviders/StradaLogDataProvider.cs:250` bails on `string.IsNullOrEmpty(entry.FilePath)`. `StradaLogWindow.DrawClickableStackTrace` (line 868) applies the same `" (at "` test to the raw stack, so the clickable frames are dead too.

**Senaryo:** A developer selects any entry in the Strada Log window and finds the "open source" button permanently greyed out (StradaLogWindow.cs:803) and no clickable frames in the stack pane — the advertised feature (LogEntry.cs:42-55: "source file path extracted from the stack trace" / "line number in the source file") is inert for every entry ever produced. Meanwhile the full `ParseStackTrace` cost — `stackTrace.Split('
')` plus the per-line scan — is paid on every single log call for zero result.

**Düzeltme:** Replace `Environment.StackTrace` with `UnityEngine.StackTraceUtility.ExtractStackTrace()` in `StradaLog.LogInternal` (gated to development builds per the earlier finding), or capture file/line at zero cost with `[CallerFilePath]`/`[CallerLineNumber]` parameters on the public Log methods and delete `ParseStackTrace` entirely.

## `log-environment-stacktrace-every-call`

**StradaLog captures Environment.StackTrace on EVERY log call in EVERY build, before any level/enable check, and retains it in the buffer**

| | |
|---|---|
| Konum | `Runtime/Logging/StradaLog.cs:222` |
| Kategori | performance · patterns-utils |
| Etki | Per log call: one full managed stack walk with file-info resolution (single-digit microseconds on Mono) + ~2-4KB string + one string[] from Split + N substrings, paid even when ShowLogs is false. Retained: up to MaxLogEntries × stack-string size (default 1000 entries) resident in every build. |
| Test | No coverage of any kind — there is no Tests/Runtime/Logging directory and no test anywhere references StradaLog, LogEntry, or StradaLogSettings. Nothing pins the current behaviour, so this is not an intentional trade-off recorded in tests. |

```csharp
            var stackTrace = Environment.StackTrace;
            var entry = new LogEntry(message, type, module, stackTrace, isDeepLog);

            AddToBuffer(entry);

            if (StradaLogSettings.Instance.ShowLogs)
```

**Sorun:** `Environment.StackTrace` is `new StackTrace(1, needFileInfo: true).ToString()` — it walks and formats the entire managed call stack including file/line resolution, allocating a multi-kilobyte string. It runs unconditionally on line 222, BEFORE the `ShowLogs` gate on line 227, so disabling log output does not avoid any of the cost. The resulting string is then re-scanned by `LogEntry.ParseStackTrace`, which does `var lines = stackTrace.Split('\n');` (LogEntry.cs:84) — one string[] plus one substring per frame, per log call. There is no `[Conditional("UNITY_EDITOR")]` / `[Conditional("DEVELOPMENT_BUILD")]` anywhere in Runtime/ (`grep -rn "Conditional(" Runtime/` returns nothing), so all of this ships in release player builds. The captured stack — which contains absolute source file paths from the developer's machine — is retained in `_logBuffer` for up to `MaxLogEntries` (default 1000) entries, i.e. megabytes of developer path data resident in a shipped build.

**Senaryo:** `Runtime/Sync/ViewPool.cs:150` calls `StradaLog.LogWarning($"[ViewPool<{typeof(TView).Name}>] No match for Entity(...)...", LogModule.Sync)` from `ViewPool.Despawn(Entity)` — a gameplay-frequency path. A game that despawns 200 already-despawned entity views in one frame pays 200 full stack captures + 200 `Split('
')` in that frame. Same shape for any user code doing `StradaLog.Log(...)` inside Update. Additionally, a release build's log buffer holds 1000 × ~2-4KB of stack strings containing `/Users/<dev>/...` paths, readable by anything that can reach `StradaLog.LogEntries`.

**Düzeltme:** (1) Move the stack capture behind the enable check and behind `#if UNITY_EDITOR || DEVELOPMENT_BUILD`; pass `null` otherwise. (2) Use `UnityEngine.StackTraceUtility.ExtractStackTrace()` rather than `Environment.StackTrace` (see the separate format-mismatch finding). (3) Add `[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]` to `Log`/`LogDeep` so the call sites — including the argument expressions — are erased in release. (4) Store `null` for `StackTrace` in non-development builds so developer paths never ship.

## `base-dispose-no-try-catch-strands-subscriptions`

**Base.Dispose has no per-item try/catch — one throwing disposable strands every remaining EventBus subscription forever, so a disposed Controller keeps receiving events**

| | |
|---|---|
| Konum | `Runtime/Patterns/Base.cs:137` |
| Kategori | bug · patterns-utils |
| Etki | Permanent subscription leak plus one exception per subsequent matching event, for the lifetime of the EventBus. |
| Test | No coverage. ControllerLifecycleTests.Controller_Subscribe_AutoUnsubscribesOnDispose (lines 76-89) verifies the happy path only; no test registers a throwing IDisposable via AddDisposable. |

```csharp
            // Dispose in LIFO order so later-acquired resources release before earlier ones.
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();
```

**Sorun:** `_disposables` mixes framework-owned `SubscriptionToken`s (added by `Subscribe`, `RegisterSignalHandler`, `RegisterQueryHandler` on lines 75, 99, 110) with arbitrary user objects added via the public `AddDisposable` (lines 119-122). If any one of them throws, the loop aborts: every earlier-added item — which in LIFO order means every EventBus subscription registered during `OnInitialize` — is never disposed, and `_disposables.Clear()` on line 140 is skipped too. Because `_disposed = true` was already set on line 133, a retry is a no-op. The disposed Controller therefore stays subscribed to the EventBus permanently and keeps executing handler code against its torn-down state. `Model.Dispose` (Model.cs:56-58) has the identical defect and additionally iterates *forward*, contradicting the LIFO contract documented here.

**Senaryo:** A Controller does `Subscribe<PlayerDied>(OnPlayerDied)` in `OnInitialize`, then later `AddDisposable(_networkStream)`. On teardown, `_networkStream.Dispose()` throws IOException (socket closed by the peer). It is index 1, disposed first under LIFO; index 0 — the `PlayerDied` subscription token — is never disposed. The EventBus keeps the delegate alive, so every subsequent `PlayerDied` publish invokes `OnPlayerDied` on a disposed controller whose `Container`/`World` may already be torn down -> NullReferenceException per event, plus the controller and everything it closes over is kept alive by the bus (leak).

**Düzeltme:** ```
for (int i = _disposables.Count - 1; i >= 0; i--)
{
    try { _disposables[i].Dispose(); }
    catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
}
_disposables.Clear();
```
Apply the same to `Model.Dispose` and switch it to reverse order for consistency.

## `patternmanager-dispose-no-try-catch`

**PatternManager.Dispose has no exception isolation even though its own tick loops do — one throwing controller prevents every other controller and ALL services from being disposed**

| | |
|---|---|
| Konum | `Runtime/Patterns/PatternManager.cs:187` |
| Kategori | bug · patterns-utils |
| Etki | Leaks the entire registered component graph on one failure; scales with scene-reload count. |
| Test | No coverage. Tests/Runtime/Patterns/ControllerLifecycleTests.cs and ServiceInjectionTests.cs never construct a PatternManager — there is no PatternManager test file anywhere under Tests/. |

```csharp
            for (int i = _controllers.Count - 1; i >= 0; i--)
            {
                if (_controllers[i] is IDisposable disposable)
                    disposable.Dispose();
            }

            for (int i = _services.Count - 1; i >= 0; i--)
            {
                if (_services[i] is IDisposable disposable)
                    disposable.Dispose();
            }
```

**Sorun:** `OnUpdate`/`OnFixedUpdate`/`OnLateUpdate` (lines 126-157) each wrap every callback in try/catch — the class clearly knows one bad component must not take down the batch. `Dispose` does not. A single throwing `controller.Dispose()` aborts the remaining controllers, skips the entire service-disposal loop, and skips the six `Clear()` calls on lines 199-204. Since `_disposed = true` was set on line 183, `Dispose` can never be retried.

**Senaryo:** Ten controllers and five services are registered. `_controllers[7].Dispose()` throws (e.g. via the `Base.Dispose` defect above, or a user `OnDispose` NRE). Controllers 0-6 are never disposed, all five services (audio, persistence, networking) are never disposed, and `_tickables`/`_fixedTickables`/`_lateTickables` are never cleared — so the lists still hold strong references to every component, defeating the whole teardown. On a scene reload this leaks the entire previous pattern graph.

**Düzeltme:** Wrap each `disposable.Dispose()` in try/catch + `Debug.LogException`, mirroring the tick loops, and move the six `Clear()` calls into a `finally`.

## `patternmanager-dual-interface-double-fixedtick`

**IFixedTickController and IFixedTickable declare the same FixedTick(float) signature, and PatternManager iterates both lists — a controller implementing both ticks twice per FixedUpdate**

| | |
|---|---|
| Konum | `Runtime/Patterns/PatternManager.cs:46` |
| Kategori | api-hazard · patterns-utils |
| Etki | 2x execution of all fixed-step logic for affected components, every FixedUpdate (50/s at the default fixed timestep). |
| Test | No coverage. No test constructs a PatternManager at all; ServiceInjectionTests.FixedTickableService_ImplementsFixedTick (lines 98-108) calls `FixedTick` directly on the instance, bypassing PatternManager entirely. |

```csharp
            if (controller is IFixedTickController fixedController)
                _fixedControllers.Add(fixedController);

            RegisterTickables(controller);
```

**Sorun:** `IFixedTickController.FixedTick(float fixedDeltaTime)` (Interfaces/IController.cs:39) and `IFixedTickable.FixedTick(float fixedDeltaTime)` (Interfaces/IFixedTickable.cs:5) are byte-identical signatures. `RegisterController` adds to `_fixedControllers` on line 47 and then `RegisterTickables` (lines 71-72) independently adds to `_fixedTickables`. `OnFixedUpdate` (lines 135-148) iterates both lists in sequence. A single C# method implicitly implements both interfaces, so it is invoked twice per physics step. Nothing in the API surface warns about this — and because both interfaces are public and semantically identical, a user has no way to know which to pick. (unit-10 Finding 11 filed this as INFO; it remains fully reproducible.)

**Senaryo:** `class PhysicsController : Controller, IFixedTickController, IFixedTickable { public void FixedTick(float dt) { _rb.AddForce(_thrust * dt); } }` — a natural thing to write when both interfaces exist and mean the same thing. The body runs twice per FixedUpdate, so the entity accelerates at exactly double the intended rate. There is no error and no warning; the bug presents as "physics tuning values are all off by 2x", which is extremely hard to trace.

**Düzeltme:** Either delete `IFixedTickController` (the framework's own `FixedTickableController`, Controller.cs:39-42, already uses `IFixedTickable`), or make `RegisterTickables` skip `IFixedTickable` when the component was already added as an `IFixedTickController`: `if (component is IFixedTickable ft && !(component is IFixedTickController)) _fixedTickables.Add(ft);`

## `pool-maxsize-discard-leaks-and-corrupts-activecount`

**ObjectPool silently drops over-cap instances without disposing them and without decrementing _totalCreated, permanently inflating ActiveCount**

| | |
|---|---|
| Konum | `Runtime/Pooling/ObjectPool.cs:75` |
| Kategori | bug · patterns-utils |
| Etki | One leaked (undisposed) object per over-cap despawn; ActiveCount error grows by 1 per over-cap despawn and never recovers. In the 500-despawn burst above: 300 leaks, +300 permanent ActiveCount error. |
| Test | No coverage. Tests/Runtime/Pooling/ObjectPoolTests.cs never passes a `maxSize` (all six tests use the 2-arg ctor, default `int.MaxValue`). `Clear_DisposesAllPooledInstances` (line 81) is misnamed — `TestPoolable` does not implement `IDisposable`, so the disposal branch on line 104 is never executed by any test. |

```csharp
            if (_available.Count < _maxSize)
            {
                if (!_inPool.Add(instance))
                    return;
                _available.Push(instance);
            }
```

**Sorun:** When the pool is at capacity the instance falls off the end of `Despawn` and is simply forgotten. Two consequences. (1) Unlike `Clear()` (lines 98-109) which does `if (instance is IDisposable d) d.Dispose();`, the over-cap path never disposes — any unmanaged handle, native buffer, or Unity GameObject held by the dropped instance leaks. Documentation~/Pooling.md:424-444 explicitly documents `DisposableResource : IPoolable, IDisposable` as a supported pooling pattern, and Pooling.md:386-395 recommends `maxSize` for memory control, so both halves of this trap are documented as best practice. (2) `_totalCreated` is never decremented for dropped instances, so `ActiveCount => _totalCreated - _available.Count` (line 20) over-reports by the total number of instances ever dropped, permanently. Prior report POOL-02 is listed as FIXED in SecurityReports/2026-05-22-low-status-review.md:131 — only the `Clear()` half was fixed (`_totalCreated -= cleared;`, line 108); this half is still broken.

**Senaryo:** `var pool = new ObjectPool<Particle>(() => new Particle(), initialSize: 50, maxSize: 200);` — the exact construction shown at Pooling.md:233-237. A burst spawns 500 particles then despawns all 500. The first 200 are pooled; particles 201-500 are dropped un-disposed (300 leaked native/Unity resources). `TotalCreated` stays 500, `AvailableCount` is 200, so `ActiveCount` reports 300 when the true active count is 0. Any capacity-planning or leak-detection logic reading `ActiveCount` is now permanently wrong.

**Düzeltme:** Add an `else` branch: `else { _totalCreated--; if (instance is IDisposable d) d.Dispose(); }`. Better: track active count with an explicit `_activeCount` field incremented in `Spawn` and decremented in `Despawn`, rather than deriving it.

## `poolregistry-static-type-silent-noop`

**PoolRegistry.Spawn<T>/Despawn<T> key on the static type parameter and silently no-op on a miss, so despawning through a base-typed variable leaks the object out of the pool**

| | |
|---|---|
| Konum | `Runtime/Pooling/PoolRegistry.cs:48` |
| Kategori | api-hazard · patterns-utils |
| Etki | Converts a zero-allocation pooled path into a full allocation per spawn (e.g. 200 bullets/s × sizeof(Bullet)), with no diagnostic. |
| Test | Partial and misleading. PoolingPerformanceTests.Benchmark_PoolRegistry_SpawnByType (lines 60-79) uses `registry.Despawn(obj)` where `obj` is `var`-typed from `Spawn<HeavyPoolable>()`, so `T` is always inferred correctly — the failing case is never exercised. There is no functional PoolRegistry test at all. |

```csharp
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Spawn<T>() where T : class
        {
            var pool = Get<T>();
            return pool?.Spawn();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Despawn<T>(T instance) where T : class
        {
            var pool = Get<T>();
            pool?.Despawn(instance);
        }
```

**Sorun:** `Get<T>()` (lines 26-29) resolves `_pools[typeof(T)]`, where `T` is inferred from the *static* type of the argument, not `instance.GetType()`. Both methods then use `?.`, converting a lookup miss into a silent no-op (`Despawn`) or a silent `null` (`Spawn`). There is no way for a caller to distinguish "returned to pool" from "pool not found".

**Senaryo:** `registry.GetOrCreate<Bullet>(() => new Bullet(), 200);` then in gameplay: `IProjectile p = registry.Spawn<Bullet>(); ... registry.Despawn(p);` — `T` is inferred as `IProjectile`, `Get<IProjectile>()` returns null, and `pool?.Despawn` does nothing. The bullet is never returned; the pool drains and `Spawn` starts allocating a new Bullet on every shot, silently defeating pooling entirely (Pooling.md:325-328 claims 0 bytes GC allocation for this path). Symmetrically, `registry.Spawn<Enemy>()` before `GetOrCreate<Enemy>` returns null, and the NullReferenceException surfaces several frames later at an unrelated call site.

**Düzeltme:** Resolve by runtime type in `Despawn`: look up `_pools[instance.GetType()]` first, falling back to `typeof(T)`. And make the miss loud: throw (or `Debug.LogError`) instead of `?.`-swallowing — the registry knows at that moment that pooling is broken.

## `timer-self-cancel-reschedule-aliasing`

**A timer callback that cancels itself and then schedules a new timer silently destroys the new timer (pooled-entry aliasing)**

| | |
|---|---|
| Konum | `Runtime/Services/TimerService.cs:83` |
| Kategori | bug · patterns-utils |
| Etki | Silent data loss whenever the cancel-then-reschedule pattern occurs — the exact pattern shown in Documentation~/TimerService.md:517-522 (ability cooldown) and 528-541 (wave spawner). Not rare. |
| Test | No coverage. TimerServiceTests.Cancel_StopsTimer (lines 53-64) cancels from *outside* the callback, which does not exercise the aliasing. No test schedules a timer from inside a callback. |

```csharp
                if (timer.RemainingRepeats > 0)
                    timer.RemainingRepeats--;

                if (timer.RemainingRepeats == 0)
                {
                    RemoveAt(i);
                    continue;
                }

                timer.RemainingTime = timer.Interval;
```

**Sorun:** `Update` holds a local `var timer = _timers[i];` (line 72) across the user callback on line 81. If that callback cancels its own handle, `Cancel` -> `RemoveAt` (lines 132-141) despawns the entry into `_entryPool`. `ObjectPool` uses a `Stack<T>` (LIFO, ObjectPool.cs:44), so the very next `_entryPool.Spawn()` returns that same object. If the callback then schedules a new timer, the new timer reuses the same `TimerEntry` instance — and `_freeIndices` also hands back the same index (line 55). Execution then returns to line 83 where the stale local `timer` reference mutates the *new* timer's fields.

**Senaryo:** ```
handle = svc.Every(1f, () => { handle.Cancel(); handle = svc.After(5f, Respawn); });
```
Frame the timer fires: callback cancels -> entry E despawned to pool, `_timers[0] = null`, index 0 enqueued. Callback schedules -> `Spawn()` pops E (LIFO), sets Id=2, RemainingRepeats=1, Callback=Respawn, and `_freeIndices.Dequeue()` returns 0 so `_timers[0] = E`. Back at line 83: stale `timer` is E, `RemainingRepeats (1) > 0` -> decremented to 0 -> line 86 true -> `RemoveAt(0)` -> `_timers[0]` is E (not null) -> `timer.Callback = null; _entryPool.Despawn(timer); _timers[0] = null`. The 5-second Respawn timer is destroyed before it ever ticks, with no error. The returned `TimerHandle` reports `IsActive == false`.

**Düzeltme:** Do not hold a raw entry reference across the callback. Either re-read `_timers[i]` after the invoke and bail if it is null or its `Id` changed (`var t = _timers[i]; if (t == null || t.Id != capturedId) continue;`), or add a monotonically-increasing generation stamp on `TimerEntry` that `Update` captures before the callback and re-validates after.

## `postprocessor-readalltext-deleted-assets`

**CodeGenPostprocessor calls File.ReadAllText on deleted asset paths — FileNotFoundException during asset import**

| | |
|---|---|
| Konum | `Editor/CodeGen/CodeGenPostprocessor.cs:59` |
| Kategori | bug · sourcegen |
| Etki | Editor-time: one thrown exception per deleted .cs under Assets/, aborting the postprocessor batch. |
| Test | No coverage. Tests/Editor/ contains zero .cs files. |

```csharp
                        var content = File.ReadAllText(path);
```

**Sorun:** `allChangedPaths` is built at lines 32-35 as `importedAssets.Concat(deletedAssets).Concat(movedAssets)`. Paths from `deletedAssets` no longer exist on disk by the time OnPostprocessAllAssets runs, yet the loop reaches File.ReadAllText for any of them that pass `path.EndsWith(".cs") && !path.Contains("Generated")` and `path.StartsWith("Assets/")` (lines 55-59). There is no File.Exists check and no try/catch anywhere in the method.

**Senaryo:** With auto code generation enabled (Strada > Settings > Enable Auto Code Generation), the user deletes Assets/Game/PlayerSystem.cs. OnPostprocessAllAssets receives it in deletedAssets, the filters pass, and File.ReadAllText throws FileNotFoundException from inside Unity's asset postprocessing callback — aborting the rest of the postprocessor chain and logging an error the user cannot act on. Deleting a folder of scripts throws on the first one.

**Düzeltme:** Only scan importedAssets and movedAssets for content; treat deletedAssets as an unconditional 'regenerate' signal without reading them. Add `if (!File.Exists(path)) continue;` and wrap the body in try/catch(IOException) regardless.

## `systemregistry-emits-inaccessible-types`

**SystemRegistryGenerator emits typeof(...) for non-public and cross-assembly ISystem types into Assembly-CSharp**

| | |
|---|---|
| Konum | `Editor/CodeGen/SystemRegistryGenerator.cs:126` |
| Kategori | bug · sourcegen |
| Etki | Editor-time: 3 compile errors per inaccessible/unreferenced ISystem type, breaking the whole project build. |
| Test | No coverage. Tests/Editor/ contains zero .cs files; no test invokes SystemRegistryGenerator. |

```csharp
                sb.AppendLine($"            typeof({typeName}),");
```

**Sorun:** FindAllSystems (lines 38-102) accepts every non-abstract, non-interface type assignable to ISystem from every non-Unity/non-System assembly, with no accessibility filter (`type.IsPublic` is never checked) and no check that the emitting assembly can even see the declaring assembly. The output is written to Assets/Strada.Generated/GeneratedSystemRegistry.cs (StradaCodeGenerator.cs:10, SystemRegistryGenerator.cs:31-32), which Unity compiles into the predefined Assembly-CSharp — an assembly that does not automatically reference user asmdefs. The same names are re-emitted at line 137 (`builder.Register<{typeName}>(...)`) and line 149 (`container.Resolve<{typeName}>()`), tripling the error count per bad type. Notably, the file already contains the sanitizer that unit-13 Finding 1 asked for, but it is dead code — see the sibling finding.

**Senaryo:** A project has `internal sealed class CombatSystem : ISystem` inside an asmdef named Game.Combat. Strada > Generate System Registry (or the auto-postprocessor) writes `typeof(Game.Combat.CombatSystem),` into Assembly-CSharp -> CS0122 (inaccessible) and CS0246 (Assembly-CSharp does not reference Game.Combat). The generated file breaks the entire project's compilation, and because it lives under Assets it survives domain reloads until manually deleted via Strada > Clean Generated Code.

**Düzeltme:** Filter FindAllSystems to `type.IsPublic && !type.IsNested` (or IsVisible), and skip any type whose declaring assembly is not referenced by Assembly-CSharp. Better: emit the registry per-assembly next to each asmdef instead of a single file in Assembly-CSharp, or fall back to `Type.GetType("AssemblyQualifiedName")` strings instead of `typeof(...)`.

## `pipeline-no-exception-handling`

**GenerationPipeline.Execute has no try/catch — a throwing step skips Rollback and wedges the generator in InProgress forever**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/Pipeline/GenerationPipeline.cs:43` |
| Kategori | bug · sourcegen |
| Etki | Editor-time: one wedged window plus orphaned files/folders per failure, and an exception logged on every subsequent repaint. |
| Test | No coverage. Tests/Editor/ has zero .cs files; no test drives GenerationPipeline at all. |

```csharp
                var stepResult = step.Execute(context);
```

**Sorun:** Rollback is only reachable through the `if (!stepResult.Success)` branch (lines 46-52). Any exception thrown out of a step bypasses it entirely. FileGenerationStep.CreateFile throws by design: `if (!fullPath.StartsWith(Application.dataPath)) throw new InvalidOperationException($"Path outside project: {fullPath}");` (FileGenerationStep.cs:130-131). FolderCreationStep.Execute calls Directory.CreateDirectory on an unvalidated path (FolderCreationStep.cs:28, 32) which can throw UnauthorizedAccessException/IOException/PathTooLongException, and AssemblyDefStep.WriteAsmdef calls File.WriteAllText with no guard (AssemblyDefStep.cs:117). The caller is equally unguarded: StradaModuleGenerator.Generation.cs sets `_generationState = GenerationState.InProgress;` (line 18) then calls `_pipeline.Execute(context)` (line 21) with no try/finally, so the state is never restored on the exception path.

**Senaryo:** TargetPath is set to `Assets/../../Escaped` (which passes the StartsWith("Assets") check at Validation.cs:135). FolderCreationStep creates Assets/../../Escaped/FooModule and its subfolders on disk, AssemblyDefStep writes FooModule/Foo.asmdef there, then FileGenerationStep.CreateFile throws InvalidOperationException. Result: (a) no Rollback runs, so the folders and .asmdef are left outside the project; (b) _generationState stays InProgress, so DrawActions renders 'Creating...' with the button permanently disabled (UI.cs:742-753) until the window is closed and reopened; (c) an unhandled exception is thrown from inside OnGUI, which Unity logs per repaint.

**Düzeltme:** Wrap the foreach body in GenerationPipeline.Execute in try/catch, converting the exception into `result.Success = false; result.ErrorMessage = ex.ToString(); Rollback(context);`. In StradaModuleGenerator.StartGeneration, wrap the Execute call in try/finally that restores `_generationState = GenerationState.Idle`.

## `editor-codegen-zero-tests`

**The entire editor code-generation domain has zero tests — Tests/Editor contains only an asmdef**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/Pipeline/Steps/FileGenerationStep.cs:39` |
| Kategori | test-gap · sourcegen |
| Etki | Not a runtime cost — but it is the reason 8 deterministic compile-breaking defects in this domain are unguarded. |
| Test | This IS the gap: Tests/Editor/ = 0 .cs files; no source-generator test project anywhere. |

```csharp
            if (!ValidNamespaceRegex.IsMatch(ns))
                return StepResult.Error($"Invalid namespace '{ns}': must contain only valid C# identifier characters");
```

**Sorun:** `find Tests -name '*.cs'` yields 63 files under Tests/Runtime, 6 under Tests/Benchmarks, 5 under Tests/Stress, and 0 under Tests/Editor — that directory holds only Strada.Core.Editor.Tests.asmdef and its .meta. Nothing in the repo tests ModuleNameValidator, TemplateContextDetector, UsingStatementGenerator, StradaTemplates, TemplateProcessor, GenerationPipeline, FileGenerationStep, FolderCreationStep, AssemblyDefStep, SystemRegistryGenerator, ModuleInitializerGenerator, or CodeGenPostprocessor. This line is the single security control the prior audit relies on (SecurityReports/2026-05-22-medium-fix-plans.md:5 declares unit-12 #3 FIXED on the strength of it) and it has no test asserting it rejects `Game.Mod}` or `Game.Mod;`. There is also no source-generator test project: no reference to Microsoft.CodeAnalysis.Testing, no GeneratorDriver usage, no snapshot of any .g.cs.

**Senaryo:** Every generator/template finding in this report is a first-use, deterministic failure that a single unit test would have caught: the [SystemOrder] CS0246 (compile the generated System source), the 'New System' space (assert the emitted class name is a valid identifier), the path traversal in CreateFileFromTemplate (assert the write stays under dataPath), and the EntityQueryGenerator CS0246/CS0122 (run the generator through a CSharpCompilation and assert zero errors). None exist, so all of them ship.

**Düzeltme:** Add Tests/Editor/*.cs covering: (1) ValidNamespaceRegex accept/reject table; (2) a GenerationPipeline run against a temp folder asserting created files, rollback on failure, and containment; (3) golden-file tests for each of the 12 templates asserting the output parses via CSharpSyntaxTree.ParseText with zero diagnostics. Add a separate generator test project using Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing that compiles the emitted source and asserts zero errors plus incremental-cache hits.

## `modulegen-validateall-per-repaint`

**CanGenerate() runs a full AppDomain reflection scan + AssetDatabase query on every OnGUI repaint**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/StradaModuleGenerator.UI.cs:740` |
| Kategori | performance · sourcegen |
| Etki | Per repaint (>= 2x per frame while the mouse is over the window): ~150-300 `Assembly.GetTypes()` calls + 1 AssetDatabase.FindAssets + a recursive directory walk. Editor-only, but it is the dominant cost of the window whenever a module name is present. |
| Test | No coverage — Tests/Editor/ has zero .cs files, and there is no editor performance test anywhere. |

```csharp
            var canGenerate = CanGenerate();
```

**Sorun:** DrawActions is called from OnGUI (StradaModuleGenerator.cs:131), which Unity invokes at least twice per repaint (Layout + Repaint) and continuously while the mouse is over the window. CanGenerate (StradaModuleGenerator.cs:193-198) calls `ValidateAll()`, which calls `ValidateModuleName()`, which reaches `ModuleDiscovery.ModuleExists(name)` (StradaModuleGenerator.Validation.cs:81). ModuleExists -> GetFlatList -> DiscoverModules (Utilities/ModuleDiscovery.cs:243-248, 38-49, 17-36) does, on every call: a recursive Directory.GetDirectories walk of Assets/Modules; `EnrichFromInstallers`, which iterates `AppDomain.CurrentDomain.GetAssemblies()` and calls `assembly.GetTypes()` on each (ModuleDiscovery.cs:154) — materialising a fresh Type[] per assembly and doing an O(types x modules) nested string comparison at lines 162-169; and `EnrichFromModuleConfigs`, which runs `AssetDatabase.FindAssets("t:ModuleConfig")` plus a LoadAssetAtPath per hit (lines 212-217). ValidateAll additionally allocates fresh ValidationMessage objects each pass (Validation.cs:29-38). OnGUI is also the same method that calls DrawStructurePreview/DrawCodePreview, which rebuild their whole StringBuilder output per repaint.

**Senaryo:** User types 'Inventory' into the Module Name field and then moves the mouse inside the window. Every mouse-move event triggers 2+ OnGUI passes; each pass enumerates all ~150-300 editor-domain assemblies, calls GetTypes() on each (tens of thousands of Type objects and one array allocation per assembly), and runs a full AssetDatabase t:ModuleConfig search. The window drops to single-digit FPS and the editor allocates tens of MB/second.

**Düzeltme:** Cache the discovery result: compute ModuleDiscovery.DiscoverModules() once in OnEnable and on the explicit Refresh button (which already exists at UI.cs:339), and store it. Run ValidateAll only inside `EditorGUI.EndChangeCheck()` blocks (UI.cs:108-112 already does this for the name field) and have CanGenerate read a cached `_lastValidationPassed` bool instead of re-validating.

## `targetpath-traversal-not-canonicalized`

**ValidateTargetPath's StartsWith("Assets") is not canonicalized, and FolderCreationStep/AssemblyDefStep have no containment check**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/StradaModuleGenerator.Validation.cs:135` |
| Kategori | security · sourcegen |
| Etki | Editor-time: directory creation and .asmdef file write at an arbitrary filesystem location the editor process can reach. |
| Test | No coverage. unit-12 Findings 1 and 5 were marked as addressed in SecurityReports/2026-05-22-medium-fix-plans.md (F1), but the fix landed only in FileGenerationStep and in SetTargetPath's call to an unfixed validator — verified NOT fixed for FolderCreationStep and AssemblyDefStep. |

```csharp
            if (!path.StartsWith("Assets"))
            {
                _validationMessages.Add(ValidationMessage.Error("Target path must be within the Assets folder.", "TargetPath"));
                return;
            }
```

**Sorun:** No Path.GetFullPath canonicalization is applied, so `Assets/../../anything` and `AssetsEvil/...` both satisfy the prefix test. unit-12 Finding 1 called for canonicalization and it was only applied to one of three write sites: FileGenerationStep.cs:129-131 got the `Path.GetFullPath(path)` + `StartsWith(Application.dataPath)` guard, but FolderCreationStep.cs:28/32/43 (`Directory.CreateDirectory(context.Definition.TargetPath)`, `Directory.CreateDirectory(basePath)`) and AssemblyDefStep.cs:117 (`File.WriteAllText(path, content);`) still have none — and AssemblyDefStep runs at Order 20, *before* FileGenerationStep's Order 30 guard (GenerationPipeline.cs:25 sorts by Order). The SetTargetPath fix from unit-12 Finding 5 (StradaModuleGenerator.cs:88-93, `if (validate) ValidateTargetPath();`) is therefore incomplete: it calls a validator that the traversal walks straight through, and ValidateTargetPath only appends a message rather than rejecting.

**Senaryo:** Any editor script calls `window.SetTargetPath("Assets/../../../../Users/Shared")`, or the value is planted in EditorPrefs key `Strada_Gen_TargetPath` (read unconditionally at StradaModuleGenerator.cs:171). ValidateTargetPath passes. FolderCreationStep creates /Users/Shared/FooModule plus its Scripts/* tree, AssemblyDefStep writes /Users/Shared/FooModule/Foo.asmdef, and only then does FileGenerationStep abort. Combined with the missing exception handling above, nothing is rolled back.

**Düzeltme:** In ValidateTargetPath, compare `Path.GetFullPath(path)` against `Path.GetFullPath(Application.dataPath)` with an ordinal StartsWith plus a directory-separator check. Add the identical guard to FolderCreationStep.Execute (before both CreateDirectory calls) and to AssemblyDefStep.WriteAsmdef (before File.WriteAllText) — factor it into a single shared PathGuard helper so the three steps cannot drift again.

## `templateprocessor-diverged-from-generator`

**TemplateProcessor duplicates FileGenerationStep's templates and has already diverged — the preview lies about what will be written**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/Utilities/TemplateProcessor.cs:121` |
| Kategori | bug · sourcegen |
| Etki | Editor-time: preview/output mismatch on at least 2 of 12 templates today, unbounded drift going forward. |
| Test | No coverage. Tests/Editor/ contains zero .cs files; a golden-file test comparing GeneratePreview against the FileGenerationStep output for each component combination would pin this. |

```csharp
            sb.AppendLine($"    public class {name}Service : I{name}Service");
```

**Sorun:** TemplateProcessor (349 lines) and FileGenerationStep (468 lines) are two independent hand-maintained copies of the same 12 templates; the preview panel calls the former (StradaModuleGenerator.UI.cs:707 `return TemplateProcessor.GeneratePreview(fileName, name, ns, _settings);`) while generation calls the latter. They have already drifted in at least two places: (1) here the preview unconditionally emits the `: I{name}Service` base clause, whereas the generator makes it conditional — FileGenerationStep.cs:210 `var iface = hasInterface ? $" : I{name}Service" : "";` used at :214; (2) TemplateProcessor.cs:146 unconditionally emits `[Inject] private readonly I{name}Service _service;` in the controller, whereas FileGenerationStep.cs:232-235 wraps that line in `if (hasServiceInterface)`. There is no shared source of truth and no test pinning them together.

**Senaryo:** User unchecks 'Service Interface' but leaves 'Service' checked (a supported combination — Validation.cs:182-185 only emits an Info message). The preview shows `public class FooService : IFooService`, so the user believes an interface will exist. The generated file is `public class FooService` with no interface, and the module's ModuleConfig — which FileGenerationStep.cs:180 always emits as `builder.RegisterService<IFooService, FooService>();` — then fails to compile because IFooService was never generated.

**Düzeltme:** Delete TemplateProcessor's duplicate bodies and have GeneratePreview call the same FileGenerationStep generator methods (make them internal static and pass the ComponentSelection through), so preview and output cannot diverge.

## `templates-default-name-has-space`

**Every Assets/Create/Strada/* menu default class name contains a space, producing a .cs file that cannot compile**

| | |
|---|---|
| Konum | `Editor/Templates/StradaTemplateMenus.cs:17` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only, but it is the default one-click path for 8 of the package's menu items. |
| Test | No coverage — Tests/Editor/ has zero .cs files, so no test exercises StradaTemplates.GenerateTemplate output at all. |

```csharp
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.System, "New System");
```

**Sorun:** All eight menu entries pass a two-word default name: "New System" (17), "New Controller" (23), "New Service" (29), "New Component" (35), "New View" (41), "New Config" (47), "New Command" (53), "New Event" (59). That string flows straight into StradaTemplates without any identifier validation. StradaTemplates.cs:24-25 `if (!className.EndsWith("System")) className += "System";` leaves "New System" unchanged (it already ends with "System"), and line 40 emits `sb.AppendLine($"    public class {className} : SystemBase");`. The Create button is only disabled on `string.IsNullOrWhiteSpace(_className)` (StradaTemplateMenus.cs:167), so a name with an embedded space passes.

**Senaryo:** User right-clicks a folder, picks Assets > Create > Strada > System, and clicks Create without renaming. The tool writes 'New System.cs' containing `public class New System : SystemBase` -> CS1002/CS1519. Same for all eight menu items; the Config path is worse because GenerateConfig prefixes CD_ giving `public class CD_New Config : ScriptableObject`.

**Düzeltme:** Change the defaults to identifier-safe values ("NewSystem", "NewController", ...) and, more importantly, gate the Create button and CreateFileFromTemplate on `Regex.IsMatch(className, @"^[A-Za-z_][A-Za-z0-9_]*$")` — reuse the same pattern FileGenerationStep.cs:28 already applies to namespaces.

## `templates-classname-injection-and-traversal`

**StradaTemplates.CreateFileFromTemplate writes an unvalidated class name into both the file path and the emitted C# — path traversal + editor code execution**

| | |
|---|---|
| Konum | `Editor/Templates/StradaTemplates.cs:370` |
| Kategori | security · sourcegen |
| Etki | Editor-time only (this code is inside Strada.Core.Editor, includePlatforms: [Editor]) — but it results in arbitrary file write outside the project and arbitrary code execution in the developer's editor. |
| Test | No coverage. Tests/Editor/ is empty. unit-12 flagged the equivalent issue for the module generator (Findings 1/3) and it was fixed there (FileGenerationStep.cs:28 and :129-131); the Templates path was never reviewed and never fixed. |

```csharp
                var filePath = Path.Combine(folderPath, fileName);

                Directory.CreateDirectory(folderPath);

                File.WriteAllText(filePath, code);
```

**Sorun:** `className` reaches this public API completely unvalidated. It is a raw `EditorGUILayout.TextField` (StradaTemplateMenus.cs:134 and :259) guarded only by `string.IsNullOrWhiteSpace` (lines 167, 288), and CreateFileFromTemplate itself applies no regex. Two distinct sinks: (1) `fileName = className + ".cs"` (line 418) is fed to Path.Combine and File.WriteAllText with no canonicalization and no containment check against Application.dataPath — unlike FileGenerationStep.cs:129-131, which does exactly that check (`var fullPath = Path.GetFullPath(path); if (!fullPath.StartsWith(Application.dataPath)) throw ...`). (2) className is interpolated verbatim into the emitted source at lines 40, 89, 129, 166, 194, 234, 299, 303, 348 (e.g. `sb.AppendLine($"    public class {className} : SystemBase");`), and the resulting .cs is dropped into Assets and compiled+loaded by the editor. The whole body is wrapped in `catch (Exception ex) { Debug.LogError(...) }` (lines 381-385), so failures are downgraded to a log line.

**Senaryo:** (a) Traversal: className = `../../../../../../tmp/pwn` for the System template -> GetFileNameForTemplate appends 'System' -> Path.Combine("Assets/Foo", "../../../../../../tmp/pwnSystem.cs") -> File.WriteAllText resolves outside the project and writes there. (b) Injection: className = `X { } public static class Boot { [UnityEditor.InitializeOnLoadMethod] static void P() { System.Diagnostics.Process.Start("open", "-a Calculator"); } } public class Y` produces a syntactically valid .cs under Assets that Unity compiles on the next refresh and executes on domain load. Both are reachable from the public static API `StradaTemplates.CreateFileFromTemplate(type, className, folderPath)`, so any third-party editor script or automation can trigger them without the UI.

**Düzeltme:** Validate at the API boundary, not the UI: at the top of CreateFileFromTemplate reject className that does not match `^[A-Za-z_][A-Za-z0-9_]*$`, and after Path.Combine apply the same `Path.GetFullPath` + `StartsWith(Application.dataPath)` containment check FileGenerationStep.cs:129-131 already uses. Do not swallow the result into Debug.LogError.

## `templates-silent-overwrite`

**CreateFileFromTemplate silently overwrites an existing .cs with no confirmation and reports success**

| | |
|---|---|
| Konum | `Editor/Templates/StradaTemplates.cs:374` |
| Kategori | bug · sourcegen |
| Etki | Editor-time, irreversible data loss of one source file per invocation. |
| Test | No coverage. Tests/Editor/ contains zero .cs files. |

```csharp
                File.WriteAllText(filePath, code);

                AssetDatabase.Refresh();

                Debug.Log($"[Strada] Created {templateType} template: {filePath}");
```

**Sorun:** There is no `File.Exists(filePath)` check and no EditorUtility.DisplayDialog confirmation anywhere on this path. File.WriteAllText truncates unconditionally. The success log then claims the file was 'Created'. Contrast the module generator, which does guard against clobbering: StradaModuleGenerator.Validation.cs:147-150 refuses to generate when `Directory.Exists(fullPath)`. The template path has no equivalent. This is also reachable from Editor/StradaContextMenus.cs:76, where controllerName is derived from an existing type name — so 'Generate Controller' on a view whose controller already exists overwrites that controller.

**Senaryo:** Developer has a hand-written Assets/Game/Controllers/PlayerController.cs with 400 lines of logic. They right-click their PlayerView MonoBehaviour and choose Strada > Generate Controller (StradaContextMenus.cs:51-82). controllerName resolves to 'PlayerController', folderPath to the Controllers folder, and File.WriteAllText replaces the 400-line file with the 20-line stub. Unity's console shows '[Strada] Generated controller: PlayerController'. There is no undo — AssetDatabase.Refresh happens after the truncation, so Unity never saw the old contents.

**Düzeltme:** Before writing, `if (File.Exists(filePath))` either return false with a LogError, or prompt via `EditorUtility.DisplayDialog("File exists", ..., "Overwrite", "Cancel")`. Prefer AssetDatabase.GenerateUniqueAssetPath(filePath) as the default so the user never loses work.

## `entityquerygen-missing-using-entitymanager`

**EntityQueryGenerator emits `this EntityManager em` without `using Strada.Core.ECS.Core;` — generated file cannot compile in any assembly**

| | |
|---|---|
| Konum | `SourceGenerationECS~/EntityQueryGenerator.cs:181` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only — but it is a total compile failure of whatever assembly the analyzer is applied to (8 CS0246 errors, one per generated arity). |
| Test | No coverage. There is no generator snapshot/compile test anywhere in Tests/. A Microsoft.CodeAnalysis.Testing `GeneratorDriver` roundtrip that compiles the emitted source would fail immediately. |

```csharp
                sb.Append($">(this EntityManager em, QueryDelegate<");
```

**Sorun:** `GenerateAllCode` (lines 29-34) emits exactly three using directives — `using System;`, `using System.Runtime.CompilerServices;`, `using Strada.Core.ECS.Storage;` — and wraps everything in `namespace Strada.Core.ECS.Query`. `EntityManager` lives in `Strada.Core.ECS.Core` (Runtime/ECS/Core/EntityManager.cs:8 `namespace Strada.Core.ECS.Core`, :18 `public sealed class EntityManager`). C# enclosing-namespace lookup from `Strada.Core.ECS.Query` searches `Strada.Core.ECS.Query`, `Strada.Core.ECS`, `Strada.Core`, global — it never descends into the sibling `Strada.Core.ECS.Core`. The hand-written equivalent at Runtime/ECS/Query/QueryBuilder.cs:4 does have `using Strada.Core.ECS.Core;`, confirming the generated file is simply missing it.

**Senaryo:** Wire the analyzer to any assembly (including Strada.Core itself). `RegisterPostInitializationOutput` unconditionally emits Strada.Generated.EntityQuery.g.cs. All 8 emitted `ForEach<T1..Tn>(this EntityManager em, ...)` extension methods fail with CS0246: 'The type or namespace name EntityManager could not be found'. The assembly stops compiling entirely — 8 errors, unfixable by the user because the file is generated.

**Düzeltme:** Add `sb.AppendLine("using Strada.Core.ECS.Core;");` after line 32, or emit the fully-qualified `global::Strada.Core.ECS.Core.EntityManager` at line 181.

## `entityquerygen-internal-manager`

**EntityQueryGenerator emits `b.Manager` which is `internal` to Strada.Core — generated Select<> is inaccessible outside that assembly**

| | |
|---|---|
| Konum | `SourceGenerationECS~/EntityQueryGenerator.cs:171` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only — 8 CS0122 errors (one per arity), total compile failure for any consumer assembly. |
| Test | No coverage. No generator test project exists; Tests/Runtime/ECS/Query never touches generated types. |

```csharp
                    sb.Append($"b.Manager.Store.GetOrCreateStorage<T{i}>()");
```

**Sorun:** `QueryBuilder.Manager` is declared `internal EntityManager Manager => _manager;` at Runtime/ECS/Query/QueryBuilder.cs:14. Source-generator output is compiled into the *consuming* assembly, not into Strada.Core. The hand-written `Select<T1..T8>` overloads sidestep this by living inside Strada.Core and using the private `_manager` field directly (QueryBuilder.cs:19 `return new EntityQuery<T1>(_manager.Store.GetOrCreateStorage<T1>());`). The generated `GeneratedQueryExtensions.Select<T1..Tn>` has no such access. Additionally, the generated `EntityQuery<T1..Tn>` struct declares `internal EntityQuery(...)` (line 72) in the consumer assembly while the same-named types already exist as public in Strada.Core for arities 1-8 in the *same* namespace `Strada.Core.ECS.Query` — two assemblies both carrying the analyzer would produce CS0433 ambiguity for the 9-16 arities.

**Senaryo:** Apply the analyzer to a game assembly (Assembly-CSharp or any user asmdef). All 8 generated `Select<T1..Tn>` bodies fail with CS0122: 'Strada.Core.ECS.Query.QueryBuilder.Manager is inaccessible due to its protection level'. This is unfixable from user code without editing Strada.Core.

**Düzeltme:** Make `QueryBuilder.Manager` public (it already returns a public type), or add a public `QueryBuilder.GetStorage<T>()` helper and emit `b.GetStorage<T{i}>()`. Also stop emitting into the framework's own `Strada.Core.ECS.Query` namespace — emit into a per-assembly namespace to avoid CS0433 across assemblies.

## `entityquerygen-unsafe-unconditional`

**EntityQueryGenerator emits an `unsafe` block unconditionally via RegisterPostInitializationOutput — CS0227 in any assembly without allowUnsafeCode**

| | |
|---|---|
| Konum | `SourceGenerationECS~/EntityQueryGenerator.cs:98` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only — but it is an unconditional, undeletable compile break for any assembly lacking allowUnsafeCode. |
| Test | No coverage. Tests/Editor/ contains only an .asmdef and zero .cs files; no generator compilation test exists. |

```csharp
            sb.AppendLine("            unsafe {");
```

**Sorun:** `Initialize` uses `context.RegisterPostInitializationOutput(...)` (line 19), which emits the file into *every* assembly the analyzer is attached to, with no predicate and no way for the consumer to opt out. The emitted `ForEach` body is an `unsafe { }` block dereferencing raw pointers (`ref *(set{i}.GetDataPtr() + d{i})`, line 141). Unity's Assembly-CSharp has 'Allow unsafe Code' OFF by default, and asmdefs default to `"allowUnsafeCode": false` (see Editor/Strada.Core.Editor.asmdef, which is `false`). Only Runtime/Strada.Core.asmdef sets it true.

**Senaryo:** A user adds the analyzer DLL to their project root (the documented way to apply a Unity Roslyn analyzer to all assemblies) so their game code gets the 9-16 queries. Assembly-CSharp immediately fails with CS0227 'Unsafe code may only appear if compiling with /unsafe' on 8 generated ForEach bodies, and the user cannot delete the generated file.

**Düzeltme:** Do not use RegisterPostInitializationOutput for unsafe code. Either gate emission on a marker attribute / MSBuild property via `context.AnalyzerConfigOptionsProvider`, or emit `#if STRADA_ECS_UNSAFE` guards, or restructure the generated body to use `NativeSlice<T>`/`Span<T>` so it compiles without /unsafe.

## `factorygen-incrementality-defeated`

**StradaFactoryGenerator defeats its own incremental pipeline: non-equatable syntax nodes + Combine(CompilationProvider)**

| | |
|---|---|
| Konum | `SourceGenerationECS~/StradaFactoryGenerator.cs:38` |
| Kategori | performance · sourcegen |
| Etki | Per-keystroke: one GetSemanticModel per registered service, plus a full regeneration + re-parse of the output file. Zero cache hits are possible with the current shape. |
| Test | No coverage. No incrementality test (`GeneratorDriver.GetRunResult().Results[0].TrackedSteps`) exists anywhere in Tests/. |

```csharp
            var compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());
```

**Sorun:** Two independent cache-busters. (1) The transform at line 35 returns `ClassDeclarationSyntax` — a syntax node, whose reference identity changes on every edit to the file and which has no value-equality. Roslyn's incremental cache compares outputs with EqualityComparer<T>.Default, so every green-tree rebuild invalidates the entry. (2) `context.CompilationProvider` changes on literally every keystroke, and Combining it forces `Execute` to re-run in full regardless of whether any candidate changed. `Execute` then rebuilds a semantic model per class (`compilation.GetSemanticModel(classDecl!.SyntaxTree)`, line 84). Roslyn's own guidance is to never flow syntax nodes or the Compilation into the final pipeline stage. `ForAttributeWithMetadataName` — the ~99% cheaper fast path and available in the 4.3.0 Microsoft.CodeAnalysis.CSharp this project references — is not used; `CreateSyntaxProvider` with a hand-rolled `IsCandidateClass` predicate (line 34/44-48) is used instead, and that predicate matches every class with any attribute.

**Senaryo:** User edits a comment in one .cs file. Compilation changes -> Combine invalidates -> Execute re-runs over every previously-collected class, building one semantic model per class, and re-emits Strada.Generated.Factories.g.cs. In a project with 500 [AutoRegister*] services this is 500 semantic models per keystroke.

**Düzeltme:** Replace CreateSyntaxProvider with `context.SyntaxProvider.ForAttributeWithMetadataName(AutoRegisterSingletonAttribute, predicate, transform)` (one call per attribute, or a combined provider), have the transform return a small equatable record of extracted strings (TypeName, ClassName, Namespace, deps, lifetime) rather than a SyntaxNode, and register the source output on `provider.Collect()` alone — do not Combine with CompilationProvider.

## `factorygen-resolve-value-type-param`

**StradaFactoryGenerator emits c.Resolve<T>() for value-type constructor parameters, violating the `where T : class` constraint**

| | |
|---|---|
| Konum | `SourceGenerationECS~/StradaFactoryGenerator.cs:226` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only — one CS0452 per value-type parameter, breaking the entire consuming assembly. |
| Test | No coverage. No generator test exercises a service with a primitive constructor parameter. |

```csharp
                sb.Append($"c.Resolve<{deps[i].TypeName}>()");
```

**Sorun:** `ExtractServiceInfo` takes the public constructor with the most parameters (lines 151-154) and maps *every* parameter to `c.Resolve<...>()` with no type-kind filter (lines 159-165, 223-229). `IContainer.Resolve<T>()` is declared `T Resolve<T>() where T : class;` (Runtime/DI/IContainer.cs:7). Any primitive, struct, or enum constructor parameter therefore produces code that violates the constraint. Nothing in the generator checks `p.Type.IsReferenceType`.

**Senaryo:** `[AutoRegisterSingleton] public class SpawnPool { public SpawnPool(IPrefabDb db, int capacity) { } }` generates `internal static global::Game.SpawnPool Create(IContainer c) => new global::Game.SpawnPool(c.Resolve<global::Game.IPrefabDb>(), c.Resolve<int>());` -> CS0452: 'The type int must be a reference type in order to use it as parameter T in the generic type or method IContainer.Resolve<T>()'. The user's whole assembly stops compiling because of a generated file they cannot edit.

**Düzeltme:** In ExtractServiceInfo, if any constructor parameter has `!p.Type.IsReferenceType`, either skip that constructor (try the next-largest) or return null and report a diagnostic explaining that value-type dependencies are unsupported. Alternatively prefer the largest constructor whose parameters are all reference types.

## `factorygen-no-diagnostics`

**Neither generator reports a single Diagnostic — all misuse fails silently**

| | |
|---|---|
| Konum | `SourceGenerationECS~/StradaFactoryGenerator.cs:156` |
| Kategori | api-hazard · sourcegen |
| Etki | Compile-time only — but converts every misuse into a silent runtime failure with no compile-time signal. |
| Test | No coverage. No test asserts a diagnostic is produced for any misuse case. |

```csharp
            if (constructor == null)
                return null;
```

**Sorun:** `grep -n "ReportDiagnostic\|DiagnosticDescriptor"` across all three generator files returns zero hits. Every rejection path is a silent `return null` / `return`: abstract or static class (line 103), no matching attribute (line 110), no public constructor (line 156), and the whole-generator bailouts at lines 73-74 and 94-95. `SourceProductionContext.ReportDiagnostic` is never called. Same in StradaDISourceGenerator (silent `continue` at lines 34-35 and 40-41, silent `return` at 56-57) and in EntityQueryGenerator (no diagnostics at all).

**Senaryo:** A developer writes `[AutoRegisterSingleton] public class SaveService { internal SaveService(IFileIo io) {} }`. `c.DeclaredAccessibility == Accessibility.Public` (line 152) filters the only constructor out, `constructor == null`, ExtractServiceInfo returns null, the service is dropped from StradaGeneratedRegistry with no message. At runtime the container throws 'not registered' from an unrelated call site, and the developer has no way to trace it back to the constructor accessibility.

**Düzeltme:** Define DiagnosticDescriptors (e.g. STRADA001 'attributed type is abstract/static', STRADA002 'no public constructor', STRADA003 'value-type constructor parameter unsupported', STRADA004 'unbound generic type unsupported') and call `context.ReportDiagnostic(...)` at each rejection site instead of returning null.

## `factorygen-generic-type-not-skipped`

**StradaFactoryGenerator does not skip open generic types — emits `new Ns.Foo<T>(...)` with an unbound T**

| | |
|---|---|
| Konum | `SourceGenerationECS~/StradaFactoryGenerator.cs:103` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only — 3+ CS0246 errors per open generic service, breaking the assembly. |
| Test | No coverage. No generator test exercises a generic service type. |

```csharp
            if (symbol.IsAbstract || symbol.IsStatic)
                return null;
```

**Sorun:** The guard filters abstract and static types but never checks `symbol.IsGenericType` / `symbol.TypeParameters.Length > 0`. For an open generic class, `symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)` (line 169) yields `global::Ns.Foo<T>`, which is then emitted verbatim into a *non-generic* static factory class: line 221 `sb.Append($"        internal static {service.TypeName} Create(IContainer c) => new {service.TypeName}(");`, line 265 `builder.Register<{service.TypeName}>({lifetime});`, and line 290 `DirectFactory<{service.TypeName}>.Register({factoryName}.Create);`. `T` is not in scope at any of those sites.

**Senaryo:** `[AutoRegisterSingleton] public class Repository<TEntity> : IRepository<TEntity> { public Repository(IDb db) {} }` generates `internal static class Game_Repository__Factory { internal static global::Game.Repository<TEntity> Create(IContainer c) => new global::Game.Repository<TEntity>(c.Resolve<global::Game.IDb>()); }` -> CS0246 for TEntity, repeated at the Register and DirectFactory sites. The consuming assembly stops compiling, with no diagnostic pointing at the offending class.

**Düzeltme:** Add `|| symbol.IsGenericType` to the guard at line 103 and report a diagnostic explaining that open generic services must be registered manually.

## `validatedbinding-updating-flag-not-exception-safe`

**ValidatedBinding's _updating reentrancy flag lacks the try/finally that prior finding SYNC-04 was marked FIXED for**

| | |
|---|---|
| Konum | `Runtime/Sync/BindingScope.cs:279` |
| Kategori | bug · sync-reactive |
| Etki | No allocation. One thrown handler permanently disables the binding for the rest of the session. |
| Test | NOT COVERED. Tests/Runtime/Sync/ReactiveExtensionsTests.cs:305-328 (ValidatedBinding_RejectsInvalidValues) only exercises the happy path and the validator-reject path; no test subscribes a throwing handler to either property. That gap is exactly why the prior audit's 'batch 4 dogruladi' verification passed. |

```csharp
            _updating = true;
            _target.Value = value;
            _updating = false;
```

**Sorun:** `_target.Value = value` synchronously runs every subscriber of the target property (ReactiveProperty.cs:105-110). If any of them throws, the exception unwinds past line 281 and `_updating` stays true forever. Both OnSourceChanged (lines 279-281) and OnTargetChanged (lines 293-295) have this shape. The two TwoWayBinding classes in the same file were fixed — TwoWayBinding<T>.OnSourceChanged uses `try { _target.Value = value; } finally { _updating = false; }` (lines 157-158) — so the asymmetry is a partial fix, not an oversight of the hazard. Prior audit finding unit-11 SYNC-04 named ValidatedBinding<T> explicitly ('The same issue exists in TwoWayBinding<TSource, TTarget> and ValidatedBinding<T>') and is listed under FIXED at SecurityReports/2026-05-22-low-status-review.md:130 with 'agent FIXED dedi, batch 4 dogruladi'. It is not fixed.

**Senaryo:** `new ValidatedBinding<int>(model.Health, ui.HealthValue, v => v >= 0 && v <= 100)`. A UI subscriber on ui.HealthValue throws (a destroyed Text component -> MissingReferenceException, or a null-ref in a formatting handler). `_updating` is stuck true. From then on both OnSourceChanged and OnTargetChanged hit `if (_updating) return;` on line 272/286 and return immediately. The binding is permanently dead — the health bar silently stops tracking the model, with no exception and no log after the first frame.

**Düzeltme:** Wrap both bodies exactly as TwoWayBinding does: `_updating = true; try { _target.Value = value; } finally { _updating = false; }` at lines 279-281, and `_updating = true; try { _source.Value = value; } finally { _updating = false; }` at lines 293-295.

## `bindingscope-dispose-aborts-on-throw`

**BindingScope.Dispose stops disposing the moment any tracked disposable throws, leaking every remaining subscription**

| | |
|---|---|
| Konum | `Runtime/Sync/BindingScope.cs:123` |
| Kategori | bug · sync-reactive |
| Etki | Up to N-1 leaked subscriptions per throwing scope teardown, where N is the scope size (default capacity 8, line 9). |
| Test | NOT COVERED. Tests/Runtime/Sync/ReactiveExtensionsTests.cs BindingScopeTests never registers a disposable that throws. |

```csharp
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();
```

**Sorun:** No per-item try/catch. Because `_disposed` was already set to true on line 120, a throw here is unrecoverable: the loop aborts, `_disposables.Clear()` is skipped so the list is left in a half-disposed state, and any subsequent Dispose call returns immediately at line 119. Every disposable at an index below the throwing one stays subscribed forever. This is the aggregating-cleanup pattern where exception isolation matters most — Runtime/Core/PlayerLoop.cs:183-196 wraps each callback invocation in try/catch for exactly this reason, so the codebase has the convention and it was not applied here.

**Senaryo:** A scope tracks [tokenA, mappedProperty, tokenB, twoWayBinding]. LIFO disposal starts at twoWayBinding, whose Dispose runs a SubscriptionToken action that throws (e.g. a user-supplied unsubscribe callback touching a destroyed object). tokenB, mappedProperty and tokenA are never disposed and `_disposables` still holds all four. The MonoBehaviour that owned the scope is destroyed but its three surviving subscriptions keep it alive and keep firing.

**Düzeltme:** Isolate each disposal: `for (int i = _disposables.Count - 1; i >= 0; i--) { try { _disposables[i].Dispose(); } catch (Exception ex) { StradaLog.LogError($"BindingScope: disposable threw during scope teardown: {ex}", LogModule.Sync); } } _disposables.Clear();` — and move Clear into a finally so it runs regardless.

## `cb-object-equals-boxes-both-operands`

**ComponentBinding<TComponent,TProperty>.Sync uses static object.Equals, boxing both values on every sync of every binding every frame**

| | |
|---|---|
| Konum | `Runtime/Sync/ComponentBinding.cs:96` |
| Kategori | allocation · sync-reactive |
| Etki | 2 boxed allocations per binding per Sync() call. At 48 bytes/sync for a float property and one sync per binding per frame: 200 bindings -> 9.6 KB/frame -> 576 KB/s at 60 fps. |
| Test | NOT COVERED for allocation. Tests/Runtime/Sync/BindingPropertyTests.cs:81-192 and Tests/Runtime/Sync/BridgeTests.cs:35-51 verify Sync() semantics but assert nothing about GC. Tests/Runtime/Sync/BridgeTests.cs:159-182 (Benchmark_100k_BindingSync) asserts only `Assert.Less(sw.ElapsedMilliseconds, 200)` for 100k syncs — a 2us/sync budget that boxing comfortably fits inside, so the benchmark actively hides this. |

```csharp
                if (!Equals(_lastValue, newValue))
```

**Sorun:** Unqualified two-argument `Equals` in a class deriving from object resolves to `static bool object.Equals(object objA, object objB)`. Both `_lastValue` and `newValue` are of the unconstrained type parameter TProperty, so both are boxed at the call site on every invocation — unconditionally, for every value type, including `float` and `int`. This is not the `EqualityComparer<T>.Default` path used elsewhere in the layer (ReactiveProperty.cs:24); it is strictly worse, because Default at least avoids boxing for types implementing IEquatable<T>. `Sync()` is the per-frame hot path: EntityMediator.SyncBindings (EntityMediator.cs:76-77) calls it on every binding, driven by UpdateMediator every frame.

**Senaryo:** `new ComponentBinding<HealthComponent, float>(entities, entity, c => c.Current, v => bar.value = v)` (the exact shape in Tests/Runtime/Sync/BridgeTests.cs:41-45). Each Sync() allocates two boxed floats (24 bytes each on 64-bit) whether or not the value changed. 200 mediators x 3 bindings x 60 fps = 72,000 boxes/sec = ~1.7 MB/s of pure garbage, producing a Gen0 collection every few seconds on a path advertised as the ECS<->UI bridge.

**Düzeltme:** Add `private static readonly EqualityComparer<TProperty> s_comparer = EqualityComparer<TProperty>.Default;` and use `if (!s_comparer.Equals(_lastValue, newValue))`. That removes boxing entirely for any TProperty implementing IEquatable<TProperty> (float, int, Vector3, all primitives). To eliminate it for all cases, add an `where TProperty : IEquatable<TProperty>` overload or accept a comparer in the constructor.

## `autosync-valuetype-equals-boxes`

**AutoSyncBinding<TComponent>.Sync boxes the component and falls into ValueType.Equals reflective comparison every frame**

| | |
|---|---|
| Konum | `Runtime/Sync/ComponentBinding.cs:196` |
| Kategori | allocation · sync-reactive |
| Etki | 1 boxed allocation of sizeof(TComponent)+16 bytes per binding per Sync(), plus a non-devirtualized ValueType.Equals call. 500 bindings x 32 bytes x 60 fps = 960 KB/s. |
| Test | NOT COVERED for allocation. Tests/Runtime/Sync/BridgeTests.cs:73-89 tests only detection semantics; Benchmark_100k_BindingSync (line 159-182) uses a 200ms budget for 100k syncs which the boxing does not breach. |

```csharp
                if (!_lastValue.Equals(current))
```

**Sorun:** TComponent is constrained to `unmanaged, IComponent`, and IComponent is an empty marker interface (Runtime/ECS/IComponent.cs:3) — no IEquatable<TComponent> constraint exists. The only applicable member is `object.Equals(object)`, so the compiler emits `constrained. !TComponent callvirt object::Equals(object)` and must box `current` at the call site. For any component struct that does not override Equals (i.e. essentially all of them — none of the test components in BridgeTests.cs:133-143 or BindingPropertyTests.cs:462-467 do), the call lands in ValueType.Equals, which on Mono performs a CanCompareBits check and, for structs with padding or mixed field types, a reflection-driven field-by-field comparison.

**Senaryo:** `new AutoSyncBinding<PositionComponent>(entities, entity, c => transform.position = ...)` (BridgeTests.cs:79-82). Each Sync() boxes one PositionComponent (16 bytes payload + 16 bytes header = 32 bytes) plus the ValueType.Equals dispatch. Under ViewSyncRunner's default ForceAll mode this runs for every AutoSyncBinding every LateUpdate: 500 entities -> 16 KB/frame -> ~1 MB/s.

**Düzeltme:** Compare bits without boxing. Since TComponent is `unmanaged`, use `System.Runtime.CompilerServices.Unsafe.AreSame`-style bitwise compare, or simplest: `private static readonly EqualityComparer<TComponent> s_comparer = EqualityComparer<TComponent>.Default;` plus a documented recommendation that components implement IEquatable<T>; best is to add `where TComponent : unmanaged, IComponent, IEquatable<TComponent>` on a fast-path overload, or use `MemoryMarshal`/`Span<byte>.SequenceEqual` over the struct bytes (legal because the constraint is `unmanaged`).

## `computed-eager-invalidate-diamond-glitch`

**ComputedProperty.Invalidate recomputes eagerly and emits a glitch value on diamond dependencies**

| | |
|---|---|
| Konum | `Runtime/Sync/ComputedProperty.cs:187` |
| Kategori | bug · sync-reactive |
| Etki | Eager evaluation: one full `_computation()` invocation per dependency per change, even with zero subscribers. In the 10-deep chain built by Tests/Benchmarks/ComputedPropertyBenchmarks.cs:118-123, each single `source.Value = i` triggers 10 synchronous recomputations; 1000 sets -> 10,000 computations. Diamond: N notifications for N converging paths, N-1 of them carrying glitch values. |
| Test | NOT COVERED. Tests/Runtime/Sync/ReactiveExtensionsTests.cs:157-220 (ComputedPropertyTests) only tests linear single- and two-dependency cases where both deps are independent ReactiveProperties, so no glitch can occur. Tests/Benchmarks/ComputedPropertyBenchmarks.cs:109-153 builds a chain, not a diamond, and asserts nothing — it only logs timings, so it measures the eager-recompute cost without flagging it. |

```csharp
            var oldValue = _cachedValue;
            _isDirty = true;
            var newValue = Value;

            if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                for (int i = 0; i < _handlers.Count; i++)
                    _handlers[i](newValue);
            }
```

**Sorun:** Two defects in one block. (1) `_isDirty = true; var newValue = Value;` sets the dirty flag and immediately reads it back, so the lazy cache at lines 28-32 is dead: every dependency change forces a full recomputation even when nobody has subscribed and nobody reads Value. (2) There is no propagation barrier, so a diamond dependency emits an inconsistent intermediate value. (3) The notification loop reads `_handlers.Count` live — a handler that unsubscribes itself shifts the list and the following handler is skipped, the exact defect ReactiveProperty.Notify was hardened against.

**Senaryo:** root = ReactiveProperty<int>(1); a = From(root, x => x+1); b = From(root, x => x*2); sum = From(a, b, (x,y) => x+y). Set root.Value = 2. ReactiveProperty.Notify snapshots [a,b]. a invalidates -> a=3 -> notifies sum -> sum recomputes as a(3) + b(2, still stale) = 5 and notifies subscribers with 5. Then b invalidates -> b=4 -> sum recomputes = 7 and notifies with 7. Subscribers observe 5, a value that corresponds to no consistent state of root (root=1 gives 4, root=2 gives 7). A health-bar driven this way flickers; a damage formula computed this way applies the wrong number.

**Düzeltme:** For (1): drop the eager read — set `_isDirty = true` and only notify/recompute if `_handlers.Count > 0`, leaving pure-pull consumers lazy. For (2): add a propagation epoch. Give the layer a static `s_epoch` counter incremented by ReactiveProperty.Notify at the start of a propagation; ComputedProperty.Invalidate records the epoch and defers notification to the end of the epoch so each computed notifies at most once per root change with fully settled inputs. For (3): snapshot `_handlers` before the loop as ReactiveProperty.Notify does.

## `computed-frommany-il2cpp-aot-break`

**ComputedProperty.FromMany builds its subscription through MakeGenericMethod + CreateDelegate + name-based GetMethod with no link.xml or [Preserve] in the package**

| | |
|---|---|
| Konum | `Runtime/Sync/ComputedProperty.cs:161` |
| Kategori | aot-il2cpp · sync-reactive |
| Etki | Startup/first-call only, but it is a hard crash, not a cost. Also allocates one `object[]` per dependency at construction (line 167) — construction-time only, not a finding on its own. |
| Test | NOT COVERED on the target platform. Tests/Benchmarks/ComputedPropertyBenchmarks.cs:16-71 exercises FromMany with 5/10/20 dependencies — but Unity test runs are Editor/Mono, where MakeGenericMethod works and nothing is stripped. There is no IL2CPP smoke test in the repo, so CI is structurally incapable of catching this. |

```csharp
                    var invalidateMethod = s_invalidateMethodCache.GetOrAdd(depType,
                        dt => s_invalidateIgnoreParamMethod.MakeGenericMethod(dt));

                    // Create handler delegate
                    var handlerType = typeof(Action<>).MakeGenericType(depType);
                    var handler = Delegate.CreateDelegate(handlerType, this, invalidateMethod);

                    var token = (IDisposable)subscribeMethod.Invoke(dependency, new object[] { handler });
```

**Sorun:** Three separate IL2CPP/AOT hazards on one path. (1) `MakeGenericMethod(dt)` where dt is a value type (int, float, any component struct) requires the closed instantiation `InvalidateIgnoreParam<int>` to have been generated at build time. IL2CPP only generates instantiations it can see statically; `InvalidateIgnoreParam<T>` is invoked exclusively through reflection, so none are generated and the call throws ExecutionEngineException at runtime. (2) `s_invalidateIgnoreParamMethod` is resolved by string at line 13-14 via `GetMethod("InvalidateIgnoreParam", BindingFlags.NonPublic | BindingFlags.Instance)`; the private method has no static caller, so Unity managed-code stripping (default Medium on IL2CPP) removes it and the field is null — the NRE surfaces at static-init of the closed generic, not at the call site. (3) `find . -name link.xml` returns nothing and there is no `[Preserve]` attribute anywhere under Runtime/, so neither hazard is mitigated.

**Senaryo:** Ship an iOS/Android/console IL2CPP build using `ComputedProperty<float>.FromMany(() => ..., statA, statB, statC, statD, statE, statF, statG)` (the documented escape hatch for >6 dependencies, ComputedProperty.cs:128-134). In the Editor (Mono) it works and the benchmark passes. In the IL2CPP player it throws ExecutionEngineException('Attempting to call method for which no ahead of time (AOT) code was generated') or NullReferenceException from the static initialiser, on the first FromMany call.

**Düzeltme:** Eliminate the reflection. Add typed overloads `FromMany<T1..Tn>` that call the existing generic `WatchDependency<TDep>` path (ComputedProperty.cs:176-182), or change FromMany's signature to accept `params IDisposable[] subscriptions` produced by the caller, or accept `Action<Action> subscribeCallbacks`. If the untyped path must survive, at minimum add a link.xml preserving Strada.Core and mark `InvalidateIgnoreParam` with `[UnityEngine.Scripting.Preserve]`, and add an explicit AOT-hint method that references `InvalidateIgnoreParam<int>/<float>/<bool>` so IL2CPP generates those instantiations.

## `docs-document-nonexistent-apis`

**The Sync documentation's ComputedProperty and BindingScope examples call constructors and methods that do not exist**

| | |
|---|---|
| Konum | `Runtime/Sync/ComputedProperty.cs:37` |
| Kategori | api-hazard · sync-reactive |
| Etki | No runtime cost. Blocks adoption of the two primary APIs in the domain. |
| Test | NOT COVERED — and structurally uncoverable, since no test compiles documentation snippets. Tests/Runtime/Sync/ReactiveExtensionsTests.cs:161 correctly uses `ComputedProperty<int>.From(source, x => x * x)`, so the tests and the docs disagree about the API and only the tests are checked. |

```csharp
        private ComputedProperty(Func<T> computation)
        {
            _computation = computation;
            _cachedValue = _computation();
            _isDirty = false;
        }
```

**Sorun:** The only constructor on ComputedProperty<T> is private, yet Documentation~/Sync.md writes `new ComputedProperty<float>(() => ..., health, maxHealth)` at lines 397, 417, 431, 432, 515 and 666 — six examples, comprising the entire ComputedProperty section including the 'Chained Computations' and 'Complex Computations' subsections. None compile; the real entry points are the static From/FromMany factories (lines 44-143). The same section documents `_scope.Bind(prop, handler)` eight times (Sync.md lines 458, 459, 460, 475, 478, 533, 534, 647) but BindingScope exposes no Bind method (it has Track, Add, Subscribe, SubscribeAndInvoke, Select, Where, CombineLatest, Computed, BindTwoWay), and documents a `SubscribeToken` extension three times (lines 789, 794, 804) that returns zero hits from `grep -rn SubscribeToken --include='*.cs' .`. A user copying any documented example of this domain's two headline APIs gets a compile error.

**Senaryo:** A developer follows Documentation~/Sync.md's ComputedProperty section verbatim. `new ComputedProperty<float>(() => (float)health.Value / maxHealth.Value, health, maxHealth)` fails with CS0122 ('ComputedProperty<float>.ComputedProperty(Func<float>)' is inaccessible due to its protection level) plus CS1729. They then try the BindingScope example and hit CS1061 for `_scope.Bind`. Nothing in the docs points to From/FromMany or Subscribe.

**Düzeltme:** Either add a public constructor with the documented `(Func<T> computation, params object[] dependencies)` signature delegating to the FromMany path — which also makes the docs true — or rewrite Sync.md lines 390-440, 505-540, 640-670 and 780-815 to use `ComputedProperty<T>.From(...)`, `scope.Subscribe(...)` and `property.Subscribe(...)`. Given the AOT problem with the untyped dependency path, rewriting the docs to the typed From overloads is the better direction.

## `ehr-version-check-is-tautology`

**EntityHandleRegistry.IsValid/Resolve compare a stored version against a copy of itself, so a handle is valid forever including after the entity is destroyed and its index recycled**

| | |
|---|---|
| Konum | `Runtime/Sync/EntityHandleRegistry.cs:59` |
| Kategori | bug · sync-reactive |
| Etki | Correctness: IsValid can never return false for a registered handle. Memory: 2 dictionary entries (~64+ bytes) leaked per entity ever registered, never reclaimed until Clear(). |
| Test | NOT COVERED. There is no EntityHandleRegistry test file anywhere under Tests/. No test registers a handle, destroys the entity, and asserts IsValid == false. Prior audit filed only the int overflow (SYNC-08, now fixed at lines 19-21) and never questioned the validity check itself. |

```csharp
            if (_handleToEntity.TryGetValue(handle.Id, out Entity entity))
                return entity.Version == handle.Version;

            return false;
```

**Sorun:** At Register time (lines 22-25) the registry stores `_handleToEntity[handleId] = entity` and returns `new EntityHandle(handleId, entity.Version)`. Both sides of the comparison are frozen snapshots of the same Entity struct taken at the same instant, so `entity.Version == handle.Version` is a tautology for any registered handle. The registry never holds an EntityManager reference and never re-queries live entity state, so it cannot detect that the entity was destroyed. Resolve has the identical check at line 35. The stale-handle protection the class exists to provide does not exist. Compounding this, nothing removes entries on entity destruction — Unregister (line 41-51) is caller-driven only — so both dictionaries grow monotonically.

**Senaryo:** handle = registry.Register(bulletEntity); the bullet is destroyed; EntityManager recycles index N with version+1 for a new pickup entity. `registry.IsValid(handle)` returns true (comparing v3 to v3). `registry.Resolve(handle)` returns the stale Entity(N, v3), and any `entityManager.GetComponent<T>(staleEntity)` then throws via the Exists check at EntityManager.cs:191-192 — the failure surfaces as an exception deep in gameplay code rather than as the false IsValid the API promised. Separately: a bullet-hell spawning/destroying 1000 entities per second adds 2 permanent dictionary entries per entity, ~50 MB/hour of unreclaimable Dictionary storage.

**Düzeltme:** Hold the EntityManager and validate against live state: `public bool IsValid(EntityHandle handle) => handle.IsValid && _handleToEntity.TryGetValue(handle.Id, out var e) && _entities.Exists(e);` and the same in Resolve (EntityManager.Exists at EntityManager.cs:131-137 already compares `_versions[entity.Index] == entity.Version`). Add an EntityDestroyed subscription (the struct exists at SyncEvents.cs:53-61) that calls Unregister so both dictionaries shrink.

## `forceall-default-unconditional-onchanged`

**ViewSyncRunner defaults to ForceAll and ComponentBinding<T>.Sync fires OnChanged every frame even when nothing changed**

| | |
|---|---|
| Konum | `Runtime/Sync/EntityView.cs:254` |
| Kategori | performance · sync-reactive |
| Etki | O(views x bindings) callback invocations per frame with a 100% false-positive rate at steady state. 500 views x 1 binding x 60 fps = 30,000 spurious handler invocations/sec, each dragging whatever UI/render work the handler does. |
| Test | NOT COVERED. Tests/Runtime/Sync/EntityViewBindingTests.cs:123-135 asserts Sync() updates the cached value but never asserts that an unchanged component produces zero OnChanged calls — contrast with Tests/Runtime/Sync/BindingPropertyTests.cs:120-145 which does exactly that assertion for the OTHER ComponentBinding class, confirming the intended contract was simply not applied here. |

```csharp
                var current = _entityManager.GetComponent<T>(_entity);
                _cachedValue = current;
                _dirty = false;
                _syncState = BindingSyncState.Synced;
                _lastError = null;
                OnChanged?.Invoke(current);
```

**Sorun:** There is no comparison against `_cachedValue` before raising OnChanged — the event fires unconditionally on every Sync(). Combined with ViewSyncRunner.cs:15 (`[SerializeField] private ViewSyncMode _syncMode = ViewSyncMode.ForceAll;`, the value PoolManager.CreateSyncRunner installs at PoolManager.cs:56) and ViewRegistry.ForceSyncAll, the default configuration invokes every view's every OnChanged handler every LateUpdate regardless of whether any ECS data moved. Every sibling binding in the layer does compare first — ComponentBinding.cs:96 and :196 both gate the callback on an inequality check — so this class is the outlier.

**Senaryo:** 500 pooled enemy views, each with one `EntityView<HealthComponent>` primary binding whose OnComponentChanged updates a UI slider and a material property block. With no entity changing at all, LateUpdate still performs 500 x (Exists + HasComponent + GetComponent + delegate invoke + slider write + SetPropertyBlock). The material/canvas writes dirty Unity's render state every frame for objects that did not change, which is far more expensive than the sync itself.

**Düzeltme:** Gate the event: `if (!EqualityComparer<T>.Default.Equals(_cachedValue, current)) { _cachedValue = current; OnChanged?.Invoke(current); }` and move `_syncState`/`_lastError` assignment outside the guard. Independently, change ViewSyncRunner's default away from ForceAll once the dirty path actually works (see markdirty-never-called-dirtyonly-dead).

## `entityview-bind-silent-noop-wrong-entity`

**EntityView.Bind silently returns when already bound, so re-binding leaves the view attached to the previous entity while the registry maps the new one**

| | |
|---|---|
| Konum | `Runtime/Sync/EntityView.cs:38` |
| Kategori | api-hazard · sync-reactive |
| Etki | No allocation. Silent cross-entity data corruption for as long as the mis-binding persists. |
| Test | NOT COVERED. Tests/Runtime/Sync/EntityViewBindingTests.cs:36-45 binds once and asserts. No test binds twice, and no test registers an already-bound view under a different entity. |

```csharp
            if (_bound) return;
```

**Sorun:** Bind is idempotent by silent no-op rather than by error, and there is no check that the incoming entity matches the currently bound one. ViewRegistry.Register relies on this shape at ViewRegistry.cs:49-52 (`if (!view.IsBound) { view.Bind(_container, _entityManager, entity); }`) and then unconditionally writes `_entityToView[GetEntityKey(entity)] = view` at line 54. So registering an already-bound view against a different entity produces a registry that claims view V represents entity E2 while V.Entity is still E1 and all of V's bindings still read E1's components.

**Senaryo:** A view is spawned for entity E1, then game code registers the same view for entity E2 (a hand-off, a possession mechanic, or the duplicate-pool-entry path from vp-double-despawn-duplicate). `registry.GetView(E2)` returns V. `V.Entity` returns E1. ForceSyncBindings reads E1's HealthComponent and writes it into the UI that the player believes belongs to E2. Push() writes UI edits back into E1's components. There is no warning anywhere.

**Düzeltme:** Make the contract explicit: `if (_bound) { if (_entity == entity) return; throw new InvalidOperationException($"{GetType().Name} is already bound to {_entity}; call Unbind() before binding to {entity}."); }` — or Unbind-then-rebind. In ViewRegistry.Register, replace the `if (!view.IsBound)` gate with an assertion that a bound view's Entity equals the entity being registered.

## `mediator-registry-syncall-empty`

**MediatorRegistry.SyncAll has an empty body while being part of the IMediatorRegistry contract**

| | |
|---|---|
| Konum | `Runtime/Sync/MediatorRegistry.cs:57` |
| Kategori | bug · sync-reactive |
| Etki | Correctness only. Zero mediators synced instead of N, permanently. |
| Test | NOT COVERED. Tests/Runtime/Sync/BridgeTests.cs:92-131 exercises Create/Release/pooling but never calls SyncAll. Tests/Runtime/Sync/BridgeIntegrationTests.cs:96 calls `mediator.SyncBindings()` directly on the mediator, bypassing the registry entirely — which is precisely why the empty method survived. |

```csharp
        public void SyncAll()
        {
        }
```

**Sorun:** `IMediatorRegistry.SyncAll()` (declared at line 18) is the only mechanism the interface offers to drive mediator synchronisation, and the sole implementation does nothing. `_activeMediators` is typed `List<IDisposable>` (line 25) rather than a mediator-typed list, so the implementation could not iterate and call SyncBindings even if it wanted to — the type was chosen for ReleaseAll's Dispose loop (lines 63-67) and forecloses the sync path. Callers who wire `registry.SyncAll()` into their update loop get a silent no-op: EntityMediator.SyncBindings (EntityMediator.cs:73-78) is real and functional but nothing in Runtime/ ever calls it.

**Senaryo:** A team follows the mediator pattern, creates mediators through MediatorRegistry.Create, and calls `_mediatorRegistry.SyncAll()` from their game loop as the interface advertises. No mediator ever syncs. Every ComponentBinding the mediators registered stays at its bind-time value, and every OnChanged handler never fires. There is no exception, no log, and no compiler warning — the method exists and returns.

**Düzeltme:** Give the registry a typed list. Add `private interface ISyncableMediator { void SyncBindings(); }` implemented by EntityMediator<TView>, store mediators in `List<ISyncableMediator>` alongside the disposal list, and implement `for (int i = 0; i < _syncable.Count; i++) _syncable[i].SyncBindings();`. If mediator sync is deliberately caller-driven, remove SyncAll from IMediatorRegistry rather than shipping an empty implementation.

## `mediator-pool-reuse-accumulates-disposables`

**Pooled mediators are returned without clearing _disposables and re-Initialized on every rent, so OnInitialize side effects accumulate across reuse cycles**

| | |
|---|---|
| Konum | `Runtime/Sync/MediatorRegistry.cs:41` |
| Kategori | bug · sync-reactive |
| Etki | One extra retained IDisposable (plus its closure) per OnInitialize registration per rent/release cycle, never reclaimed. Grows linearly with spawn count over the session. |
| Test | NOT COVERED. Tests/Runtime/Sync/BridgeTests.cs:115-131 (MediatorRegistry_PoolsMediator) does exactly one create/release/create round trip and asserts only AreSame. TestMediator (line 147-151) has an empty OnBind/OnUnbind and no OnInitialize, so nothing can accumulate. |

```csharp
            var mediator = MediatorPool<TMediator, TView>.Instance.Rent();
            mediator.Initialize(_container);
            mediator.Bind(entity, view);
```

**Sorun:** Release (lines 48-55) calls only `mediator.Unbind()` before returning the instance to the static pool. Unbind (EntityMediator.cs:54-71) clears `_bindings` and `_bindSubscriptions` but deliberately does NOT touch `_disposables` — that list is only drained by Dispose (EntityMediator.cs:185-187), which the pooled path never calls. Meanwhile Create calls `Initialize(_container)` on every rent, which re-runs `OnInitialize()` (EntityMediator.cs:40). Any resource a subclass registers via `AddDisposable` (EntityMediator.cs:139-142) inside OnInitialize is therefore appended again on every reuse and never released. The static pool (MediatorPool<TMediator,TView>, lines 79-113) has no domain-reload reset hook, so with Unity's Fast Enter Play Mode the accumulation persists across play sessions.

**Senaryo:** `protected override void OnInitialize() { AddDisposable(_bus.Subscribe<GamePaused>(OnPaused)); }`. A wave-based shooter rents and releases the same mediator 500 times over a session. `_disposables` reaches 500 entries and the EventBus holds 500 live GamePaused handlers on one mediator — every pause event invokes OnPaused 500 times, and 500 closures are retained. Nothing is ever released because the pooled instance is never Disposed (MediatorRegistry.ReleaseAll at line 65 only disposes ACTIVE mediators; pooled ones are unreachable from the registry).

**Düzeltme:** Make Initialize idempotent (`if (_initialized) return;` guarded by a flag reset in Dispose) so OnInitialize runs once per instance lifetime, and drain `_disposables` in Release before pooling — or move Initialize out of Create so it is called once when the instance is first constructed. Additionally add a `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` reset for the static pool, mirroring the one Runtime/Core/PlayerLoop.cs:24-33 already uses.

## `derived-properties-no-snapshot`

**Every derived reactive type iterates its live handler list during notification (SYNC-01 fix never propagated past ReactiveProperty)**

| | |
|---|---|
| Konum | `Runtime/Sync/ReactiveExtensions.cs:104` |
| Kategori | bug · sync-reactive |
| Etki | No allocation cost (that is the bug's only upside); one skipped handler per unsubscribe-during-notify, silently. |
| Test | NOT COVERED. Tests/Runtime/Sync/ReactiveExtensionsTests.cs never subscribes more than one handler to a derived property and never unsubscribes from inside a handler. The corresponding scenario is tested only for ReactiveProperty, which is why the partial fix looked complete. |

```csharp
            for (int i = 0; i < _handlers.Count; i++)
                _handlers[i](_cachedValue);
```

**Sorun:** MappedProperty.OnSourceChanged (lines 104-105), FilteredProperty.OnSourceChanged (153-154), CombinedProperty<T1,T2>.UpdateValue (213-214), CombinedProperty<T1,T2,T3>.UpdateValue (280-281), ThrottledProperty.OnSourceChanged (338-339) and Flush (354-355), DistinctProperty.OnSourceChanged (401-402), and ComputedProperty.Invalidate (ComputedProperty.cs:193-194) all index a live List<Action<T>> while invoking arbitrary handlers. A handler that unsubscribes itself — the normal `token.Dispose()` inside a callback, which is exactly what Subscribe's returned token invites (line 112: `new SubscriptionToken(() => _handlers.Remove(handler))`) — shifts every subsequent element down by one, so the handler at the old index i+1 is skipped for that notification. Prior finding SYNC-01 named MappedProperty, FilteredProperty and CombinedProperty explicitly; only ReactiveProperty/ReactiveCollection received the ToArray fix, and SYNC-01 does not appear in any OPEN/PARTIAL/OPEN-BY-DESIGN table of SecurityReports/2026-05-22-medium-status-review.md, i.e. it was bucketed FIXED.

**Senaryo:** `var mapped = model.State.Select(s => s.ToString());` with three subscribers A, B, C. A is a one-shot that disposes its token inside its own callback (a 'fire once on first transition' pattern). Notification: i=0 invokes A, A removes itself, list becomes [B, C]; i=1 invokes C. B is never called for this change. B's UI element is silently one update stale, permanently.

**Düzeltme:** Apply the same treatment ReactiveProperty received, ideally as copy-on-write: store handlers in an immutable array rebuilt on Subscribe/unsubscribe, and iterate a local reference in the notify loops. Doing it as `ToArray()` per notification would fix correctness but reintroduce the per-notification allocation described in rp-notify-array-per-notification.

## `chained-operator-intermediate-leak`

**Chaining reactive operators leaks the intermediate operator: disposing the outer one leaves the inner permanently subscribed to the source**

| | |
|---|---|
| Konum | `Runtime/Sync/ReactiveExtensions.cs:143` |
| Kategori | bug · sync-reactive |
| Etki | One permanently-retained operator object plus its closure per chain link per chained subscription, plus one wasted delegate invocation per source notification per leaked link. |
| Test | NOT COVERED. Tests/Runtime/Sync/ReactiveExtensionsTests.cs disposes every operator it creates (lines 20, 35, 57, 75, 94, 118) but never chains two operators together, so the leak cannot appear. No test asserts on `source.SubscriberCount` after disposing a derived property. |

```csharp
            if (_predicate(_source.Value))
                _lastValidValue = _source.Value;
            _sourceToken = _source.Subscribe(OnSourceChanged);
```

**Sorun:** Every operator subscribes to its source in its constructor and stores only its own `_sourceToken`. Dispose (FilteredProperty lines 164-170, and the identical shape in MappedProperty 115-121, CombinedProperty 224-231 / 291-299, ThrottledProperty 365-371, DistinctProperty 412-418) disposes that one token and clears its own handlers — it never disposes `_source`. When `_source` is itself an operator the caller does not hold a reference to, that intermediate is unreachable but still registered in the root property's `_handlers` list, so the root keeps it and its entire closure graph alive forever. Documentation~/Sync.md lines 810-813 claims 'Derived reactive types (MappedProperty, FilteredProperty, CombinedProperty, ThrottledProperty, DistinctProperty, PropertyBinding, ConvertedBinding, TwoWayBinding, ValidatedBinding, ComputedProperty) already capture tokens internally and dispose them when their own Dispose runs' — true for their own token, false for the chain.

**Senaryo:** `_scope.Add(model.Health.Select(h => h / 100f).Where(p => p > 0f));` — the idiomatic chain. BindingScope disposes only the FilteredProperty. The MappedProperty stays in `model.Health._handlers` forever, holding the selector closure and, through it, whatever the lambda captured (typically the MonoBehaviour). Repeat per spawned enemy: model.Health's handler list grows without bound, every Health set notifies N dead mappers, and N destroyed MonoBehaviours are kept alive.

**Düzeltme:** Give each operator ownership of a source it created. Simplest: have the extension methods (Select/Where/CombineLatest/Throttle/DistinctUntilChanged, lines 9-57) pass an `bool ownsSource` flag when the source is itself IDisposable and came from this API, and dispose it in Dispose. Alternative: make the operators track an `IDisposable _upstream` set by the extension method when it wraps another operator.

## `throttle-drops-trailing-value`

**ThrottledProperty stores a pending value that is never emitted unless the caller manually calls Flush, silently dropping the final update**

| | |
|---|---|
| Konum | `Runtime/Sync/ReactiveExtensions.cs:343` |
| Kategori | bug · sync-reactive |
| Etki | Correctness only. Up to one dropped update per quiet period, held indefinitely. |
| Test | NOT COVERED. There is no ThrottledProperty test anywhere: Tests/Runtime/Sync/ReactiveExtensionsTests.cs covers Select, Where, CombineLatest, DistinctUntilChanged and BindTo but has no Throttle test, and no test calls Flush(). |

```csharp
                _pendingValue = value;
                _hasPending = true;
```

**Sorun:** When a change arrives inside the throttle window it is stashed in `_pendingValue` and `_hasPending` is set — and nothing ever drains it. There is no timer, no PlayerLoop registration, no coroutine; the only drain is the public `Flush()` at line 348, which no code in Runtime/ calls. `Value` returns `_lastEmittedValue` (line 317), so the pending value is invisible to both push and pull consumers. A source that changes once inside the window and then goes quiet leaves the throttled property permanently reporting the pre-change value.

**Senaryo:** `var throttled = player.Position.Throttle(0.1f);` driving a minimap marker. The player moves, stops, and stays still. The last movement lands 30 ms after the previous emit, so it is stashed as pending. No further source change ever occurs, so OnSourceChanged is never called again and Flush is never invoked. The minimap marker is stuck at the position from up to 100 ms before the player stopped — permanently, until the player moves again. Standard throttle semantics guarantee a trailing emit.

**Düzeltme:** Register a drain with the framework's PlayerLoop (Runtime/Core/PlayerLoop.cs:61-65 exposes RegisterUpdate) in the constructor and unregister in Dispose, calling Flush when `_hasPending && Time.realtimeSinceStartup - _lastEmitTime >= _interval`. If a self-driving throttle is undesirable, rename the type to make the manual contract explicit and document that Flush must be pumped — but as shipped the type silently loses data.

## `rp-notify-array-per-notification`

**ReactiveProperty.Notify allocates a fresh Action<T>[] snapshot on every single notification**

| | |
|---|---|
| Konum | `Runtime/Sync/ReactiveProperty.cs:107` |
| Kategori | allocation · sync-reactive |
| Etki | 24 + 8*N bytes per notification, where N = subscriber count. Per property, per value change, per frame. Zero after the copy-on-write fix. |
| Test | NOT COVERED for allocation. Tests/Runtime/Sync/ReactivePropertyTests.cs and ReactivePropertyPropertyTests.cs exercise notification semantics exhaustively (including 20-subscriber cases at ReactivePropertyPropertyTests.cs:30) but contain no GC.Alloc or Unity.PerformanceTesting measurement. No benchmark exists for ReactiveProperty at all (Tests/Benchmarks/ has ComputedPropertyBenchmarks but not ReactivePropertyBenchmarks). |

```csharp
            var snapshot = _handlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_value);
```

**Sorun:** Every `Value` setter that actually changes the value calls Notify(), which unconditionally copies the whole handler list to a new array. The snapshot is the correct fix for reentrancy (it closed prior finding SYNC-01 for this class) but it is applied eagerly rather than copy-on-write: the array is allocated even when no handler mutates the list, which is the overwhelmingly common case. The same pattern is repeated three more times in ReactiveCollection: NotifyAdd (line 181), NotifyRemove (line 189), NotifyClear (line 195) — so `ReactiveCollection<T>.Add` allocates an array per item added.

**Senaryo:** A HUD model with 40 ReactiveProperty fields (health, ammo, score, cooldowns...) each with 2 subscribers, updated once per frame: 40 arrays x (24 header + 2*8 refs) = 1600 bytes/frame = 96 KB/s. Filling a `ReactiveCollection<ItemStack>` inventory of 1000 items on level load allocates 1000 separate `Action<ItemStack>[]` arrays in one frame.

**Düzeltme:** Copy-on-write instead of copy-on-notify: keep an immutable `Action<T>[] _handlers` field, rebuild it only inside Subscribe/Unsubscribe/Clear, and have Notify iterate the field directly after taking a local reference (`var h = _handlers; for (int i = 0; i < h.Length; i++) h[i](_value);`). That preserves the exact reentrancy guarantee documented at lines 99-103 with zero steady-state allocation and moves the cost to the rare mutation path.

## `vp-spawn-no-null-check-on-pop`

**ViewPool.Spawn calls SetActive on a view popped from the free stack without checking it still exists**

| | |
|---|---|
| Konum | `Runtime/Sync/ViewPool.cs:65` |
| Kategori | bug · sync-reactive |
| Etki | No steady-state cost; one MissingReferenceException per destroyed pooled instance, thrown from an unrelated spawn call site. |
| Test | NOT COVERED. Tests/Runtime/Sync/ViewPoolTests.cs never destroys a pooled GameObject out from under the pool; TearDown (lines 34-44) destroys the prefab and roots only after the pool is disposed. |

```csharp
                view = _available.Pop();
                view.gameObject.SetActive(true);
```

**Sorun:** Pooled views live under `_poolRoot`, a plain GameObject that is not DontDestroyOnLoad in the direct-construction path (ViewPoolFactory.Create at lines 216-218 creates loose roots; only PoolManager.CreatePersistentRoots marks its own root persistent, PoolManager.cs:40-41). A scene load destroys the pooled GameObjects while the Stack still holds their managed wrappers. Spawn pops one and immediately dereferences `.gameObject` with no guard — while the very same class does guard in Clear() at line 190 (`if (view != null && view.gameObject != null)`), proving the author knew pooled views can be destroyed.

**Senaryo:** Pool prewarmed with 20 views under a ViewPoolFactory-created root. `SceneManager.LoadScene` destroys the root and its children. The next `pool.Spawn(entity)` pops a destroyed view and throws MissingReferenceException('The object of type TView has been destroyed but you are still trying to access it') from `view.gameObject`, taking down whatever spawn loop called it. `_available` still holds 19 more corpses that will throw one at a time.

**Düzeltme:** Guard and fall through to instantiation: wrap the pop in a loop — `while (_available.Count > 0) { view = _available.Pop(); if (view != null) break; view = null; }` then `if (view == null) { /* instantiate path */ }`. Also mark the ViewPoolFactory roots DontDestroyOnLoad, or have the pool subscribe to sceneUnloaded and purge `_available`.

## `vr-unregister-unity-fake-null-leak`

**ViewRegistry.Unregister early-returns on Unity fake-null and gates the entity map on IsBound, making destroyed views permanently unremovable**

| | |
|---|---|
| Konum | `Runtime/Sync/ViewRegistry.cs:63` |
| Kategori | bug · sync-reactive |
| Etki | Unbounded growth of `_allViews` and `_entityToView`. Each leaked entry costs a HashSet slot plus a full per-frame iteration step in SyncAll/ForceSyncAll — a scene that loads and unloads 300 views ten times leaves 3000 dead entries walked 60 times per second. |
| Test | NOT COVERED. Tests/Runtime/Sync/ViewPoolTests.cs:235-250 verifies Unregister works for a live view. Tests/Benchmarks/ViewPoolBenchmarks.cs:111-116 unregisters live views only, and its cleanup at lines 126-130 destroys the GameObjects AFTER unregistering. No test destroys a view's GameObject and then unregisters it, nor asserts ViewCount returns to 0. |

```csharp
            if (view == null) return;

            if (view.IsBound)
            {
                _entityToView.Remove(GetEntityKey(view.Entity));
            }
```

**Sorun:** Two independent leaks. (1) `view == null` uses UnityEngine.Object's overloaded operator==, which reports true for a managed wrapper whose native GameObject was destroyed. The C# object is still a live key in `_allViews` (a plain HashSet using reference equality), so Unregister returns before reaching `_allViews.Remove(view)` at line 70 and the entry can never be removed. (2) The `_entityToView` removal is gated on `view.IsBound`; EntityView.OnDestroy -> Dispose -> Unbind (EntityView.cs:138-150) sets `_bound = false`, so any Unregister called after the GameObject dies also skips the entity-map cleanup, and the `_entity` field has already been reset to default so the key would be wrong anyway.

**Senaryo:** A scene unload destroys 300 enemy GameObjects without routing through ViewPool.Despawn. Each EntityView's OnDestroy runs Unbind. A later cleanup pass calls `registry.Unregister(view)` for each: `view == null` is true (destroyed), so every call returns immediately. `_allViews` still holds 300 destroyed views and `_entityToView` still holds 300 stale keys. ForceSyncAll now iterates 300 dead entries every LateUpdate forever, and the managed wrappers (plus their binding lists and handler closures) are never collected.

**Düzeltme:** Compare identity, not Unity-null: `if (ReferenceEquals(view, null)) return;` so destroyed views still reach the removal code. Remove the `IsBound` gate by tracking the registered entity key alongside the view (e.g. `Dictionary<EntityView, long> _viewToKey`) so the entity map can be cleaned regardless of bind state. Also have EntityView.Dispose notify its registry so destruction alone is sufficient.

## `readme-test-counts-and-numbers-inconsistent`

**README states three mutually inconsistent test counts, all far below the actual 556 test methods, and publishes performance numbers that contradict its own Benchmarks.md**

| | |
|---|---|
| Konum | `README.md:7` |
| Kategori | test-gap · tests-bench |
| Etki | Documentation only — no runtime cost. But it is the surface a reader uses to judge the framework, and it is self-refuting in four places. |
| Test | No test or script validates the README's counts or numbers against the suite; Documentation~/Benchmarks.md:459-473 documents a manual `grep` of the log as the only extraction step. |

```csharp
[![Tests](https://img.shields.io/badge/tests-324%20passing-brightgreen)]()
```

**Sorun:** Three different totals appear in one document: the badge says 324 (line 7); the directory tree says "Runtime/ # Functional Tests (324)" + "Performance/ # Benchmarks (93)" = 417 (lines 392-393); the Test Coverage section says "330 functional tests" + "94 performance benchmarks" + "All 424 tests passing" (lines 532-534). Actual counts from the tree: 556 `[Test]`-attributed methods (529 plain `[Test]` + 27 `[Test, Performance]`), distributed as Tests/Runtime 538 (of which Tests/Runtime/Performance 85, functional 453), Tests/Stress 4, Tests/Benchmarks 14, Tests/Editor 0; plus 6 `[TestCase]` parameterizations on 2 methods giving ~560 executed cases. Separately, the published numbers contradict each other: README:49 says MessageBus dispatch is 4ns while Benchmarks.md:208/211 say Publish ~20ns and Send Command ~15ns (5x apart); README:212 says Container Build (100 types) 0.05ms while Benchmarks.md:95 says ~2ms (40x apart); README:213 claims 1.56x vs manual new() while README:208 puts the 4-level chain at 0.27μs against Benchmarks.md:72's 59ns manual baseline, which is 4.6x, not 1.56x — and no benchmark in the repo produces 92ns, 59ns or the 1.56x ratio (Benchmark_Comparison_ManualVsDI is the only test that computes the ratio, and it does so for the 4-object chain with `Assert.Less(overhead, 20)`).

**Senaryo:** A prospective adopter cross-checks the README against Documentation~/Benchmarks.md and finds the dispatch figure differs by 5x, the container-build figure by 40x, and the DI overhead ratio internally inconsistent with the README's own DI table. The '324 passing' badge understates the suite by 42% while ~100 of those cases (71 no-op property tests + 29 assertion-free benchmarks) cannot fail. The credibility of the whole performance section collapses on inspection.

**Düzeltme:** Regenerate all counts from the test runner output rather than by hand, and reconcile the two documents to a single set of numbers produced by named tests — annotate each published figure with the test method that produces it (e.g. "6.6ns/entity — ECSPerformanceTests.Benchmark_Query_SingleComponent_100k"). Remove or re-derive the 1.56x, 92ns, 59ns and 4ns figures, none of which any test in the repo emits.

## `benchmarks-with-zero-assertions`

**25 benchmark methods (29 executed cases) contain zero assertions and count toward the advertised passing-test total**

| | |
|---|---|
| Konum | `Tests/Benchmarks/ComputedPropertyBenchmarks.cs:74` |
| Kategori | test-gap · tests-bench |
| Etki | 29 of ~560 executed test cases (5.2%) are non-tests. Combined with the 71 FsCheck no-op property tests, 100 of ~560 cases (17.9%) cannot fail on a real defect. |
| Test | This is the coverage gap itself. |

```csharp
        public void ComputedProperty_ValueAccess_Performance()
```

**Sorun:** Nine files contain benchmark methods with no `Assert.` call anywhere in the file: ComputedPropertyBenchmarks (3 methods, one parameterized 3x), ContainerBenchmarks (2), ECSBenchmarks (3), EventBusBenchmarks (2), ViewPoolBenchmarks (2 methods, one parameterized 3x), PoolingPerformanceTests (3), ReactiveSystemPerformanceTests (4), StateMachinePerformanceTests (3), TimerServicePerformanceTests (3) = 25 methods / 29 executed cases. They can only fail by throwing. They log numbers to the Unity console and report green regardless of what those numbers are.

**Senaryo:** Object pooling regresses from ~10ns to ~10μs per spawn. `Benchmark_10k_PoolSpawnDespawn` (PoolingPerformanceTests.cs:25) records the slower median in the Performance Testing report and PASSES. Nothing in CI fails. Meanwhile these 29 green cases are counted in the "324 passing" / "424 tests passing" figures the README uses as a quality signal.

**Düzeltme:** Either add explicit bounds (`Assert.Less(...)` against a value derived from the published figure) or wire the `[Test, Performance]` benchmarks to Unity.PerformanceTesting baseline comparison so a regression fails the run. At minimum, exclude assertion-free benchmarks from the advertised passing-test count.

## `addcomponent-benchmark-measures-overwrite`

**ECSBenchmarks.AddComponent_Benchmark adds to the SAME entity 100,000 times — it measures the overwrite branch, not the insert branch**

| | |
|---|---|
| Konum | `Tests/Benchmarks/ECSBenchmarks.cs:46` |
| Kategori | bug · tests-bench |
| Etki | 110,000 of 110,000 measured calls after the first take the early-return overwrite path; the insert path is measured exactly once, inside warmup. |
| Test | Benchmark_ComponentAddRemove_100k (ECSPerformanceTests.cs:352) does cover the real insert path over 100k distinct entities — but with no warmup (see missing-warmup-component-benchmarks). |

```csharp
                _world.EntityManager.AddComponent(entity, new TestComponent { Value = 1 });
```

**Sorun:** `entity` is created once at line 42 and captured by the measured closure, which runs `WarmupCount(10) + MeasurementCount(100) * IterationsPerMeasurement(1000)` = 110,000 times against that single entity. `SparseSet<T>.Add` short-circuits on an already-present entity: `if (_sparse[entityIndex] >= 0) { _data[_sparse[entityIndex]] = component; return; }` (Runtime/ECS/Storage/SparseSet.cs:35-39). So iteration 1 performs a real insert (capacity checks, dense append, sparse write, count increment) and iterations 2..110,000 take a two-array-access overwrite. The reported median is the overwrite cost.

**Senaryo:** A regression in the insert path — e.g. `EnsureDenseCapacity` changed to reallocate on every call instead of doubling — costs ~O(n) per genuine AddComponent, and this benchmark's median does not move at all, because it never reaches that code after the first iteration. (Note the *published* 95ns AddComponent figure comes from ECSPerformanceTests.Benchmark_ComponentAddRemove_100k, which does use distinct entities; so this benchmark is mislabeled rather than the source of a false published number.)

**Düzeltme:** Create a fresh entity per iteration, or use `Measure.Method(...).SetUp(() => { entity = _world.EntityManager.CreateEntity(); })` so each measured call hits the insert path. Rename the current test to `AddComponent_Overwrite_Benchmark` if the overwrite cost is genuinely wanted, and add a separate insert benchmark.

## `property-generators-exclude-scoped-and-null-entity`

**Property-test generators never produce Lifetime.Scoped, Entity.Null, or out-of-range entity indices — the highest-risk inputs for the unsafe SparseSet path are excluded by construction**

| | |
|---|---|
| Konum | `Tests/Runtime/Generators/RegistrationGenerator.cs:104` |
| Kategori | test-gap · tests-bench |
| Etki | 1 of 3 DI lifetimes and 100% of the invalid-entity input space are excluded from property-based testing, across all 71 property tests. |
| Test | Tests/Runtime/ECS/Core/EntityPropertyTests.cs (7 properties) and Tests/Runtime/ECS/Storage/ComponentPropertyTests.cs (9 properties) consume EntityArbitrary and inherit the restriction. |

```csharp
            Gen.Elements(Lifetime.Singleton, Lifetime.Transient);
```

**Sorun:** `LifetimeGen` enumerates only Singleton and Transient; `Lifetime.Scoped` is never generated, and `grep -n 'Lifetime.Scoped' Tests/Runtime/DI/ContainerPropertyTests.cs` returns nothing. Every registered `RegistrationConfig`, `SingletonRegistrationGen`, `TransientRegistrationGen` and `UniqueRegistrations` inherits the omission. In parallel, `EntityGenerator.EntityArbitrary` (EntityGenerator.cs:67-68) is `Arb.From(ValidEntity, ShrinkEntity)` where `ValidEntity` is `Gen.Choose(1, 10000)` x `Gen.Choose(1, 100)` — so the FsCheck-registered `Arbitrary<Entity>` never yields index 0, never yields a negative index, never yields an index above storage capacity, and never yields `Entity.Null`. `NullEntity` and `AnyEntity` (lines 25-35) are defined but never registered and never referenced from any test.

**Senaryo:** `SparseSet<T>.Add` indexes `_sparse[entityIndex]` directly after `EnsureSparseCapacity(entityIndex + 1)`, and `SparseSet<T>.Remove` guards only `entityIndex >= _sparse.Length` — a negative index would read out of bounds of a NativeArray in a release build with safety checks off. `EntityManager.GetEntity` explicitly handles `index <= 0`. None of the 71 property tests can ever generate index 0 or a negative index, so the boundary that the unsafe pointer path (`ComponentJobParallel.Execute`: `int idx = entity < MaxSparse1 ? SparseIndex1[entity] : -1;` — note it checks only the upper bound) actually depends on is untested. Similarly, Lifetime.Scoped has a published perf number (21ns, README:211) and a published "0 bytes GC" claim (README:234) with zero property-test coverage.

**Düzeltme:** Add `Lifetime.Scoped` to `LifetimeGen`. Register `AnyEntity` (or a wider `Gen.Choose(int.MinValue, int.MaxValue)` boundary generator) as the `Arbitrary<Entity>` in `StradaArbitraries.StradaArbitraryProvider.Entity()` so Entity.Null, index 0 and out-of-capacity indices are exercised; keep `ValidEntity` for tests that genuinely need a valid entity.

## `container-build-benchmark-one-registration`

**Benchmark_ContainerBuild_100Types/1000Types register the SAME type repeatedly — the container is built with exactly one registration and zero constructor analysis**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/DIPerformanceTests.cs:253` |
| Kategori | bug · tests-bench |
| Etki | Startup-only cost, but the published figure understates real 100-type container build by roughly two orders of magnitude and is self-contradicted by the project's own Benchmarks.md. |
| Test | Benchmark_ContainerBuild_100Types (line 244) and Benchmark_ContainerBuild_1000Types (line 268) are the only build-time benchmarks; both share the defect. No test builds a container with N distinct types. |

```csharp
                builder.RegisterFactory<SimpleService>(_ => new SimpleService());
```

**Sorun:** This line runs inside `for (int i = 0; i < TypeCount; i++)` with `TypeCount = 100` (and 1000 in the sibling test at line 277). `ContainerBuilder.RegisterFactory<T>` writes `_registrations[typeof(T)] = Registration.FromFactory(...)` (Runtime/DI/ContainerBuilder.cs:51) — a Dictionary indexer, so the 100 iterations overwrite one another and `_registrations` ends with exactly ONE entry for `typeof(SimpleService)`. `Build()` then calls `DetectCircularDependencies()`, which skips factory registrations outright (`if (registration.Factory != null || registration.Instance != null) continue;` — ContainerBuilder.cs:96-97), so ZERO constructor reflection, ZERO expression-tree compilation, and ZERO dependency-graph traversal happen. The benchmark measures 100 closure allocations plus 100 dictionary overwrites into a 1-entry dictionary.

**Senaryo:** README.md:212 publishes "Container Build (100 types) 0.05ms | ~0.5μs per registration". A real 100-type container (100 distinct `Register<TInterface,TImpl>` calls) triggers 100 `GetBestConstructor` reflection calls, 100 cycle-detection walks, and 100 `Expression.Lambda<...>.Compile()` calls (Runtime/DI/Container.cs:394). Expression.Compile alone is ~50-200μs per delegate on Mono, so the true 100-type build is likely 5-20 ms, i.e. 100-400x the published 0.05ms. Documentation~/Benchmarks.md:95 independently claims ~2ms for 100 registrations, contradicting the README's 0.05ms by 40x.

**Düzeltme:** Generate 100 distinct closed types (e.g. a `Dep<T0>...Dep<T99>` family or `Register<IFoo_i, Foo_i>`) so the dictionary actually holds 100 entries, and use `Register<T>`/`Register<TIface,TImpl>` rather than `RegisterFactory` so the constructor-analysis and expression-compilation path is exercised. Then re-derive the published number.

## `no-il2cpp-benchmark-expression-compile`

**Every published DI number comes from an Expression.Compile() path that does not exist under IL2CPP; no benchmark or test ever runs under AOT**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/DIPerformanceTests.cs:101` |
| Kategori | aot-il2cpp · tests-bench |
| Etki | Per-resolve on the hottest DI path. Editor-Mono 0.11μs transient / 61ns singleton / 21ns scoped are not representative of a shipped IL2CPP build; the interpreted-expression fallback is typically 10-100x slower per transient resolve. |
| Test | Zero. `grep -rn 'link.xml|Preserve|IL2CPP|AOT' Tests/` finds nothing; all 556 tests are Editor-only (UNITY_INCLUDE_TESTS + UnityEditor.TestRunner reference in both test asmdefs). |

```csharp
                container.Resolve<SimpleService>();
```

**Sorun:** The resolve path this benchmark times is built by `Expression.Lambda<Func<IIndexResolver, object>>(Expression.New(ctor, args), resolverParam).Compile()` (Runtime/DI/Container.cs:394, with a second one at line 384) plus a reflective `genericMethod.Invoke(null, new object[] { this })` at Container.cs:366. Under IL2CPP / full AOT there is no Reflection.Emit, so `Expression.Compile()` returns an *interpreted* delegate (or throws, depending on the linker configuration), and `MakeGenericMethod(...).Invoke(...)` can be stripped. The repo contains no `link.xml` (`find . -name link.xml` → nothing) and no `[Preserve]` attributes. Both test asmdefs (`Tests/Runtime/Strada.Core.Tests.asmdef` and `Tests/Benchmarks/Strada.Core.Tests.Benchmarks.asmdef`) reference `UnityEditor.TestRunner` and gate on `UNITY_INCLUDE_TESTS`, so they only ever compile and run inside the Editor under Mono JIT — which is exactly what Documentation~/Benchmarks.md:43 ("Scripting Backend: Mono") and :452-455 ("Select PlayMode tab") describe.

**Senaryo:** A studio ships an IL2CPP iOS/Android build. Transient resolution goes through an interpreted expression tree (typically 10-100x slower than the JIT-compiled delegate measured in the Editor), so the advertised 0.11μs becomes single-digit-to-tens of microseconds, and the "1.56x vs manual new()" claim inverts. Nothing in the test suite would have caught this because no benchmark — and no functional test — ever executes under IL2CPP.

**Düzeltme:** Add a Player-build benchmark pass: `-buildTarget`/`-testPlatform` with IL2CPP scripting backend in CI, publishing a second column of numbers. Separately, add an AOT-safety test that asserts the container falls back to a source-generated `DirectFactory<T>` (Runtime/DI/IStradaFactory.cs) rather than `Expression.Compile()` when AOT is detected — and label the README table "Editor / Mono JIT" until IL2CPP numbers exist.

## `missing-warmup-component-benchmarks`

**The benchmarks producing the published per-component ECS numbers have no warmup at all — first-call JIT and cold caches are inside the timed window**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ECSPerformanceTests.cs:434` |
| Kategori | test-gap · tests-bench |
| Etki | 9 of 16 ECS benchmarks, 5 of 14 MessageBus benchmarks, all 5 Bridge benchmarks and 4 of 9 MVCS benchmarks. Between them they produce 9 of the 12 ECS numbers published in README.md lines 219-225 and Benchmarks.md lines 154-175. |
| Test | No test verifies that a warmup was performed; the documented methodology (Benchmarks.md:50) is simply wrong for these files. |

```csharp
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++)
            {
                var pos = _entityManager.GetComponent<Position>(entities[i]);
                sum += pos.X;
            }
```

**Sorun:** `Benchmark_GetComponent_100k` starts the Stopwatch as the first executable statement after the setup loop — no warmup pass. The same applies to Benchmark_SetComponent_100k (line 463), Benchmark_HasComponent_100k (line 402), Benchmark_ComponentAddRemove_100k (lines 362 and 369), Benchmark_EntityCreation_WithComponent_100k (line 99), Benchmark_EntityCreation_With3Components_100k (line 121), Benchmark_EntityDestruction_100k (line 302), Benchmark_Query_TwoComponents_100k (line 190) and Benchmark_Query_ThreeComponents_100k (line 226). Only Benchmark_EntityCreation_Simple_100k (line 70) and Benchmark_Query_SingleComponent_100k (line 152) warm up. The same omission exists in MessageBusPerformanceTests.Benchmark_100Subscribers_EventPublish (line 321), Benchmark_MixedOperations (line 173), the three SignalSequence benchmarks (lines 404, 441, 475), all five BridgePerformanceTests benchmarks, and MVCSPerformanceTests lines 107/194/219/291. This directly contradicts Documentation~/Benchmarks.md line 50, "- Warm-up iterations performed before measurement".

**Senaryo:** The first `GetComponent<Position>` call in a fresh test fixture triggers Mono JIT of the closed generic `EntityManager.GetComponent<Position>`, `ComponentStore.GetOrCreateStorage<Position>`, `ComponentStorage<Position>.Get` and `SparseSet<Position>.Get`, plus a `Dictionary<Type, object>` miss and a `ComponentStorage<Position>` allocation. Amortized over 100k iterations the JIT cost is small, but the cold-cache first pass over a 100k-entry sparse array is not, and the number is a single sample (see single-sample-no-median), so it is unbounded noise on the exact figures published as 67ns/76ns/78ns/95ns/149ns/374ns/180ns/18ns/28ns.

**Düzeltme:** Prepend a warmup pass over a subset (e.g. `for (int i = 0; i < 1000; i++) { var w = _entityManager.GetComponent<Position>(entities[i]); }`) before `Stopwatch.StartNew()` in each of the listed tests, matching the pattern already used at line 152.

## `ecs-memory-benchmark-blind-to-native`

**Benchmark_MemoryUsage_100k measures the managed heap, but 100% of ECS entity/component storage is NativeArray(Allocator.Persistent) and therefore invisible to it**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ECSPerformanceTests.cs:497` |
| Kategori | bug · tests-bench |
| Etki | Backs the published per-entity memory figure and the entire Storage Efficiency section of Benchmarks.md with a measurement blind to ~100% of the memory involved (at 100k entities with 2 components: >= 100k*(4+4+12+12) native bytes for the sparse/dense/data arrays alone, plus 100k*5 bytes for versions/active). |
| Test | Benchmark_MemoryUsage_100k (line 480) is the only ECS memory test. No test measures native allocation. |

```csharp
            long memAfter = GC.GetTotalMemory(true);
```

**Sorun:** `GC.GetTotalMemory` reports only the Mono managed heap. Every byte of Strada's ECS storage is native: `SparseSet<T>`'s ctor allocates `_sparse`, `_dense` and `_data` as `new NativeArray<...>(capacity, allocator)` (Runtime/ECS/Storage/SparseSet.cs:22-25) and `ComponentStorage<T>` always passes `Allocator.Persistent` (Runtime/ECS/Storage/ComponentStorage.cs:25); `EntityManager._versions` and `._active` are likewise `new NativeArray<...>(initialCapacity, Allocator.Persistent)` (Runtime/ECS/Core/EntityManager.cs:37-38), and `GrowCapacity` reallocates them natively (lines 312-313). The measured delta therefore captures only the handful of managed `ComponentStorage` wrapper objects and Dictionary entries — a few KB — not the multi-MB native arrays the test claims to size.

**Senaryo:** Double the SparseSet growth factor so every component storage over-allocates 2x native memory (tens of MB at 100k entities). `bytesPerEntity` barely moves, `Assert.Less(bytesPerEntity, 128)` at line 509 passes, and the log continues to print a per-entity figure that has no relationship to actual memory consumption. README.md:232 publishes "Memory per Entity (2 components) 56 bytes" and Benchmarks.md:189-191 builds an "Overhead ~100%" analysis on top of it — all from a measurement that cannot see the memory in question.

**Düzeltme:** Measure native memory instead: sample `Unity.Collections.NativeLeakDetection`-independent counters via `ProfilerRecorder(ProfilerCategory.Memory, "Total Reserved Memory")` / `"GC Reserved Memory"` difference, or expose byte-size accessors on `SparseSet<T>` (`_sparse.Length * 4 + _dense.Length * 4 + _data.Length * sizeof(T)`) and sum them across storages. Keep the GC.GetTotalMemory delta as a separate "managed overhead" figure.

## `mvcs-benchmarks-measure-empty-methods`

**MVCS "InjectionProcessor", "Controller Lifecycle" and "Module Lifecycle" benchmarks measure empty method bodies and zero-dependency injection**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/MVCSPerformanceTests.cs:344` |
| Kategori | test-gap · tests-bench |
| Etki | 3 of 9 MVCS benchmarks measure empty work. The injection benchmark backs no published README number but is presented as evidence of MVCS performance. |
| Test | Functional injection is covered elsewhere (Tests/Runtime/DI), but no performance test exercises a non-empty injection path. |

```csharp
            public void Install(IContainerBuilder builder) { }
            public void Initialize(IContainer container) { }
            public void Shutdown() { }
```

**Sorun:** `Benchmark_ModuleInstaller_Lifecycle` (line 286) times 1,000 iterations of `new BenchmarkModuleInstaller(); installer.Install(builder); installer.Initialize(_container); installer.Shutdown();` — all three methods have empty bodies (quoted above), so it measures 1,000 small allocations plus 3,000 empty calls and calls the result "Module Full Lifecycle". Likewise `BenchmarkService` (line 312) and `BenchmarkController` (line 317) declare only `protected override void OnInitialize() { }` and carry no `[Inject]` members — `grep -rn '\[Inject' Tests/Runtime/Performance/` returns ZERO hits. `InjectionProcessor.Inject` is `GetOrCreateInfo(type)` (a ConcurrentDictionary lookup) followed by `InjectMethods/InjectProperties/InjectFields` over three empty lists (Runtime/DI/InjectionProcessor.cs:15-23). So `Benchmark_InjectionProcessor_10k_Injections` measures a dictionary lookup and three empty foreach loops, not injection.

**Senaryo:** Change `InjectionProcessor` to resolve each `[Inject]` member through a `Dictionary<Type,...>` lookup plus a boxing `object[]` per member — a genuine per-injection regression. All three MVCS benchmarks report unchanged times, because none of them has a single injectable member. `Assert.Less(sw.ElapsedMilliseconds, 100)` at line 58 and `< 500` at line 210 and `< 200` at line 307 all continue to pass.

**Düzeltme:** Give BenchmarkService/BenchmarkController 3-5 `[Inject]` private fields backed by real registrations in the container, and give BenchmarkModuleInstaller a non-trivial `Install` (register N services) and `Initialize` (resolve them). Assert on the injected fields being non-null so the work is rooted.

## `speedup-integer-ms-divide-by-zero`

**Parallel speedup is computed by dividing by Stopwatch.ElapsedMilliseconds (integer) — a sub-1ms parallel run yields +Infinity and the assertion passes**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ParallelJobPerformanceTests.cs:174` |
| Kategori | bug · tests-bench |
| Etki | The single headline claim "Parallel Job Speedup 17x" (README.md:226, Benchmarks.md:337) is computed from a division that can produce Infinity and carries up to 100% quantization error at the stated operating point. |
| Test | Benchmark_ParallelVsSequential_100k (ParallelJobPerformanceTests.cs:138) and Benchmark_100k_ParallelVsSequential_TargetSpeedup (JobSystemPerformanceTests.cs:49) are the only tests producing the 17x figure. Neither guards the denominator. |

```csharp
            float speedup = (float)swSequential.ElapsedMilliseconds / swParallel.ElapsedMilliseconds;
```

**Sorun:** `Stopwatch.ElapsedMilliseconds` is a `long` truncated to whole milliseconds. Because the left operand is cast to `float`, this is float division: when `swParallel.ElapsedMilliseconds == 0` the result is `float.PositiveInfinity` rather than an exception. `Assert.Greater(speedup, 1.0f)` (line 181) and the identical `Assert.Greater(speedup, 1.5f)` in JobSystemPerformanceTests.cs:90 both pass on Infinity. Even when non-zero, whole-millisecond quantization dominates: at the published 17ms-vs-1ms operating point the true ratio lies anywhere in [17/1.999, 17.999/1] = [8.5x, 18.0x]. The identical expression appears at JobSystemPerformanceTests.cs:83.

**Senaryo:** 100k entities, 10 frames of the Burst MoveJob complete in 0.9 ms total. `swParallel.ElapsedMilliseconds == 0`. Line 179 logs "Speedup: ∞x", line 181's `Assert.Greater(speedup, 1.0f)` passes, and the sequential path could have been *faster* in absolute terms without the test noticing. Conversely, a real regression from 17x to 9x is invisible inside the quantization band.

**Düzeltme:** Use `Elapsed.TotalMilliseconds` (double) on both sides and guard the denominator: `double par = swParallel.Elapsed.TotalMilliseconds; Assert.Greater(par, 0.0); double speedup = swSequential.Elapsed.TotalMilliseconds / par;`. Apply the same fix at JobSystemPerformanceTests.cs:83.

## `parallel-vs-sequential-not-apples-to-apples`

**The "17x vs sequential ForEach" comparison confounds parallelism, Burst codegen and managed-delegate overhead, and warms up only the parallel side**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ParallelJobPerformanceTests.cs:150` |
| Kategori | performance · tests-bench |
| Etki | Sequential leg absorbs first-call JIT of one generic method + one delegate across 10 frames of 100k entities; parallel leg does not. Direction of bias is entirely toward inflating the published 17x. |
| Test | No test measures single-threaded Burst as a control. Benchmark_ParallelVsSequential_100k and Benchmark_100k_ParallelVsSequential_TargetSpeedup are the only two, and both share the defect. |

```csharp
            var swSequential = Stopwatch.StartNew();
            for (int frame = 0; frame < Frames; frame++)
            {
                _entityManager.ForEach<Position, Velocity>((int e, ref Position t, ref Velocity v) =>
```

**Sorun:** The sequential leg (lines 150-160) is a managed lambda invoked once per entity through `EntityManager.ForEach<Position,Velocity>`, started with ZERO warmup — the first frame pays JIT of the closed generic `ForEach<Position,Velocity>` plus the delegate, and cold data caches. The parallel leg is explicitly warmed at lines 164-165 (`var warmup = _entityManager.ScheduleParallel<MoveJob, Position, Velocity>(job); warmup.Complete();`) and then runs a `[BurstCompile]` struct job. The published ratio therefore folds together three independent effects: (1) thread parallelism, (2) Burst native codegen + SIMD, (3) elimination of one managed delegate invocation per entity per frame. It is not a measurement of parallel speedup. The same asymmetry exists in JobSystemPerformanceTests.cs (sequential at lines 61-71 with no warmup, parallel warmed at line 74).

**Senaryo:** A reader benchmarking Strada against Unity DOTS reads "17x speedup over sequential" (README.md:44/226) and budgets 1 ms/frame for 100k entities. In reality, running the same MoveJob single-threaded under Burst (the correct control) would already capture most of that factor, and on a 2-core mobile target the parallel component contributes ~1.8x (Benchmarks.md's own thread-scaling table, line 361). The number does not decompose the way the README implies.

**Düzeltme:** Warm both legs symmetrically (run each path once and discard before starting either Stopwatch), and report three numbers instead of one ratio: (a) managed-delegate ForEach, (b) single-threaded Burst `IJobComponent` via `Schedule(count, count)` — batch size == count forces one batch, (c) parallel Burst. The parallelism claim is then (c)/(b); the Burst claim is (b)/(a).

## `burst-generic-job-not-registered`

**The Burst job wrapper is an open generic that is never [RegisterGenericJobType]-registered, so Burst cannot AOT-compile it in a Player build**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ParallelJobPerformanceTests.cs:92` |
| Kategori | aot-il2cpp · tests-bench |
| Etki | Per-entity per-frame in every parallel job. Losing Burst on the 100k-entity MoveJob is the difference between the published ~1ms/frame and the ~17ms/frame managed sequential figure quoted alongside it (Benchmarks.md:337). |
| Test | None. ParallelJobPerformanceTests and JobSystemPerformanceTests both schedule these generic jobs and never verify Burst actually compiled them; Tests/Stress/ParallelCommandBufferTests is the only other job-adjacent test. |

```csharp
                var handle = _entityManager.ScheduleParallel<MoveJob, Position, Velocity>(job);
```

**Sorun:** `ScheduleParallel` funnels into `EntityJobs.Schedule` which constructs `new ComponentJobParallel<TJob, T1, T2> { ... }.Schedule(...)` (Runtime/ECS/Jobs/EntityJobs.cs:55-65). `ComponentJobParallel<TJob,T1,T2>` is declared as `public unsafe struct ComponentJobParallel<TJob, T1, T2> : IJobParallelFor` (Runtime/ECS/Jobs/ParallelComponentJob.cs:37) with `[BurstCompile]` on the type. Burst's AOT compiler cannot discover closed instantiations of generic job types on its own — Unity requires `[assembly: RegisterGenericJobType(typeof(ComponentJobParallel<MoveJob, Position, Velocity>))]` for each concrete combination. `grep -rn 'RegisterGenericJobType' .` over the whole repo returns ZERO matches, and there is no link.xml. In the Editor, Burst's JIT resolves the closed generic at schedule time, which is why the benchmark can report a large speedup there.

**Senaryo:** A Player build with Burst AOT: `ComponentJobParallel<MoveJob, Position, Velocity>` has no AOT-compiled variant, Burst logs a compilation-not-found warning and the job executes as plain managed IL on the worker threads. The per-entity inner loop loses SIMD and native codegen, and the measured Editor speedup does not reproduce. No test detects this because there is no Player-build test pass and no test asserts `BurstCompiler.IsEnabled` or checks that a Burst-compiled variant exists.

**Düzeltme:** Emit `[assembly: RegisterGenericJobType(typeof(ComponentJobParallel<TJob,T1,T2>))]` for each concrete job/component combination (a source generator can do this from the `IJobComponent` implementations), and add a test that fails when `BurstCompiler.Options.EnableBurstCompilation` is true but the scheduled job type has no registered Burst variant.

## `pooling-baseline-does-double-work`

**The non-pooled baseline in PoolingPerformanceTests zeroes 4 KB twice per object, inflating the published pooling speedup**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/PoolingPerformanceTests.cs:50` |
| Kategori | performance · tests-bench |
| Etki | One redundant 4 KB memset per iteration x 10,000 iterations x 10 measurements = ~400 MB of redundant writes charged only to the non-pooled baseline. |
| Test | PoolingPerformanceTests has zero assertions (0 `Assert.` in the file), so neither leg's absolute cost is bounded. |

```csharp
                    var obj = new HeavyPoolable();
                    obj.OnSpawn();
```

**Sorun:** `HeavyPoolable` declares `public int[] Data = new int[1000];` (line 13) — the CLR zero-initializes that 4,000-byte array as part of allocation. `OnSpawn()` (lines 15-19) then writes 1,000 zeros over it again. The "direct allocation" baseline therefore pays allocation + zero-init + a redundant 4 KB memset per object, while the pooled path (lines 33-36) pays only `pool.Spawn()` (one OnSpawn) + `pool.Despawn(obj)`. The comparison is structurally biased toward the pool by one full 4 KB clear per iteration, 10,000 times per measurement.

**Senaryo:** Benchmarks.md:265 publishes "Create 10k objects: 45ms without pool / 3ms with pool = 15x". Roughly a third of the baseline's work is the redundant second memset that the pooled path never performs; a corrected baseline (drop `obj.OnSpawn()`, since `new` already produced a zeroed object) shrinks the numerator and the published multiplier with it. Separately, Benchmarks.md:256 publishes "Spawn (from pool) ~10ns" — impossible for a path whose OnSpawn alone clears 4 KB, so that figure is not produced by this test either.

**Düzeltme:** Make the two legs do identical semantic work: either drop `obj.OnSpawn()` from the direct-allocation leg (the object is already in spawn state), or give HeavyPoolable a non-trivially-zero spawn state so both legs must run it. Add assertions bounding both medians.

## `statemachine-benchmark-zero-transitions`

**StateMachinePerformanceTests registers two states of the same type and every transition is a self-transition that SetState early-returns from — Benchmark_StateTransitions_1k measures zero transitions**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/StateMachinePerformanceTests.cs:83` |
| Kategori | bug · tests-bench |
| Etki | 0 of 2,000 Update calls per measurement perform a state transition; the benchmark named "StateTransitions_1k" measures only predicate evaluation and `OnUpdate`. |
| Test | None of the three tests in StateMachinePerformanceTests asserts anything (0 `Assert.` in the file), so the defect cannot surface as a failure. |

```csharp
            sm.AddTransition<TestState, TestState>(() => toggle);
```

**Sorun:** Two problems compound. (1) `sm.AddState(state1); sm.AddState(state2);` (lines 72-73) both infer `T == TestState`, and `StateMachineCore.AddState<T>` does `States[typeof(T)] = state;` (Runtime/StateMachine/StateMachine.cs:27) — so `state2` silently overwrites `state1` and only one state is ever registered. (2) The transition's target type equals the current state type, and `SetState(Type stateType)` opens with `if (stateType == CurrentStateTypeInternal) return;` (StateMachine.cs:80). So when `toggle` is true, `CheckTransitions` finds the transition, evaluates the condition, calls `SetState(typeof(TestState))`, and returns immediately without running OnExit/OnEnter/OnStateChanged. The benchmark's 2,000 `sm.Update(0.016f)` calls per measurement perform ZERO state changes. The same duplicate-type AddState occurs in Benchmark_StateMachine_TransitionCheck_10k (lines 43-44).

**Senaryo:** Make `SetState` allocate a `List<Transition>` on every transition (a real per-transition GC regression). `Benchmark_StateTransitions_1k` reports the identical median because it never enters the transition body — and it has no assertion (0 `Assert.` in the file) so it cannot fail either way. Documentation~/Benchmarks.md's StateMachine memory line ("~100B per state") and any transition-cost claim are unbacked.

**Düzeltme:** Define a second distinct state type (`TestStateB : StateBase`), `sm.AddState(new TestStateA()); sm.AddState(new TestStateB());`, and alternate `AddTransition<TestStateA, TestStateB>(() => toggle)` / `AddTransition<TestStateB, TestStateA>(() => !toggle)` so real OnExit/OnEnter/OnStateChanged work is measured. Add an assertion on the observed transition count.


---

# LOW (118)

## `eventbus-dispatch-slot-toctou-nre`

**Dispatch paths read the handler slot twice; a concurrent unregister between the null-check and the invoke produces NullReferenceException**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:102` |
| Kategori | concurrency · communication |
| Etki | Per-dispatch correctness hazard on every Send/Query/SendAsync/QueryAsync; window is a few instructions wide, so it only manifests under concurrent register/unregister/Dispose, but it is on the framework's advertised thread-safe path. |
| Test | Tests/Runtime/Communication/EventBusThreadSafetyTests.cs exercises concurrent Subscribe+Publish (line 50) and concurrent Send (line 90) but never concurrently unregisters or disposes while dispatching, so this window is untested. No test in the repo disposes a SubscriptionToken from another thread. |

```csharp
            var handlers = Volatile.Read(ref _signalHandlers);
            if (id < handlers.Length && handlers[id] != null)
            {
                ((Action<TSignal>)handlers[id])(signal);
```

**Sorun:** `handlers` is a snapshot of the array *reference*, but the element `handlers[id]` is loaded twice: once for the null test and once for the cast+invoke. Both the token-disposal closure (line 186, `Volatile.Write(ref arr[id], null)`) and `Clear()` (line 296, `Array.Clear(_signalHandlers, 0, _signalHandlers.Length)`) mutate the *same array object* the dispatcher captured, so the snapshot gives no protection at the element level. The identical double-read pattern appears in `Query` (lines 124-125), `SendAsync` (lines 317-319) and `QueryAsync` (lines 350-351). Prior finding FIND-09-01 (HIGH, unit-09) was marked FIXED in SecurityReports/2026-05-22-status-review.md line 41 on the grounds that the dispatch paths now use `Volatile.Read`; the volatile read of the array was added but the TOCTOU on the slot was not fixed.

**Senaryo:** Thread A calls `bus.Send(new JumpSignal())`. It evaluates `handlers[id] != null` -> true. Thread B (a controller tearing down, or `bus.Dispose()` from a scene unload) executes the token closure at line 186 or `Clear()` at line 296, writing null into that same slot. Thread A re-loads `handlers[id]` -> null, the `castclass` to `Action<TSignal>` succeeds on null, and the invoke throws `NullReferenceException` out of `Send` instead of the documented `InvalidOperationException` ("No signal handler registered for ..."). In `Query` the same window yields an NRE from `((IQueryHandler<TQuery,TResult>)null).Handle(ref query)`.

**Düzeltme:** Load the slot exactly once into a strongly-typed local and test that: `var h = handlers[id] as Action<TSignal>; if (h != null) { h(signal); return; }`. Apply the same single-load shape to Query (125), SendAsync (319) and QueryAsync (351). This also removes the redundant second `castclass`/`isinst` from each dispatch.

## `eventchannel-cow-quadratic-subscribe-unsubscribe-alloc`

**Copy-on-write channel reallocates the whole handler array on every Subscribe and every Unsubscribe — O(N^2) allocation, contradicting the documented "Unsubscribe: 0 bytes"**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:464` |
| Kategori | allocation · communication |
| Etki | Per Subscribe: 24 + 8*(N+1) bytes. Per Unsubscribe: 24 + 8*(N-1) bytes plus up to N Delegate.Equals calls. Not per-frame for static wiring; per-frame for spawn/despawn-driven subscription churn. |
| Test | Tests/Benchmarks/EventBusSubscribeBenchmarks.cs:71 asserts only `Assert.AreEqual(0, _bus.GetSubscriberCount<TestEvent>())` — it walks straight into the O(N^2) path and measures wall time but makes no allocation assertion. No test in the repo uses Unity's GC.Alloc / `Measure.Method().GC()` on the subscribe path. |

```csharp
                    var oldHandlers = _handlers;
                    var newHandlers = new Action<T>[oldHandlers.Length + 1];
                    Array.Copy(oldHandlers, newHandlers, oldHandlers.Length);
```

**Sorun:** Every `Subscribe` allocates a fresh `Action<T>[N+1]` and copies N references; every `Unsubscribe` (line 479, `var newHandlers = new Action<T>[oldHandlers.Length - 1];`) allocates a fresh `Action<T>[N-1]` and does an O(N) `Array.IndexOf(oldHandlers, handler)` (line 476) whose comparisons go through `EqualityComparer<Action<T>>.Default` -> `Delegate.Equals`. Building or tearing down a channel of N subscribers is therefore O(N^2) in both bytes allocated and comparisons. Documentation~/Benchmarks.md lines 219-220 state `| Subscribe | **~64 bytes** (one-time) |` and `| Unsubscribe | **0 bytes** |`; both are wrong — Unsubscribe is never zero-alloc and Subscribe is O(N) bytes, not a constant. (The COW read side is genuinely allocation-free, which is the correct half of the tradeoff — the cost was moved wholesale onto the mutation side.)

**Senaryo:** Tests/Benchmarks/EventBusSubscribeBenchmarks.cs:26-72 subscribes 1000 handlers then disposes all 1000 tokens in reverse. Subscribe allocates sum_{i=1..1000}(24 + 8i) = ~4.03 MB; the reverse unsubscribe allocates another ~3.83 MB and performs ~500,500 `Delegate.Equals` calls (reverse order hits the worst case of IndexOf every time) — ~7.9 MB of garbage and half a million delegate comparisons for one stress run. In gameplay: a pooled EntityView that subscribes on Spawn and unsubscribes on Despawn against a 200-subscriber channel costs (24 + 8*200) * 2 = ~3.2 KB per spawn/despawn pair; 20 spawn/despawn per frame at 60 fps = ~65 KB/frame = ~3.9 MB/s of Gen0 garbage.

**Düzeltme:** Replace the exact-fit COW array with `Action<T>[] _handlers` + `int _count` and amortized doubling: Subscribe appends in place (only reallocating on growth), Unsubscribe swaps the tail into the hole and decrements `_count`. To keep the lock-free reader correct, have `Publish` snapshot `(_handlers, _count)` atomically (store both in a small immutable holder object, or copy-on-write only while a publish is in flight, tracked by a version counter). At minimum, correct Documentation~/Benchmarks.md lines 219-220.

## `eventchannel-exception-tostring-alloc-per-publish`

**A permanently-throwing subscriber is never evicted and allocates a full exception ToString() on every publish**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:454` |
| Kategori | allocation · communication |
| Etki | 500-2000+ bytes per throwing handler per publish, i.e. per-frame for a per-frame event. Zero cost when no handler throws (the try/catch itself is free on the non-throwing path). |
| Test | No test in Tests/Runtime/Communication/ publishes to a throwing subscriber at all — exception isolation itself is untested despite being an advertised feature (Documentation~/Messaging.md line 302: "EventBus isolates handler exceptions"). |

```csharp
                    catch (Exception ex)
                    {
                        Debug.LogError($"Exception in event handler: {ex}");
                    }
```

**Sorun:** The exception-isolation try/catch (added for prior finding FIND-09-02, correctly) swallows the exception and interpolates `{ex}` — which invokes `Exception.ToString()`, materializing the full message + stack trace + inner exceptions as a string, then a second string for the interpolation. The failing handler stays in the channel forever; there is no eviction, no rate limit, and no dedupe. Because the exception never propagates, the caller has no signal that a subscriber is permanently broken.

**Senaryo:** The canonical Unity leak: a `MonoBehaviour` subscribes via `Subscribe<TickEvent>` and is destroyed without disposing its token. Every publish now invokes the dead handler, which throws `MissingReferenceException`; each throw allocates `ex.ToString()` (typically 500-2000 bytes with an IL2CPP stack trace) plus the interpolated wrapper. For a per-frame event at 60 fps that is roughly 60-240 KB/s of Gen0 garbage plus 60 `Debug.LogError` calls per second (each of which itself allocates and, in the editor, writes to the console log file), for the entire remaining session.

**Düzeltme:** Log without the full `ToString()` — `Debug.LogError($"[EventBus] Handler for {typeof(T).Name} threw: {ex.GetType().Name}: {ex.Message}")` and pass `ex` as the second argument to `Debug.LogException` only on the first occurrence. Add a per-handler consecutive-failure counter and either evict the handler or stop logging after N (e.g. 3) consecutive throws, logging once that it was suppressed/evicted.

## `eventbus-disposed-field-not-volatile`

**EventBus._disposed is a plain bool read from 13 unsynchronized sites, unlike every sibling class in the framework**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:93` |
| Kategori | concurrency · communication |
| Etki | Free (a volatile bool read is a plain load on x64 and an ldar on ARM64; the field is already read once per dispatch). |
| Test | Tests/Runtime/Communication/MessageBusTests.cs:242-250 covers only single-threaded repeated Dispose. EventBusThreadSafetyTests never disposes concurrently with dispatch. |

```csharp
        private bool _disposed;
```

**Sorun:** `_disposed` is written at line 307 (`_disposed = true;`) with no lock and no fence, and read at lines 98, 120, 140, 165, 180, 199, 216, 228, 253, 273, 282, 313, 333, 346, 366 — almost all outside `_lock`. The JIT is free to hoist the read out of a dispatch loop. Every other lifecycle-managed class in this framework uses `private volatile bool _disposed;` (Runtime/DI/Container.cs:44, Runtime/DI/ContainerScope.cs:17), and Documentation~/DI.md:230 advertises exactly that pattern. Prior finding unit-19 Finding 12 recommended "Mark _disposed as volatile. Consider checking _disposed in Send/Publish"; SecurityReports/2026-05-22-low-status-review.md line 136 lists it as FIXED — only the second half (the Send/Publish checks) was implemented, the volatile was not.

**Senaryo:** A worker thread runs `for (...) bus.Publish(evt);`. The JIT hoists the non-volatile `_disposed` load out of the loop. The main thread calls `bus.Dispose()` during a scene unload, which sets `_disposed = true` and clears the channel arrays. The worker keeps dispatching against the array reference it captured at line 143 (already-clear entries are null so nothing is invoked, but no `ObjectDisposedException` is ever raised) and continues believing the bus is live — the fail-fast the guard was added for never fires.

**Düzeltme:** `private volatile bool _disposed;` — matching Container.cs:44. Also make Dispose idempotent under contention with `if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;` since the current `if (_disposed) return; _disposed = true;` (lines 306-307) lets two threads both enter Clear().

## `eventchannel-published-with-plain-write`

**Newly constructed EventChannel is published with a plain array store — the only non-Volatile.Write slot store in the class**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:266` |
| Kategori | concurrency · communication |
| Etki | One store-release instead of a plain store, on the first Subscribe per event type only — effectively free. |
| Test | Tests/Runtime/Communication/EventBusThreadSafetyTests.cs:50-87 runs concurrent Subscribe+Publish but pre-subscribes 5 handlers first (line 56-59), so the channel already exists and the first-publication window is never hit; and CI runs on x86-64 where the reordering cannot be observed. |

```csharp
                    _eventChannels[id] = channel;
```

**Sorun:** Every other handler-slot store in EventBus uses `Volatile.Write`: lines 175, 186, 223, 234, 339, 372. Line 266 is a plain store, and it publishes a *freshly constructed* object whose field initializers (`_handlers = Array.Empty<Action<T>>()` at 437 and `_lock = new object()` at 438) are ordered only by the writer's own program order. Readers (`Publish` at 143-147, `GetSubscriberCount` at 285-288, and the unsubscribe closure at 274-276) load the slot without taking `_lock`. Under the .NET memory model the release at Monitor.Exit orders those writes before the unlock, but it does not order the constructor stores before the *reference publication* as observed by a reader that never acquires `_lock`. On ARM64 — Unity's Android/iOS/Switch targets — that reordering is architecturally permitted.

**Senaryo:** Thread A calls `bus.Subscribe<Tick>(h)` for the first Tick subscriber. Thread B concurrently calls `bus.Publish(new Tick())` on the same bus (the framework's own EventBusThreadSafetyTests.cs:50 does exactly this shape). On ARM64, B's `channels[id]` load can return the non-null channel reference while B's subsequent `Volatile.Read(ref _handlers)` (line 445) still observes the pre-constructor default null, so `handlers.Length` at line 446 throws NullReferenceException inside the try-less part of Publish, propagating out to the caller.

**Düzeltme:** `Volatile.Write(ref _eventChannels[id], channel);` — a one-token change that makes line 266 consistent with the other five slot stores and inserts the release barrier between the constructor stores and the publication.

## `eventchannel-publish-aggressiveinlining-is-a-noop`

**[AggressiveInlining] on EventChannel.Publish is dead because the method contains an exception handler**

| | |
|---|---|
| Konum | `Runtime/Communication/EventBus.cs:442` |
| Kategori | performance · communication |
| Etki | One non-inlined call plus its prologue/epilogue per Publish — roughly 1-3 ns on desktop RyuJIT, more on IL2CPP, against a claimed 4-20 ns budget. |
| Test | Tests/Benchmarks/EventBusBenchmarks.cs:24-37 and Tests/Runtime/Performance/MessageBusPerformanceTests.cs:92-121 measure wall time only, with a loose `Assert.Less(sw.ElapsedMilliseconds, 20)` for 100k publishes (= a 200ns/publish ceiling), so a 10x regression against the documented 20ns would still pass. |

```csharp
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Publish(ref T message)
```

**Sorun:** RyuJIT unconditionally rejects any inline candidate whose body contains an EH clause (inline observation `CALLEE_HAS_EH`); `AggressiveInlining` raises the size/complexity budget but cannot override that rejection. The body at lines 448-456 wraps each handler invocation in `try { handlers[i](message); } catch (Exception ex) { ... }`, so `EventChannel<T>.Publish` is never inlined into `EventBus.Publish` (line 147, `channel?.Publish(ref message);`), which is itself marked AggressiveInlining at line 137. IL2CPP likewise emits a C++ try/catch frame and will not inline it. The attribute is therefore misleading about the achieved cost, which matters given README.md:49 ("array-indexed dispatch (4ns/dispatch)") and Documentation~/Messaging.md:296 ("Publish (1 subscriber) | ~20ns").

**Senaryo:** Not a wrong-answer bug — a cost the code claims to have removed and has not. A publish to one subscriber costs: static-field load for the type id, Volatile.Read + bounds check, `isinst EventChannel<TEvent>` (line 146), a real (non-inlined) call into Publish, a second Volatile.Read, then the delegate invoke inside the EH region — versus the single indexed delegate call the attribute implies.

**Düzeltme:** Hoist the try/catch out of the hot method: keep `Publish` EH-free and fast-path the common case, e.g. iterate without try/catch and, on the first throw, restart the remaining handlers through a `[MethodImpl(NoInlining)] PublishSlow(handlers, i, ref message)` helper that owns the EH region. If exception isolation must stay inline, drop the `AggressiveInlining` attribute so the code does not misrepresent its own cost.

## `signalsequence-entry-boxing-per-then`

**readonly struct sequence entries are boxed on every Then() because they are stored in List<ISequenceEntry>**

| | |
|---|---|
| Konum | `Runtime/Communication/SignalSequence.cs:43` |
| Kategori | allocation · communication |
| Etki | ~32 bytes per `Then<TSignal>()`/`ThenIf<TSignal>()` call at sequence-build time (larger for larger signal structs). Zero additional allocation at Execute time. |
| Test | Tests/Runtime/Performance/MessageBusPerformanceTests.cs:348-353 builds the 5-signal sequence once *outside* the timed loop and then executes it 10,000 times, so the benchmark structurally cannot observe the per-build boxing. Tests/Runtime/Communication/SignalSequenceTests.cs makes no allocation assertions. |

```csharp
            _entries.Add(new SignalEntry<TSignal>(signal, null));
```

**Sorun:** `SignalEntry<TSignal>` is declared `private readonly struct SignalEntry<TSignal> : ISequenceEntry` (line 185) and `ConditionalSignalEntry<TSignal>` likewise (line 217) — clearly written as structs to avoid allocation — but `_entries` is `List<ISequenceEntry>` (line 15), so each `Add` performs a `box` of the struct into a heap object. The same boxing happens at line 52 (`Then(signal, targetBus)`), line 100 (`ThenIf(bool, signal)`) and line 110 (`ThenIf(Func<bool>, signal)`). The `readonly struct` gives zero allocation benefit here; it only makes the boxed copy immutable.

**Senaryo:** `new SignalSequence(bus).Then(new CastStart{..}).Then(new PlayVfx{..}).Then(new ApplyDamage{..}).Then(new CastEnd{..})` allocates 4 boxes. For a 4-byte signal struct plus the `IEventBus` field each box is 32 bytes on 64-bit, so 128 bytes per sequence build, plus the `List<ISequenceEntry>` backing array (line 21 pre-sizes to 8 = 88 bytes) and the SignalSequence object itself. A game that rebuilds an ability chain per cast at 10 casts/second sustains ~2.5 KB/s from boxing alone; a per-frame rebuild of a 5-entry sequence at 60 fps is ~9.6 KB/s.

**Düzeltme:** Either (a) accept that entries are heap objects, make them `sealed class`, and pool them on `Clear()`/`Dispose()` so rebuilds are allocation-free; or (b) keep the signals in per-T typed storage (a `List<TSignal>` created lazily per closed generic) with a parallel `List<byte>` opcode/index stream so `Execute` dispatches without a boxed interface call. Option (a) is the smaller change and removes the misleading `readonly struct` declarations.

## `signalsequence-include-indirect-cycle-stackoverflow`

**Include() guards only direct self-reference; an A->B->A cycle causes an uncatchable StackOverflowException**

| | |
|---|---|
| Konum | `Runtime/Communication/SignalSequence.cs:62` |
| Kategori | bug · communication |
| Etki | Process termination on the first Execute of a cyclic graph; no allocation or per-frame cost otherwise. |
| Test | Tests/Runtime/Communication/SignalSequenceTests.cs:174-181 `Include_SelfReference_Ignored` pins only the direct `sequence.Include(sequence)` case. No test constructs a two-node cycle. |

```csharp
            if (other != null && other != this)
            {
                _entries.Add(new SequenceEntry(other));
            }
```

**Sorun:** The cycle guard is a single reference comparison against `this`. Any indirect cycle is accepted, and `Execute` recurses through `SequenceEntry.Execute` (line 264-267, `_sequence?.Execute(defaultBus);`) with no depth limit and no visited set. There is no recursion counter anywhere in the class. This is prior finding FIND-09-07 (unit-09, LOW), still listed as open in SecurityReports/2026-05-22-low-status-review.md line 72 ("SignalSequence.Include recursion depth tracking yok") — confirmed unfixed in the current code.

**Senaryo:** `var a = new SignalSequence(bus); var b = new SignalSequence(bus); a.Include(b); b.Include(a); a.Execute();` recurses `a.Execute -> SequenceEntry.Execute -> b.Execute -> SequenceEntry.Execute -> a.Execute -> ...` until the thread stack is exhausted. `StackOverflowException` cannot be caught in .NET/Mono and terminates the Unity process (and the editor) with no crash handler opportunity. If sequences are assembled from designer-authored ScriptableObject data or downloaded config, this is a remote-data-triggered process kill.

**Düzeltme:** Track depth explicitly: add a `[ThreadStatic] private static int _depth;` incremented/decremented around the body of `Execute`/`ExecuteAsync` and throw `InvalidOperationException("SignalSequence recursion limit exceeded — cyclic Include?")` past a bound (e.g. 32). Alternatively walk the include graph in `Include()` and reject `other` if it transitively reaches `this`.

## `signalsequence-silently-drops-signals-without-bus`

**SignalEntry silently discards the signal when no bus is available, unlike EventBus.Send which throws**

| | |
|---|---|
| Konum | `Runtime/Communication/SignalSequence.cs:198` |
| Kategori | api-hazard · communication |
| Etki | Correctness/diagnosability only; no runtime cost. |
| Test | Tests/Runtime/Communication/SignalSequenceTests.cs:61-71 `Then_WithAction_ExecutesAction` uses `new SignalSequence()` with no bus but adds only an `Action` entry, so the signal-drop path is never exercised. No test asserts what happens to a signal entry with a null bus. |

```csharp
                var bus = _targetBus ?? defaultBus;
                if (bus != null)
                {
                    var signal = _signal;
                    bus.Send(ref signal);
                }
```

**Sorun:** `_defaultBus` is settable to null (the parameterless constructor at line 19 leaves it null, `WithBus(null)` at line 32 sets it null, and `Execute(null)` at line 125 falls back to it), and both `SignalEntry.Execute` (199) and `ConditionalSignalEntry.Execute` (236) simply skip the send when the resolved bus is null. `Execute()` returns void with no exception, no log and no return code. This is the opposite of the bus's own convention: `EventBus.Send` throws `InvalidOperationException` when nothing is registered (line 108). The async paths (211, 249) have the same silent-drop shape, returning `default(ValueTask)`.

**Senaryo:** A sequence is built in a factory before the bus is resolved from DI: `var seq = new SignalSequence().Then(new StartWave{Index=3});` and the `WithBus` call is lost in a refactor or the DI resolve returns null. `seq.Execute()` returns cleanly, `seq.Count` is 1, no log line is produced, and the wave never starts. Diagnosing this requires reading SignalSequence source.

**Düzeltme:** Throw `InvalidOperationException($"SignalSequence entry for {typeof(TSignal).Name} has no target bus; pass one to the constructor, WithBus(), Then(signal, bus) or Execute(bus).")` when `bus == null`, or at minimum `Debug.LogWarning` once per Execute. Also null-guard `WithBus` and the `SignalSequence(IEventBus)` constructor.

## `signalsequence-mutation-during-execute-throws`

**Execute iterates _entries with foreach; any entry that calls Dispose/Clear/Then on the same sequence throws InvalidOperationException**

| | |
|---|---|
| Konum | `Runtime/Communication/SignalSequence.cs:129` |
| Kategori | bug · communication |
| Etki | Correctness only; the index loop is also marginally cheaper than the List<T> struct enumerator. |
| Test | Tests/Runtime/Communication/SignalSequenceTests.cs:246-258 `Dispose_PreventsExecution` disposes strictly *before* Execute. No test mutates the sequence from inside an entry. |

```csharp
            foreach (var entry in _entries)
            {
                entry.Execute(defaultBus ?? _defaultBus);
            }
```

**Sorun:** `List<T>`'s enumerator version-checks on every MoveNext. `Dispose()` (line 174, `_entries.Clear();`), `Clear()` (line 162, `_entries.Clear();`), and every `Then`/`ThenIf`/`Include`/`ThenAsync` builder call mutate `_entries` and bump its version. All of these are public and none is guarded against being called while `Execute`/`ExecuteAsync` is walking the list — `_disposed` is checked once on entry (line 127) and never re-checked. `ExecuteAsync` (line 150) has the same `foreach` and additionally holds the enumerator across `await` points, widening the window to anything that runs on the main thread between continuations.

**Senaryo:** A self-terminating chain: `seq.Then(new Fire()).Then(() => { if (ammo == 0) seq.Dispose(); }).Then(new Reload()); seq.Execute();` — the second entry clears `_entries`, and the next `MoveNext()` throws `InvalidOperationException: Collection was modified; enumeration operation may not execute` out of `Execute`, not out of `Dispose`. The same happens for the natural "queue a follow-up" pattern `seq.Then(() => seq.Then(new NextStep()))`, and for `ExecuteAsync` when a UI teardown disposes the sequence while an entry is awaiting.

**Düzeltme:** Snapshot the count and index the list: `var entries = _entries; for (int i = 0; i < entries.Count; i++) { if (_disposed) return; entries[i].Execute(bus); }` (re-checking `_disposed` per iteration also makes mid-execution Dispose stop the chain cleanly instead of throwing). In `Dispose`, set `_disposed = true` before `_entries.Clear()` — it already does — and consider deferring the Clear when an execution is in flight.

## `sequence-registry-null-name-inconsistent`

**SignalSequenceRegistry silently ignores a null/empty name on Register but throws ArgumentNullException on Get/Contains/Execute**

| | |
|---|---|
| Konum | `Runtime/Communication/SignalSequence.cs:352` |
| Kategori | api-hazard · communication |
| Etki | Correctness/diagnosability only; no runtime cost. |
| Test | Tests/Runtime/Communication/SignalSequenceTests.cs:346-420 exercise the registry only with valid literal names ("test", "spawn", "seq1", "async_test"). No null/empty-name test exists. |

```csharp
            if (string.IsNullOrEmpty(name)) return;
            _sequences[name] = sequence;
```

**Sorun:** `Register` swallows a null-or-empty name and returns without registering. But `Get` (line 372, `_sequences.TryGetValue(name, ...)`), `Contains` (line 380, `_sequences.ContainsKey(name)`), `Execute` (line 388) and `ExecuteAsync` (line 399) pass the name straight to `Dictionary<string, SignalSequence>`, which throws `ArgumentNullException` for a null key. So the write path is silently forgiving and the read path is hard-failing for the same input. `Create(name, builder)` (line 359-365) compounds this: it constructs and returns a fully built sequence, calls the no-op `Register`, and returns the sequence as if registration succeeded.

**Senaryo:** `registry.Create(config.SequenceName, b => b.Then(new StartWave()))` where `config.SequenceName` is null (unset in a ScriptableObject) — returns a live sequence, no warning, `Contains` is never consulted. Later `registry.Execute(config.SequenceName)` throws `ArgumentNullException: Value cannot be null. (Parameter 'key')` from Dictionary.TryGetValue, several frames away from the actual mistake. With an empty string instead of null, `Register` silently drops it and `Execute("")` silently no-ops — a third distinct behaviour.

**Düzeltme:** Pick one policy and apply it to all five methods. Recommended: `if (string.IsNullOrEmpty(name)) throw new ArgumentException("Sequence name must be non-empty.", nameof(name));` in `Register` and `Create`, and an explicit `if (string.IsNullOrEmpty(name)) return false/null/default;` guard in `Contains`/`Get`/`Execute`/`ExecuteAsync` so the read path never surfaces a raw Dictionary exception.

## `subscriptiontoken-contract-untested`

**SubscriptionToken's documented idempotency/stale-token guarantees have no test anywhere in the repo**

| | |
|---|---|
| Konum | `Runtime/Core/SubscriptionToken.cs:30` |
| Kategori | test-gap · communication |
| Etki | No runtime cost; this is the regression net missing under the entire v2.0 token API, which is the headline change of commits e3d8292/8c75172/556a3e9. |
| Test | Only Tests/Benchmarks/EventBusSubscribeBenchmarks.cs:31,41,45,52,60 touch SubscriptionToken, and it is a [Category("Benchmark")] timing test asserting only GetSubscriberCount == 0. |

```csharp
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
```

**Sorun:** The implementation is correct (Interlocked.Exchange makes disposal idempotent and race-free), and EventBus documents three further guarantees on top of it: "Disposal is idempotent" (SubscriptionToken.cs:14-16), "if a later RegisterSignalHandler call has already replaced the slot with a different handler, the token does not clear it" (EventBus.cs:158-161 and 209-211, implemented via `ReferenceEquals` at lines 185 and 233), and token disposal after `bus.Dispose()` being a no-op (the `if (_disposed) return;` at lines 180, 228, 273). `grep -rln "SubscriptionToken" Tests/` returns exactly one file — Tests/Benchmarks/EventBusSubscribeBenchmarks.cs — which only disposes tokens in a loop and asserts a final count. None of the three documented guarantees is pinned by a test, and neither is unsubscribe-during-dispatch or re-entrant publish.

**Senaryo:** Someone "optimizes" the signal-token closure at EventBus.cs:185 from `ReferenceEquals(arr[id], handler)` to a bare `arr[id] != null`. Every test in the repo still passes, but disposing a stale token now silently unregisters whoever replaced the handler: `var t1 = bus.RegisterSignalHandler<Jump>(h1); var t2 = bus.RegisterSignalHandler<Jump>(h2); t1.Dispose();` would leave Jump unhandled, and the next `bus.Send(new Jump())` throws InvalidOperationException in production. Likewise, removing the `if (_disposed) return;` guards would only surface as an NRE at runtime.

**Düzeltme:** Add Tests/Runtime/Core/SubscriptionTokenTests.cs covering: (a) double Dispose invokes the underlying action exactly once and IsActive flips to false; (b) stale-token safety — dispose t1 after registering h2 and assert h2 still receives the signal; (c) token.Dispose() after bus.Dispose() does not throw; (d) disposing a token from inside a handler during Publish does not affect the in-flight dispatch but does affect the next one (pinning the COW snapshot semantics); (e) a handler that publishes the same event type during dispatch terminates and does not corrupt iteration.

## `asyncscope-disposed-not-volatile`

**AsyncContainerScope._disposed is a plain non-volatile bool; Dispose/DisposeAsync race and _initLock is disposed out from under in-flight ResolveAsync calls**

| | |
|---|---|
| Konum | `Runtime/DI/AsyncContainerScope.cs:112` |
| Kategori | concurrency · di-core |
| Etki | Per concurrent dispose/resolve pair; spurious exceptions and a double SemaphoreSlim.Dispose. |
| Test | NOT COVERED. AsyncContainerTests.cs:156-164 disposes a quiescent scope on a single thread. No concurrent dispose test exists for AsyncContainerScope. |

```csharp
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _innerScope.Dispose();
            _initLock.Dispose();
        }
```

**Sorun:** `private bool _disposed;` (line 15) is neither volatile nor guarded — unlike ContainerScope.cs:17 (`private volatile bool _disposed;`) and Container.cs:44, which were both hardened for DI-02/DI-03. The check-then-set at lines 114-115 is not atomic, so two threads (or Dispose() racing DisposeAsync(), lines 120-131) can both enter and call _initLock.Dispose() twice. Worse, an in-flight ResolveAsync holding _initLock (line 66/98) will have `_initLock.Release()` in its finally block throw ObjectDisposedException, and a pending WaitAsync throws as well — the exception escapes from the finally and masks whatever the caller was doing.

**Senaryo:** Task A: await scope.ResolveAsync<AssetService>();   // inside AssetService.InitializeAsync, holding _initLock
Task B (scene unload): scope.Dispose();             // _initLock.Dispose() while A holds it
Task A's finally: _initLock.Release() -> ObjectDisposedException thrown from a finally block, replacing the real result and leaving the resolve half-completed.

**Düzeltme:** Mark _disposed volatile and use Interlocked.Exchange(ref _disposedFlag, 1) to make disposal single-entry; do not dispose _initLock until outstanding waiters have drained (or drop the SemaphoreSlim in favour of a lock-free initialized-set as suggested in asyncscope-reinitializes).

## `buildasync-leaks-scope`

**AsyncScopeBuilder.BuildAsync leaks the inner scope (and any instances it already created) when PreWarm throws or is cancelled**

| | |
|---|---|
| Konum | `Runtime/DI/AsyncScopeBuilder.cs:45` |
| Kategori | bug · di-core |
| Etki | One leaked ContainerScope plus every scoped IDisposable created before the failure, per failed/cancelled BuildAsync. |
| Test | PINNED BUT NOT ASSERTED. AsyncContainerTests.cs:141-153 (AsyncScope_Cancellation_ThrowsOperationCanceled) drives exactly this path — it pre-cancels the token and asserts only that TaskCanceledException is thrown. It never checks that the scope was disposed, so the leak is exercised on every test run and passes. |

```csharp
            var innerScope = _container.CreateScope();

            foreach (var type in _preWarmTypes)
            {
                cancellation.ThrowIfCancellationRequested();
                var instance = innerScope.Resolve(type);

                if (instance is IAsyncInitializable asyncInit)
                    await asyncInit.InitializeAsync(cancellation).ConfigureAwait(false);
            }
```

**Sorun:** innerScope is created before the loop and there is no try/catch or try/finally. Three throw sites exist inside the loop (ThrowIfCancellationRequested at line 49, Resolve at line 50, InitializeAsync at line 53) and one after it (the async-factory array construction at lines 59-76 can throw from TypeRegistry.GetId). On any of them the exception propagates with innerScope never disposed, so every scoped IDisposable already constructed by earlier PreWarm iterations is leaked.

**Senaryo:** var cts = new CancellationTokenSource(100);
await container.CreateAsyncScopeBuilder().PreWarm<DbSession>().PreWarm<AssetLoader>().BuildAsync(cts.Token);
DbSession is resolved and initialized; the token fires during AssetLoader.InitializeAsync; TaskCanceledException propagates. innerScope — holding the live, undisposed DbSession — is unreachable and never disposed. Repeated on every timed-out level load, this accumulates open DB sessions/handles.

**Düzeltme:** Wrap the body from line 47 to line 78 in try/catch, and on any exception `innerScope.Dispose();` before rethrowing.

## `expression-compile-no-aot-fallback`

**Expression.Compile() with no IL2CPP/AOT guard, fallback, or benchmark — the "expression tree compiled factories / 1.56x manual new()" claim does not hold on AOT targets**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:394` |
| Kategori | aot-il2cpp · di-core |
| Etki | Per-resolve on every AOT platform for every registration without a source-generated factory. Also affects Build(): one Compile() per registered type at startup. |
| Test | NOT COVERED. Tests/Runtime/DI/ContainerPerformanceTests.cs runs only in the editor (Mono). There is no IL2CPP/AOT smoke test and no test asserting Build() succeeds under managed stripping. |

```csharp
            return Expression.Lambda<Func<IIndexResolver, object>>(Expression.New(ctor, args), resolverParam).Compile();
```

**Sorun:** CompileFactory is the fallback path for every type registration that has no DirectFactory (Container.cs:299), i.e. the default path for anything not touched by the source generator. On IL2CPP (iOS, Android IL2CPP, consoles, WebGL) RuntimeFeature.IsDynamicCodeCompiled is false and LambdaExpression.Compile() silently degrades to the System.Linq.Expressions interpreter — the returned delegate is a LightLambda, not JIT'd IL. There is no `#if ENABLE_IL2CPP` branch, no `preferInterpretation` handling, no warning, and no reflection-based fallback anywhere in Runtime/ (grep for ENABLE_IL2CPP / UNITY_IOS / AOT over Runtime/ returns zero hits). There is also no link.xml anywhere in the package and zero [Preserve] attributes in Runtime/. README.md:200 states the numbers were "measured on Apple Silicon (Unity 6, Mono)"; README.md:32 and Documentation~/DI.md:3,362 present "expression tree compiled factories" and "1.56x manual new()" as unqualified product claims, and Documentation~/DI.md:377-378 claims "0 bytes GC allocation" for singleton/scoped resolve. None of those were measured on the target where the compilation strategy changes.

**Senaryo:** Ship an IL2CPP iOS build. builder.Register<IPlayerService, PlayerService>() -> CompileFactory -> Compile() returns an interpreted lambda. Every transient/first-time resolve now walks the interpreter instead of executing a compiled `new PlayerService(...)`, and interpreter invocation allocates per call (argument frames), so the documented "0 bytes GC" for the resolve path is not reproducible on the shipped configuration. Nothing in the code or docs tells the developer this happened.

**Düzeltme:** Add an explicit AOT path: under `#if ENABLE_IL2CPP || UNITY_IOS || UNITY_WEBGL` build the factory from a cached ConstructorInfo + preallocated object[] (or require the source generator and hard-fail at Build() when no DirectFactory exists), and mark CompileFactory's use of expression trees as Mono-only. Add an IL2CPP row to the Documentation~/Benchmarks.md and README tables, or scope the existing rows to "Mono editor/standalone".

## `container-does-not-dispose-scopes`

**CreateScope() hands out untracked scopes; disposing the container never disposes outstanding scopes' IDisposable instances**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:148` |
| Kategori | bug · di-core |
| Etki | Every scoped IDisposable in every outstanding scope, per container teardown. |
| Test | NOT COVERED. ContainerScopeTests.cs:196-207 disposes the scope explicitly. ContainerDisposalTests.cs covers singletons only. No test disposes a container while a scope holds a scoped IDisposable. |

```csharp
        public IContainerScope CreateScope()
        {
            if (_disposed) ThrowDisposed();
            return new ContainerScope(this, _factories, _scopedFactories, _lifetimes, _typeIdToIndex, _maxTypeId, _singletons);
        }
```

**Sorun:** The container keeps no reference to the scopes it creates, and Container.Dispose() (lines 178-213) only drains its own _disposalStack and nulls _singletons. Every scoped IDisposable held by a still-live ContainerScope._scopedInstances is therefore leaked when the owning container is torn down. This compounds with resolvebyindex-no-disposed-check: not only are those scopes not disposed, they remain fully functional against the dead container.

**Senaryo:** A gameplay system holds `IContainerScope _sessionScope = container.CreateScope();` containing an IDisposable NetworkSession. On scene unload the app disposes the container (the documented teardown, README.md:28 `using var container = builder.Build();`). NetworkSession.Dispose() is never called — the socket stays open — and nothing in the API surface hints that the scope needed separate disposal.

**Düzeltme:** Track scopes in a synchronized collection of WeakReference<ContainerScope> populated by CreateScope, and dispose all live entries at the start of Container.Dispose() (before draining _disposalStack, so scoped instances are released before the singletons they depend on).

## `tracked-transients-survive-scope`

**[TrackTransientDisposal] transients resolved through a scope are pushed onto the CONTAINER's disposal stack, so scope disposal never releases them**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:340` |
| Kategori | allocation · di-core |
| Etki | 8 bytes of stack slot plus the full retained object graph per resolve, unbounded for the container's lifetime. At 60 resolves/sec: ~3,600 retained instances per minute. |
| Test | NOT COVERED. There is no test for TrackTransientDisposalAttribute at all in Tests/Runtime/DI/ — neither the container path nor the scope path. |

```csharp
                        _factories[index] = _ =>
                        {
                            var instance = rawFactory(this);
                            if (instance is IDisposable d)
                            {
                                lock (_lock)
                                {
                                    if (_disposed) { d.Dispose(); ThrowDisposed(); }
                                    _disposalStack.Push(d);
                                }
                            }
                            return instance;
                        };
```

**Sorun:** ContainerScope routes transients straight to this lambda: `return _factories[index](this);` (ContainerScope.cs:102). The closure captures the Container's _lock and _disposalStack, so the instance is retained for the whole container lifetime regardless of which scope created it. ContainerScope.Dispose() (lines 158-163) only walks _scopedInstances and never touches these. The attribute's own doc (Attributes/TrackTransientDisposalAttribute.cs) warns about container-lifetime retention but says nothing about the scope interaction, and the container-per-scope leak is exactly the unbounded disposal-stack growth shape this design is supposed to bound.

**Senaryo:** A [TrackTransientDisposal] Transient (e.g. a pooled command buffer wrapper) is resolved once per request from a short-lived per-request scope, 60 times per second. Each resolve pushes 8 bytes onto Container._disposalStack and pins the instance. After 10 minutes: 36,000 retained instances plus a Stack<IDisposable> backing array grown to 65,536 slots (512KB) — none released by the per-request scope.Dispose() calls that the developer correctly wrote. Memory grows monotonically until the container is disposed.

**Düzeltme:** When a tracked transient is resolved through an IIndexResolver that is a ContainerScope, register it on that scope's disposal list instead of the container's. The lambda already receives the resolver as its parameter — it currently discards it (`_ =>`) and captures `this` instead; use the parameter.

## `dead-methodinfo-field`

**Cached ResolveByIndexMethod field is never used; BuildDependencyExpr re-runs the same reflection lookup for every constructor parameter**

| | |
|---|---|
| Konum | `Runtime/DI/Container.cs:420` |
| Kategori | performance · di-core |
| Etki | Startup only: one Type.GetMethod per constructor parameter across all registrations (~600 lookups for a 200-registration/3-param container). |
| Test | N/A — not observable from tests; ContainerPerformanceTests.cs:138 would have surfaced it if the build benchmark registered more than one type (see build-benchmark-registers-one-type). |

```csharp
        private static readonly MethodInfo ResolveByIndexMethod =
            typeof(IIndexResolver).GetMethod(nameof(IIndexResolver.ResolveByIndex));
```

**Sorun:** ResolveByIndexMethod is assigned once and referenced nowhere (grep over Container.cs returns exactly one hit, this declaration). Ten lines below, BuildDependencyExpr repeats the identical lookup inline for every dependency: `Expression.Call(resolverParam, typeof(IIndexResolver).GetMethod(nameof(IIndexResolver.ResolveByIndex)), Expression.Constant(index))` (line 430). The cache exists but was never wired up, so the reflection it was meant to eliminate still runs N times per registration.

**Senaryo:** Not a runtime failure — a build-time cost and dead state. For a container with 200 registrations averaging 3 constructor parameters, Container.Build() performs 600 redundant Type.GetMethod string lookups (roughly 0.5-2us each on Mono) that the existing static field was written to avoid, plus it initializes a MethodInfo that is immediately garbage from the GC's perspective (rooted forever by the static).

**Düzeltme:** Replace the inline `typeof(IIndexResolver).GetMethod(nameof(IIndexResolver.ResolveByIndex))` at line 430 with the ResolveByIndexMethod field.

## `generated-registry-partial-registration`

**A throwing generated RegisterAll leaves the builder partially populated, then the runtime scanner is layered on top and the log says "not found"**

| | |
|---|---|
| Konum | `Runtime/DI/ContainerBuilderExtensions.cs:62` |
| Kategori | bug · di-core |
| Etki | Startup only; produces an unspecified registration set. |
| Test | NOT COVERED. There is no test for ContainerBuilderExtensions.TryUseSourceGenerated (no generated registry exists in the test assembly, so only the null-registryType path is ever taken). |

```csharp
                var registerMethod = registryType.GetMethod("RegisterAll");
                if (registerMethod == null)
                    return false;

                registerMethod.Invoke(null, new object[] { builder });
                return true;
```

**Sorun:** registerMethod.Invoke mutates the caller's builder in place. If RegisterAll throws part-way through (a bad user registration, a TargetParameterCountException from a signature mismatch, an AmbiguousMatchException from GetMethod when RegisterAll is overloaded), the exception is caught by the generic handler at line 79, which logs "Strada generated registry not found, skipping auto-registration" — factually wrong, it was found and it failed — and returns false. RegisterAutoBindings (lines 24-28) then runs RuntimeAutoBindingScanner.RegisterAll on the SAME, partially-populated builder. There is no rollback and no way for the caller to distinguish "no generator" from "generator failed halfway".

**Senaryo:** The generated RegisterAll registers services alphabetically and the 40th throws (e.g. an [AutoRegister] type whose As= target is not assignable). Services 1-39 stay registered from the generated pass; the scanner then re-registers everything it can find using its own include/exclude filters, silently overwriting some of those 39 with different lifetimes/implementations. The app boots with a hybrid registration set nobody authored, and the only signal is a LogWarning saying the registry was "not found".

**Düzeltme:** Distinguish the failure modes: catch around Invoke separately and rethrow (or surface a distinct error) rather than falling back; and make the message accurate — reflect TargetInvocationException.InnerException. If a fallback is genuinely wanted, register into a throwaway builder first and merge only on success.

## `spurious-startup-warning`

**A LogWarning is emitted on the success path of source-generated registration, on every app start, in shipping builds**

| | |
|---|---|
| Konum | `Runtime/DI/ContainerBuilderExtensions.cs:44` |
| Kategori | api-hazard · di-core |
| Etki | Once per app start (per domain load); log noise plus one string allocation. |
| Test | NOT COVERED. No test asserts on the logging behaviour of RegisterAutoBindings. |

```csharp
                if (!s_loggedTypeResolutionWarning)
                {
                    Debug.LogWarning("ContainerBuilderExtensions: Using runtime type resolution from string to locate StradaGeneratedRegistry.");
                    s_loggedTypeResolutionWarning = true;
                }
```

**Sorun:** This fires before the Type.GetType attempt, so it is logged whether or not the registry is found — i.e. on the intended, fully-working source-generated path that Documentation~/DI.md:142,199 recommends. It is a Debug.LogWarning (yellow in the console, captured by Unity's log handler and by crash reporters) describing an implementation detail the caller cannot act on, and GetAutoBindingCount emits the same text again at line 92. The `s_loggedTypeResolutionWarning` guard is a plain non-volatile static bool read/written without synchronization, so concurrent callers can emit it more than once.

**Senaryo:** Every shipped build logs a framework warning at startup for correct usage. Teams that treat warnings as actionable (or that fail CI/QA on unexpected LogWarning) must either suppress it or ignore all Strada warnings. In the editor it also fires inside NUnit fixtures, where an unexpected LogWarning can fail tests under LogAssert strictness.

**Düzeltme:** Drop the warning, or gate it behind `#if UNITY_EDITOR` and demote it to Debug.Log; if the intent is to flag the reflection-based lookup, emit it only when registryType == null (the actual fallback case).

## `scope-resolvebyindex-public`

**ContainerScope.ResolveByIndex is public with no disposed check and no bounds check, exposing internal registration indices as a public API**

| | |
|---|---|
| Konum | `Runtime/DI/ContainerScope.cs:70` |
| Kategori | api-hazard · di-core |
| Etki | Per call; API-surface and use-after-dispose correctness. |
| Test | NOT COVERED. No test calls ResolveByIndex on a ContainerScope. |

```csharp
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object ResolveByIndex(int index)
        {
            var lifetime = _lifetimes[index];
```

**Sorun:** IIndexResolver is internal (IIndexResolver.cs:3) and Container implements ResolveByIndex as `internal` plus an explicit interface implementation (Container.cs:216, 222). ContainerScope instead exposes it publicly on a public sealed class. The method (a) never checks _disposed, unlike every other resolve entry point on the type (lines 57, 107, 127), and (b) never bounds-checks `index` against _lifetimes.Length, so out-of-range values fault with a raw IndexOutOfRangeException rather than a domain error. It also leaks the container's internal registration ordering into the public surface, freezing it as a compatibility contract for a v2.0 package.

**Senaryo:** scope.Dispose();
((ContainerScope)scope).ResolveByIndex(3); // no ObjectDisposedException — resolves a transient on a disposed scope, or hands back a Volatile.Read of a slot that Dispose just nulled
((ContainerScope)scope).ResolveByIndex(-1); // IndexOutOfRangeException from _lifetimes[-1]

**Düzeltme:** Change to `object IIndexResolver.ResolveByIndex(int index)` (explicit interface implementation, matching Container.cs:222) plus a private implementation, and add the _disposed check at the top.

## `typeregistry-aot-value-type`

**TypeRegistry.GetId(Type) builds a closed generic via MakeGenericType — throws ExecutionEngineException on IL2CPP for value-type arguments reachable from public APIs**

| | |
|---|---|
| Konum | `Runtime/DI/TypeRegistry.cs:17` |
| Kategori | aot-il2cpp · di-core |
| Etki | Startup or first-call on AOT platforms; hard crash. Also a startup-only boxing allocation (`GetValue(null)` boxes the int) on each new type. |
| Test | NOT COVERED. ContainerTests.cs:339-345 tests IsRegistered(null) only. No test passes a value type to any Type-taking DI API, and there is no AOT test target. |

```csharp
        public static int GetId(Type type)
        {
            return _typeCache.GetOrAdd(type, static t =>
                (int)typeof(TypeId<>)
                    .MakeGenericType(t)
                    .GetField("Id")
                    .GetValue(null));
        }
```

**Sorun:** TypeId<T> (line 34) has no `class` constraint, so MakeGenericType accepts value types. IL2CPP shares generic code only for reference-type arguments; a value-type instantiation such as TypeId<int> that was never statically referenced does not exist in the AOT binary and MakeGenericType throws ExecutionEngineException. This is reachable from the public API surface — IContainer.Resolve(Type) (IContainer.cs:8 -> Container.cs:227), IContainer.IsRegistered(Type) (IContainer.cs:11 -> Container.cs:174), ContainerScope.Resolve(Type)/IsRegistered(Type) (ContainerScope.cs:50,142), AsyncScopeBuilder.PreWarm(Type) (AsyncScopeBuilder.cs:37-41), and AsyncContainerScope.ResolveAsync(Type) (AsyncContainerScope.cs:85) — none of which constrain or validate the Type argument. There is no [Preserve]/link.xml to force the instantiations.

**Senaryo:** IL2CPP iOS build. Diagnostic/inspector code (or a generic helper) calls `container.IsRegistered(typeof(int))` or `container.IsRegistered(someValueType)` expecting `false`. Instead the app throws ExecutionEngineException("Attempting to call method ... for which no ahead of time (AOT) code was generated"). Works fine in the Mono editor, crashes on device.

**Düzeltme:** Guard GetId(Type) with `if (type.IsValueType) return int.MaxValue;` (an id that can never be <= _maxTypeId, so IsRegistered returns false and Resolve throws the domain exception), or add a `where T : class` constraint on TypeId<T> and validate at the entry points.

## `typeregistry-limit-poisons-type`

**TypeRegistry's 8192-type limit throws from inside a static constructor, producing TypeInitializationException and permanently poisoning the registry**

| | |
|---|---|
| Konum | `Runtime/DI/TypeRegistry.cs:26` |
| Kategori | bug · di-core |
| Etki | Startup/registration-time; hard failure with the wrong exception type and no recovery. |
| Test | NOT COVERED. No test exercises the type-count limit or asserts the exception type. |

```csharp
        internal static int AllocateId()
        {
            int id = Interlocked.Increment(ref _nextId);
            if (id > MaxTypeCount)
                throw new InvalidOperationException("Maximum number of registered types (8192) exceeded");
            return id;
        }
```

**Sorun:** AllocateId is called only from the static constructor of TypeId<T> (line 38-42: `static TypeId() { Id = AllocateId(); ... }`). The CLR wraps any exception escaping a type initializer in TypeInitializationException, so callers never see the intended InvalidOperationException and the documented message is buried in InnerException. Additionally, `Interlocked.Increment(ref _nextId)` runs BEFORE the bound check, so the counter keeps advancing past 8192 on every failed attempt — there is no reset path and no way to recover; every subsequent new type also fails. Documentation~/DI.md:383 describes this as "a configurable MaxTypeCount limit (default 8,192)", but MaxTypeCount is `private const int MaxTypeCount = 8192;` (line 10) — not configurable at all.

**Senaryo:** A project (or a long editor session with domain reload disabled, where _nextId is never reset) touches 8193 distinct types through the DI APIs. The 8193rd `builder.Register<TFoo>()` -> TypeId<TFoo> static ctor -> AllocateId throws -> caller receives TypeInitializationException, not the descriptive InvalidOperationException. Any catch handler written against InvalidOperationException does not match. Every later type allocation fails identically and permanently.

**Düzeltme:** Check the bound without consuming an id: use a CAS loop (`do { cur = Volatile.Read(ref _nextId); if (cur >= MaxTypeCount) throw ...; } while (Interlocked.CompareExchange(ref _nextId, cur + 1, cur) != cur);`). Perform the limit check in TypeRegistry.GetId<T>/GetId(Type) — outside the type initializer — so the intended exception type reaches the caller. Either make MaxTypeCount actually configurable or correct Documentation~/DI.md:383.

## `missing-meta-files-for-two-attribute-sources`

**AutoBindingScopeAttribute.cs and TrackTransientDisposalAttribute.cs ship with no .meta file, unlike every other source file in the package**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/AutoBindingScopeAttribute.cs:19` |
| Kategori | bug · di-injection |
| Etki | Build/packaging only; zero runtime cost. Affects every consumer importing the package from git or a registry. |
| Test | Not covered — there is no packaging/meta-file validation step in the repo. |

```csharp
    public sealed class AutoBindingScopeAttribute : Attribute
    {
    }
```

**Sorun:** `git ls-files Runtime/DI/` shows `Runtime/DI/AutoBinding/AutoBindingScopeAttribute.cs` and `Runtime/DI/Attributes/TrackTransientDisposalAttribute.cs` tracked WITHOUT a companion `.cs.meta`, while all 20 other tracked `.cs` files in `Runtime/DI/` (including `RuntimeAutoBindingScanner.cs.meta` in the same folder and `ServiceAttribute.cs.meta` in the same folder) have one. `package.json` declares this as a UPM package (`com.strada.core`, `"type": "library"`). When a UPM package is resolved from a registry or git URL, Unity stages it under `Library/PackageCache/` and treats it as IMMUTABLE — it cannot persist newly generated `.meta` files there, so these two files receive a freshly generated, non-deterministic GUID on every import. Both files were clearly added late in the two most recent security commits (`e3d8292`/`556a3e9`, which introduced exactly these two attributes) with `git add` on the `.cs` only.

**Senaryo:** A consumer adds `"com.strada.core": "https://github.com/.../Strada.Core.git"` to their manifest. On import Unity logs asset-import warnings for the two meta-less scripts, and their GUIDs differ between machines and between library rebuilds. Because `AutoBindingScopeAttribute` is the type users are told to reference via `[assembly: Strada.Core.DI.AutoBinding.AutoBindingScope]` (RuntimeAutoBindingScanner.cs:144), and `TrackTransientDisposalAttribute` is looked up by `Container.cs:336`, any import hiccup on those two files takes out the two features the last two security commits added. If instead the package is embedded under `Packages/`, Unity generates both `.meta` files on first open, dirtying the consumer's working tree with untracked files on every fresh clone.

**Düzeltme:** Generate both `.meta` files (open the project once in Unity, or copy the format of `ServiceAttribute.cs.meta`, which is 59 bytes: fileFormatVersion + a fixed guid + MonoImporter stub) and commit them alongside the sources. Add a CI check asserting that every tracked `.cs` under `Runtime/` and `Editor/` has a sibling `.cs.meta`.

## `autobinding-scope-attribute-not-enforced`

**Auto-registration from third-party assemblies is opt-OUT: [AutoBindingScope] only logs, and Game.* / Assembly-CSharp are still default includes**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:19` |
| Kategori | security · di-injection |
| Etki | Startup-only in CPU terms; the security exposure lasts the whole session. Attack surface is every assembly named `Game.*`, `Strada.*`, or `Assembly-CSharp` in the player build. |
| Test | Not covered. `AutoBindingTests.cs` always passes explicit patterns (`new[] { "Strada.*" }`); `ContainerBuilderExtensions_RegisterAutoBindings_DoesNotThrow` (line 56-61) is the only default-pattern test and it asserts nothing but the absence of a throw. There is no test that an assembly lacking `[AutoBindingScope]` is (or is not) scanned — `2026-05-22-medium-fix-plans.md:341` specified exactly that test and it was never written. |

```csharp
        private static readonly string[] DefaultIncludePatterns = { "Strada.*", "Game.*", "Assembly-CSharp" };
```

**Sorun:** `ContainerBuilderExtensions.RegisterAutoBindings()` (the zero-argument overload the docs recommend, ContainerBuilderExtensions.cs:13-16) passes null patterns, so line 49 substitutes these defaults. Any loaded assembly whose simple name starts with `Game.` or equals `Assembly-CSharp` gets `GetTypes()`d and every `[AutoRegister*]`/`[Service]` type in it registered into the application container. The mitigation introduced for this (unit-03 Finding 2 / plan F6) is `AutoBindingScopeAttribute`, but it has ZERO enforcement: `WarnIfMissingScopeAttribute` (124-145) checks `assembly.IsDefined(typeof(AutoBindingScopeAttribute), inherit: false)` on line 132 and, when absent, merely calls `Debug.LogWarning` and returns — control falls straight through to `assembly.GetTypes()` on line 113 and every type is registered anyway. The warning is also suppressed after the first occurrence per assembly (line 137), so in a long session it is one line in a console nobody reads. Registration is unconditional and silent-override (see `autobinding-silent-override-nondeterministic`), so a matching assembly can replace an already-registered `IWeapon`, `ISaveService`, `ILogger`, `IAnalytics` etc. Net effect: the plan's Option A ("remove Game.* from defaults, force opt-in") was documented in `SecurityReports/2026-05-22-medium-fix-plans.md:311-320` but Option B shipped, and `2026-05-22-medium-status-review.md:36` still lists this as an open MEDIUM.

**Senaryo:** A Unity project imports an Asset Store package or a UGC mod DLL whose assembly definition is named `Game.Analytics`. It contains `[AutoRegisterSingleton(As = typeof(ISaveService), Priority = 1000)] public class Exfiltrator : ISaveService`. The developer calls `builder.RegisterAutoBindings()`. Line 69 enumerates the assembly, line 75 matches `Game.*`, line 111 logs one deprecation warning and returns, line 113 enumerates its types, line 149 finds the attribute, `RegisterAll` sorts by Priority ascending (line 37) so Priority=1000 is registered LAST, and `ContainerBuilder.cs:22` overwrites the legitimate `ISaveService` binding. Every `Resolve<ISaveService>()` in the game now returns the attacker's type. No exception, no error, one suppressible warning.

**Düzeltme:** Make `[assembly: AutoBindingScope]` a hard gate now rather than "in a future major release": in `ScanAssembly`, `if (!assembly.IsDefined(typeof(AutoBindingScopeAttribute), false)) { log; return; }`. Simultaneously reduce `DefaultIncludePatterns` to `{ "Strada.*" }` (or empty) so pattern breadth stops being load-bearing, and require the caller to pass patterns explicitly. Patterns on an untrusted assembly's self-declared simple name can never be a trust boundary — see `strada-prefix-implicit-trust`.

## `autobinding-silent-override-nondeterministic`

**Auto-binding silently overwrites existing registrations, and equal-Priority collisions resolve non-deterministically (unstable sort + unspecified enumeration order)**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:37` |
| Kategori | security · di-injection |
| Etki | Startup-only in cost; the mis-binding persists for the whole session and differs between machines/builds. |
| Test | Not covered. `AutoBindingPropertyTests.AutoBindingDiscovery_Priority_ServicesRegisteredInOrder` (line 219-245) only re-sorts the entries with LINQ `OrderBy` and asserts the result is sorted — a tautology that tests LINQ, not `RegisterAll`. No test registers two implementations of one interface. |

```csharp
            sorted.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            foreach (var entry in sorted)
            {
                RegisterEntry(builder, entry);
            }
```

**Sorun:** Three unspecified orderings stack up and the result is a silent binding override. `List<T>.Sort` is documented as an UNSTABLE sort (introsort), so entries with equal `Priority` come out in arbitrary relative order. Their input order was already arbitrary: `AppDomain.CurrentDomain.GetAssemblies()` (line 69) returns assemblies in load order, and `Assembly.GetTypes()` (line 113) has no documented ordering. `RegisterEntry` then calls `IContainerBuilder.Register<,>`, which is `_registrations[typeof(TInterface)] = ...` (ContainerBuilder.cs:22) — last write wins. There is no duplicate detection and no warning anywhere: two types claiming the same `ServiceType` produce no diagnostic at all. Note `[Service]` entries are all hard-coded `Priority = 0` (line 183), so they can never break a tie. Combined with `autobinding-scope-attribute-not-enforced` this is the actual exploitation mechanism: a scanned third-party type with a higher `Priority` deterministically wins, and with an equal `Priority` wins about half the time.

**Senaryo:** Two assemblies both ship `[AutoRegisterSingleton(As = typeof(IAnalytics))]` — the legitimate `Game.Core.Analytics` and an imported `Game.Vendor.VendorAnalytics`, both at the default `Priority = 0`. On developer machine A, `GetAssemblies()` returns Core before Vendor and the unstable sort preserves that, so Vendor is registered last and wins. On CI machine B the load order differs and Core wins. Nothing is logged in either case; the difference surfaces as an unreproducible "analytics events missing in the release build" bug. Give the vendor entry `Priority = 1` and it wins deterministically on every machine, replacing a shipped service with no console output whatsoever.

**Düzeltme:** In `RegisterAll`, track seen `ServiceType`s in a `Dictionary<Type, AutoBindingEntry>`; when a second entry claims the same `ServiceType`, log an error naming both `ImplementationType.FullName`s and their priorities, and throw on an exact Priority tie (a tie is always a project bug, never a legitimate override). Make the sort deterministic by adding a stable tiebreak: `sorted.Sort((a, b) => { var c = a.Priority.CompareTo(b.Priority); return c != 0 ? c : string.CompareOrdinal(a.ImplementationType.FullName, b.ImplementationType.FullName); });`

## `scanner-cache-nonvolatile-and-scan-outside-lock`

**Scan cache field is non-volatile and read lock-free, and the scan itself runs outside the lock so concurrent callers duplicate work and clobber each other**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:52` |
| Kategori | concurrency · di-injection |
| Etki | Duplicated 3-50 ms scan per racing caller at startup; publication tear risk is ARM64-only and manifests as an NRE inside ScanAssemblies. |
| Test | Not covered. `AutoBindingTests.RuntimeScanner_CachesResults` (line 29-40) asserts `AreSame` single-threaded only; no test calls `ScanAssemblies` from two threads. |

```csharp
            var cached = _cache;
            if (MatchesCachedPatterns(cached, includePatterns, excludePatterns))
            {
                return cached.Entries;
            }

            lock (_lock)
            {
                cached = _cache;
                if (MatchesCachedPatterns(cached, includePatterns, excludePatterns))
                {
                    return cached.Entries;
                }
            }
```

**Sorun:** Two defects in one pattern. (1) `_cache` is declared `private static CacheSnapshot _cache;` (line 22) with no `volatile` and no `Volatile.Read`, yet line 52 reads it entirely outside any lock. The publishing write is `_cache = new CacheSnapshot(...)` at line 103. The CLR memory model gives no store-store ordering guarantee for ordinary field writes on ARM64 (Unity's dominant IL2CPP target), and C# `readonly` fields carry no Java-style final-field freeze semantics, so a reader on another core can observe a non-null `_cache` reference before the `CacheSnapshot`'s `IncludePatterns`/`ExcludePatterns`/`Entries` writes — or before the `List<T>`'s `_items`/`_size` writes — are visible. `MatchesCachedPatterns` then dereferences a partially-published object. Every other synchronised site in this codebase gets this right (`EventBus.cs:104,126,146` and `Container.cs:306` all use `Volatile.Read`). (2) The double-checked lock is structurally pointless: the expensive work — assembly enumeration and `GetTypes()` on lines 67-99 — happens BETWEEN the two lock blocks, not inside either. Two threads that both miss the cache both perform the full scan, and both then overwrite `_cache` at line 103 with different `List` instances, so callers hold references to different lists and `RuntimeScanner_CachesResults`'s `AreSame` contract silently breaks. `GetCachedCount()` (line 294) reads `_cache` unsynchronised too.

**Senaryo:** `GameBootstrapper` kicks off container construction on the main thread while a background asset-load continuation also calls `RegisterAutoBindingsRuntime` with the same patterns. Both threads reach line 52, both see a stale/null `_cache`, both pass the locked re-check at line 61, and both run the full 3-50 ms assembly scan (per `Documentation~/Benchmarks.md:120`) — doubling the documented startup cost and doubling every `Debug.LogWarning` from `WarnIfMissingScopeAttribute`. Both then write `_cache`; whichever loses the race has already returned a list that is no longer the cached one, so a later `GetCachedCount()` disagrees with what the caller registered. On ARM64 the same interleaving can surface the publication tear: thread B reads a non-null `_cache` whose `Entries` field still reads null and NREs inside `PatternListsEqual`/`cached.Entries`.

**Düzeltme:** Mark the field `private static volatile CacheSnapshot _cache;` (or use `Volatile.Read`/`Volatile.Write` at lines 52, 103 and 294), and move the entire scan (lines 67-99) inside the `lock (_lock)` block so the double-checked lock actually covers the work it is guarding. The scan is startup-only, so holding the lock across it costs nothing.

## `pattern-midstring-wildcard-silently-degrades`

**MatchesPattern silently degrades any mid-string wildcard to exact equality, so exclude patterns like "Unity.*.Tests" exclude nothing**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:241` |
| Kategori | api-hazard · di-injection |
| Etki | Startup-only, but unbounded: a mis-specified exclude with a wide include takes the scan from ~3-50 ms to seconds and auto-registers types from unintended assemblies. |
| Test | Actively misleading. `AutoBindingTests.RuntimeScanner_PatternMatching_MatchesCorrectly` (line 174-193) does NOT call the production `MatchesPattern`; it calls a private CASE-SENSITIVE copy re-implemented in the test file at lines 184-193, so the production matcher's actual semantics (OrdinalIgnoreCase on all branches, interior-wildcard fallthrough) are untested, and the test would keep passing if the production method were deleted. |

```csharp
        private static bool MatchesPattern(string name, string pattern)
        {
            if (pattern.StartsWith("*") && pattern.EndsWith("*"))
                return name.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
            if (pattern.StartsWith("*"))
                return name.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
            if (pattern.EndsWith("*"))
                return name.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
```

**Sorun:** Only three wildcard shapes are supported — leading `*`, trailing `*`, both. A `*` anywhere else falls through every branch to the exact-equality comparison on the last line, where the literal asterisk can never match a real assembly name, so the pattern matches nothing and no diagnostic is emitted. Failing open is fine for an INCLUDE pattern (nothing gets scanned) but fails DANGEROUSLY for an EXCLUDE pattern: the intended exclusion silently does not happen and the assembly is scanned and auto-registered. The same asymmetry applies to the ineffective default excludes: `"Unity.*"` on line 20 becomes `StartsWith("Unity.")`, which does NOT match `UnityEngine`, `UnityEngine.CoreModule` or `UnityEditor` (no dot after "Unity"), and `"System.*"` does not match the assembly literally named `System`; `netstandard` is not listed at all. With the shipped default INCLUDE list those gaps are masked, but `Documentation~/DI.md:148` tells users to pass exactly `excludePatterns: new[] { "Unity.*", "System.*" }` as their exclusion set.

**Senaryo:** A team broadens the include list to `new[] { "*" }` (which `MatchesPattern` evaluates as `name.Contains("")` == true for every assembly) and relies on `excludePatterns: new[] { "Unity.*", "System.*", "UnityEngine.*Module" }` to keep engine assemblies out. `"UnityEngine.*Module"` has an interior `*`, hits the final `Equals` branch, and matches nothing; `"Unity.*"` never matched `UnityEngine.CoreModule` in the first place. The scanner now calls `GetTypes()` on every UnityEngine module plus `netstandard` — tens of thousands of types, each getting three `GetCustomAttribute` calls in `TryCreateEntry` — turning the documented 3-50 ms startup scan (`Documentation~/Benchmarks.md:118-120`) into seconds, with no error explaining why.

**Düzeltme:** Validate patterns at entry to `ScanAssemblies`: if a pattern contains a `*` that is neither the first nor the last character, `Debug.LogError` and reject it rather than silently treating it as a literal. Better, replace the hand-rolled matcher with a `Regex` translated from the glob so interior wildcards work as users expect. Independently, extend `DefaultExcludePatterns` to `"Unity*"`, `"System*"`, `"netstandard"`, `"Mono*"` so the exclusion list means what it appears to mean, and fix the example at `Documentation~/DI.md:148`.

## `loaderexceptions-discarded`

**ReflectionTypeLoadException.LoaderExceptions is discarded, leaving assembly load failures undiagnosable**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:85` |
| Kategori | bug · di-injection |
| Etki | Startup-only; diagnosability, not performance. |
| Test | Not covered — no test drives an assembly into a partial-load state, so lines 83-98 never execute in CI. |

```csharp
                    UnityEngine.Debug.LogWarning($"Partial type load from assembly {assembly.GetName().Name}: {ex.Message}");
```

**Sorun:** `ReflectionTypeLoadException.Message` is the fixed, information-free string "Unable to load one or more of the requested types." — all the actionable detail lives in the `LoaderExceptions` array (one entry per failed type, typically `FileNotFoundException` naming the missing dependency assembly). That array is never read. Silent type-load failures are precisely how a service goes missing from the container and is only discovered as an `InvalidOperationException: Type 'IFoo' is not registered` thrown from `Container.ResolveByType` (Container.cs:244) at some unrelated later point with nothing connecting the two. This was reported as unit-03 Finding 4 (MEDIUM) with the explicit recommendation to "Log the exception details (including LoaderExceptions)"; the `LogWarning` call was added but the `LoaderExceptions` half of the recommendation was not.

**Senaryo:** `Game.Combat` references an Asset Store DLL that is excluded from the mobile build. `GetTypes()` throws; the console shows only `Partial type load from assembly Game.Combat: Unable to load one or more of the requested types.` Every `[AutoRegisterSingleton]` in the affected types is skipped. Three screens later the game dies with `Type 'IDamageService' is not registered` and the developer has no path from the crash back to the missing DLL — the one message that would name it (`FileNotFoundException: Could not load file or assembly 'ThirdParty.Combat'`) sits unread in `ex.LoaderExceptions[0]`.

**Düzeltme:** Append the distinct loader exceptions: `var detail = ex.LoaderExceptions == null ? "" : string.Join("; ", ex.LoaderExceptions.Where(l => l != null).Select(l => l.Message).Distinct().Take(5));` and include it in the warning. Startup-only path, so the LINQ cost is irrelevant.

## `scanner-returns-mutable-shared-cache`

**ScanAssemblies hands every caller the same mutable cached List<AutoBindingEntry> of mutable entries**

| | |
|---|---|
| Konum | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:55` |
| Kategori | api-hazard · di-injection |
| Etki | Startup-only; a corrupted cache persists for the whole Editor domain / process lifetime. |
| Test | Not covered. `AutoBindingTests.RuntimeScanner_CachesResults` asserts `AreSame(entries1, entries2)` — it actually PINS the aliasing that makes this hazard possible, so tightening the return type would require updating that test. |

```csharp
            var cached = _cache;
            if (MatchesCachedPatterns(cached, includePatterns, excludePatterns))
            {
                return cached.Entries;
            }
```

**Sorun:** `ScanAssemblies` is `public static` and returns `cached.Entries` — the exact `List<AutoBindingEntry>` instance stored in the static cache — on lines 55, 63 and 106. Every `AutoBindingEntry` property is a public settable auto-property (`ServiceType`, `ImplementationType`, `Lifetime`, `Priority`, `RegisterSelf`, lines 10-14). Any caller can therefore `Add`, `Remove`, `Clear` or field-mutate entries and permanently corrupt the cache for every subsequent `RegisterAll` in the process, including retargeting an entry's `ImplementationType` to a different class. This was reported as unit-03 Finding 8 and is still unfixed. It also interacts with `scanner-cache-nonvolatile-and-scan-outside-lock`: a caller enumerating the returned list while another thread's scan mutates a concurrently-published list gets `InvalidOperationException: Collection was modified`.

**Senaryo:** Editor tooling or a diagnostics window calls `RuntimeAutoBindingScanner.ScanAssemblies(...)` to display discovered services and applies a filter in place (`entries.RemoveAll(e => e.Lifetime != Lifetime.Singleton)`). The static cache is now permanently missing every Transient and Scoped binding. The next `builder.RegisterAutoBindings()` in the same domain (Unity Editor domains survive many play-mode entries) silently registers only the singletons, and the game fails at `Resolve<ITransientThing>()` with "not registered" — a bug that vanishes on domain reload and is therefore near-impossible to reproduce.

**Düzeltme:** Change the public return type to `IReadOnlyList<AutoBindingEntry>` and return `cached.Entries.AsReadOnly()` (cache the wrapper inside `CacheSnapshot` so it costs nothing per call). Make `AutoBindingEntry`'s properties `{ get; init; }` or set them through a constructor so entries are immutable once published. `RegisterAll` already copies before sorting (line 36) so it is unaffected.

## `getoradd-method-group-delegate-alloc-per-inject`

**Inject() allocates a Func<Type,TypeInjectionInfo> on every call because GetOrAdd is passed a method group**

| | |
|---|---|
| Konum | `Runtime/DI/InjectionProcessor.cs:31` |
| Kategori | allocation · di-injection |
| Etki | ~64-96 bytes of Gen0 garbage per `InjectionProcessor.Inject()` / `InjectInto<T>()` call, on every call including cache hits. 10k injections = ~0.6-1 MB. |
| Test | Not covered. `MVCSPerformanceTests.Benchmark_InjectionProcessor_10k_Injections` asserts only wall-clock (`Assert.Less(sw.ElapsedMilliseconds, 100)`) with no `Measure...GC()` allocation assertion, unlike `Tests/Runtime/DI/ContainerPerformanceTests.cs` which does call `.GC()`. An allocation-asserting benchmark for Inject() would catch this. |

```csharp
            return _cache.GetOrAdd(type, BuildInjectionInfo);
```

**Sorun:** `BuildInjectionInfo` is passed as a method group, so the compiler must materialise a `Func<Type, TypeInjectionInfo>` delegate instance at the call site BEFORE `GetOrAdd` runs — `ConcurrentDictionary.GetOrAdd(TKey, Func<TKey,TValue>)`'s internal cache-hit fast path cannot avoid it, because the argument is already constructed by then. Unity 6 compiles with `-langversion:9.0`; Roslyn's static-method-group delegate caching is gated on C# 11, so no cached backing field is emitted and a fresh delegate is allocated on every single `Inject()` call, cache hit or miss. The adjacent file gets this right: `TypeRegistry.cs:19` uses `GetOrAdd(type, static t => ...)` — a static lambda with no captures, which Roslyn caches in a static field even under C# 9 — proving the allocation-free form is already known in this codebase.

**Senaryo:** `Tests/Runtime/Performance/MVCSPerformanceTests.cs:41-48` runs 10 000 `InjectionProcessor.Inject(service, _container)` calls; that is 10 000 delegate allocations, ~640 KB - 960 KB of Gen0 garbage attributable to nothing but the method-group conversion, on a path whose reflection cache is 100% warm. In shipping code the same happens per pooled-view rebind and per spawned controller: a bullet-hell scene rebinding 200 pooled views per second burns ~13-19 KB/s of pure Gen0 churn and pulls forward GC spikes on IL2CPP's Boehm collector.

**Düzeltme:** `private static readonly Func<Type, TypeInjectionInfo> BuildInjectionInfoFactory = BuildInjectionInfo;` and call `_cache.GetOrAdd(type, BuildInjectionInfoFactory);`. One-line change, correct under any language version, zero behavioural difference.

## `injection-uses-uncompiled-reflection-accessors`

**Member injection pays FieldInfo.SetValue / MethodInfo.Invoke plus an object[] allocation per call — only constructor factories are expression-compiled**

| | |
|---|---|
| Konum | `Runtime/DI/InjectionProcessor.cs:138` |
| Kategori | performance · di-injection |
| Etki | Per `Inject()` call: 24+8N bytes per `[Inject]` method (32 B for the common 1-arg case) plus ~300-600 ns per method invoke and ~150-300 ns per field/property set on Mono, versus ~2-5 ns and 0 bytes for compiled accessors. Scales with objects injected per frame, not with type count. |
| Test | Partially covered but with a useless bar: `Tests/Runtime/Performance/MVCSPerformanceTests.cs:33-58` asserts 10 000 injections finish under 100 ms, i.e. a 10 us per-injection budget that the current implementation beats by an order of magnitude, so the assertion can never fail. No allocation assertion exists for this path. |

```csharp
            var args = new object[types.Length];
            for (int i = 0; i < types.Length; i++)
                args[i] = container.Resolve(types[i]);

            return args;
```

**Sorun:** `_cache` memoises only the reflection METADATA (`MethodInfo`/`PropertyInfo`/`FieldInfo`), never a compiled accessor. So every `Inject()` re-pays full late-bound reflection: `ResolveParameters` allocates a fresh `object[]` per `[Inject]` method per call (line 138), `method.Method.Invoke(target, args)` (line 88) does argument type-checking plus Mono's internal marshalling, and `prop.SetValue` (108) / `field.SetValue` (128) each go through an icall with per-call type validation. `Documentation~/DI.md:3` and `README.md:32` sell this as an "expression tree compiled" container, but `Expression.Lambda(...).Compile()` appears only in `Container.CompileFactory` (Container.cs:384, 394) for CONSTRUCTOR injection; the member-injection path in this file has no compiled fast path at all. (On IL2CPP the reverse problem applies to Container.cs: `Expression.Compile` cannot emit IL under AOT and falls back to the slow interpreter — so neither path is actually compiled in a player build.)

**Senaryo:** A `Controller<TModel>` has two `[Inject]` methods inherited from the framework itself (`Base.Construct(IContainer)` at Runtime/Patterns/Base.cs:31 and `Controller<TModel>.InjectModel(TModel)` at Runtime/Patterns/Controller.cs:20). Each `InjectionProcessor.Inject(controller, container)` therefore allocates two `object[1]` arrays (32 bytes each) on top of the delegate from `getoradd-method-group-delegate-alloc-per-inject`, and performs two `MethodInfo.Invoke` calls at roughly 300-600 ns each on Mono versus ~2-5 ns for an equivalent compiled `Action<object,object[]>`. A pooling system that re-injects 500 controllers on a scene load spends ~0.3-0.6 ms in reflection dispatch alone and produces ~48 KB of Gen0 garbage that a compiled-accessor design would not produce at all.

**Düzeltme:** In `BuildInjectionInfo`, build and cache compiled accessors alongside the metadata: `Action<object,object>` per field/property via `Expression.Lambda(Expression.Assign(Expression.Field(Expression.Convert(objParam, type), fi), Expression.Convert(valParam, fi.FieldType)))` (or `System.Reflection.Emit`/`DynamicMethod` where available), and `Action<object,object[]>` per method. On IL2CPP where `Compile()` interprets, fall back to the current reflection path behind `#if ENABLE_IL2CPP` or preferably to source-generated setters. Also hoist the `object[]` into the cached `MethodInjectionInfo` and reuse it (safe only if `Inject` is documented single-threaded per target) or pool it.

## `inject-readonly-property-silently-skipped`

**[Inject] on a get-only property is silently dropped with no diagnostic**

| | |
|---|---|
| Konum | `Runtime/DI/InjectionProcessor.cs:66` |
| Kategori | api-hazard · di-injection |
| Etki | Startup/first-touch only; the cost is developer time, not frames. |
| Test | Not covered — there is no `InjectionProcessorTests.cs`, and no test in the repo declares an `[Inject]` property of any kind. |

```csharp
                if (property.CanWrite)
                    properties.Add(property);
```

**Sorun:** A property carrying `[Inject]` but lacking any setter fails `CanWrite` and is discarded with no warning, no error, and no entry in the cached `TypeInjectionInfo`. Contrast the same file's handling of an actual type mismatch (lines 100-106 and 120-126), which at least emits a `Debug.LogWarning`. `InjectAttribute`'s `AttributeUsage` (Attributes/InjectAttribute.cs:5) permits `AttributeTargets.Property` with no distinction, so nothing at compile time flags the mistake either. Note `PropertyInfo.SetValue` uses `GetSetMethod(nonPublic: true)` internally, so a `private set` or `init` accessor works fine — the only broken shape is the fully get-only property, which is exactly the shape a developer reaches for when writing `[Inject] public IFoo Foo { get; }`.

**Senaryo:** `[Inject] public IEventBus Bus { get; }` — an entirely reasonable-looking declaration, since `Base.cs:24-27` uses `{ get; private set; }` properties for the same role. `Inject()` completes with no output; `Bus` stays null; the first use NREs at an unrelated call site with nothing pointing back at the missing setter. Adding `private set;` fixes it, but there is no signal telling the developer that.

**Düzeltme:** In the `else` branch, `UnityEngine.Debug.LogError($"[InjectionProcessor] [Inject] property '{type.Name}.{property.Name}' has no setter and will not be injected; add a (private) setter.");` This runs once per type inside the cached build path, so it costs nothing at runtime.

## `postconstruct-catch-too-narrow`

**InvokePostConstruct catches only TargetInvocationException, so generic-method [PostConstruct] escapes uncaught and unwrapped**

| | |
|---|---|
| Konum | `Runtime/DI/LifecycleProcessor.cs:28` |
| Kategori | bug · di-injection |
| Etki | Startup/first-touch only. |
| Test | ZERO — `grep -rl "LifecycleProcessor\|PostConstruct\|DeConstruct" Tests/` returns no files. Neither the happy path nor any failure path of this class is exercised. |

```csharp
                catch (TargetInvocationException e)
                {
                    throw new InvalidOperationException(
                        $"[PostConstruct] Error invoking {method.Name} on {type.Name}", e.InnerException ?? e);
                }
```

**Sorun:** `MethodInfo.Invoke` wraps exceptions thrown by the target in `TargetInvocationException`, but it also throws several exceptions of its own that are NOT wrapped and therefore escape this handler with no context: `InvalidOperationException` when the method is a generic method definition ("Late bound operations cannot be performed on types or methods for which ContainsGenericParameters is true"), `TargetException` when `target` is not an instance of the declaring type, and `MethodAccessException` under restricted trust. `FindMethodsWithAttribute` (lines 73-85) filters only on attribute presence and `GetParameters().Length == 0` — it never checks `method.ContainsGenericParameters` or `IsGenericMethodDefinition`, so a zero-parameter generic method reaches `Invoke` on line 26. Contrast `InvokeDeConstruct`, which catches the base `Exception` (line 49). Additionally, because the handler rethrows rather than continuing, the FIRST failing `[PostConstruct]` prevents every subsequent one on the same object from running.

**Senaryo:** `[PostConstruct] private void Warm<T>() where T : IComponent { }` — `GetParameters().Length` is 0 so it passes the filter on line 80 and is cached. `method.Invoke(target, null)` on line 26 throws `InvalidOperationException` (not `TargetInvocationException`), which sails past the handler and out of `InvokePostConstruct` with the raw framework message and no mention of the offending type — the caller sees "Late bound operations cannot be performed..." with no clue which class is at fault.

**Düzeltme:** Add `if (method.ContainsGenericParameters) continue;` (with a one-time `Debug.LogError` naming the method) to the filter on line 80, and widen the catch to `catch (Exception e)` while preserving the `TargetInvocationException` unwrapping: `catch (Exception e) { throw new InvalidOperationException($"[PostConstruct] Error invoking {method.Name} on {type.Name}", (e as TargetInvocationException)?.InnerException ?? e); }`.

## `deconstruct-logs-full-exception-in-release`

**DeConstruct failures log the full exception (stack trace, source paths) unconditionally in release player builds**

| | |
|---|---|
| Konum | `Runtime/DI/LifecycleProcessor.cs:51` |
| Kategori | security · di-injection |
| Etki | Information disclosure only; no runtime cost. Triggered per failing `[DeConstruct]` method. |
| Test | ZERO — no test touches LifecycleProcessor at all, so neither the log format nor the build-configuration branch is verified. |

```csharp
                    UnityEngine.Debug.LogError(
                        $"[DeConstruct] Error invoking {method.Name} on {type.Name}: {e}");
```

**Sorun:** `{e}` on an `Exception` calls `ToString()`, which emits the message, the full type name, the inner-exception chain and the complete stack trace — including source file paths and line numbers whenever debug symbols are present, which they are in Development builds and in Release builds where the developer left `Debug` symbols enabled. This is written to the Unity player log, which on desktop and Android is world-readable on the user's device. The same repo already establishes the correct pattern for this: `Container.cs:198-202` guards the verbose form behind `#if UNITY_EDITOR || DEVELOPMENT_BUILD` and logs only `e.Message` in release. `LifecycleProcessor` has no such guard. The neighbouring `InvokePostConstruct` handler (line 31) does the right thing by not interpolating the exception at all.

**Senaryo:** A shipped game's `[DeConstruct] void Close()` throws during shutdown. The player log receives `[DeConstruct] Error invoking Close on SaveService: System.IO.IOException: ... at Game.Persistence.SaveService.Close() in /Users/<devname>/Projects/RealProjectName/Assets/Scripts/Persistence/SaveService.cs:line 84 ...` — leaking the developer's home directory, the internal project name, and the full internal type/namespace layout of the shipping assembly to anyone who reads the log file.

**Düzeltme:** Mirror Container.cs:198-202: `#if UNITY_EDITOR || DEVELOPMENT_BUILD` log `{e}`, `#else` log `{e.Message}` `#endif`. Consider routing through `StradaLog.LogError(..., LogModule.DI)` for consistency with Container.cs:203.

## `ecb-unaligned-struct-load-in-playback`

**ComponentPlaybackHandler dereferences a byte* at an arbitrary alignment as T* — component payloads always start at a non-8-byte-aligned stream offset**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:400` |
| Kategori | aot-il2cpp · ecs-jobs |
| Etki | Zero measurable cost to fix (MemCpy of sizeof(T) bytes is what the compiler emits for the struct copy anyway). Risk: crash or silent data corruption on ARM32 IL2CPP, and on any platform once a component contains a SIMD/16-byte-aligned field. |
| Test | NOT COVERED. Every component used in the ECB tests is scalar float/int (`Position{float X,Y,Z}` EntityCommandBufferTests.cs:14, `TestComponentA{int Value}` ParallelCommandBufferTests.cs:83-86, `JBenchPosition` JobSystemPerformanceTests.cs:12), and all tests run in the Editor on Mono where the JIT emits unaligned-tolerant loads. No test uses a double/long/float4 component. |

```csharp
        public unsafe void AddComponent(EntityManager em, Entity entity, byte* data, int size)
        {
            CheckComponentSize(size);
            T component = *(T*)data;
            em.AddComponent(entity, component);
        }
```

**Sorun:** `data` is `CommandReader.ReadBytes`'s return value: `(byte*)_data.GetUnsafeReadOnlyPtr() + _position` (line 321), i.e. the base pointer plus a byte offset accumulated from a variable-length command stream. The layout written by `AddComponent<T>` is cmd(1) + index(4) + version(4) + isDeferred(1) + typeHash(8) + size(4) = a 22-byte header, so the payload is at offset 22 from the start of that command, and commands are packed with no padding (`WriteCommand`/`WriteInt`/`WriteByte` all append single bytes). The payload alignment is therefore effectively arbitrary mod 1. `*(T*)data` is an aligned typed load in both IL (`ldobj` without the `unaligned.` prefix — ECMA-335 makes this undefined for misaligned pointers) and in the C++ IL2CPP emits. The same construct is at line 415 in `SetComponent`. Note the reader's own scalar reads (`ReadInt` line 290-301, `ReadULong` line 304-313) deliberately assemble bytes one at a time and are alignment-safe — only the struct payload load is not.

**Senaryo:** A component containing a 16-byte-aligned type, e.g. `struct Body : IComponent { public Unity.Mathematics.float4 Velocity; }` (or any struct with a `double`/`long` on ARM32 Android, still a shipping IL2CPP target). Playback lands the payload at, say, stream offset 23. IL2CPP's clang sees `*(Body*)data` as a naturally-aligned access and is free to emit `movaps`/`ldr q0`; at a 23-byte offset that is a SIGBUS on ARM32 and an unaligned-access fault or a torn read for the vector case. On x86-64/ARM64 with scalar-only components it happens to work today, which is why the bug is latent.

**Düzeltme:** Replace both dereferences with an explicitly unaligned copy: `UnsafeUtility.CopyPtrToStructure(data, out T component);` (or `T component; UnsafeUtility.MemCpy(&component, data, sizeof(T));`). Alternatively pad the header to 8 bytes and align each payload, but the memcpy fix is one line and removes the constraint entirely.

## `ecb-readbytes-bounds-check-overflow`

**CommandReader.ReadBytes bounds check overflows on large counts and accepts negative counts — the prior audit's HIGH finding #1 is marked FIXED but the fix is incomplete**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:316` |
| Kategori | security · ecs-jobs |
| Etki | One extra compare in a path that already branches — no measurable cost. Impact when triggered: main-thread infinite loop, or an unbounded out-of-bounds read in release builds. |
| Test | NOT COVERED. No test feeds a malformed or truncated command stream; there is no negative test for any of the reader guards. Tests/Stress/ParallelCommandBufferTests.cs deliberately uses one buffer per thread (line 37-39) so it never produces the torn stream that reaches this code. |

```csharp
        public unsafe byte* ReadBytes(int count)
        {
            if (_position + count > _data.Length)
                throw new InvalidOperationException("Command buffer read overflow");

            byte* result = (byte*)_data.GetUnsafeReadOnlyPtr() + _position;
            _position += count;
            return result;
        }
```

**Sorun:** `count` comes from `reader.ReadInt()` (lines 219 and 237) — an attacker-influenced or corruption-influenced value straight out of the stream. `_position + count` is unchecked int arithmetic: for `count` near `int.MaxValue` the sum wraps negative and the guard passes. There is also no `count >= 0` check, so a negative `count` trivially passes and then *decrements* `_position`. Compare with the sibling guards, which use the overflow-safe form `if (Remaining < 4)` (line 292) / `if (Remaining < 8)` (line 306); `ReadBytes` is the one that was written differently. `Remaining` itself (line 265, `_data.Length - _position`) goes negative once `_position` is corrupted, and `HasRemaining` (line 264, `_position < _data.Length`) stays true for any negative `_position`. SecurityReports/2026-05-22-status-review.md row 9 lists this as FIXED: "ReadCommand/ReadByte/ReadInt/ReadULong/ReadBytes hepsi Remaining veya _position + count > _data.Length kontrolü yapıyor" — the ReadBytes variant is precisely the unsound one.

**Senaryo:** Two threads write to the same EntityCommandBuffer (a scenario the class doc at lines 25-30 explicitly says produces 'undefined behavior during playback' rather than a bounded failure). A torn `WriteInt(size)` yields size = -1000. `Playback` -> `PlaybackAddComponent` -> `ReadBytes(-1000)`: `_position + (-1000) > _data.Length` is false, so no throw; `_position -= 1000` rewinds the reader. `HasRemaining` is still true, so the while loop at line 118 re-reads the same region forever — an infinite loop on the main thread, i.e. a hard hang rather than an exception. With a large positive size the check wraps, `_position` becomes negative, and `_data[_position++]` in `ReadCommand`/`ReadByte` reads out of bounds (NativeArray's indexer has no bounds check once `ENABLE_UNITY_COLLECTIONS_CHECKS` is off in player builds).

**Düzeltme:** Rewrite using the same overflow-safe form as the other readers: `if (count < 0 || Remaining < count) throw new InvalidOperationException("Command buffer read overflow");`. Also validate the decoded size against a sane maximum before calling ReadBytes at lines 219/237, and consider making `Remaining` clamp at 0.

## `ecb-playback-hoists-all-creates`

**Playback creates all deferred entities up front instead of in recorded order, so interleaved Create/Destroy commands do not replay in sequence and index recycling within a buffer is impossible**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:113` |
| Kategori | bug · ecs-jobs |
| Etki | Up to 2x the steady-state sparse-array footprint for create/destroy-balanced workloads, and halves the effective entity-index budget before the 1,048,576 SparseSet ceiling is hit. |
| Test | NOT COVERED. EntityCommandBufferTests.cs::ComplexSequence_WorksCorrectly (line 179) mixes a create with a Set on a pre-existing entity but never interleaves a Destroy with a Create in the same buffer, and asserts only entity count and one component value — never an entity index. No test pins replay ordering. |

```csharp
            _createdEntities.Clear();
            for (int i = 0; i < _createEntityCount; i++)
                _createdEntities.Add(entityManager.CreateEntity());

            var reader = new CommandReader(_commandStream.AsArray());
```

**Sorun:** All `_createEntityCount` entities are materialised before the stream is walked; the `CreateEntity` case in the replay switch is then a bare `break;` (lines 123-124). Because `EntityManager.CreateEntity` pops from `_recycledIndices` only if the list is non-empty at call time (EntityManager.cs:51-56), and all ECB `DestroyEntity` commands execute *after* every create, no index freed by this buffer can ever be reused by a create in the same buffer. The recorded order — which is what the byte stream faithfully preserves and what a user reading `CreateEntity(); DestroyEntity(x); CreateEntity();` would expect — is not the executed order.

**Senaryo:** A pooling/respawn system records, in one buffer, `DestroyEntity` for 100,000 dead entities and `CreateEntity` for 100,000 replacements. At playback the 100,000 creates run first against an empty recycle list, so `_nextEntityIndex` advances by 100,000 and `SparseSet.EnsureSparseCapacity` grows every component storage to cover the new high-water index; only then are the 100,000 old indices released. The steady-state sparse footprint is permanently ~2x what the recorded order would produce, and the hard ceiling `MaxSparseCapacity = 1_048_576` (SparseSet.cs:185) is reached at half the entity turnover it should support — after which `EnsureSparseCapacity` throws `InvalidOperationException` (SparseSet.cs:191-193) and entity creation stops working.

**Düzeltme:** Either replay creates in stream order — reserve the deferred-entity slot at the `EntityOperation.CreateEntity` case (`_createdEntities.Add(entityManager.CreateEntity())`) instead of hoisting, which is a two-line change and matches the recorded semantics — or document explicitly in the class remarks that all creates are hoisted ahead of all other commands and that intra-buffer index recycling does not occur.

## `ecb-burst-attribute-inert-and-record-path-burst-hostile`

**[BurstCompile] on EntityCommandBuffer compiles nothing, and the record path reads TypeHash<T>.Value — a managed static initialised from typeof(T).FullName string hashing — contradicting the documented "usable from a single Burst job" contract**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:37` |
| Kategori | aot-il2cpp · ecs-jobs |
| Etki | Startup-only for the hash itself (one static ctor per closed generic). The real impact is the ECB record path failing to Burst-compile or silently falling back inside user jobs. |
| Test | NOT COVERED. No test records into an EntityCommandBuffer from inside a Unity job — every test in EntityCommandBufferTests.cs, ParallelCommandBufferTests.cs and JobSystemPerformanceTests.cs records from managed main-thread or Task.Parallel.For code, so the Burst-compile step is never attempted on this code path. |

```csharp
    [BurstCompile]
    public unsafe struct EntityCommandBuffer : IDisposable
```

**Sorun:** Burst only produces code for types implementing a Unity job interface, or for static methods explicitly marked `[BurstCompile]` inside a `[BurstCompile]` type. `EntityCommandBuffer` is neither a job nor does any of its members carry `[BurstCompile]` — so the type-level attribute emits nothing and is a false signal. Worse, the recording path it advertises is Burst-hostile: `WriteTypeHash<T>` (line 180) reads `ulong hash = TypeHash<T>.Value;`, a `static readonly` on the managed generic class `TypeHash<T>` (lines 328-343) whose initializer calls `typeof(T).FullName` and iterates a `string` with `foreach (char c in name)`. The class remarks at lines 25-27 nonetheless tell users the buffer is 'intended to be used by a single thread (or a single Burst job at a time)'. The playback half is unambiguously non-Burstable too: `ConcurrentDictionary` lookups (line 347), `IComponentPlaybackHandler` interface dispatch (line 361), and `throw` sites at lines 252/277/285/293/307/319/424.

**Senaryo:** A user follows the class documentation and calls `ecb.AddComponent(entity, new Position())` from inside a `[BurstCompile] struct SpawnJob : IJob`. Burst must resolve `TypeHash<Position>.Value` through a managed static-constructor chain that performs `System.Type` reflection and `System.String` iteration. If Burst cannot constant-fold that initializer it reports a compile error and silently falls back to the managed implementation for the whole job (in AOT, no error surfaces at all — see the RegisterGenericJobType finding) — so the 'Burst job records into an ECB' pattern the docs promise either fails to compile or quietly runs unBursted.

**Düzeltme:** Remove the inert `[BurstCompile]` from line 37. Replace the string-derived `TypeHash<T>` with a Burst-friendly stable id — `Unity.Burst.BurstRuntime.GetHashCode64<T>()` is designed exactly for this and is a compile-time constant under Burst. Then update the class remarks to state precisely which methods are Burst-safe (record) and which are main-thread-managed-only (Playback).

## `ecb-playback-dictionary-lookup-per-command`

**Every replayed component command costs a ConcurrentDictionary<ulong,...> lookup plus an interface dispatch, when the type is already known at record time**

| | |
|---|---|
| Konum | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:347` |
| Kategori | performance · ecs-jobs |
| Etki | ~12-25 ns per replayed component command; ~0.24-0.5 ms per frame at 20,000 commands/frame. Also 8 bytes of stream per command (24% of a 34-byte AddComponent<Position> command) spent on the type hash. |
| Test | Measured only in aggregate by JobSystemPerformanceTests.cs:94 (`Benchmark_EntityCommandBuffer_10k_Commands`), whose budget of `< 50 ms` for 20,000 commands (line 122) is ~2,500 ns/command — three orders of magnitude looser than the dispatch cost, so it cannot detect a regression here. |

```csharp
        private static readonly ConcurrentDictionary<ulong, IComponentPlaybackHandler> _handlers = new();
```

**Sorun:** `PlaybackAddComponent`/`PlaybackSetComponent`/`PlaybackRemoveComponent` (lines 215-241) each decode a 64-bit hash out of the stream and route through `ComponentPlayback.AddComponent/SetComponent/RemoveComponent` (lines 358-374), which perform `_handlers.TryGetValue(typeHash, out var handler)` followed by a virtual interface call. ConcurrentDictionary's read path is lock-free but still hashes the key, indexes a volatile bucket array and walks a node chain — noticeably more work than a plain Dictionary, and the concurrency is pointless here because the class doc (lines 31-32) already requires playback on the main thread. The 8-byte hash is also re-serialised into every single command even though a per-command 2-byte type index into a registration table would do.

**Senaryo:** JobSystemPerformanceTests.cs:94-125 replays 10,000 AddComponent commands per iteration; each pays one ConcurrentDictionary lookup (~10-20 ns) plus one interface dispatch (~2-5 ns) on top of the actual `em.AddComponent` work. A game flushing 20,000 structural commands per frame spends ~0.2-0.5 ms/frame purely in handler dispatch on the main thread.

**Düzeltme:** Resolve the handler once per type rather than once per command: cache `IComponentPlaybackHandler` in a `static class HandlerCache<T> { public static IComponentPlaybackHandler Value; }` populated by EnsureHandler<T> (generic static, zero lookup), and write a small dense type index into the stream instead of the 8-byte hash so playback indexes a flat array. Swap ConcurrentDictionary for a plain Dictionary guarded at registration only, since playback is documented main-thread.

## `benchmark-speedup-degenerate-and-unfair`

**The "17x parallel speedup" benchmarks divide integer milliseconds (0 denominator gives Infinity, which passes the assert) and compare a Burst job against a managed per-entity delegate loop with warmup only on the parallel side**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ParallelJobPerformanceTests.cs:174` |
| Kategori | test-gap · ecs-jobs |
| Etki | The published 17x is not a parallelism measurement; on an 8-core machine Documentation~/Benchmarks.md:360-364 itself claims only 6.5x from threading, so roughly 2.6x of the 17x is attributable to Burst/delegate-elimination rather than parallelism. |
| Test | This IS the test. Both Benchmark_ParallelVsSequential_100k (ParallelJobPerformanceTests.cs:138) and Benchmark_100k_ParallelVsSequential_TargetSpeedup (JobSystemPerformanceTests.cs:49) share all three defects; there is no benchmark isolating Burst-off vs Burst-on, and none isolating 1-worker vs N-worker. |

```csharp
            float speedup = (float)swSequential.ElapsedMilliseconds / swParallel.ElapsedMilliseconds;
```

**Sorun:** Three separate defects in the measurement that produces README.md:226's `| **Parallel Job Speedup** | **17x** |`. (1) `Stopwatch.ElapsedMilliseconds` is an integer with ~1 ms granularity; if the parallel run finishes in under 1 ms the denominator is 0 and `(float)a / 0` yields `float.PositiveInfinity`, so `Assert.Greater(speedup, 1.0f)` (line 181) and the identical assert in JobSystemPerformanceTests.cs:90 pass unconditionally and report an unbounded speedup. `sw.ElapsedTicks` is available and is used elsewhere in the same file's sibling test (JobSystemPerformanceTests.cs:115). (2) The 'sequential' baseline is `_entityManager.ForEach<Position, Velocity>((int e, ref Position t, ref Velocity v) => {...})` (lines 153-158), which EntityQuery.cs:94 dispatches as `action(entityIndex, ref *ptr1, ref *ptr2)` — a managed delegate invocation per entity, non-Burst, non-vectorised, non-inlinable. The 'parallel' side is a Burst-compiled IJobParallelFor. The ratio therefore measures Burst codegen + delegate-call elimination + N worker threads combined, not 'parallel vs sequential'. (3) The parallel side gets an explicit warmup (lines 164-165 / JobSystemPerformanceTests.cs:74) that triggers Burst JIT and first-touch page faults; the sequential loop at lines 150-160 runs cold with no warmup and pays JIT + delegate-site setup inside the measured window.

**Senaryo:** On a machine where 100k entities x 10 frames of the Burst job take 0.7 ms, `swParallel.ElapsedMilliseconds == 0`, `speedup == Infinity`, the test logs `Speedup: Infinity x` and passes. Conversely a genuine regression that made the parallel path 3x slower would still pass `Assert.Greater(speedup, 1.5f)` because the sequential delegate baseline is roughly 17x slower to begin with. Neither test can detect a parallel-path regression, and neither substantiates the 17x figure as a parallelism number.

**Düzeltme:** Use `sw.Elapsed.TotalMilliseconds` (or ElapsedTicks / Stopwatch.Frequency) for both sides; warm up the sequential path identically; and add a third arm — the same MoveJob run through `.Schedule()` on a single worker or `IJobFor.Run()` — so the report separates the Burst contribution from the parallel contribution. Then split README.md:226 / Documentation~/ECS.md:412 / Documentation~/Benchmarks.md:337-339 into 'Burst vs managed delegate' and 'parallel vs single-threaded Burst' rows.

## `query-dense-index-no-upper-bound-check`

**Pointer arithmetic validates only the sign of the dense index, never the upper bound — prior audit unit-05 Finding 8, still NOT fixed**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQuery.cs:88` |
| Kategori | security · ecs-query |
| Etki | Startup/debug-only cost if the assert is gated behind ENABLE_UNITY_COLLECTIONS_CHECKS. The unsigned-compare fix in GetDenseIndex is zero-cost (one `cmp` either way). Security impact: converts an arbitrary-write primitive into a caught error. |
| Test | NOT COVERED. Tests/Runtime/ECS/Storage/SparseSetTests.cs does not test GetDenseIndex with a negative index; no query test exercises an out-of-range dense index. |

```csharp
                    if (idx1 < 0 || idx2 < 0)
                        continue;

                    T1* ptr1 = set1.GetDataPtr() + idx1;
                    T2* ptr2 = set2.GetDataPtr() + idx2;
```

**Sorun:** `GetDenseIndex` (SparseSet.cs:122) is `entityIndex < _sparse.Length ? _sparse[entityIndex] : -1` — it neither rejects a negative `entityIndex` nor validates that the returned dense index is `< _count`. Every query then adds that index to a raw `T*` and hands out a `ref` to the result, checking only `idx < 0`. Any positive-but-stale index (which finding `query-stale-count-silent-write-loss` shows is reachable, and finding `query-foreach-dangling-native-ptr-on-grow` shows can be arbitrary garbage) produces an out-of-range `ref` that the caller writes through. Identical in EntityQuery.cs:159-164, FilteredQuery.cs:142/233, and EntityQueryExtended.cs:60-61, :109-110, :162-163, :219-220, :280-282.

**Senaryo:** Prior audit SecurityReports/unit-05-ecs-jobs-parallel.md Finding 8 (LOW) recommended "In debug/editor builds, add upper-bound assertions (`idx < set.Count`) before pointer arithmetic." Current code at EntityQuery.cs:88 still checks only `< 0`. The finding is not listed as FIXED and not listed as ACCEPTED-BY-DESIGN in SecurityReports/2026-05-22-low-status-review.md (only unit-05 #5, #9, #10 appear there) — it was silently dropped. Concretely: combine with the dangling-pointer path — `entities[i]` reads freed memory, yields e.g. 0x5F3A1C, `_sparse[0x5F3A1C]` reads out of bounds (no bounds check on NativeArray with checks disabled), returns a large positive value, and `GetDataPtr() + thatValue` produces an arbitrary writable `ref` handed to user code.

**Düzeltme:** Add `#if ENABLE_UNITY_COLLECTIONS_CHECKS` guards: in SparseSet.GetDenseIndex, return -1 for `entityIndex < 0` and for `_sparse[entityIndex] >= _count`; and in each query loop assert `idxK < setK.Count` before the pointer add. The sign check in GetDenseIndex is free in release (it can be folded into the existing `entityIndex < _sparse.Length` test as an unsigned compare: `(uint)entityIndex < (uint)_sparse.Length`).

## `entityquery-idisposable-arity-inconsistency`

**EntityQuery arities 4-8 implement IDisposable with an empty Dispose while arities 1-3 do not implement it at all**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/EntityQueryExtended.cs:66` |
| Kategori | api-hazard · ecs-query |
| Etki | Startup/none — no runtime cost unless a query is boxed to IDisposable (1 allocation per box). This is an API-shape defect, not a perf defect. |
| Test | NOT COVERED. Tests/Runtime/ECS/Query/EntityQueryTests.cs:198-323 exercise the 4-8 arity queries via the `EntityManagerQueryExtensions.ForEach` helpers and never touch Dispose or the `using` form. |

```csharp
        public void Dispose() { }
```

**Sorun:** `EntityQuery<T1,T2,T3,T4>` through `<...,T8>` are declared `: IDisposable` (EntityQueryExtended.cs:22, 69, 118, 171, 228) with a no-op `Dispose()` (lines 66, 115, 168, 225, 287), while `EntityQuery<T1>`, `<T1,T2>`, `<T1,T2,T3>` in EntityQuery.cs are not IDisposable at all. The queries own nothing — they hold borrowed `ComponentStorage<T>` class references — so the interface is a false ownership signal, and the arity split makes the API non-uniform.

**Senaryo:** `using var q = em.Query().Select<A,B,C,D>();` compiles; `using var q = em.Query().Select<A,B,C>();` fails with CS1674 ("type used in a using statement must implement System.IDisposable"). A user who standardises on the `using` form for 4-component queries then cannot write generic helper code over queries, and any code path that stores one of these as `IDisposable` boxes the readonly struct (one heap allocation) for a Dispose that does nothing.

**Düzeltme:** Remove `: IDisposable` and the empty `Dispose()` from all five extended arities so all eight are uniformly non-disposable value views, matching EntityQuery.cs. If a future arity ever needs cleanup, add it to all arities at once.

## `query-select-creates-persistent-storage`

**Select<T>()/Filter<T>()/None<T>() silently allocate persistent native storage for component types that are never used**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/QueryBuilder.cs:17` |
| Kategori | api-hazard · ecs-query |
| Etki | ~5.4 KB of Allocator.Persistent native memory per component type first touched by a query, permanently, including types only ever named in None<T>(). One-time per type (not per frame), but never reclaimed. Thread-safety impact is a Dictionary corruption, not a perf cost. |
| Test | NOT COVERED. No test asserts that querying an unused component type leaves the store unchanged; Tests/Runtime/ECS/Query/EntityQueryTests.cs:161-171 (`Query_EmptyResult_DoesNotThrow`) exercises exactly this path and only checks that count==0. |

```csharp
        public EntityQuery<T1> Select<T1>() where T1 : unmanaged, IComponent
        {
            return new EntityQuery<T1>(_manager.Store.GetOrCreateStorage<T1>());
        }
```

**Sorun:** `GetOrCreateStorage<T>` (ComponentStorage.cs:135-144) is a MUTATING operation: on a miss it constructs `new ComponentStorage<T>(1024, 256)` — three `Allocator.Persistent` NativeArrays — and inserts it into the `Dictionary<Type, IComponentStorage>`. Every query-construction entry point calls it: `Select` (lines 19, 28-29, 39-41, 50-51, 60-62, 71-73, 83-86, 96-99), `Filter` (lines 105, 114-115, 125-127), and — most surprisingly — `None<TExclude>()` at FilteredQuery.cs:62/120/190, which allocates a full storage for a component type the caller is explicitly EXCLUDING. Two consequences: (1) a read-shaped API permanently allocates native memory; (2) it mutates a plain non-concurrent Dictionary, so constructing a query off the main thread corrupts the store.

**Senaryo:** A world in which no entity ever has `Dead`: `em.Query().Filter<Position>().None<Dead>().ForEach(...)` allocates, on first call and forever after, a `ComponentStorage<Dead>` = 1024 ints sparse (4,096 B) + 256 ints dense (1,024 B) + 256 * sizeof(Dead) data, all `Allocator.Persistent`, never freed until `EntityManager.Dispose`. A project with 30 tag types used only in `None` clauses leaks ~160 KB of persistent native memory for storage that will never hold a single component. Separately, calling `Query().Select<NewType>()` from a worker thread while the main thread also creates a storage races on `_storages` and can corrupt the Dictionary (resize during insert) — silent, non-deterministic.

**Düzeltme:** Split the API: give ComponentStore a `TryGetStorage<T>(out ComponentStorage<T>)` that does not create, and have query construction use it — a missing storage means Count==0, which every ForEach already handles correctly (the loop simply does not execute; `Query_EmptyResult_DoesNotThrow` at EntityQueryTests.cs:161 already asserts this). Reserve `GetOrCreateStorage` for `AddComponent`. Additionally, document/assert main-thread-only construction, or make `_storages` a ConcurrentDictionary.

## `querybuilder-obsolete-past-removal-version`

**With<T>() is marked "will be removed in v2.0" but ships in 2.0.0-alpha.1, and the repo's own test still depends on it**

| | |
|---|---|
| Konum | `Runtime/ECS/Query/QueryBuilder.cs:138` |
| Kategori | api-hazard · ecs-query |
| Etki | None at runtime — `With<T>` is an aggressive-inlining forwarder to `Select<T>`. Build/consumer-contract impact only. |
| Test | Tests/Runtime/ECS/Query/EntityQueryTests.cs:148-158 uses the deprecated API; no test covers `Count` via `Select<T1>()`. |

```csharp
        [Obsolete("Use Select<T>() instead. This method will be removed in v2.0.")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1> With<T1>() where T1 : unmanaged, IComponent => Select<T1>();
```

**Sorun:** package.json declares `"version": "2.0.0-alpha.1"`, and the preceding commit (556a3e9, "security: Phase 2B (v2.0) — remove legacy Unsubscribe API") removed other legacy APIs for this exact release, but the three `With<T...>` overloads (lines 140, 151-154, 165-169) survived with a deprecation contract that has already come due. Downstream consumers who read the message have no way to know whether the removal happened or slipped.

**Senaryo:** Tests/Runtime/ECS/Query/EntityQueryTests.cs:156 still calls `_entityManager.Query().With<PositionComponent>()`, producing CS0618 in the test assembly with no `#pragma warning disable 618` anywhere under Tests/Runtime/ECS/Query/ (verified by grep) — even though commit 21e02ab added exactly that suppression pattern elsewhere. If the test assembly is ever built with warnings-as-errors the ECS query tests stop compiling; and `QueryBuilder_ReturnsCorrectCount` is the ONLY test of `EntityQuery<T1>.Count`, so the sole coverage of that property runs through the deprecated path.

**Düzeltme:** Either delete the three `With<T...>` overloads for 2.0.0 (and migrate EntityQueryTests.cs:156 to `Select<PositionComponent>()`), or bump the message to the real removal version (e.g. "removed in v3.0") and add the CS0618 suppression to the test file so the deprecation contract stops lying.

## `archetype-registerdescriptor-orphans-tracked-entities`

**Re-registering a descriptor replaces its tracking list, orphaning every entity created before the re-registration**

| | |
|---|---|
| Konum | `Runtime/ECS/Archetypes/ArchetypeManager.cs:24` |
| Kategori | bug · ecs-storage |
| Etki | Every entity tracked under the descriptor at the moment of re-registration leaks: its EntityManager slot (5 B), its 8-byte handle in the orphaned List, and one sparse+dense+data slot per component it holds, for the lifetime of the World. |
| Test | None. Tests/Runtime/ECS/ArchetypeTests.cs builds a fresh ArchetypeManager per test in SetUp (line 18) and never registers the same descriptor twice; `EnsureDescriptor` (line 34-42) only registers on a miss, so the normal path never re-registers either. |

```csharp
            _entitiesByArchetype[typeof(T)] = new List<Entity>(256);
```

**Sorun:** Both `RegisterDescriptor<T>()` (line 24) and `RegisterDescriptor<T>(T descriptor)` (line 30) unconditionally assign a fresh `List<Entity>` under `typeof(T)`. Any second registration of the same descriptor type discards the existing list. `Clear()` (lines 109-114) and `Dispose()` (line 121) iterate only the lists currently in `_entitiesByArchetype`, so the discarded list's entities are never passed to `_entities.DestroyEntity`. They stay alive in the EntityManager — holding their components and their sparse-set slots — after the ArchetypeManager that created them has been disposed.

**Senaryo:** A module re-registers its descriptors on hot reload, or a test fixture calls `RegisterDescriptor<PlayerDescriptor>()` in `SetUp` on a shared ArchetypeManager: 500 previously spawned players are dropped from tracking. `GetEntityCount<PlayerDescriptor>()` returns 0 while 500 entities are still live and still consuming component storage, `GetEntities<PlayerDescriptor>()` returns an empty list so no system can find them, and `archetypes.Dispose()` leaves all 500 alive in the World. Because `EntityManager.EntityCount` still counts them, the leak is visible only as a number that never goes down.

**Düzeltme:** Preserve the list on re-registration: `if (!_entitiesByArchetype.ContainsKey(typeof(T))) _entitiesByArchetype[typeof(T)] = new List<Entity>(256);` in both overloads. If replacing a descriptor is meant to be destructive, make that explicit — destroy the tracked entities before swapping the list, and say so in the XML doc.

## `archetype-destroy-quadratic-teardown`

**ArchetypeManager.DestroyEntity is O(n) per call, making a full archetype teardown O(n^2)**

| | |
|---|---|
| Konum | `Runtime/ECS/Archetypes/ArchetypeManager.cs:86` |
| Kategori | performance · ecs-storage |
| Etki | n^2/2 comparisons + n^2/2 element moves for a full teardown of n entities: 5x10^9 operations at n=100,000 (README's stated scale), 5x10^7 at n=10,000. O(1) after the fix. |
| Test | Test gap that hides it: Tests/Runtime/ECS/ArchetypeTests.cs has `Benchmark_10k_EntityCreation_WithDescriptor` (line 113) and `Benchmark_10k_BatchCreation` (line 133), both with `Assert.Less(sw.ElapsedMilliseconds, 100)` — but there is no destruction benchmark, so the quadratic cost never appears in CI. `DestroyEntity_RemovesFromTracking` (line 72) destroys exactly one entity. |

```csharp
            if (_entitiesByArchetype.TryGetValue(typeof(T), out var list))
                list.Remove(entity);

            _entities.DestroyEntity(entity);
```

**Sorun:** `List<Entity>.Remove` is a linear scan. `Entity` implements `IEquatable<Entity>` (Runtime/ECS/Entity.cs:13) so the comparer is the non-boxing generic one, but the scan itself is O(n) and the subsequent `Array.Copy` shifts every following element. Destroying all n entities of an archetype therefore costs sum(i) = n^2/2 comparisons plus n^2/2 element moves. The 8-line comment at lines 78-85 answers the prior audit's *memory* concern ("unbounded entity-list growth") and asserts the O(n) Remove is an intentional trade for cache-friendly iteration — but a swap-remove would keep the list exactly as dense and exactly as cache-friendly while being O(1), so the stated trade does not actually exist. The comment also does not mention the quadratic teardown at all.

**Senaryo:** A level unload destroys 100,000 entities of one archetype through `archetypes.DestroyEntity<EnemyDescriptor>(e)` in a loop: 5x10^9 Entity comparisons plus 5x10^9 element moves on a 800 KB array — tens of seconds of frozen main thread, on a code path whose whole purpose is bulk teardown. At the 10,000-entity scale the ArchetypePerformanceTests already exercise it is 5x10^7 comparisons.

**Düzeltme:** Swap-remove: keep a `Dictionary<Entity,int> _slotByEntity` per archetype (or store the slot in a component), then `int slot = _slotByEntity[entity]; var last = list[list.Count-1]; list[slot] = last; _slotByEntity[last] = slot; list.RemoveAt(list.Count-1); _slotByEntity.Remove(entity);`. The list stays dense and contiguous — identical iteration characteristics — and removal drops to O(1). If the tracking list is replaced by a tag component per `archetype-tracking-list-keeps-dead-entities`, this cost disappears entirely.

## `em-capturestate-allocates-full-capacity-array`

**CaptureState copies the entire versions capacity into a managed int[], not just the used range**

| | |
|---|---|
| Konum | `Runtime/ECS/Core/EntityManager.cs:358` |
| Kategori | allocation · ecs-storage |
| Etki | One managed int[] of 4 x capacity bytes per CaptureState call (4 MB at 1,048,576 capacity, LOH), plus a redundant List-then-array copy of the active set. Editor snapshot path, once per recorded frame. |
| Test | None. No test calls CaptureState; the only caller is Editor/Windows/TimeMachineWindow.cs:906. |

```csharp
            activeIndices = activeList.ToArray();

            versions = _versions.ToArray();
```

**Sorun:** `_versions.ToArray()` allocates a managed `int[]` of `_versions.Length` — the array's *capacity*, which `EnsureCapacity` grows by power-of-two doubling and never shrinks — rather than the `_nextEntityIndex` entries that actually carry meaning. A world that briefly peaked at 600,000 entities has capacity 1,048,576 and produces a 4 MB `int[]` per snapshot, which lands on the Large Object Heap. Line 358 additionally double-allocates: `activeList` is built as a `List<int>` and then copied again by `ToArray()`.

**Senaryo:** `Editor/Windows/TimeMachineWindow.cs:906` calls `entityManager.CaptureState(...)` once per recorded frame and retains the arrays in its snapshot ring buffer. With a 1,048,576-slot capacity, each recorded frame pins a 4 MB LOH `int[]` plus the `activeIndices` array; a 120-frame history is ~480 MB of LOH that the GC cannot compact, in-Editor. The `versions` array is also what feeds `RestoreState`'s unvalidated write loop at lines 341-347.

**Düzeltme:** Copy only the meaningful range and let the caller supply the buffer: `public void CaptureState(out int nextEntityIndex, List<int> activeIndices, int[] versions)` where `versions` must be at least `_nextEntityIndex` long, filled via `NativeArray<int>.Copy(_versions, 0, versions, 0, _nextEntityIndex)`. If the allocating signature must stay for compatibility, at minimum size it to `_nextEntityIndex`: `versions = new int[_nextEntityIndex]; NativeArray<int>.Copy(_versions, 0, versions, 0, _nextEntityIndex);`.

## `em-getallentities-allocates-list-and-boxed-enumerator`

**GetAllEntities allocates a List<int> per call and returns IEnumerable, forcing a boxed enumerator at every foreach**

| | |
|---|---|
| Konum | `Runtime/ECS/Core/EntityManager.cs:240` |
| Kategori | allocation · ecs-storage |
| Etki | 4 bytes per active entity plus a ~40 B boxed enumerator, per call. 40 KB + 40 B per call at 10k entities; 2.4 MB/s if called per frame. |
| Test | No GC-allocation test exists for this path. Tests/Benchmarks/ECSBenchmarks.cs measures time only and Tests/Stress/MassEntityTests.cs asserts counts only, so the allocation is invisible to CI. The README claims 0-byte GC allocation only for DI resolution (README.md:233-234), not for ECS iteration. |

```csharp
            var result = new List<int>(_entityCount);
            for (int i = 1; i < _nextEntityIndex; i++)
            {
                if (_active[i] == 1)
                    result.Add(i);
            }
            return result;
```

**Sorun:** Two allocations per call. The `List<int>` costs 4 bytes per active entity, and because the return type is `IEnumerable<int>` rather than `List<int>`, every `foreach` over the result binds `IEnumerable<int>.GetEnumerator()` and boxes `List<int>.Enumerator` (~40 B) instead of using the struct enumerator. The XML doc on lines 234-237 acknowledges the List and points at `GetActiveEntitiesNonAlloc`, but does not mention the boxed enumerator, and the non-alloc variant requires a pre-sized `NativeArray<int>` so it is not a drop-in replacement.

**Senaryo:** Tests/Benchmarks/ECSBenchmarks.cs:66 shows the intended usage — `foreach (var index in _world.EntityManager.GetAllEntities())` inside `Measure.Method`, i.e. once per measured iteration. At 10,000 active entities that is a 40 KB `List<int>` plus a 40 B boxed enumerator per iteration. Tests/Stress/ParallelCommandBufferTests.cs:72 and Editor/DataProviders/WorldDataProvider.cs:42/110/158 use the same shape. A game system copying this pattern at 60 fps with 10k entities generates 2.4 MB/s of garbage from the Lists alone.

**Düzeltme:** Change the return type to `List<int>` so `foreach` binds the struct enumerator (removes the boxing with no call-site change), and add a `List<int>`-filling overload mirroring `ComponentStorage<T>.GetEntityIndices(List<int>)` at ComponentStorage.cs:78 — `public void GetAllEntities(List<int> output)` that clears and refills a caller-owned list, so a per-frame consumer allocates nothing.

## `entity-serializable-with-readonly-fields`

**Entity is marked [Serializable] but its readonly fields make it invisible to Unity's serializer — it always round-trips as Entity.Null**

| | |
|---|---|
| Konum | `Runtime/ECS/Entity.cs:11` |
| Kategori | api-hazard · ecs-storage |
| Etki | No runtime cost. Correctness: every serialized Entity field silently becomes Entity.Null on every deserialization. |
| Test | None. Tests/Runtime/ECS/Core/EntityPropertyTests.cs covers uniqueness and version increment but never serializes an Entity. No test uses JsonUtility or Unity serialization on the type. |

```csharp
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Entity : IEquatable<Entity>
    {
        public readonly int Index;
        public readonly int Version;
```

**Sorun:** Unity's built-in serializer (inspector/scene/prefab serialization and `JsonUtility`) refuses `static`, `const`, and `readonly` fields. `Index` and `Version` are both `readonly`, so `[Serializable]` on line 11 advertises a capability the type does not have: nothing is written and nothing is read back. An `Entity` field on a MonoBehaviour or ScriptableObject silently deserializes as `(0, 0)`, which is exactly `Entity.Null` (line 18) — and `EntityManager.Exists` rejects it at line 133 (`entity.Index <= 0`), so the failure presents as "my entity reference is gone after a domain reload" rather than as a serialization error.

**Senaryo:** `public class EnemyView : MonoBehaviour { public Entity Target; }` — a designer wires it up, or code assigns `Target = archetypes.CreateEntity<EnemyDescriptor>()`. On the next domain reload, scene load, or Play-mode enter, `Target` is `(0,0)`; `world.GetComponent<Health>(Target)` throws `InvalidOperationException: Entity 0:0 does not exist or version mismatch` from EntityManager.cs:218, pointing at the accessor rather than at the serialization that silently dropped the value. Same for any `[Serializable]` save struct containing an `Entity` and passed through `JsonUtility.ToJson` — which is exactly what `Editor/HotReload/EntityStatePreserver.cs:58` does for component values, so the pattern is already established in this codebase.

**Düzeltme:** Pick one and make it explicit. Either drop `[Serializable]` and add an XML remark that Entity handles are runtime-only and must not be persisted (referencing `EntityManager.CaptureState`/`RestoreState` as the supported mechanism); or keep serialization by making the fields non-readonly (`public int Index; public int Version;`) — the struct is still `readonly struct` so callers cannot mutate them — and add a round-trip test. Do not leave the attribute present and non-functional.

## `sparse-memory-scales-with-type-count-not-entities`

**Documented "56 bytes per entity" omits the dominant term: sparse arrays cost 4 bytes x maxEntityIndex x componentTypeCount**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/ComponentStorage.cs:24` |
| Kategori | allocation · ecs-storage |
| Etki | 4 bytes x maxEntityIndex per registered component type, allocated in Allocator.Persistent for the life of the world. 30 types x 100k entities = 12 MB, i.e. 120 B/entity against a documented 56 B/entity. |
| Test | No memory test exists. Tests/Stress/MassEntityTests.cs:31 creates 10k entities with a single component and asserts only EntityCount; Tests/Benchmarks/ECSBenchmarks.cs measures time, never bytes. The 56 B/entity figure in three documents is unverified by any test. |

```csharp
            _sparseSet = new SparseSet<T>(sparseCapacity, denseCapacity, Allocator.Persistent);
```

**Sorun:** Every `ComponentStorage<T>` owns its own `_sparse` array sized by the maximum entity *index*, not by the number of entities that hold T. Deriving the real per-entity cost from the field layout: EntityManager charges `_versions` 4 B + `_active` 1 B = 5 B per entity slot; each component type charges 4 B of `_sparse` per entity slot (paid whether or not the entity has that component) plus 4 B `_dense` + sizeof(T) per entity that does. For two 12-byte components that is 5 + 2*(4+4+12) = 45 B, which with the 1.5x dense and 2x versions growth slack lands near the documented 56 B — so the headline number is defensible only for a world containing exactly two component types. Total memory is O(entities x componentTypes), and README.md:232, Documentation~/ECS.md:437, and Documentation~/Benchmarks.md:189 all present it as O(entities) with "Theoretical Minimum 28 bytes / Overhead ~100%", which understates the real overhead. The default `sparseCapacity = 1024` (line 22) also means every component type allocates 4 KB of sparse indices the moment it is first touched, even for one entity.

**Senaryo:** A realistic game with 30 component types and 100,000 entities: sparse arrays alone are 30 x 100,000 x 4 = 12 MB = 120 bytes per entity, before a single byte of component data or the 500 KB of `_versions`/`_active`. Budgeting from the documented 56 B/entity (5.6 MB) under-provisions by more than 3x once component data is included. On a memory-constrained mobile target this is the difference between shipping and an OOM kill, and it is not discoverable from the docs.

**Düzeltme:** Two parts. (1) Documentation: state the actual formula — `perEntity = 5 + componentTypeCount*4 + sum over held components of (4 + sizeof(T))` — and present 56 B as the two-component-world case rather than a general figure. (2) Code: give `ComponentStore` a constructor path that sizes `sparseCapacity` from the expected entity count rather than the fixed 1024 default, and consider a paged sparse array (e.g. 4096-entry pages allocated on demand) so a component type held by 100 entities out of 100,000 pays for one page instead of the full index range.

## `componentstore-boxed-accessors-uncached-reflection`

**GetComponentBoxed/SetComponentBoxed do an uncached GetMethod plus an object[] allocation and two boxes per call, and are IL2CPP-stripping-exposed**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/ComponentStorage.cs:202` |
| Kategori | allocation · ecs-storage |
| Etki | Per (entity x present-component) pair: one uncached reflection member lookup, one 24-32 B object[] allocation, two boxes (~48 B). ~30,000 reflection invokes and ~1.4 MB of garbage per 10k-entity snapshot, plus one boxed enumerator (~40 B) per entity from GetComponentTypes(). Editor-path today, but the API is Runtime and public. |
| Test | None. No test in Tests/ calls GetComponentBoxed or SetComponentBoxed. Tests/Runtime/ECS/ManagedComponentTests.cs is the only reflection-touching ECS test and it targets EntityManager.AddComponent's generic constraint, not this path. |

```csharp
            var method = storage.GetType().GetMethod("Get");
            if (method == null) return null;

            try
            {
                return method.Invoke(storage, new object[] { entityIndex });
            }
```

**Sorun:** Per call this performs a `RuntimeType.GetMethod(string)` name-based member lookup (no MethodInfo cache anywhere), allocates a one-element `object[]`, boxes `entityIndex`, and boxes the returned `T`. `SetComponentBoxed` (lines 221-226) does the same with a two-element array. A `Dictionary<Type, MethodInfo>` cache is the obvious first fix, but the whole reflection layer is avoidable: `IComponentStorage` already exists as the abstraction, so adding `object GetBoxed(int)` / `void SetBoxed(int, object)` to it turns each call into one interface dispatch and one box. Separately, `ComponentStore` is Runtime (not Editor) public API and there is no `link.xml` anywhere in the package and no `[Preserve]` attribute anywhere in Runtime/ (verified by search), so on IL2CPP with managed stripping the `ComponentStorage<T>.Get`/`Set` metadata can be removed; `GetMethod` then returns null and line 203 silently returns `null` while line 222 silently no-ops — a save/replay system built on these APIs fails quietly in the player and works in the Editor.

**Senaryo:** `Editor/Windows/TimeMachineWindow.cs:906-923` snapshots the world by looping every active entity and, inside that loop, every component type, calling `GetComponentBoxed` for each hit. For 10,000 entities x 3 components each that is 30,000 `GetMethod` lookups + 30,000 `object[1]` allocations + 60,000 boxes per snapshot, and the time machine takes a snapshot per recorded frame. The same shape runs in `Editor/HotReload/EntityStatePreserver.cs:55` on every script recompile and in `Editor/DataProviders/WorldDataProvider.cs:133` on inspector refresh. Compounding it, `ComponentStore.GetComponentTypes()` (line 176-179) returns `_storages.Keys` typed as `IEnumerable<Type>`, so the `foreach` at TimeMachineWindow.cs:911 — which sits inside the per-entity loop — boxes a `Dictionary.KeyCollection.Enumerator` once per entity: 10,000 more allocations per snapshot.

**Düzeltme:** Add `object GetBoxed(int entityIndex);` and `void SetBoxed(int entityIndex, object value);` to `IComponentStorage` (line 7-14) and implement them on `ComponentStorage<T>` as `=> _sparseSet.Get(entityIndex)` / `_sparseSet.Set(entityIndex, (T)value)`. Then `ComponentStore.GetComponentBoxed` is `_storages.TryGetValue(type, out var s) ? s.GetBoxed(entityIndex) : null` — no reflection, no object[], one box, and no IL2CPP stripping exposure. Also change `GetComponentTypes()` to return `Dictionary<Type, IComponentStorage>.KeyCollection` so `foreach` binds the struct enumerator instead of boxing.

## `sparseset-densecapacity-torn-on-alloc-failure`

**EnsureDenseCapacity replaces _dense before allocating _data, leaving _dense.Length > _data.Length permanently if the second allocation throws**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:217` |
| Kategori | bug · ecs-storage |
| Etki | No steady-state cost (growth happens O(log n) times over a storage's life). On failure: permanent unchecked OOB writes of sizeof(T) bytes on every subsequent Add, plus a leaked Persistent allocation from the constructor path. |
| Test | None. Tests/Runtime/ECS/Storage/SparseSetTests.cs:205 `GrowCapacity_Automatically` only exercises the success path (capacity 2 -> 10 elements). No test injects an allocation failure or asserts `_dense.Length == _data.Length` after growth. |

```csharp
            var newDense = new NativeArray<int>(newCapacity, _allocator);
            NativeArray<int>.Copy(_dense, newDense, _count);
            _dense.Dispose();
            _dense = newDense;

            var newData = new NativeArray<T>(newCapacity, _allocator);
            NativeArray<T>.Copy(_data, newData, _count);
            _data.Dispose();
            _data = newData;
```

**Sorun:** The method commits the `_dense` swap before it attempts the `_data` allocation, and `_dense.Length` is the sole capacity check for both arrays (`if (required <= _dense.Length) return;` at line 213). If `new NativeArray<T>(newCapacity, _allocator)` on line 222 throws — OutOfMemoryException on the large `sizeof(T) * newCapacity` request, or `Allocator.Invalid` on a default-constructed struct — the object is left with `_dense.Length == newCapacity` and `_data.Length` at the old, smaller value, and that state is permanent because every future `EnsureDenseCapacity` short-circuits on `_dense.Length`. The `_data` array is typically the largest of the two (sizeof(T) vs 4 bytes), so it is the one that fails first under memory pressure. The constructor at lines 23-25 has the mirror-image problem: if line 25 throws, the `_sparse` and `_dense` Persistent allocations from lines 23-24 leak with no reference left to free them.

**Senaryo:** A world grows a `ComponentStorage<Matrix4x4>` (64 B/entity) past 8 M entries under memory pressure. Line 217 allocates 32 MB for `newDense` and succeeds; line 222 requests 512 MB for `newData` and throws OOM. The exception propagates out of `Add`, the caller catches or logs it, and the game continues. Every subsequent `Add` now sees `required <= _dense.Length` and returns immediately, then executes lines 43-45: `_dense[_count]` is in range but `_data[_count] = component` writes past the end of the unchanged `_data` array — unchecked in a release player, so component writes silently corrupt whatever native block follows `_data`.

**Düzeltme:** Allocate both new arrays before mutating any field, so the method is exception-safe: `var newDense = new NativeArray<int>(newCapacity, _allocator); NativeArray<T> newData; try { newData = new NativeArray<T>(newCapacity, _allocator); } catch { newDense.Dispose(); throw; }` then copy, dispose, and assign both. Apply the same allocate-all-then-assign shape to the constructor (lines 23-25), wrapping the second and third allocations so the first two are freed on failure.

## `sparseset-ctor-memset-int-overflow`

**SparseSet constructor computes the MemSet byte count in int arithmetic and bypasses the MaxSparseCapacity cap**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:28` |
| Kategori | bug · ecs-storage |
| Etki | Construction only — a startup/first-use cost, one branch. Without it: an unbounded memset over the process address space. |
| Test | None. SparseSetTests only constructs with capacities of 2, 10, and 100. |

```csharp
            UnsafeUtility.MemSet(_sparse.GetUnsafePtr(), 0xFF, sparseCapacity * sizeof(int));
```

**Sorun:** `UnsafeUtility.MemSet(void*, byte, long size)` takes a `long`, but `sparseCapacity * sizeof(int)` is evaluated in `int` and only then widened. For `sparseCapacity >= 536_870_912` the product overflows to a negative int, which widens to a negative long and reaches the underlying memset as a huge unsigned size. Compare `EnsureSparseCapacity` at lines 197-199, which was explicitly hardened for exactly this class of bug (`long grown = (long)_sparse.Length * 3 / 2;` with an in-code comment about preventing int overflow) — the constructor on the same type was left in the old form. The constructor also does not enforce `MaxSparseCapacity` (line 185), which `EnsureSparseCapacity` throws on at line 191, so the constructor is a documented-cap bypass: `new SparseSet<T>(50_000_000, 1, Allocator.Persistent)` succeeds where growing to that size would have thrown.

**Senaryo:** `new SparseSet<TestComponent>(600_000_000, 16, Allocator.Persistent)` — the `NativeArray<int>` allocation of 2.4 GB succeeds on a 64-bit machine with headroom, then line 28 calls MemSet with `600_000_000 * 4 = 2_400_000_000` which wraps to `-1_894_967_296`. The memset runs with a size interpreted as ~1.8e19 bytes and destroys the process address space. Both the `SparseSet<T>` constructor and `ComponentStorage<T>(int sparseCapacity, int denseCapacity)` (line 22) are public and take these values unvalidated.

**Düzeltme:** Cast before multiplying and validate against the existing cap: `if (sparseCapacity < 0 || sparseCapacity > MaxSparseCapacity) throw new ArgumentOutOfRangeException(nameof(sparseCapacity)); if (denseCapacity < 0) throw new ArgumentOutOfRangeException(nameof(denseCapacity)); ... UnsafeUtility.MemSet(_sparse.GetUnsafePtr(), 0xFF, (long)sparseCapacity * sizeof(int));`

## `sparseset-addrange-unvalidated-inputs`

**AddRange does not validate array lengths or negative entity indices before unchecked writes**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:133` |
| Kategori | security · ecs-storage |
| Etki | Batch-add path only. The negative check folds into the existing max-scan (zero extra passes); the length check is one compare per AddRange call. Without them: OOB reads of sizeof(T) per element and OOB 4-byte writes per negative index. |
| Test | None at all — `AddRange` and `RemoveRange` have zero callers in Runtime/ and zero tests in Tests/. They are untested public API on an unsafe type. |

```csharp
        public void AddRange(NativeArray<int> entityIndices, NativeArray<T> components)
        {
            int addCount = entityIndices.Length;
            EnsureDenseCapacity(_count + addCount);

            int maxEntity = 0;
            for (int i = 0; i < addCount; i++)
                if (entityIndices[i] > maxEntity) maxEntity = entityIndices[i];

            EnsureSparseCapacity(maxEntity + 1);
```

**Sorun:** Three unvalidated inputs. (1) `components.Length` is never compared to `entityIndices.Length`; the loop reads `components[i]` for `i` up to `entityIndices.Length - 1` (line 149 and line 154), so a shorter `components` array produces unchecked OOB reads in release builds and stores garbage as component data. (2) `maxEntity` is seeded to 0 and only ever raised, so negative entries in `entityIndices` do not affect `EnsureSparseCapacity` — line 147 then evaluates `_sparse[entityIndex]` and line 155 writes `_sparse[entityIndex] = _count;` at a negative offset. (3) `_count + addCount` on line 136 is an unchecked int add that can overflow to a negative value, making `EnsureDenseCapacity` return immediately (`required <= _dense.Length`) and letting the loop write `_dense[_count]`/`_data[_count]` past the end.

**Senaryo:** A batch-spawn helper builds `entityIndices` from a pooled NativeArray it reuses at a larger length than the matching `components` array (a common off-by-one when a caller passes `Length` from the wrong buffer). In the Editor this throws IndexOutOfRangeException; in a release player `components[i]` reads past the end of the smaller array and the resulting garbage is committed into the component storage as if it were valid data, so entities spawn with nonsense transforms/health that no assertion catches. With a negative index in `entityIndices`, line 155 writes `_count` at `sparseBase + entityIndex*4`, corrupting the heap.

**Düzeltme:** Validate at the top: `if (components.Length < entityIndices.Length) throw new ArgumentException("components must be at least as long as entityIndices", nameof(components)); if ((long)_count + entityIndices.Length > int.MaxValue) throw new ArgumentException("AddRange would overflow count");` and seed the scan defensively so negatives are rejected: `for (int i = 0; i < addCount; i++) { int e = entityIndices[i]; if (e < 0) throw new ArgumentOutOfRangeException(nameof(entityIndices)); if (e > maxEntity) maxEntity = e; }` — the negative check rides on the scan loop that already exists, so it costs nothing extra.

## `sparseset-remove-denseindex-not-validated`

**SparseSet.Remove does not validate denseIndex against _count before the swap-and-pop (prior finding unit-04 #3, never triaged, still open)**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:54` |
| Kategori | bug · ecs-storage |
| Etki | One predicted compare per Remove call (sub-nanosecond). Without it: writes of sizeof(T) + 8 bytes into the dead region, or reads at `_dense[-1]`/`_data[-1]` when _count is 0. |
| Test | Tests/Runtime/ECS/Storage/SparseSetTests.cs:51-94 covers Remove for the consistent-state cases only (`Remove_ExistingElement_Success`, `Remove_NonExistingElement_ReturnsFalse`, `Remove_SwapAndPop_MaintainsDensity`). Nothing constructs an inconsistent sparse/count pair. |

```csharp
            int denseIndex = _sparse[entityIndex];
            int lastIndex = _count - 1;

            if (denseIndex != lastIndex)
            {
                int lastEntityIndex = _dense[lastIndex];
                _dense[denseIndex] = lastEntityIndex;
                _data[denseIndex] = _data[lastIndex];
                _sparse[lastEntityIndex] = denseIndex;
            }
```

**Sorun:** `denseIndex` is checked only for `< 0` (via the guard on line 51), never against `_count`. `GetRef` (lines 88-89) and `TryGet` (line 99) both check `denseIndex >= _count`; `Remove` does not, even though it is the method that *writes*. If `denseIndex >= _count`, line 61 writes into the dead region past the live elements, and line 62 writes `_sparse[lastEntityIndex]` using a `lastEntityIndex` read from beyond the live region. If `_count == 0` while a sparse entry is non-negative, `lastIndex` is -1 and lines 59/61 read `_dense[-1]` and `_data[-1]` — unchecked in release. Prior audit finding unit-04 #3 named exactly this; it appears in none of the three status-review documents, so it was never triaged as FIXED, OPEN, or by-design.

**Senaryo:** Reachable through two documented-unsafe surfaces this file exposes. (1) `GetSparsePtr()` (line 121) hands a writable `int*` to the sparse array straight into Burst jobs (Runtime/ECS/Jobs/EntityJobs.cs:30, 61-62, 101-103); any job that writes a sparse slot leaves `Remove` reading an index it did not validate. (2) A by-value copy of the struct (see `sparseset-struct-copy-double-free`) carries a stale `_count`: after the original set shrinks, `copy.Remove(e)` sees `_sparse[e] = 5` while the shared `_count` is 2, and writes `_data[5] = _data[1]` into the dead region — corrupting a slot that a subsequent `Add` will hand out as live component data.

**Düzeltme:** Match the guard `GetRef` already uses: after line 54, `if (denseIndex >= _count) return false;`. One compare against a value already in a register; the branch is never taken in correct operation and costs nothing measurable on the remove path.

## `sparseset-ensuredensecapacity-int-overflow`

**EnsureDenseCapacity still uses int arithmetic for the 1.5x growth and has no capacity cap (half of prior finding unit-04 #8, dropped from tracking)**

| | |
|---|---|
| Konum | `Runtime/ECS/Storage/SparseSet.cs:215` |
| Kategori | bug · ecs-storage |
| Etki | Growth path only, ~O(log n) times per storage. Past the threshold, insertion degrades from amortized O(1) to O(n) with a full reallocate-and-copy of both arrays per Add. |
| Test | Tests/Runtime/ECS/Storage/SparseSetTests.cs:205 `GrowCapacity_Automatically` grows from capacity 2 to 10 elements. No test approaches the overflow threshold and no test asserts the 1.5x growth factor, so the degradation would be silent. |

```csharp
            int newCapacity = Math.Max(required, _dense.Length * 3 / 2);
```

**Sorun:** `_dense.Length * 3` is an unchecked int multiply that wraps for `_dense.Length > 715,827,882`, producing a negative value; `Math.Max` then selects `required`, so growth degenerates from 1.5x-amortized to exactly-what-was-asked-for — every subsequent Add reallocates and copies both the dense and data arrays, turning amortized O(1) insertion into O(n) per insert. Unlike `EnsureSparseCapacity`, which was rewritten for exactly this (lines 195-199 carry an explicit comment: "Use long arithmetic to prevent int overflow when _sparse.Length is large, then clamp to MaxSparseCapacity"), the dense path has no `long` arithmetic and no `MaxDenseCapacity` analogue. Prior audit unit-04 #8 said "The same pattern exists in EnsureDenseCapacity (line 195)" but the fix landed only on the sparse half, and SecurityReports/2026-05-22-medium-status-review.md OPEN row 3 names only `EnsureSparseCapacity` — so the dense half is no longer tracked anywhere.

**Senaryo:** Requires ~7.2x10^8 elements in a single component storage (2.9 GB of dense indices plus sizeof(T) x 7.2x10^8 of data), which the 1,048,576 entity-index ceiling makes unreachable through `EntityManager` today. It is reachable directly: `SparseSet<T>` and `ComponentStorage<T>(sparseCapacity, denseCapacity)` are public and take arbitrary capacities, and `Reserve(int)` (line 127) forwards straight to `EnsureDenseCapacity`. Once past the threshold, every Add reallocates and memcopies the full ~3 GB pair.

**Düzeltme:** Mirror the sparse fix exactly: `long grown = (long)_dense.Length * 3 / 2; long target = Math.Max(required, grown); int newCapacity = (int)Math.Min(target, MaxDenseCapacity);` with a `MaxDenseCapacity` constant and a throw when `required` exceeds it, matching lines 191-199.

## `reactive-clear-skips-onremove`

**ReactiveComponentStorage.Clear() and Dispose() wipe all components without firing a single OnRemove callback**

| | |
|---|---|
| Konum | `Runtime/ECS/Reactive/ReactiveComponentStorage.cs:157` |
| Kategori | api-hazard · ecs-systems |
| Etki | Correctness only; `Clear` is not a per-frame path. If notification is added, cost is O(live entities × subscribers) at clear time. |
| Test | None. `ReactiveComponentStorageTests` never calls `Clear`. `ReactiveSystemPerformanceTests` calls `storage.Clear()` in `SetUp` (lines 36, 94, 116) purely as benchmark reset and asserts nothing. `ReactiveEntityManagerTests.Dispose_CleansUpAllStorages` (line 126) checks a *different, freshly constructed* manager's storage count, so it does not test that Dispose notified anyone. |

```csharp
        public void Clear()
        {
            _storage.Clear();
        }
```

**Sorun:** `Clear` forwards straight to `ComponentStorage<T>.Clear` → `SparseSet<T>.Clear` (SparseSet.cs:165-174), which resets every `_sparse` slot to -1 and sets `_count = 0`. Every subscriber's view of the world is invalidated and none of them is told. `Dispose()` (lines 162-168) has the same hole and additionally clears the callback lists after disposing the storage. `_notifyDepth` is also not reset by either method, so if `Clear` is ever reached from inside a notification the depth counter stays elevated.

**Senaryo:** A scene-reset routine calls `storage.Clear()` (or `ReactiveEntityManager.Dispose()`) to drop all Health components. UI bound via `OnRemove` never receives a removal event, so every health bar stays on screen showing the last value; the view-side lookup then queries `storage.Get(entityIndex)`, which throws `InvalidOperationException: Entity N does not exist in sparse set` (SparseSet.cs:76). The reactive contract — 'every removal raises OnRemove' — is broken by the framework's own API.

**Düzeltme:** Either fire `NotifyRemove` for each live entity before clearing (`_storage.GetEntityIndices(reusableList)` at ComponentStorage.cs:78 gives a non-allocating enumeration), or rename to `ClearSilent()` and document the contract explicitly. Reset `_notifyDepth = 0` in both `Clear` and `Dispose`.

## `reactive-remove-notifies-before-mutating`

**ReactiveComponentStorage.Remove fires OnRemove before the storage write, causing reentrant double-notification and a wrong return value**

| | |
|---|---|
| Konum | `Runtime/ECS/Reactive/ReactiveComponentStorage.cs:61` |
| Kategori | bug · ecs-systems |
| Etki | Up to 8× duplicate callback invocations per reentrant removal plus one `Debug.LogError`; wrong bool returned to the caller. No per-frame cost in the non-reentrant case. |
| Test | `ReactiveComponentStorageTests.Remove_TriggersOnRemoveCallback` (lines 52-72) and `Remove_ReturnsFalse_WhenEntityNotExists` (74-88) both use non-reentrant handlers, so neither the duplicate-notification nor the wrong-return-value path is covered. No test asserts what `Contains` returns inside an OnRemove callback. |

```csharp
        public bool Remove(int entityIndex)
        {
            if (!_storage.Contains(entityIndex))
                return false;

            var component = _storage.Get(entityIndex);
            NotifyRemove(entityIndex, component);
            return _storage.Remove(entityIndex);
        }
```

**Sorun:** Notification happens on line 67, mutation on line 68 — the opposite order from `Add` (line 54 mutates, line 57 notifies) and `Set` (line 81 mutates, line 82 notifies). During an `OnRemove` callback the component is therefore still present: `Contains(entityIndex)` returns `true` and `Get(entityIndex)` succeeds. The re-entrancy guard (`MaxNotifyDepth = 8`, line 17) turns what would be a stack overflow into 8 duplicate notifications plus an error log.

**Senaryo:** A cascade-cleanup handler subscribed via `SubscribeOnRemove` defensively calls `storage.Remove(entityIndex)` for the same index (a normal idempotent-cleanup idiom). `Remove(5)` → `Contains` true → `NotifyRemove` → handler → inner `Remove(5)` → `Contains` STILL true because line 68 has not executed → `NotifyRemove` again → … recursing until `_notifyDepth` reaches 8, at which point line 119 logs `Max notify depth (8) exceeded in OnRemove`. The handler has now run 8 times for one logical removal. As the stack unwinds, the innermost `_storage.Remove(5)` returns `true` and all 7 outer frames return `false` — so the public `Remove` reports failure even though the component was removed. Any caller branching on that bool (e.g. `ReactiveEntityManager.RemoveReactiveComponent`, line 69, which returns it verbatim) takes the wrong path.

**Düzeltme:** Mutate first, then notify: `var component = _storage.Get(entityIndex); if (!_storage.Remove(entityIndex)) return false; NotifyRemove(entityIndex, component); return true;`. This also makes `Contains(entityIndex)` correctly report `false` inside OnRemove handlers, matching the post-state semantics of OnAdd/OnChange.

## `systembase-per-frame-exception-logging`

**SystemBase.Update logs a full exception every frame with no rate limiting or auto-disable**

| | |
|---|---|
| Konum | `Runtime/ECS/Systems/SystemBase.cs:40` |
| Kategori | bug · ecs-systems |
| Etki | ~60 stack-trace formatting operations/second (60-240 KB/s of string garbage) and 60 console writes/second per continuously-failing system, indefinitely. |
| Test | None. No test drives a `SystemBase` whose `OnUpdate` throws. |

```csharp
            try
            {
                OnUpdate(deltaTime);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
```

**Sorun:** The catch is the right call for isolation (and is what `Documentation~/ECS.md:238` advertises), but a system that fails deterministically fails every frame. `Debug.LogException` formats and allocates the full stack-trace string on each call, appends an entry to Unity's console ring buffer, and — in the Editor — triggers a console repaint. Nothing marks the system as faulted, disables it, or collapses repeats.

**Senaryo:** A system throws `NullReferenceException` in `OnUpdate` every frame — for example any `SystemBase` built through `ECSBuilder` (see ecsbuilder-never-injects-systems), where `EntityManager` is null. At 60 fps this produces 60 exceptions/second: ~60 stack-trace strings (typically 1-4 KB each, so 60-240 KB/s of garbage) plus 60 console entries. Within a minute the Editor console holds 3,600 identical entries and becomes unresponsive; in a player build with a log-forwarding service this floods the network sink.

**Düzeltme:** Track consecutive failures: `_consecutiveFailures++;` in the catch, log fully for the first N (e.g. 3), then log a single "suppressing further exceptions from <type>" message and either stop logging or set `_disposed`-style faulted state that skips `OnUpdate`. Reset the counter on a successful frame.

## `ecsbuilder-initial-capacity-silently-ignored`

**ECSBuilder.WithInitialEntityCapacity is a no-op — the value is never passed to EntityManager (prior unit-06 #9, still unfixed and untracked)**

| | |
|---|---|
| Konum | `Runtime/ECS/World/ECSBuilder.cs:52` |
| Kategori | api-hazard · ecs-systems |
| Etki | Startup/growth-only: 7 NativeArray grow-and-copy cycles instead of 0 for a 100k-entity world (~1 MB transient Allocator.Persistent + ~600 KB memcpy). The real severity is the silent API lie. |
| Test | `BridgeIntegrationTests.cs:31` calls `WithInitialEntityCapacity(128)` but never asserts anything about capacity, so the no-op is invisible. No other test touches it. |

```csharp
            var entities = new EntityManager();
            var scheduler = new SystemScheduler();
            var bus = _eventBus ?? new EventBus();
```

**Sorun:** `_initialEntityCapacity` is declared on line 11 and assigned by `WithInitialEntityCapacity` (lines 14-18) but never read. `EntityManager` has an `EntityManager(int initialCapacity)` constructor (EntityManager.cs:35) that the builder ignores in favour of the parameterless one, which hard-codes `InitialCapacity = 1024` (EntityManager.cs:20, 33). Prior audit `SecurityReports/unit-06-ecs-reactive-world.md:142-153` reported this as finding #9; it does not appear in any FIXED, PARTIAL, OPEN or OPEN-BY-DESIGN list in the 2026-05-22 status reviews (grep for `initialEntityCapacity` across SecurityReports/ returns nothing), so it was silently dropped rather than triaged.

**Senaryo:** `new ECSBuilder().WithInitialEntityCapacity(100_000).Build()` then spawn 100k entities. The capacity request is discarded, so `EntityManager.EnsureCapacity` (EntityManager.cs:300-323) doubles from 1024 and performs 7 grow cycles (1024→2048→…→131072), each allocating a fresh `NativeArray<int>` + `NativeArray<byte>` from `Allocator.Persistent` and running two `NativeArray.Copy` calls, then disposing the old pair. Total ~1 MB of transient Allocator.Persistent traffic and 7 full array copies that the API explicitly promised to avoid. `BridgeIntegrationTests.cs:31` already calls `.WithInitialEntityCapacity(128)` and is silently getting 1024.

**Düzeltme:** `var entities = new EntityManager(_initialEntityCapacity);`. Also validate `capacity > 0` in `WithInitialEntityCapacity`, and consider threading a component-storage capacity through to `new ComponentStore(...)` (EntityManager.cs:42 also hard-codes the defaults 1024/256).

## `scheduler-editor-stopwatch-alloc-per-system-per-frame`

**SystemScheduler allocates a Stopwatch per system per frame in the Editor, perturbing the numbers it is measuring**

| | |
|---|---|
| Konum | `Runtime/ECS/World/SystemScheduler.cs:56` |
| Kategori | allocation · ecs-systems |
| Etki | ~40 bytes per system per phase-run per frame, Editor-only. 20 systems × 3 phases @60 fps ≈ 144 KB/s. Player builds are unaffected (the code is `#if UNITY_EDITOR`), but this is precisely where developers read their perf numbers. |
| Test | None. No test exercises `SystemScheduler.Update/LateUpdate/FixedUpdate` or reads `LastExecutionTimes`. |

```csharp
#if UNITY_EDITOR
                var sw = System.Diagnostics.Stopwatch.StartNew();
#endif
                systems[i].Update(deltaTime);
#if UNITY_EDITOR
                sw.Stop();
                _lastExecutionTimes[systems[i].GetType()] = sw.Elapsed.TotalMilliseconds;
#endif
```

**Sorun:** `Stopwatch` is a reference type, so `Stopwatch.StartNew()` heap-allocates a new instance on every iteration of the inner loop — once per system, per phase, per frame. On top of that, `systems[i].GetType()` plus a `Dictionary<Type,double>` insert runs per system per frame; `Type` has no `IEquatable<Type>`, so the default comparer goes through virtual `RuntimeType.GetHashCode()`/`Equals()`. The keys are the system's runtime `Type`, so two instances of the same system type registered in different phases silently overwrite each other's timing, and `_lastExecutionTimes` is never cleared in `Dispose()` (lines 67-75) so `SystemProfilerWindow.cs:992` and `SystemProfilerHook.cs:110` can read timings for systems that no longer exist.

**Senaryo:** Editor play mode with 20 systems across 3 phases at 60 fps: 20 × 3 × 60 = 3,600 `Stopwatch` allocations/s ≈ 144 KB/s (Stopwatch = 16 B header + 2 longs + padded bool ≈ 40 B), plus 3,600 `QueryPerformanceCounter` pairs and 3,600 dictionary hash inserts. The measurement overhead lands inside the same frame budget the profiler window is reporting, so short systems' reported times are dominated by the instrumentation.

**Düzeltme:** Replace the per-iteration `Stopwatch` with a single reusable `Stopwatch` field (`_sw.Restart()` … `_sw.Stop()`), or better, use `Stopwatch.GetTimestamp()` deltas (a static long, zero allocation) and convert with `Stopwatch.Frequency`. Precompute the `Type` key once at `AddSystem` time into a parallel array so `GetType()` and the dictionary hash are off the per-frame path entirely. Clear `_lastExecutionTimes` in `Dispose()`.

## `scheduler-addsystem-after-initialize`

**SystemScheduler.AddSystem accepts registrations after Initialize(), producing a permanently inert system (prior unit-06 #10, confirmed still OPEN)**

| | |
|---|---|
| Konum | `Runtime/ECS/World/SystemScheduler.cs:22` |
| Kategori | api-hazard · ecs-systems |
| Etki | Functional: the late-added system contributes 0% of its intended work. No perf cost. |
| Test | None. No test calls `SystemScheduler.AddSystem`. |

```csharp
        public void AddSystem(ISystem system, UpdatePhase phase = UpdatePhase.Update)
        {
            _systemsByPhase[(int)phase].Add(system);
            _allSystems.Add(system);
        }
```

**Sorun:** No `_initialized` check and no null check. `Initialize()` guards on `if (_initialized) return;` (line 30), so a system added afterwards never receives `Initialize()`. For `SystemBase` subclasses the consequence is silent: `Update` returns immediately at SystemBase.cs:39 (`if (!_initialized || _disposed) return;`). For a hand-written `ISystem` the consequence is worse — `Update` runs on an uninitialized object. A null argument is accepted and throws `NullReferenceException` on the first `RunPhase` iteration (line 59), inside the loop, with no indication of which registration was bad. Prior audit unit-06 #10 is correctly listed as still OPEN in `SecurityReports/2026-05-22-low-status-review.md:73`; this confirms it against current code.

**Senaryo:** `world.Initialize(); world.SystemScheduler.AddSystem(new DamageSystem());` — a natural pattern for a feature enabled after boot. `DamageSystem` is added to `_allSystems` and to phase Update, appears in `SystemProfilerWindow`'s system list, and never executes a single frame because `SystemBase._initialized` stays false. No warning is logged.

**Düzeltme:** `if (system == null) throw new ArgumentNullException(nameof(system));` and either `if (_initialized) system.Initialize();` right after adding (late-init support), or `if (_initialized) throw new InvalidOperationException("Systems cannot be added after SystemScheduler.Initialize().")`. Silently accepting and never running is the worst of the three.

## `world-current-unreachable-for-builder-worlds`

**World.Current has an internal setter and ECSBuilder never assigns it, so builder-created worlds are invisible to every Editor tool**

| | |
|---|---|
| Konum | `Runtime/ECS/World/World.cs:21` |
| Kategori | api-hazard · ecs-systems |
| Etki | Tooling/DX, no runtime cost. Affects 100% of worlds not created by GameBootstrapper — which is every world in the repo's own tests and benchmarks (BridgeIntegrationTests.cs:30, MassEntityTests.cs:21, ParallelCommandBufferTests.cs:21, ECSBenchmarks.cs:17, BenchmarkRunner.cs:284/311/344). |
| Test | None. No test reads or writes `World.Current`. |

```csharp
        public static World Current
        {
            get => _current;
            internal set => _current = value;
        }
```

**Sorun:** After the `9ac6714 fix(ECS): protect World.Current setter and add volatile` change, the only assignment sites in the whole package are `Runtime/Bootstrap/GameBootstrapper.cs:281` and `:413`. `ECSBuilder.Build()` (ECSBuilder.cs:50-65) returns a fully-formed `World` and never touches `Current`, and external assemblies cannot set it. `World.Dispose()` (World.cs:121-122) additionally nulls it when `_current == this` with no way for the application to restore it. The prior audit tracked this as PARTIAL (`SecurityReports/2026-05-22-medium-status-review.md:52`: "Setter internal yapıldı, alan volatile; ama hâlâ değiştirilebilir global") but recorded only the mutability half, not the unreachability half this created.

**Senaryo:** A project that builds its own world (`var world = new ECSBuilder().Build();`) instead of using `GameBootstrapper` gets `World.Current == null` permanently. Every Editor tool that gates on it goes dark with no diagnostic: `WorldDataProvider.IsAvailable` (Editor/DataProviders/WorldDataProvider.cs:31), `BusDataProvider` (:40), `SystemProfilerWindow` (:990, :1044), `SystemProfilerHook` (:108), `EntityQueryTesterWindow` (:157, :635, :653), `TimeMachineWindow` (:811, :852, :879), `StradaEntityInspectorWindow` (:1007…), `StradaDashboardWindow` (:678), `EntityStatePreserver` (:21, :92). Symmetrically, in a two-world setup, disposing world B nulls `Current` and leaves live world A unreachable with no public way to re-register it.

**Düzeltme:** Give `ECSBuilder.Build()` an explicit, opt-in registration — e.g. `public ECSBuilder AsCurrent()` setting a flag that `Build()` honours — or add `public static void SetCurrent(World world)` with an explicit-intent name, or expose `World.MakeCurrent()` as a public instance method. Also make `Dispose` restore rather than blank: keep a small stack of registered worlds so disposing the top one re-exposes the previous.

## `reactive-perf-tests-measure-time-not-allocation`

**Reactive performance tests measure wall-clock only, so the per-write array allocation and the reactive-vs-raw delta are unasserted**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/ReactiveSystemPerformanceTests.cs:25` |
| Kategori | test-gap · ecs-systems |
| Etki | Test-only. Currently 0 of the ~3.2 MB of garbage produced by `Benchmark_ReactiveChange_10k` (100,000 × 32 B arrays across 10 measurement iterations) is detected. |
| Test | This IS the coverage gap. Combined with the total absence of test files for SystemBase, SystemScheduler, World and ECSBuilder, the entire Runtime/ECS/World and Runtime/ECS/Systems surface is untested. |

```csharp
            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    storage.Add(i, new TestComponent { Value = i });
                }
            })
            .WarmupCount(1)
            .MeasurementCount(5)
```

**Sorun:** All four benchmarks (`Benchmark_ReactiveAdd_10k` line 18, `Benchmark_ReactiveChange_10k` line 45, `Benchmark_MultipleSubscribers_10k` line 72, `Benchmark_NonReactive_Baseline_10k` line 103) use `Measure.Method` with no `.GC()` sample group, no `Measure.ProfilerMarkers`, no `GC.GetTotalMemory` delta, and — critically — no `Assert` of any kind. The baseline benchmark exists specifically to be compared against the reactive ones but nothing compares them, so the reactive layer's overhead is recorded in a report a human must read rather than enforced. `README.md:41` and `Documentation~/Benchmarks.md:401-403` publish absolute numbers ("Query Iteration | 0 bytes", "All hot paths are allocation-free after initialization") that no test in the repo can falsify.

**Senaryo:** The `.ToArray()` snapshot added to `NotifyAdd`/`NotifyRemove`/`NotifyChange` (lines 105/126/147) introduced one heap allocation per component write. All four benchmarks still pass, because they assert nothing. The same is true of the per-frame closure allocation in `SystemBase<T1..T8>` — `ECSPerformanceTests` benchmarks `EntityManager.ForEach` directly (lines 141-287) and never instantiates a `SystemBase`, so the framework's own per-frame delegate allocation is outside every measured path.

**Düzeltme:** Add `.GC()` to the `Measure.Method` chains (Unity Performance Testing supports a GC allocation sample group) and add hard assertions: `Assert.Less(reactiveAddNs, baselineAddNs * 3)` for the overhead ratio, and a dedicated allocation test using `GC.GetTotalMemory(true)` around N reactive Sets asserting `bytes / N < 8`. Separately, add a `SystemBaseAllocationTests` fixture that instantiates a `SystemBase<Position, Velocity>`, injects an EntityManager, and asserts zero allocation across 100 `Update` calls — that test does not exist and would have caught both this and the SystemBase closure finding.

## `benchmarkpersistence-prefix-path-check-bypassable`

**BenchmarkPersistence.ValidatePath uses a culture-sensitive StartsWith with no directory separator, so sibling directories pass the containment check**

| | |
|---|---|
| Konum | `Editor/Benchmarking/BenchmarkPersistence.cs:26` |
| Kategori | security · editor-tools |
| Etki | Startup/on-demand only. Editor-only, so the blast radius is a developer machine, but DeleteSession can remove files outside the project. |
| Test | NONE — no editor tests exist. Prior audit unit-15 reviewed BenchmarkPersistence for JSON schema validation (Finding 6) but did not examine ValidatePath. |

```csharp
            if (!fullPath.StartsWith(projectRoot))
                throw new InvalidOperationException($"Path outside project: {fullPath}");
```

**Sorun:** Two defects in one line. (1) No trailing directory separator is appended to projectRoot, so `/Users/me/MyProject-backup/x.json` passes containment against project root `/Users/me/MyProject` — the classic prefix-match escape. (2) `string.StartsWith(string)` uses CurrentCulture comparison, not Ordinal, so culture-specific collation (and ignorable characters) can affect the result; path containment must always be ordinal. ValidatePath guards SaveSession, LoadSession, DeleteSession and ExportSession, and DeleteSession performs `File.Delete(path)`.

**Senaryo:** ExportSession or DeleteSession is called with a path under a sibling directory sharing the project-root prefix (e.g. a `MyProject-backup` or `MyProject.old` folder next to the project). ValidatePath accepts it, and DeleteSession deletes a file outside the project. The path reaches these APIs from BenchmarkRunnerWindow.LoadBaseline via `EditorUtility.OpenFilePanel` (line 630), which lets the user pick any location on disk.

**Düzeltme:** Normalise both sides with a trailing separator and compare ordinally: `var root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!fullPath.StartsWith(root, StringComparison.Ordinal)) throw ...`. Wrap Path.GetFullPath in a try/catch so malformed input surfaces as the same InvalidOperationException rather than an ArgumentException.

## `benchmark-query-closure-inside-timed-region`

**ECS_ComponentQuery allocates a closure and a delegate inside the timed region on every measured iteration**

| | |
|---|---|
| Konum | `Editor/Benchmarking/BenchmarkRunner.cs:352` |
| Kategori | allocation · editor-tools |
| Etki | 2 heap allocations (~64 bytes) per measured iteration inside the timed window; 2,000 allocations across the default 1,000-iteration run, all attributed to query cost. |
| Test | NONE — no editor tests exist. |

```csharp
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                int count = 0;
                world.EntityManager.ForEach<TestComponent>((int entityIndex, ref TestComponent c) => count++);
                sw.Stop();
```

**Sorun:** `int count = 0;` is declared inside the loop body *after* `sw.Restart()`, and the lambda captures it. C# therefore emits a display-class instantiation plus a delegate allocation per loop iteration, both inside the timing window. Because the lambda captures a local, the delegate cannot be cached by the compiler's static-lambda cache. So every 'query performance' sample includes two heap allocations before the query even begins, and those allocations also pollute the memoryAfter-memoryBefore delta.

**Senaryo:** ECS_ComponentQuery runs 1,000 iterations over 1,000 entities. Each measured sample includes 1 display-class + 1 delegate allocation (~64 bytes) that has nothing to do with query iteration cost. The resulting per-entity nanosecond figure — the number the README publishes as '6.6ns/entity' — is inflated by allocation cost the framework does not actually incur in production code paths, contradicting Documentation~/Benchmarks.md's claim that 'Results represent real production code paths'.

**Düzeltme:** Hoist `int count = 0;` and the delegate out of the loop (declare the counter as a field or use a non-capturing struct-based iterator), and re-zero it before `sw.Restart()`. Better: measure with the non-allocating query API if one exists, since the README's '0 bytes' allocation claims imply one does.

## `mediator-registry-method-does-not-exist`

**EntityMediatorInspector's primary mediator lookup is dead code: MediatorRegistry has no GetMediatorForView and no static Instance**

| | |
|---|---|
| Konum | `Editor/Inspectors/EntityMediatorInspector.cs:89` |
| Kategori | bug · editor-tools |
| Etki | Startup/per-repaint only in terms of cost; correctness impact is permanent — the documented registry path never executes. |
| Test | NONE. Prior audit unit-14 flagged the substring-match pattern as a risk but did not establish that the primary path is unreachable. |

```csharp
                        var getMediatorMethod = registryType.GetMethod("GetMediatorForView",
                            BindingFlags.Public | BindingFlags.Instance);
```

**Sorun:** I verified against the runtime: `grep -rn "GetMediatorForView" Runtime` returns zero hits, and MediatorRegistry's public surface (Runtime/Sync/MediatorRegistry.cs) is ActiveCount, Create<TMediator,TView>, Release<TMediator,TView>, SyncAll, ReleaseAll, Dispose — no GetMediatorForView. The `Instance` property the code looks up at line 82-83 lives on a nested `MediatorPoolInstance` class (MediatorRegistry.cs:84), not on MediatorRegistry, so `registryType.GetProperty("Instance", Public|Static)` returns null and the branch is abandoned before line 89 is even reached. Execution always falls through to the fallback at lines 99-108, which scans every field of the View type and matches on `field.FieldType.Name.Contains("Mediator")` — a substring match that will happily bind to an unrelated field such as `_mediatorConfig` or `_mediatorPrefab`.

**Senaryo:** A View stores its mediator in a field typed `EntityMediator<Foo>` and also holds `MediatorSettings _mediatorSettings`. Field iteration order is unspecified; if _mediatorSettings comes first, `field.FieldType.Name.Contains("Mediator")` matches it and FindMediatorForView returns the settings object. The inspector then reflects for Bindings/IsBound/SyncBindings on the wrong object, finds nothing, and the panel silently renders as "Not Bound" with no bindings — the developer concludes their mediator is broken.

**Düzeltme:** Either add a real `GetMediatorForView(View)` to IMediatorRegistry/MediatorRegistry and a static accessor, or delete the dead registry branch. For the fallback, require an exact assignability test (`typeof(EntityMediator<>).IsAssignableFrom(...)` / a marker interface) instead of a `Name.Contains("Mediator")` substring match, and return null rather than a first-substring-match guess.

## `gamebootstrapper-editor-dead-menu-item`

**GameBootstrapperConfigEditor's 'View Dependency Graph' button invokes a menu path that does not exist**

| | |
|---|---|
| Konum | `Editor/Inspectors/GameBootstrapperConfigEditor.cs:206` |
| Kategori | bug · editor-tools |
| Etki | On-click only. The button is inert. |
| Test | NONE — no editor tests exist. |

```csharp
                EditorApplication.ExecuteMenuItem("Strada/Dependency Graph");
```

**Sorun:** I enumerated every [MenuItem] attribute in Editor/. The Dependency Graph window is registered at `[MenuItem(MenuRoot + "Debugger/Dependency Graph %#d", priority = 100)]` (Editor/StradaEditorMenus.cs:100) with `MenuRoot = "Strada/"` (line 16), i.e. the real path is 'Strada/Debugger/Dependency Graph'. There is no 'Strada/Dependency Graph'. (The sibling call on line 201, 'Strada/Dashboard', does resolve — StradaEditorMenus.cs:18 registers 'Strada/Dashboard %#&d' and Unity strips the hotkey suffix.) ExecuteMenuItem returns false and logs a 'menu not found' error.

**Senaryo:** Developer selects their GameBootstrapperConfig asset and clicks 'View Dependency Graph'. Nothing opens; Unity logs 'ExecuteMenuItem: Menu Strada/Dependency Graph not found'.

**Düzeltme:** Call the window class directly — `Graph.DependencyGraphWindow.ShowWindow();` — as StradaDashboardWindow already does at lines 491 and 730, rather than round-tripping through a stringly-typed menu path that no compiler or test can validate.

## `busdebugger-bookmark-dictionary-leak`

**BusDebuggerWindow._bookmarkedEntries retains evicted MessageLogEntry objects (and their boxed payloads) forever**

| | |
|---|---|
| Konum | `Editor/Windows/BusDebuggerWindow.cs:60` |
| Kategori | allocation · editor-tools |
| Etki | Unbounded growth of one dictionary entry plus one retained MessageLogEntry and boxed payload per bookmark, for the lifetime of the window. |
| Test | NONE. ToggleBookmarkAt/IsBookmarkedAt are internal test seams with no tests behind them. |

```csharp
        private Dictionary<MessageLogEntry, bool> _bookmarkedEntries = new Dictionary<MessageLogEntry, bool>();
```

**Sorun:** MessageLogEntry is a class (DataModels.cs:139) with no GetHashCode/Equals override, so this dictionary keys on reference identity — which is correct for tracking bookmarks across refreshes, since GetLogEntriesNonAlloc re-adds the same object references. But BusDataProvider trims its backing list with `_logEntries.RemoveRange(0, excess);` (BusDataProvider.cs:202) once MaxLogEntries (1000) is exceeded, and nothing removes the corresponding keys from _bookmarkedEntries. The dictionary is only cleared on play-mode transitions (lines 147-148, 155-156) and ClearLog (line 1479). Each retained key holds a MessageLogEntry whose `Payload` is a boxed message struct, so the whole payload graph is kept alive. The parallel `_bookmarkedIndices` HashSet<int> is worse: it stores *list indices*, which are invalidated by every RemoveRange shift, so it silently points at unrelated entries.

**Senaryo:** Developer bookmarks 40 interesting messages during a long session while thousands of messages flow through the 1000-entry ring. All 40 bookmarked entries fall out of the provider's list within seconds but remain rooted by _bookmarkedEntries, along with their boxed payloads. Meanwhile the 'Bookmarked' filter (line 672) finds none of them in _displayedEntries, so the bookmarks appear to have vanished while still leaking memory.

**Düzeltme:** Prune _bookmarkedEntries in RefreshDisplayedEntries: drop any key not present in the refreshed _displayedEntries. Delete _bookmarkedIndices entirely — it is index-based and cannot survive list shifts; ToggleBookmark/IsBookmarked already work off the entry reference. Alternatively key bookmarks on a stable id assigned at log time.

## `about-window-stale-version-and-dead-doc-links`

**StradaAboutWindow shows version 1.0.0-alpha.1 and its documentation links can never resolve, because Documentation~ is excluded from the AssetDatabase**

| | |
|---|---|
| Konum | `Editor/Windows/StradaAboutWindow.cs:45` |
| Kategori | bug · editor-tools |
| Etki | Window-open only. Wrong version displayed and six dead documentation links. |
| Test | NONE — no editor tests exist. |

```csharp
            if (!File.Exists(PackageJsonPath)) return;
```

**Sorun:** Two issues. (1) PackageJsonPath is the *virtual* path "Packages/com.strada.core/package.json". That resolves on disk only when the package is embedded under the project's Packages/ folder; when installed from a registry or a local path the real files live under Library/PackageCache/com.strada.core@<hash>/, so File.Exists returns false, LoadPackageInfo silently returns, and the window renders the hardcoded fallback `private string _version = "1.0.0-alpha.1";` (line 18) — while package.json actually says "version": "2.0.0-alpha.1". (2) OpenDocumentation (lines 229-239) does `AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.strada.core/Documentation~/DI.md")`. Unity excludes any folder whose name ends in `~` from the AssetDatabase by design, so LoadAssetAtPath always returns null for every Documentation~ path; only the RevealInFinder fallback can fire, and only when File.Exists succeeded.

**Senaryo:** A user installs com.strada.core 2.0.0-alpha.1 via UPM and opens Strada → About. The window reports 'Version 1.0.0-alpha.1'. They click 'DI Container Guide'; File.Exists on the virtual path fails, so nothing happens at all — no ping, no reveal, no error.

**Düzeltme:** Read the version through UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(StradaAboutWindow).Assembly), which returns the resolved on-disk `resolvedPath` and `version` regardless of install method, and build documentation paths from PackageInfo.resolvedPath. Drop the AssetDatabase.LoadAssetAtPath call for Documentation~ paths — use Application.OpenURL("file://" + fullPath) or EditorUtility.RevealInFinder on the resolved path. Also drop the hardcoded fallback string, or at minimum keep it in sync with package.json.

## `playerloop-leaked-on-bootstrap-failure`

**PlayerLoop.Initialize() is never undone when bootstrap fails — Shutdown() is gated on _isInitialized, which a failed init never sets**

| | |
|---|---|
| Konum | `Runtime/Bootstrap/GameBootstrapper.cs:373` |
| Kategori | bug · modules-bootstrap |
| Etki | Startup only; leaves 4 empty PlayerLoopSystem delegate invocations per frame permanently installed plus an unrecoverable inconsistent PlayerLoop static state. |
| Test | NO COVERAGE. No test file exists for GameBootstrapper or PlayerLoop. Tests/Runtime/Modules/ contains only legacy ModuleRegistry tests; there is no Tests/Runtime/Bootstrap directory. |

```csharp
            if (!_isInitialized)
            {
                return;
            }
```

**Sorun:** `Awake()` calls `PlayerLoop.Initialize()` at line 127 — before any initialization phase runs, and unconditionally once a config is assigned. `PlayerLoop.Shutdown()` is called only at line 383, inside `Shutdown()`, which early-returns at lines 373-376 when `_isInitialized` is false. Every failure path sets/leaves `_isInitialized = false`: `HandleInitializationError` line 368 sets it false explicitly, and phases 1-5 all `yield break` to it. So: bootstrap fails -> OnDestroy -> Shutdown() -> immediate return -> the four Strada PlayerLoopSystem entries stay installed in Unity's global player loop for the rest of the process, and `PlayerLoop._initialized` stays true so a later bootstrapper's `Initialize()` is a no-op that silently keeps the stale loop. `HandleInitializationError` correctly calls `DisposeResources()` at line 365 — the prior audit's unit-07 Finding 4, left PARTIAL by the 2026-05-22 status review, is otherwise genuinely fixed (see domainNotes) — but PlayerLoop was left out of that rollback.

**Senaryo:** Any Phase 2 failure (today: a single Inspector-configured ServiceEntry, per the AmbiguousMatchException finding; generally: a module Install throwing, or ContainerBuilder.Build detecting a circular dependency). `PlayerLoop.Initialize()` has already run in Awake. HandleInitializationError runs DisposeResources — which disposes modules, systemRunner, timerService, world, container and nulls the statics — but never calls PlayerLoop.Shutdown(). The developer then fixes the module and calls the public `Initialize()` (line 439) for a retry: BuildContainer succeeds, but `PlayerLoop.Initialize()` is never re-run (it is only in Awake) and `PlayerLoop._initialized` is still true from the first attempt, so the state is inconsistent between the two static classes. In a player build the modified loop simply persists forever with no owner.

**Düzeltme:** Move `PlayerLoop.Shutdown()` from `Shutdown()` (line 383) into `DisposeResources()` so it runs on both the success-teardown and the initialization-failure path, guarded by a `_playerLoopInitialized` instance flag set next to the `PlayerLoop.Initialize()` call in Awake. Combine with the surgical-removal fix from the `playerloop-shutdown-nukes-global-loop` finding so that this additional call site does not start wiping the global loop on every failed boot.

## `duplicate-module-double-registers-systems`

**A ModuleConfig listed twice has its systems instantiated and ticked twice while Install/Initialize run once — the two module enumerations disagree on deduplication**

| | |
|---|---|
| Konum | `Runtime/Bootstrap/GameBootstrapper.cs:284` |
| Kategori | bug · modules-bootstrap |
| Etki | Per-frame: N duplicate ISystem.Update calls every frame for every system in the duplicated module, for the life of the process — a 2x simulation cost and 2x behavioral effect for that module. |
| Test | NO COVERAGE. No test constructs a GameBootstrapperConfig or a SystemRunner. GameBootstrapperConfig.Validate's duplicate check (the only thing that would catch this) has no test either. |

```csharp
            _systemRunner.AddSystemsFromConfigs(_gameConfig.GetEnabledModules());
```

**Sorun:** Two different enumerations of the module set are used and they disagree. `BuildContainer` (line 232) runs `_gameConfig.GetEnabledModules().ToList()` through `TopologicalSortModules`, which ends in `TopologicalSorter<ModuleConfig>.Sort` — and Sort deduplicates, because `Visit` marks each item in `visited` and the top-level loop skips already-visited items (TopologicalSorter.cs:32-38, 95-96). I verified the dedupe behavior empirically with the same algorithm: input list of 3 with one repeat produced a sorted list of 2. So `_sortedModules` (used for Install at line 234-238 and Initialize at line 294) contains each ModuleConfig once. `CreateWorld` at line 284 instead passes the RAW `_gameConfig.GetEnabledModules()` — a plain `Where/OrderBy/Select` (GameBootstrapperConfig.cs:63-69) with no deduplication — straight into `AddSystemsFromConfigs`, which loops `AddSystemsFromConfig` per config and has no per-config guard (SystemRunner.cs:112-118, 90-106). Duplicate module entry => every system in that module is instantiated twice and inserted into the phase list twice.

**Senaryo:** A designer drags the same PlayerModuleConfig asset into GameBootstrapperConfig's Modules list twice (easy to do by accident with two ModuleEntry rows), and the project has `_validateOnStart = false` or `_failOnValidationError = false` (both are serialized toggles). GameBootstrapperConfig.Validate's duplicate check (lines 100-114) is skipped, so nothing complains. `_sortedModules.Count == 1` — Install and Initialize run once and the log at line 243 says "Container built with 1 modules". But SystemRunner gets two independent instances of every system in that module: each `SystemRunner.Update` iteration ticks both, so movement integrates twice per frame, damage applies twice, spawners emit twice. Because both instances share the same EntityManager, the symptom is doubled simulation rate rather than an exception — extremely hard to diagnose.

**Düzeltme:** Use `_sortedModules` at line 284 instead of re-querying the config: `_systemRunner.AddSystemsFromConfigs(_sortedModules);`. This makes system registration follow the same deduplicated, topologically-sorted list that Install/Initialize use, eliminates a redundant re-execution of the Where/OrderBy/Select LINQ chain, and removes the possibility of the two lists diverging. Optionally also add a `HashSet<ModuleConfig>` guard inside `SystemRunner.AddSystemsFromConfigs` as defense in depth.

## `stacktrace-unconditional-in-release`

**Full stack traces are logged in release builds via StradaLog while the adjacent Debug.LogError is correctly build-gated — prior finding claimed PARTIAL, still open**

| | |
|---|---|
| Konum | `Runtime/Bootstrap/GameBootstrapper.cs:353` |
| Kategori | security · modules-bootstrap |
| Etki | Startup only, on the failure path — but it is an information-disclosure regression that survives into shipped builds. |
| Test | NO COVERAGE. No test asserts on log output or on the release-vs-development logging split; no GameBootstrapper test file exists. |

```csharp
                StradaLog.LogError($"{phaseName} failed: {ex.Message}\n{ex.StackTrace}", LogModule.Bootstrap);
```

**Sorun:** `TryExecute` (lines 348-353) and `HandleInitializationError` (lines 360-366) both do the right thing for the `Debug.LogError` call — `#if UNITY_EDITOR || DEVELOPMENT_BUILD` emits `{ex}`, else only `{ex.Message}` — and then immediately undo it by unconditionally appending `\n{ex.StackTrace}` to a StradaLog call outside the preprocessor block. Line 366 is the identical pattern: `StradaLog.LogError($"Initialization failed: {ex.Message}\n{ex.StackTrace}", LogModule.Bootstrap);`. I confirmed `StradaLog.LogError` carries no `[Conditional]` attribute and is not compiled out in release: Runtime/Logging/StradaLog.cs:104-107 is a plain `public static void LogError(object message, LogModule module) { LogInternal(...); }`. So the build gating on the line above is cosmetic — the stack trace, including build-machine file paths and internal type/method names, still reaches Player.log in a shipping build. The same unguarded pattern exists at ModuleBootstrapper.cs:68 (`StradaLog.LogError($"[{GetType().Name}] Module initialization failed: {ex.Message}\n{ex.StackTrace}", LogModule.Modules);`).

**Senaryo:** A shipped non-development IL2CPP build hits any initialization failure. Player.log receives the full managed stack trace with the developer's build-machine paths (e.g. C:/Users/<name>/Projects/...) plus the complete internal call chain through GameBootstrapper -> ModuleConfig -> ContainerBuilder. Any player, or anyone with access to a crash-report upload, gets a free map of the framework internals and the studio's directory layout. This was reported as unit-07 #3 / unit-20 #1 and the 2026-05-22 medium status review recorded it as OPEN/PARTIAL with the note "Debug.LogError prod'da .Message kullaniyor, StradaLog her zaman .StackTrace ekliyor" — that assessment is still exactly correct against current code; the fix was never applied.

**Düzeltme:** Move the stack trace into the same preprocessor block as the Debug.LogError, or build the message once:

```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    var detail = $"{phaseName} failed: {ex.Message}\n{ex.StackTrace}";
#else
    var detail = $"{phaseName} failed: {ex.Message}";
#endif
    Debug.LogError($"[GameBootstrapper] {detail}");
    StradaLog.LogError(detail, LogModule.Bootstrap);
```

Apply the same at line 366 and at ModuleBootstrapper.cs:68. Better still, add a `[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")] StradaLog.LogErrorVerbose(string, Exception, LogModule)` helper so the pattern cannot regress.

## `eventbus-leaked-when-buildcontainer-throws`

**_sharedEventBus and _sharedHandleRegistry are never disposed if BuildContainer() throws after creating them — DisposeResources disposes _timerService but not its two siblings**

| | |
|---|---|
| Konum | `Runtime/Bootstrap/GameBootstrapper.cs:224` |
| Kategori | bug · modules-bootstrap |
| Etki | Startup-failure path only; retains the EventBus handler tables (managed memory proportional to registered handler count) until the bootstrapper GameObject is destroyed. |
| Test | NO COVERAGE. No test exercises GameBootstrapper's failure path or DisposeResources at all. |

```csharp
            _sharedEventBus = new EventBus();
            _sharedHandleRegistry = new EntityHandleRegistry();
            _timerService = new TimerService();
```

**Sorun:** All three are created at the top of `BuildContainer` and handed to `builder.RegisterInstance(...)` (lines 227-229). The container is what takes ownership of IDisposable instance registrations — Container.BuildFactories pushes them onto `_disposalStack` at Container.cs:286-289 — but that only happens once `builder.Build()` (line 240) actually returns. If anything between line 229 and line 240 throws (a module's `Install` at line 237, or `Build()` itself throwing on ContainerBuilder.DetectCircularDependencies), `_container` stays null and the container never assumes ownership. `DisposeResources` (lines 388-426) then disposes `_systemRunner`, `_timerService` (line 408), `_world`, and `_container` — but has no statement for `_sharedEventBus` or `_sharedHandleRegistry`, and does not null them. EventBus is IDisposable (via `IEventBus : ... IDisposable`, EventBus.cs:73) and its Dispose calls `Clear()`, so its subscription tables are never released. This is the only surviving resource gap from prior finding unit-07 #4, which the 2026-05-22 status review left as PARTIAL — everything else in that finding is genuinely fixed (see domainNotes).

**Senaryo:** A module has a circular dependency between two registered services. Phase 2 runs, `_sharedEventBus = new EventBus()` executes, modules install, then `builder.Build()` -> `DetectCircularDependencies()` throws InvalidOperationException. TryExecute catches, HandleInitializationError -> DisposeResources: TimerService is disposed, but the EventBus keeps every handler array it accumulated during module Install and is never Cleared. The bootstrapper instance holds the reference in `_sharedEventBus` for the life of the GameObject. If the developer then calls the public `Initialize()` (line 439) to retry, BuildContainer overwrites `_sharedEventBus` with a new instance and the old one is finally garbage — but any handler it captured that transitively references a scene object keeps that object alive until then.

**Düzeltme:** Add explicit disposal to DisposeResources, after the container disposal so nothing resolves them afterward:

```csharp
_sharedEventBus?.Dispose();
_sharedEventBus = null;
_sharedHandleRegistry = null;
_sortedModules = null;
```

EventBus.Dispose and World.Dispose are both idempotent (`if (_disposed) return;` at EventBus.cs:306 and World.cs:118), so the double-dispose that results when the container/world DID take ownership is harmless. Also null `_sortedModules`, which currently retains references to every ModuleConfig asset after teardown.

## `isinitialized-serialized-runtime-state`

**GameBootstrapper._isInitialized is a [SerializeField] runtime flag exposed in the Inspector — setting it lets Shutdown() tear down state that was never built**

| | |
|---|---|
| Konum | `Runtime/Bootstrap/GameBootstrapper.cs:41` |
| Kategori | api-hazard · modules-bootstrap |
| Etki | Startup/teardown only; converts a stray Inspector checkbox into a global player-loop reset and a World.Current wipe. |
| Test | NO COVERAGE. No GameBootstrapper test file exists. |

```csharp
        [Header("Runtime State")]
        [SerializeField] private bool _isInitialized;
```

**Sorun:** `_isInitialized` is pure runtime state — written by `CompleteInitialization` (line 327), `HandleInitializationError` (line 368), and `Shutdown` (line 381) — but it is serialized and therefore persisted in the prefab/scene asset and editable in the Inspector under a "Runtime State" header that invites exactly that. It is simultaneously the guard for the per-frame update methods (lines 133, 140, 146), the guard for `Shutdown()` (line 373), and the guard for the public `Initialize()` (line 441). A stale `true` value in the asset makes all three guards lie in the same direction at once.

**Senaryo:** A developer ticks the visible "Is Initialized" checkbox in the Inspector while debugging (or a scene is saved with it true after a merge). On the next Play: Awake runs, PlayerLoop.Initialize() runs, InitializeAsync starts. Meanwhile `Update`/`LateUpdate`/`FixedUpdate` pass their `if (!_isInitialized) return;` guard immediately and call `_timerService?.Update` / `_systemRunner?.Update` on still-null fields (null-conditional saves them, so this is silent). The public `Initialize()` entry point now refuses with "Already initialized!" and never boots. Worst: if the object is destroyed before InitializeAsync completes, `Shutdown()` passes its `_isInitialized` guard and runs `DisposeResources()` on a half-built framework — including the unconditional `ECS.World.World.Current = null;` at line 413, which clears a World the bootstrapper never created and may belong to someone else — plus `PlayerLoop.Shutdown()`, which resets Unity's global player loop (see playerloop-shutdown-nukes-global-loop).

**Düzeltme:** Remove `[SerializeField]` and make the field plain private, or mark it `[field: NonSerialized]`. If the Inspector readout is wanted for debugging, keep a non-serialized field and surface it read-only via a custom editor or `[ShowInInspector]`-style attribute. Independently, guard line 413 — only null `World.Current` when it actually is `_world`: `if (ReferenceEquals(ECS.World.World.Current, _world)) ECS.World.World.Current = null;` (World.Dispose already does this correctly at World.cs:121-122, so DisposeResources' unconditional version is strictly worse than the one it duplicates).

## `modulebuilder-registerfactory-per-resolve-alloc`

**ModuleBuilder.RegisterFactory allocates a fresh ServiceLocator on every resolve for Transient lifetimes**

| | |
|---|---|
| Konum | `Runtime/Modules/ModuleBuilder.cs:86` |
| Kategori | allocation · modules-bootstrap |
| Etki | Per-resolve: 24 bytes for every Resolve<T>() of a Transient factory registration. Zero for Singleton (one allocation total). |
| Test | NO COVERAGE. No test calls ModuleBuilder.RegisterFactory, and no allocation/GC.Alloc benchmark exists for the module registration path (Tests/Benchmarks/ contains ViewPoolBenchmarks.cs and similar, nothing for DI/modules). |

```csharp
            _containerBuilder.RegisterFactory<T>(container =>
            {
                var serviceLocator = new ServiceLocator(container);
                return factory(serviceLocator);
            }, lifetime);
```

**Sorun:** The adapter lambda constructs a new `ServiceLocator` wrapper on every invocation rather than once at registration time. The `IContainer` passed in is invariant — it is always the same container instance the registration was built against — so the wrapper is pure per-call garbage. For `Lifetime.Singleton` the container's singleton factory (Container.cs:302-325) runs the raw factory once, so the cost is one allocation total. For `Lifetime.Transient` — which `IModuleBuilder.RegisterFactory` fully supports via its `lifetime` parameter and which Documentation~/Modules.md line 423 documents — the container installs the raw factory directly (Container.cs:356) and it runs on every single `Resolve<T>()`. `ServiceLocator` is a sealed class with one reference field: 24 bytes on 64-bit .NET (16-byte object header + 8-byte field).

**Senaryo:** A module registers a transient factory for a per-shot object, e.g. `builder.RegisterFactory<IProjectile>(s => new Projectile(s.Get<IPhysics>()), Lifetime.Transient);` — the exact pattern Documentation~/Modules.md line 451 shows (`.RegisterFactory(services => new EnemyPool(...))`). A weapon firing 100 projectiles per second resolves IProjectile 100 times/sec: 100 x 24 bytes = 2.4 KB/s of pure wrapper garbage on top of the projectiles themselves, all of it Gen-0 churn that contributes to GC spikes on mobile/console. At 100 resolves per frame (a bullet-hell spawner) it is 2.4 KB/frame = ~144 KB/s.

**Düzeltme:** Hoist the wrapper out of the lambda so it is created once per registration and captured by the closure:

```csharp
public IModuleBuilder RegisterFactory<T>(Func<IServiceLocator, T> factory, Lifetime lifetime = Lifetime.Singleton)
    where T : class
{
    IServiceLocator cached = null;
    _containerBuilder.RegisterFactory<T>(container =>
        factory(cached ??= new ServiceLocator(container)), lifetime);
    return this;
}
```

This reduces the allocation to exactly one per registration regardless of lifetime and resolve count.

## `runtimediscovery-cacheflag-before-scan`

**RuntimeSystemDiscovery marks its cache initialized BEFORE scanning and does not skip dynamic assemblies — one GetTypes() throw permanently poisons the cache with partial results**

| | |
|---|---|
| Konum | `Runtime/Modules/RuntimeSystemDiscovery.cs:103` |
| Kategori | bug · modules-bootstrap |
| Etki | Editor only; zero runtime/startup cost (no runtime caller exists). The scan itself is one GetTypes() plus one IsAssignableFrom per type across all non-skipped assemblies, paid once per Editor domain. |
| Test | NO COVERAGE. No RuntimeSystemDiscoveryTests.cs exists. Nothing tests EnsureCacheInitialized, ClearCache/Refresh, ShouldSkipAssembly, ValidateSystemDependencies, or the Kahn TopologicalSort at lines 218-282 (including its cycle fallback path at 275-279). |

```csharp
            _cacheInitialized = true;
            ScanAllAssemblies();
```

**Sorun:** Two defects compound. (1) `_cacheInitialized = true` is set before `ScanAllAssemblies()` runs, so if the scan throws partway through, the exception propagates to the caller but the cache is permanently flagged as initialized while holding only the assemblies enumerated before the throw. Every subsequent `DiscoverSystems()` returns silently-incomplete results with no indication anything went wrong. (2) `ShouldSkipAssembly` (lines 133-144) filters only by name prefix and — unlike the legacy `ModuleRegistry.DiscoverModules`, which correctly does `if (assembly.IsDynamic) continue;` at ModuleRegistry.cs:38-39 — never checks `Assembly.IsDynamic`. `GetSystemTypesFromAssembly` (line 151, `types = assembly.GetTypes();`) catches only `ReflectionTypeLoadException`; calling `GetTypes()` on a `System.Reflection.Emit` dynamic assembly with unfinished TypeBuilders throws `NotSupportedException`, which is not caught and escapes the iterator. The Unity Editor domain routinely hosts dynamic assemblies (Json.NET's dynamic serializers, Moq/NSubstitute proxies, FsCheck — which this repo's ModulePropertyTests.cs already depends on). Prior audit MOD-08 flagged the unsynchronized static cache; it is still unsynchronized and additionally has no `[RuntimeInitializeOnLoadMethod]` reset, unlike PlayerLoop which got one (PlayerLoop.cs:24-33).

**Senaryo:** A developer opens a ModuleConfig inspector and clicks "Discover Systems" (Editor/Inspectors/ModuleConfigEditor.cs:265-276 -> `RuntimeSystemDiscovery.Refresh()` / `DiscoverSystems(...)`) in a session where a test run or a serialization library has emitted a dynamic assembly. `ScanAllAssemblies` reaches it, `GetTypes()` throws NotSupportedException, the exception surfaces as an inspector error, and `_cacheInitialized` is already true. Every later `DiscoverSystems()` call in that session returns whatever partial set was gathered before the throw — the system-picker dropdown silently omits real systems, and the developer concludes the systems "don't exist" or, worse, adds a system to the wrong module. `ClearCache()` fixes it but nothing calls it automatically.

**Düzeltme:** Move `_cacheInitialized = true` to AFTER `ScanAllAssemblies()` returns, and wrap the call so a failed scan resets the cache: `try { ScanAllAssemblies(); _cacheInitialized = true; } catch { _cachedSystems.Clear(); throw; }`. Add `if (assembly.IsDynamic) return true;` as the first line of `ShouldSkipAssembly`, matching ModuleRegistry.cs:38-39. Broaden the catch in `GetSystemTypesFromAssembly` to also handle NotSupportedException/TypeLoadException by yielding nothing for that assembly and logging a warning. NOTE for the brief's question (f): this class costs ZERO at runtime startup — `grep -rn RuntimeSystemDiscovery` shows the only callers are Editor/PropertyDrawers/SystemEntryDrawer.cs:120 and Editor/Inspectors/ModuleConfigEditor.cs:265,268,276. Nothing in Runtime/ or Bootstrap/ ever triggers a scan, so the assembly-wide GetTypes() sweep is an Editor-only cost. It still ships in the Runtime assembly as dead code in players.

## `serviceentry-no-basetype-validation`

**ServiceEntry resolves a Type from a serialized string with zero base-type validation, unlike SystemEntry — the hardening was applied to systems but not services**

| | |
|---|---|
| Konum | `Runtime/Modules/ServiceEntry.cs:58` |
| Kategori | security · modules-bootstrap |
| Etki | Startup only (one resolve per ServiceEntry), but it is an arbitrary-type-instantiation primitive, not a perf cost. |
| Test | NO COVERAGE. No test constructs a ServiceEntry, calls GetImplementationType/GetInterfaceType, or exercises SerializableType at all. There is no SerializableTypeTests.cs in the repo. Neither ModuleRegistryTests.cs nor ModulePropertyTests.cs touches the ModuleConfig path. |

```csharp
        public Type GetImplementationType() => _implementationType?.Type;
```

**Sorun:** SerializableType grew a defensive `AsType<TBase>()` helper (SerializableType.cs:56-67) whose own doc-comment says it exists as "defense-in-depth against tampered assets or asset bundles that may carry an arbitrary assembly-qualified name". SystemEntry uses it — `public Type GetSystemType() => _systemType?.AsType<ISystem>();` (SystemEntry.cs:68). ServiceEntry does NOT: both `GetInterfaceType()` (line 51-52, `var interfaceType = _interfaceType?.Type; return interfaceType ?? _implementationType?.Type;`) and `GetImplementationType()` (line 58) call the raw `.Type` property, which is `Type.GetType(_assemblyQualifiedName)` (SerializableType.cs:28) with no allowlist, no namespace restriction, and no interface constraint. `IsValid` (line 63) only checks `_implementationType.IsValid`, i.e. "the string resolved to some Type". The full attacker path is: tampered .asset / AssetBundle -> `_assemblyQualifiedName` string -> `Type.GetType` -> `ModuleConfig.Install` line 120-123 `if (interfaceType != null && implType != null) builder.Register(interfaceType, implType, service.Lifetime);` -> ContainerBuilder registration -> `Container.CompileFactory` -> constructor invocation on first Resolve. When no interface is set, `GetInterfaceType()` falls back to the implementation type, so the code takes the `interfaceType == implementationType` branch and calls `Register<T>(lifetime)` whose only constraint is `where T : class` — literally any reference type in any loaded assembly can be registered and later constructed. (The two-type branch is incidentally constrained because MakeGenericMethod enforces `where TImplementation : class, TInterface`.) Prior audit MOD-03 flagged this and the 2026-05-22 medium status review still lists it as OPEN with the recommendation "SerializableType.Type getter'a allowlist"; that recommendation was implemented for the system path only.

**Senaryo:** An attacker who can modify a shipped .asset file or swap an AssetBundle (modding scenario, unsigned DLC/patch download, or a rooted device) edits a ModuleConfig's `_services` array and sets `_implementationType._assemblyQualifiedName` to an arbitrary reference type with a parameterless constructor — e.g. `System.Net.WebClient, System` or any game type whose constructor has side effects — leaving `_interfaceType` empty. `IsValid` returns true, `GetEnabledServices()` yields it, and `ModuleConfig.Install` registers it as a container singleton. On first Resolve the constructor executes. There is no code path that rejects it. NOTE: this is currently masked because finding `modulebuilder-ambiguousmatch-kills-bootstrap` makes `builder.Register(Type,Type,Lifetime)` throw before registration happens — so fixing that bug WITHOUT fixing this one converts a latent gadget surface into a live one.

**Düzeltme:** Give ServiceEntry the same treatment SystemEntry got. Because the implementation type has no single framework base type, validate the relationship instead of a fixed base: (1) `GetImplementationType()` must reject abstract types, interfaces, and value types, and (2) when an interface type is set, assert `interfaceType.IsAssignableFrom(implType)` and log+reject otherwise; (3) additionally gate on a configurable assembly/namespace allowlist mirroring `RuntimeSystemDiscovery.ShouldSkipAssembly`, so only types from the game's own assemblies (`Strada.*`, `Game.*`, `Assembly-CSharp`) can be named — the same prefix policy ModuleRegistry.cs:42 already applies. Add an `AsType(Type expectedBase)` non-generic overload to SerializableType so both entry types share one validation point.

## `systementry-phase-unchecked-array-index`

**SystemEntry.Phase is used as an unchecked array index into _systemsByPhase — an out-of-range serialized enum value throws IndexOutOfRangeException and aborts the bootstrap**

| | |
|---|---|
| Konum | `Runtime/Modules/SystemRunner.cs:137` |
| Kategori | bug · modules-bootstrap |
| Etki | Startup only — but it converts a one-byte asset edit into a total bootstrap failure. |
| Test | NO COVERAGE. No test constructs a SystemEntry or a SystemRunner; no test passes an out-of-range enum to AddSystem. |

```csharp
            var phaseList = _systemsByPhase[(int)phase];
```

**Sorun:** `_systemsByPhase` is sized to `Enum.GetValues(typeof(UpdatePhase)).Length` (line 63), i.e. 4 (UpdatePhase.cs: Initialization=0, Update=1, LateUpdate=2, FixedUpdate=3). `phase` arrives from `entry.Phase` (SystemRunner.cs:103), which is the serialized `[SerializeField] private UpdatePhase _phase` on SystemEntry (SystemEntry.cs:19). Unity serializes enums as raw int and performs no range validation on deserialization — a hand-edited .asset, a tampered AssetBundle, or an enum reordering/removal in a future version will produce a value outside 0..3. Nothing validates it: `SystemEntry` has no clamping, `GameBootstrapperConfig.Validate` (lines 81-136) never inspects SystemEntry at all, and `AddSystem` indexes directly. Note `AddSystem` is also public API (line 127) with a `UpdatePhase phase` parameter, so `runner.AddSystem(sys, (UpdatePhase)99)` from user code hits the same path — C# does not range-check enum casts.

**Senaryo:** An attacker (or a corrupted asset, or a merge conflict in a YAML .asset) sets a SystemEntry's `_phase: 7`. `entry.IsValid` is true (it only checks the type resolves), `entry.Enabled` is true, `CreateSystem` succeeds, and `AddSystem` throws IndexOutOfRangeException at line 137. That propagates out of `CreateWorld()` -> TryExecute at GameBootstrapper.cs:194 -> HandleInitializationError -> the entire framework fails to boot. A one-byte asset edit is a reliable denial of service on the game's startup, and the error message ("World Creation failed: Index was outside the bounds of the array.") points nowhere near the cause.

**Düzeltme:** Validate at the boundary in `AddSystem`: `int p = (int)phase; if ((uint)p >= (uint)_systemsByPhase.Length) { StradaLog.LogError($"System '{name}' has invalid UpdatePhase {p}; defaulting to Update.", LogModule.Modules); p = (int)UpdatePhase.Update; }` and index with `p`. Additionally have `GameBootstrapperConfig.Validate` walk every enabled module's SystemEntry list and report out-of-range phases as validation errors so `FailOnValidationError` catches it at a place that names the offending asset.

## `systemrunner-no-disposal-guard`

**SystemRunner has no post-dispose guard and AddSystem null-derefs — MOD-10 from the prior audit is still open**

| | |
|---|---|
| Konum | `Runtime/Modules/SystemRunner.cs:129` |
| Kategori | api-hazard · modules-bootstrap |
| Etki | Not a per-frame cost — a silent lifecycle-violation window where systems are initialized against disposed state and leak. |
| Test | NO COVERAGE. No SystemRunnerTests.cs exists; nothing tests Dispose-then-AddSystem, Dispose-then-Initialize, or AddSystem(null). |

```csharp
            if (_initialized)
            {
                StradaLog.LogWarning("Adding system after initialization. System will be initialized immediately.", LogModule.Modules);
                InjectSystem(system);
                system.Initialize();
            }
```

**Sorun:** Three distinct gaps in one method plus its siblings. (1) `_disposed` (set at line 214) is checked nowhere except in `Dispose` itself. `AddSystem` after `Dispose` succeeds: the system is injected, initialized, and inserted into lists that will never be drained again, so it is never disposed and, because `_disposed` short-circuits a second `Dispose()` call, cannot be. (2) `Initialize()` guards on `_initialized` (line 155) but not `_disposed`, so post-dispose it re-initializes the systems it just disposed. (3) `system` is never null-checked: line 132 `InjectSystem(system)` tolerates null (it is an `is SystemBase` pattern test at line 274), but line 133 `system.Initialize()` and line 136 `name ?? system.GetType().Name` both NullReferenceException. `AddSystem` is public API (line 127) and `GameBootstrapper.GetSystemRunner()` (line 453) hands the runner to arbitrary caller code. `AddSystemsFromConfig` does guard `if (system != null)` at line 101, so only the direct public entry point is exposed. Prior audit MOD-10 raised (1)/(2) as INFO; both are unchanged.

**Senaryo:** A gameplay script caches `bootstrapper.GetSystemRunner()` and adds a system lazily on a level-load event. The bootstrapper's scene is unloaded first (OnDestroy -> Shutdown -> DisposeResources -> `_systemRunner.Dispose()`), then the level-load event fires. `AddSystem` logs the misleading warning "Adding system after initialization", injects the system with an EntityManager belonging to an already-disposed World, calls `Initialize()` on it (which will touch disposed native containers), and inserts it into a list nothing will ever tick or dispose. No exception names the real problem. Separately, `runner.AddSystem(null)` from any caller throws NullReferenceException at line 133 with no indication that the argument was the issue.

**Düzeltme:** Add `if (_disposed) throw new ObjectDisposedException(nameof(SystemRunner));` at the top of `AddSystem`, `AddSystemsFromConfig`, `AddSystemsFromConfigs`, and `Initialize`. Add `if (system == null) throw new ArgumentNullException(nameof(system));` as the first line of `AddSystem`. For the update loops, prefer an early `if (_disposed) return;` over throwing, so a late frame after teardown degrades quietly rather than spamming exceptions.

## `systemrunner-bypasses-getenabledsystems`

**SystemRunner iterates ModuleConfig.Systems directly, bypassing GetEnabledSystems() — the only null-filter in the module system has zero callers**

| | |
|---|---|
| Konum | `Runtime/Modules/SystemRunner.cs:95` |
| Kategori | bug · modules-bootstrap |
| Etki | Startup only; `GetEnabledSystems()` adds two LINQ iterator allocations plus an OrderBy buffer per module at bootstrap, which is negligible against the safety it restores. |
| Test | NO COVERAGE. No test calls GetEnabledSystems, GetEnabledServices, AddSystemsFromConfig, or constructs a ModuleConfig. The dead-method status is itself evidence of the gap — a single test asserting that SystemRunner honours GetEnabledSystems would have caught the divergence. |

```csharp
            foreach (var entry in config.Systems)
            {
                if (!entry.Enabled || !entry.IsValid)
```

**Sorun:** `ModuleConfig` exposes `GetEnabledSystems()` (ModuleConfig.cs:90-95) which filters `s != null && s.Enabled && s.IsValid` and orders by `s.Order`. `grep -rn 'GetEnabledSystems'` over the whole repo returns exactly one hit — the declaration itself. Nothing calls it. `SystemRunner.AddSystemsFromConfig` instead walks the raw `config.Systems` list (the `IReadOnlyList<SystemEntry>` at ModuleConfig.cs:75) and reproduces only two of the three predicates: `!entry.Enabled || !entry.IsValid`. The missing `entry != null` check means a null element dereferences at line 97. The ordering divergence is benign — `AddSystem` does its own insertion sort by Order at lines 139-146 — but the null-filter divergence is not, and the dead method means the safe path exists and is simply not wired up. `ModuleConfig.OnValidate` strips nulls (`_systems?.RemoveAll(s => s == null);`, line 151) but it is inside `#if UNITY_EDITOR`, so it never runs in a player against an asset that shipped with a null element, and it also never runs against a ModuleConfig built at runtime via `ScriptableObject.CreateInstance` + `EditorAddSystem`-equivalent code.

**Senaryo:** A ModuleConfig is constructed programmatically (test harness, procedural content, or a runtime-generated module) and its `_systems` list ends up with a null element — nothing in the runtime API prevents it, and `EditorAddSystem` (ModuleConfig.cs:164-169) accepts null without checking. `SystemRunner.AddSystemsFromConfig` throws NullReferenceException at line 97 on `entry.Enabled`, which propagates out of `CreateWorld()` -> TryExecute -> HandleInitializationError, failing the whole bootstrap with an error that names neither the module nor the index.

**Düzeltme:** Route through the existing safe accessor: `foreach (var entry in config.GetEnabledSystems())` and drop the now-redundant `if (!entry.Enabled || !entry.IsValid) continue;`. That eliminates the dead method, restores the null guard, and makes the ordering intent explicit in one place. Also add `if (entry == null) return;` to `ModuleConfig.EditorAddSystem`.

## `assetref-mints-random-guid-per-conversion`

**AssetRef<T> invents a fresh random GUID in its constructor, so AssetRef.Guid never matches the asset's AssetGuid and every implicit conversion allocates**

| | |
|---|---|
| Konum | `Runtime/Data/AssetContainer.cs:27` |
| Kategori | bug · patterns-utils |
| Etki | Correctness: GUID-based lookups through AssetRef can never succeed. Allocation: 1 Guid + 1 32-char string (~88 bytes) per implicit conversion, on whatever path the conversion sits. |
| Test | No coverage — there is no Tests/Runtime/Data directory and no test references AssetRef. |

```csharp
        public AssetRef(T asset)
        {
            _asset = asset;
            _guid = asset != null ? System.Guid.NewGuid().ToString("N") : string.Empty;
        }

        public static implicit operator T(AssetRef<T> assetRef) => assetRef._asset;
        public static implicit operator AssetRef<T>(T asset) => new(asset);
```

**Sorun:** The identifier is generated from `Guid.NewGuid()` with no relationship to the referenced asset. It does not read `AssetContainer.AssetGuid` (AssetContainer.cs:41-49), which is the key `RuntimeAssetDatabase` indexes on (`var guid = asset.AssetGuid;`, AssetDatabase.cs:61). So `IAssetRef.Guid` — the entire point of the `IAssetRef` abstraction (lines 6-15) — can never be used to resolve the asset, and two `AssetRef<T>` values wrapping the *same* asset carry different Guids. The implicit operator on line 34 means this runs on any `AssetRef<T> r = someAsset;` assignment, allocating a `Guid` plus a 32-char string each time. SecurityReports/2026-05-22-low-status-review.md:134 lists "unit-11 DATA-01 ConfigData GUID lazy + serialized" as FIXED; DATA-01 explicitly named `AssetRef<T> (line 30)` as part of the same finding, and this line is unchanged — the fix was applied to ConfigData only.

**Senaryo:** `[SerializeField] AssetRef<EnemyConfig> _enemy;` in a component; code does `var cfg = db.Get<EnemyConfig>(_enemy.Guid);` -> `KeyNotFoundException: Asset with GUID '...' not found` (AssetDatabase.cs:27) even though the asset IS registered, because `_enemy.Guid` is a random value minted when the struct was constructed, not the asset's `AssetGuid`. Separately, `AssetRef<EnemyConfig> r = cfgAsset;` inside any per-frame or per-spawn code allocates ~88 bytes (Guid.NewGuid + 32-char string) per conversion.

**Düzeltme:** Derive from the asset: `_guid = asset is AssetContainer c ? c.AssetGuid : string.Empty;` — and since `T : ScriptableObject` is broader than `AssetContainer`, either tighten the constraint to `where T : AssetContainer` or make `_guid` editor-assigned from the real Unity asset GUID via a property drawer. Either way, remove `Guid.NewGuid()` from the conversion path.

## `configdata-getdataref-bypasses-null-guard`

**ConfigData<T>.GetDataRef() hands out a mutable ref to the backing field, defeating the null-guard added to the Data setter (DATA-02 fix bypass)**

| | |
|---|---|
| Konum | `Runtime/Data/ConfigData.cs:50` |
| Kategori | api-hazard · patterns-utils |
| Etki | A single call silently nulls shared config state; the null-guard on the setter provides no actual protection. |
| Test | No coverage — there is no Tests/Runtime/Data directory. The DATA-02 fix itself has no regression test, which is why the bypass went unnoticed. |

```csharp
        public ref T GetDataRef() => ref _data;
```

**Sorun:** The `Data` setter on lines 46-47 was hardened for prior finding DATA-02: `set => _data = value ?? throw new ArgumentNullException(nameof(value), "ConfigData<T>.Data cannot be set to null; use a default instance instead.");` (SecurityReports/2026-05-22-low-status-review.md:50 lists this as quick-win Q6). `GetDataRef()` returns a writable `ref` to the very same field, so `config.GetDataRef() = null;` sets it to null with no check at all. It also lets any caller replace a shared ScriptableObject's config payload wholesale from anywhere, with no validation hook — `ConfigDataValue.Validate()` (line 56) is never invoked on the assigned value.

**Senaryo:** `someConfig.GetDataRef() = null;` (or `ref var d = ref cfg.GetDataRef(); d = null;`) succeeds silently. `ConfigDatabase.GetData<TConfig, TData>()` (ConfigDatabase.cs:60-66) then returns a brand-new default instance, because the `Data` getter auto-creates on null (lines 40-43) — so the game silently reverts to default balance/config values instead of the authored asset, with no error. Because `ConfigData` is a ScriptableObject, in the editor this mutation persists on the asset.

**Düzeltme:** Remove `GetDataRef()`, or make it `internal`, or route it through validation: `public ref readonly T GetDataRef() { _data ??= new T(); return ref _data; }` (a readonly ref gives the zero-copy read benefit without the write hazard).

## `log-object-overload-boxes-every-value-type`

**Every StradaLog overload takes `object`, boxing value types at every call site, with no [Conditional] gate to erase the call in release builds**

| | |
|---|---|
| Konum | `Runtime/Logging/StradaLog.cs:72` |
| Kategori | allocation · patterns-utils |
| Etki | Per call with a value-type argument: 1 box (~24 bytes) + 1 string. Per interpolated call: 1 string + 1 box per value-type hole. Paid even when the log is disabled, in every build. |
| Test | No coverage — there is no Tests/Runtime/Logging directory and no test anywhere calls StradaLog. Notably there is also no GC.Alloc assertion anywhere in Tests/Runtime/Performance/, so no benchmark would catch the regression either. |

```csharp
        public static void Log(object message, LogModule module)
        {
            LogInternal(message?.ToString() ?? "null", LogType.Info, module, false);
        }
```

**Sorun:** All seven public entry points (`Log` x2 lines 64/72, `LogWarning` x2 lines 80/88, `LogError` x2 lines 96/104, `LogDeep` line 132) declare `object message`. Passing any value type boxes it at the call site, and `message?.ToString()` then allocates the formatted string — both unconditionally, before any enable check. There is no deferred form (no `Func<string>` overload, no interpolated-string-handler overload, not even a `string` overload to avoid the box). And there is no `[Conditional("UNITY_EDITOR")]` / `[Conditional("DEVELOPMENT_BUILD")]` anywhere in Runtime/ (`grep -rn "Conditional(" Runtime/` → 0 hits), so neither the call nor its argument expressions are erased in a release player build. `LogDeep` is the only method that checks a setting first (line 134) — but by then the caller's interpolation and boxing have already happened.

**Senaryo:** User code in a per-entity update: `StradaLog.LogDeep(entityCount, LogModule.ECS);` with `DeepLogsEnabled == false`. Per call this still boxes the int (24 bytes) and — for the far more common interpolated form `StradaLog.LogDeep($"entity {i} at {pos}", LogModule.ECS)` — allocates the interpolated string plus a boxed float3 per argument, all discarded one line later inside `LogDeep`. At 1000 entities × 60fps that is 60,000 discarded strings/second driving continuous Gen0 pressure and GC spikes, in a shipped build, with logging turned off.

**Düzeltme:** Add `string`-typed overloads to kill the box; add `[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]` to `Log`/`LogDeep` so release builds erase the call and its arguments entirely; and add a deferred `LogDeep(Func<string> factory, LogModule)` (or an `[InterpolatedStringHandler]` type gated on `DeepLogsEnabled`) so the message is never materialised when the level is off.

## `log-settings-load-from-worker-thread`

**StradaLog advertises thread safety (lock + [ThreadStatic]) but LogInternal reaches Resources.Load / ScriptableObject.CreateInstance, which are main-thread-only Unity APIs**

| | |
|---|---|
| Konum | `Runtime/Logging/StradaLog.cs:227` |
| Kategori | concurrency · patterns-utils |
| Etki | Startup-order-dependent hard exception on the first off-thread log; also a duplicate-settings race on first concurrent access. |
| Test | No coverage — there is no Tests/Runtime/Logging directory, and no test in the repo exercises StradaLog from a second thread. The `_lock` + `[ThreadStatic]` design intent is therefore entirely unverified. |

```csharp
            if (StradaLogSettings.Instance.ShowLogs)
```

**Sorun:** `StradaLog` is explicitly built for concurrent use: `private static readonly object _lock` (line 14) guards the buffer, and `[ThreadStatic] private static StringBuilder t_stringBuilder` (lines 19-20) exists specifically so multiple threads can format concurrently. But `LogInternal` touches `StradaLogSettings.Instance` on line 227 (and `AddToBuffer` again on line 246, and `LogDeep` on line 134), and that property does:

    if (_instance == null)
    {
        _instance = Resources.Load<StradaLogSettings>(ResourcePath);

        if (_instance == null)
        {
            _instance = CreateInstance<StradaLogSettings>();
            _instance.InitializeDefaults();
        }
    }

(StradaLogSettings.cs:54-62). `Resources.Load` and `ScriptableObject.CreateInstance` may only be called from Unity's main thread. Separately, `_instance` is a plain non-volatile static assigned without synchronisation, so two threads racing the first access can each build a settings object and publish a duplicate instance.

**Senaryo:** A background asset-loading task or a `Task.Run` continuation calls `StradaLog.LogError(ex, LogModule.Core)` to report a failure, and it happens to be the first StradaLog call of the session (a plausible ordering, since the first thing that logs is usually the first thing that fails). `Resources.Load` throws `UnityException: ... can only be called from the main thread`, which replaces the original error with a confusing secondary one and loses the diagnostic entirely. Note `Debug.Log` itself IS thread-safe in Unity, so the class would otherwise work off-thread — the settings load is the sole blocker.

**Düzeltme:** Resolve and cache the settings instance eagerly on the main thread (e.g. from `[RuntimeInitializeOnLoadMethod]`), snapshot `ShowLogs`/`DeepLogsEnabled`/`MaxLogEntries` into plain static volatile fields that `LogInternal` reads, and make `_instance` assignment go through `Interlocked.CompareExchange` or a main-thread-only initializer. If off-thread logging is not intended, remove the `_lock`/`[ThreadStatic]` machinery and document main-thread-only.

## `log-entries-ring-buffer-order-scrambled`

**StradaLog.LogEntries returns the ring buffer in physical order, so once it wraps the entries are chronologically scrambled**

| | |
|---|---|
| Konum | `Runtime/Logging/StradaLog.cs:35` |
| Kategori | bug · patterns-utils |
| Etki | Editor/diagnostic correctness only. Also allocates a full List copy (1000 refs = 8KB) per property access, which the editor window may do per repaint. |
| Test | No coverage — no tests exist for Runtime/Logging. A single test logging MaxLogEntries+1 messages and asserting Timestamp monotonicity across LogEntries would catch it. |

```csharp
                lock (_lock)
                {
                    var result = new List<LogEntry>(_logBuffer.Count);
                    for (int i = 0; i < _logBuffer.Count; i++)
                    {
                        result.Add(_logBuffer[i]);
                    }
                    return result;
                }
```

**Sorun:** `AddToBuffer` (lines 242-260) implements a circular buffer: once full it overwrites at `_bufferHead` and advances it. `LogEntries` copies slots 0..Count-1 in physical order and never unrolls from `_bufferHead`, so after the first wrap the returned list is `[newest..., oldest...]` — indices 0..(_bufferHead-1) are the most recent entries and _bufferHead..end are the oldest. `GetEntriesByModule` (line 155) and `GetEntriesByType` (line 172) have the same defect.

**Senaryo:** With the default MaxLogEntries = 1000, the 1001st log wraps. The Strada Log window then renders entry #1001 at the top of the list followed by entries #2..#1000 — the log appears to jump backwards in time by ~1000 entries at an arbitrary point, and the discontinuity migrates one row per new log. Debugging an ordered sequence of events after 1000 logs is impossible without manually cross-referencing `LogEntry.Timestamp`.

**Düzeltme:** Unroll the ring: when `_logBuffer.Count == maxEntries`, copy `_bufferHead..Count-1` first, then `0.._bufferHead-1`. Apply to `LogEntries`, `GetEntriesByModule`, and `GetEntriesByType`.

## `log-maxentries-zero-crashes-first-log`

**StradaLogSettings._maxLogEntries is only clamped in the property setter, but the inspector edits the raw serialized field — a value of 0 makes the first log call throw**

| | |
|---|---|
| Konum | `Runtime/Logging/StradaLogSettings.cs:89` |
| Kategori | bug · patterns-utils |
| Etki | Total logging outage (exception on every call) from a single inspector value; startup-triggered, permanent until the asset is edited. |
| Test | No coverage — no tests exist for Runtime/Logging. Note the Project Settings path DOES clamp (Editor/Settings/StradaLogSettingsProvider.cs:83 uses `EditorGUILayout.IntSlider(..., 100, 10000)`), so only the direct-inspector path is exposed — which is exactly why nothing catches it. |

```csharp
        public int MaxLogEntries
        {
            get => _maxLogEntries;
            set => _maxLogEntries = Mathf.Max(100, value);
        }
```

**Sorun:** The `Mathf.Max(100, value)` guard exists only on the setter. The backing field carries no `[Min]`/`[Range]` attribute (line 25: `[SerializeField] private int _maxLogEntries = 1000;`) and `Editor/Settings/StradaLogSettingsEditor.cs:205` draws it with a bare `EditorGUILayout.PropertyField(_maxLogEntries, ...)`, which writes the raw value straight to the serialized field, bypassing the setter. `StradaLog.AddToBuffer` then consumes it unvalidated:

                var maxEntries = StradaLogSettings.Instance.MaxLogEntries;

                if (_logBuffer.Count < maxEntries)
                {
                    _logBuffer.Add(entry);
                }
                else
                {
                    _logBuffer[_bufferHead] = entry;
                    _bufferHead = (_bufferHead + 1) % maxEntries;
                }

(StradaLog.cs:246-256).

**Senaryo:** A user types `0` into "Max Log Entries" in the StradaLogSettings inspector and saves the asset. On the very first log call of the next run: `_logBuffer.Count` is 0, `0 < 0` is false, so `_logBuffer[_bufferHead]` indexes an empty List -> `ArgumentOutOfRangeException` thrown from inside `lock (_lock)` in `AddToBuffer`. Since every StradaLog entry point funnels through here, all logging in the project throws — including the framework's own error reporting in GameBootstrapper and Container. (Had the indexer not thrown first, `% 0` on line 255 would be a DivideByZeroException.) A related, non-crashing variant: lowering MaxLogEntries at runtime through the setter leaves `_logBuffer.Count` above the new modulus, so slots at index >= maxEntries hold stale entries that can never be overwritten.

**Düzeltme:** Put `[Min(100)]` on the serialized field, clamp in `OnValidate`, and defensively clamp at the consumption site: `var maxEntries = Mathf.Max(1, StradaLogSettings.Instance.MaxLogEntries);` plus `if (_logBuffer.Count > maxEntries) { _logBuffer.RemoveRange(maxEntries, _logBuffer.Count - maxEntries); _bufferHead = 0; }`.

## `reactivemodel-getproperty-unchecked-cast`

**ReactiveModel.GetProperty<T> still performs an unchecked cast — the unit-10 #8 fix was applied to Property<T> only**

| | |
|---|---|
| Konum | `Runtime/Patterns/Model.cs:112` |
| Kategori | bug · patterns-utils |
| Etki | Diagnostic quality only — a crash either way, but with a much worse message. |
| Test | No coverage. ModelReactiveTests.ReactiveModel_Property_CreatesByName (lines 124-133) only exercises the matching-type path through `Property<T>`; `GetProperty<T>` is never called by any test, and the mismatch path of `Property<T>` is untested too. |

```csharp
        protected IReadOnlyReactiveProperty<T> GetProperty<T>(string name)
        {
            return _properties.TryGetValue(name, out var property)
                ? (IReadOnlyReactiveProperty<T>)property
                : null;
        }
```

**Sorun:** `Property<T>` (lines 98-110) was hardened with an explicit type check and a descriptive `InvalidOperationException` (lines 102-103). The sibling `GetProperty<T>` — which unit-10 Finding 8 explicitly named ("Similarly, `GetProperty<T>` performs an unsafe cast") — was left unchanged. unit-10 #8 does not appear in the OPEN list of SecurityReports/2026-05-22-medium-status-review.md (lines 25-40), i.e. it is accounted as FIXED, but only half of it was.

**Senaryo:** A model does `Property<int>("health", 100)` in `OnInitialize`. A view later calls `GetProperty<float>("health")` (a plausible mistake, since the key is a raw string with no type association). Result: `InvalidCastException: Unable to cast object of type 'ReactiveProperty`1[System.Int32]' to type 'IReadOnlyReactiveProperty`1[System.Single]'` — an opaque BCL message, versus the descriptive `Property type mismatch: expected ..., got ...` the sibling method produces for the identical mistake.

**Düzeltme:** Mirror `Property<T>`: `if (_properties.TryGetValue(name, out var p)) { if (p is IReadOnlyReactiveProperty<T> typed) return typed; throw new InvalidOperationException($"Property '{name}' type mismatch: expected {typeof(T)}, got {p.GetType()}"); } return null;`

## `patternmanager-catch-logs-full-exception-in-release`

**PatternManager's tick catch blocks interpolate the full exception (stack trace included) in every build and never identify which tickable failed**

| | |
|---|---|
| Konum | `Runtime/Patterns/PatternManager.cs:130` |
| Kategori | security · patterns-utils |
| Etki | Information disclosure in release builds; when triggered, ~1-3KB string allocation per faulting tickable per frame (60-180 KB/s at 60fps). |
| Test | No coverage. No test constructs a PatternManager, so neither the catch behaviour nor the message content is pinned anywhere. |

```csharp
                try { _tickables[i].Tick(deltaTime); }
                catch (Exception ex) { UnityEngine.Debug.LogError($"Exception in pattern update: {ex}"); }
```

**Sorun:** `$"...{ex}"` calls `Exception.ToString()`, which includes the full stack trace and, in a Mono build with symbols, absolute source file paths. There is no `#if UNITY_EDITOR || DEVELOPMENT_BUILD` gate and no `[Conditional]` on the enclosing method, so this ships in release players — the same information-disclosure pattern that SecurityReports/2026-05-22-medium-status-review.md:53 flags as PARTIAL for GameBootstrapper/Container, now reproduced at four fresh sites (lines 131, 140, 146, 155). The message also omits `_tickables[i].GetType().Name`, so with 30 registered tickables the log names none of them. Secondary cost: when a tickable throws every frame, `ex.ToString()` allocates a multi-KB string 60 times per second.

**Senaryo:** A shipped build hits a bug in a user Controller's `Tick`. The player-visible log (and any crash-reporting upload) now contains `/Users/<developer>/Documents/Strada/...` paths and the full internal call chain. Meanwhile the developer reading the report sees only "Exception in pattern update" repeated 3,600 times with no indication of which of the 30 tickables is at fault.

**Düzeltme:** `catch (Exception ex) { UnityEngine.Debug.LogError($"Exception in {_tickables[i].GetType().Name}.Tick: {ex.Message}"); UnityEngine.Debug.LogException(ex); }` — `Debug.LogException` gives Unity's own build-appropriate stack handling while the message stays release-safe. Consider also removing a repeatedly-throwing tickable from the list after N consecutive failures.

## `pool-double-despawn-runs-reset-callbacks-twice`

**ObjectPool.Despawn runs OnDespawn and the _onDespawn callback BEFORE the double-return guard, so a double-despawn resets a live pooled object twice**

| | |
|---|---|
| Konum | `Runtime/Pooling/ObjectPool.cs:70` |
| Kategori | bug · patterns-utils |
| Etki | One spurious full reset per accidental double-despawn — silent state corruption of a pooled object, surfacing on a later unrelated Spawn. |
| Test | No coverage. ObjectPoolTests never calls Despawn twice on the same instance; `TestPoolable.DespawnCount` exists (line 12) and would catch this immediately, but is only asserted once, in `Despawn_CallsOnDespawn` (line 60). |

```csharp
            if (instance is IPoolable p)
                p.OnDespawn();

            _onDespawn?.Invoke(instance);

            if (_available.Count < _maxSize)
            {
                if (!_inPool.Add(instance))
                    return;
                _available.Push(instance);
            }
```

**Sorun:** The `_inPool` HashSet added for prior finding POOL-03 correctly prevents the same instance from being pushed onto `_available` twice, but it is consulted on line 77 — three statements after the lifecycle callbacks have already run. A second `Despawn(x)` on an instance already sitting in the pool re-runs `IPoolable.OnDespawn()` and the user-supplied `_onDespawn` action against an object the pool believes is idle. The framework itself makes double-despawn easy to trigger: `PooledObject<T>.Dispose()` calls `ReturnToPool()` (PooledObject.cs:24-27) with no idempotency flag, and `PooledHandle<T>` is a `readonly struct` (PooledObject.cs:30) whose `Dispose()` calls `_pool?.Despawn(_instance)` — copying the struct and disposing both copies double-despawns.

**Senaryo:** `onDespawn: p => { p.gameObject.SetActive(false); p.RefCount--; }` (the shape shown at Pooling.md:65-69). Code does `pool.Despawn(bullet);` and elsewhere `bullet.Dispose()` — the second despawn decrements `RefCount` a second time on an object already parked in the pool. When that object is next spawned, its RefCount is off by one. Equivalently with `PooledHandle<T>`: `var h = pool.SpawnScoped(); var copy = h; h.Dispose(); copy.Dispose();` runs OnDespawn twice on a pooled instance.

**Düzeltme:** Move the membership test to the top of `Despawn`, before any callback: `if (_inPool.Contains(instance)) return;` (or restructure so `_inPool.Add` is the first mutation and callbacks run only on success). Also give `PooledObject<T>` and `PooledHandle<T>` an idempotency flag so `Dispose()` cannot fire twice.

## `pool-foreign-instance-accepted`

**ObjectPool.Despawn accepts instances the pool never created, poisoning the pool and driving ActiveCount negative**

| | |
|---|---|
| Konum | `Runtime/Pooling/ObjectPool.cs:65` |
| Kategori | api-hazard · patterns-utils |
| Etki | Diagnostic counters permanently wrong; one pooled object silently escapes the pool per foreign despawn. |
| Test | No coverage. ObjectPoolTests.ActiveCount_TracksCorrectly (lines 95-108) only despawns instances obtained from the same pool, which is exactly the case that works. |

```csharp
        public void Despawn(T instance)
        {
            if (instance == null) return;
            if (_disposed) return;
```

**Sorun:** There is no ownership check. Any `T` handed to `Despawn` is pushed onto `_available` (line 79) and becomes spawnable, even though the pool never ran its `_factory` on it and never called `IPoolable<T>.SetPool` (which happens only on the create path, lines 52-53, and in `Prewarm`, lines 90-91). `_totalCreated` is not incremented for the foreign object either, so `ActiveCount => _totalCreated - _available.Count` (line 20) can go negative. Prior finding POOL-02 ("ActiveCount becomes negative") is listed as FIXED at SecurityReports/2026-05-22-low-status-review.md:131 — the `Clear()` path was fixed, this path was not.

**Senaryo:** `var pool = new ObjectPool<Bullet>(() => new Bullet());` then `pool.Despawn(new Bullet());` (or, far more commonly, a shared helper that despawns into the wrong pool instance after a scene reload created a second pool). `TotalCreated` is 0, `AvailableCount` becomes 1, so `ActiveCount` reports **-1**. Worse, the next `Spawn()` returns that foreign instance — and since `SetPool` was never called on it, if it derives from `PooledObject<T>` its `_pool` field is null, so `ReturnToPool()` (PooledObject.cs:19-22) is a silent no-op and the object escapes the pool permanently on its first use.

**Düzeltme:** Track ownership: have `Spawn`/`Prewarm` record created instances in a `HashSet<T>` (or extend `_inPool` semantics with a separate `_owned` set) and reject-with-log in `Despawn` when the instance is not owned. At minimum, increment `_totalCreated` for accepted foreign instances so `ActiveCount` cannot go negative.

## `timer-cancelall-leaks-every-slot`

**TimerService.CancelAll() discards all recycled indices, permanently leaking every list slot and paying to iterate them every frame forever**

| | |
|---|---|
| Konum | `Runtime/Services/TimerService.cs:143` |
| Kategori | performance · patterns-utils |
| Etki | Per-frame: +1 wasted list read and branch per leaked slot, growing without bound across CancelAll cycles (10k dead slots ≈ 10k iterations/frame = 600k/s at 60fps). Memory: `_timers` backing array never shrinks. |
| Test | Actively hidden by the tests. TimerServiceTests.CancelAll_StopsAllTimers (lines 111-122) asserts only that callbacks stop firing — which the leaked null slots satisfy — and never inspects timer-list growth or re-schedules afterwards. TimerServicePerformanceTests.Benchmark_TimerExpiration_1k (lines 66-78) sidesteps it by constructing a brand-new TimerService in SetUp instead of calling CancelAll. |

```csharp
        public void CancelAll()
        {
            for (int i = 0; i < _timers.Count; i++)
                RemoveAt(i);
            _freeIndices.Clear();
        }
```

**Sorun:** `RemoveAt(index)` (lines 132-141) does `_timers[index] = null; _freeIndices.Enqueue(index);` — it never shrinks `_timers`, it relies on `_freeIndices` to make the hole reusable. `CancelAll` then calls `_freeIndices.Clear()`, throwing away every index it just enqueued. The result: `_timers.Count` is unchanged and every slot is a permanently unreachable `null`. All subsequent `Schedule` calls take the `else` branch (lines 59-62) and append past the dead region. `Update` (line 70) iterates the whole list including every dead slot, forever.

**Senaryo:** A game calls `_timerService.CancelAll()` on each scene transition — the pattern the docs recommend (Documentation~/TimerService.md:271 `_timerService.CancelAll();` under "Cleanup on Scene Unload"). With 500 live timers per level and 20 level loads, `_timers.Count` reaches 10,000 while only ~500 are live. `TimerService.Update` — driven from `GameBootstrapper.Update` every frame — then performs 10,000 `List<T>` indexer reads + null checks per frame instead of 500, and `_timers` retains 10,000 slots of backing array that can never be reclaimed.

**Düzeltme:** Either drop the `_freeIndices.Clear()` line entirely (RemoveAt already enqueued every index), or replace the whole body with a real reset: iterate calling `RemoveAt`, then `_timers.Clear(); _freeIndices.Clear();`. The latter is preferable since it also releases the entries. Note `CancelAll` is also called from `Dispose` (line 152).

## `timer-repeat-interval-drift`

**Repeating timers reset to the full interval instead of accumulating, dropping the overshoot — up to a full frame of drift per fire**

| | |
|---|---|
| Konum | `Runtime/Services/TimerService.cs:92` |
| Kategori | bug · patterns-utils |
| Etki | Per repeating timer per fire: loses up to one `deltaTime`. For interval 0.1s @60fps that is a 16.7% period error, compounding linearly (~86 missed fires per minute per timer). |
| Test | Actively hidden by the tests. TimerServiceTests.Every_ExecutesRepeatedly (lines 37-50) calls `Update(0.1f)` against `Every(0.1f, ...)` — deltaTime exactly equals interval, so overshoot is exactly zero and the bug is invisible. Every_WithRepeatCount_StopsAfterCount (lines 125-137) does the same. No test uses a realistic 1/60 deltaTime against a repeating timer. |

```csharp
                timer.RemainingTime = timer.Interval;
```

**Sorun:** A repeating timer fires when `RemainingTime <= 0` (line 78), meaning it has usually overshot past zero by some fraction of `deltaTime`. Line 92 discards that overshoot by assigning the full interval instead of `+=`, so every fire loses up to one whole frame of time. This makes repeating timers systematically SLOW, and the error compounds across every repetition. A second consequence: an interval shorter than `deltaTime` can fire at most once per `Update` call, so `Every(0.001f, ...)` fires 60 times/second, not 1000.

**Senaryo:** `Every(0.1f, cb)` at a locked 60fps (`deltaTime` = 0.01667). Countdown: 6 frames brings RemainingTime to ~0.0000 but still positive; the 7th frame drives it to ~-0.0167 and fires. Period = 7 frames = 0.1167s, not 0.100s — 16.7% slow. Over a 60-second match a 'spawn wave every 0.1s' timer fires ~514 times instead of 600. The DOT example in Documentation~/TimerService.md:495-501 (`Every(interval, ..., repeatCount: ticks)`) therefore deals its damage over a materially longer wall-clock window than the designer specified.

**Düzeltme:** `timer.RemainingTime += timer.Interval;` and convert the fire check to a `while` loop so sub-frame intervals catch up: `while (timer.RemainingTime <= 0 && timer.RemainingRepeats != 0) { invoke; decrement; timer.RemainingTime += timer.Interval; }` (guarding `Interval <= 0` to avoid an infinite loop).

## `timer-schedule-from-callback-ticks-same-frame`

**A timer scheduled from inside a callback can land on a recycled index below the loop cursor and be ticked in the same frame, losing a frame of its delay**

| | |
|---|---|
| Konum | `Runtime/Services/TimerService.cs:52` |
| Kategori | bug · patterns-utils |
| Etki | Up to one full deltaTime of delay lost per callback-scheduled timer; nondeterministic (depends on free-index availability). |
| Test | No coverage. No test in TimerServiceTests or TimerServicePerformanceTests schedules a timer from inside a callback, and no test ever calls Schedule after a timer has completed (which is what populates _freeIndices). |

```csharp
            int index;
            if (_freeIndices.Count > 0)
            {
                index = _freeIndices.Dequeue();
                _timers[index] = entry;
            }
            else
            {
                index = _timers.Count;
                _timers.Add(entry);
            }
```

**Sorun:** `Update` iterates downward (`for (int i = _timers.Count - 1; i >= 0; i--)`, line 70), which correctly protects against the append path on lines 59-62 — a newly appended timer sits above the cursor and is not visited. But the recycle path on lines 53-57 places the new entry at whatever index `_freeIndices` hands back, which is frequently *below* the cursor. That entry is then visited later in the same `Update` pass and immediately gets `timer.RemainingTime -= deltaTime` (line 76) applied, even though zero real time has passed since it was scheduled.

**Senaryo:** `svc.After(1f, () => svc.After(0.01f, PlaySound));` with `_freeIndices` non-empty (which it is as soon as any earlier timer has completed). The inner 0.01s timer is placed at a recycled low index; the same `Update` pass reaches it, subtracts the frame's 0.0167s, gets -0.0067 which is not `> 0`, and fires `PlaySound` in the *same* frame it was scheduled instead of ~1 frame later. Timing-sensitive chains — e.g. the staggered wave spawn at Documentation~/TimerService.md:536-541, `timerService.After(i * 0.2f, () => SpawnEnemy());` inside a wave callback — collapse nondeterministically depending on how many free indices happen to be available.

**Düzeltme:** Record `entry.ScheduledFrame` (or a monotonic `_updateGeneration` counter incremented at the top of `Update`) and skip entries created during the current pass; or queue callback-originated schedules into a pending list drained at the end of `Update`.

## `fsm-transition-condition-exception-claimed-fixed-but-open`

**PRIOR FINDING FSM-04 IS MARKED FIXED BUT IS NOT — CheckTransitions still has no exception handling**

| | |
|---|---|
| Konum | `Runtime/StateMachine/StateMachine.cs:107` |
| Kategori | bug · patterns-utils |
| Etki | Per-frame while the faulting condition holds: current state's OnUpdate skipped every frame, indefinitely. |
| Test | No coverage. StateMachineTests has no throwing-condition test; StateMachinePerformanceTests uses only `() => false` (lines 45-49) and `() => toggle` (line 74). |

```csharp
        private void CheckTransitions()
        {
            foreach (var transition in AnyTransitions)
            {
                if (transition.ToType != CurrentStateTypeInternal && transition.Condition())
                {
                    SetState(transition.ToType);
                    return;
                }
            }
```

**Sorun:** SecurityReports/2026-05-22-low-status-review.md:133 lists "unit-11 FSM-04 Transition condition exceptions (CheckTransitions caller safe)" under **FIXED**. It is not fixed. Neither `CheckTransitions` (lines 107-129) nor `Update` (lines 58-64) contains a try/catch, and the original recommendation was explicitly "Wrap condition evaluation in try/catch blocks and log errors rather than propagating exceptions." A throwing `Func<bool>` propagates straight out of `StateMachineCore.Update`. There is no framework-level wrapper either — `StateMachine` is not registered with `PatternManager` or `PlayerLoop`, so the "caller safe" justification has no basis in the code: the caller is arbitrary user code.

**Senaryo:** `sm.AddAnyTransition<DeadState>(() => _health.Value <= 0)` where `_health` is a `ReactiveProperty` disposed on level teardown. On the next `sm.Update(dt)` the condition throws NullReferenceException; it escapes `Update`, so `CurrentStateInternal.OnUpdate(deltaTime)` (line 63) never runs and — if the machine is driven from a Controller's `Tick` — the exception is caught by `PatternManager.OnUpdate`'s catch (PatternManager.cs:131) which logs a message that does not identify which tickable failed. The state machine is frozen but the game keeps running with no actionable diagnostic.

**Düzeltme:** Wrap each `transition.Condition()` in try/catch, log with the from/to state types, and treat a throwing condition as `false` so the machine keeps running. Update the status-review row for FSM-04 from FIXED to OPEN.

## `fsm-update-nre-after-stop-during-transition`

**StateMachine.Update dereferences CurrentStateInternal after CheckTransitions without re-checking null — Stop() from OnEnter/OnExit/OnStateChanged causes a NullReferenceException**

| | |
|---|---|
| Konum | `Runtime/StateMachine/StateMachine.cs:58` |
| Kategori | bug · patterns-utils |
| Etki | Immediate crash on the frame the terminal state is entered. |
| Test | No coverage. StateMachineTests.Stop_ExitsCurrentState (lines 148-160) calls `Stop()` from outside `Update`, which cannot reproduce this. |

```csharp
        public void Update(float deltaTime)
        {
            if (CurrentStateInternal == null || IsTransitioningInternal) return;

            CheckTransitions();
            CurrentStateInternal.OnUpdate(deltaTime);
        }
```

**Sorun:** The null guard on line 60 is evaluated before `CheckTransitions()` runs. `CheckTransitions` can call `SetState`, which invokes `previousState.OnExit()`, `newState.OnEnter()` and the `OnStateChanged` event (lines 95-99). Any of those can call the public `Stop()` (lines 72-79), which sets `CurrentStateInternal = null`. Control then returns to line 63 and dereferences the now-null field. `IsTransitioningInternal` does not help — the `finally` on lines 101-104 clears it before `SetState` returns. A related design hazard on the same line: after a successful transition, `OnUpdate` is immediately invoked on the state that was entered this very frame, so a freshly entered state gets `OnEnter` and `OnUpdate` back to back.

**Senaryo:** ```
class DeadState : StateBase { public StateMachine<StateBase> M; public override void OnEnter() => M.Stop(); }
sm.AddAnyTransition<DeadState>(() => health <= 0);
```
When health drops to 0, `sm.Update(dt)` -> CheckTransitions -> SetState(DeadState) -> DeadState.OnEnter -> Stop() -> `CurrentStateInternal = null` -> unwind to line 63 -> `null.OnUpdate(deltaTime)` -> NullReferenceException thrown out of Update.

**Düzeltme:** Re-check after the transition pass: `CheckTransitions(); var s = CurrentStateInternal; if (s == null) return; s.OnUpdate(deltaTime);` — caching to a local also removes a second field read.

## `fsm-addstate-keys-on-static-type-parameter`

**StateMachineCore.AddState<T> keys the registry on the compile-time type argument, so registering through a base-typed variable silently registers under the wrong key**

| | |
|---|---|
| Konum | `Runtime/StateMachine/StateMachine.cs:23` |
| Kategori | api-hazard · patterns-utils |
| Etki | Startup-only misconfiguration, but the failure mode is a silently dead state machine — one warning line, then permanent no-op. |
| Test | No coverage. StateMachineTests always calls `AddState(concreteLocalVariable)` where the local is declared with the concrete type (lines 42, 71, 110) — exactly the case that works. |

```csharp
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddState<T>(T state) where T : TState
        {
            OnStateAdded(state);
            States[typeof(T)] = state;
        }
```

**Sorun:** `typeof(T)` is the static type at the call site, not `state.GetType()`. Retrieval goes through `SetState<T>()` / `Start<T>()` which also use `typeof(T)` — so the two only agree when the caller happens to write the concrete type inline. Any indirection (a factory returning the base type, a `foreach` over a `List<StateBase>`, a helper method with a base-typed parameter) registers every state under the *same* base-type key, so each `AddState` silently overwrites the previous one and `SetState<Concrete>()` hits the not-registered path.

**Senaryo:** ```
void RegisterAll(StateMachine<StateBase> sm, IEnumerable<StateBase> states)
{ foreach (var s in states) sm.AddState(s); }   // T is inferred as StateBase
```
All N states collapse into `States[typeof(StateBase)]`, keeping only the last one. `sm.Start<IdleState>()` then logs `Attempted transition to unregistered state: IdleState` (line 86) and returns, leaving `CurrentStateInternal` null — so `Update` no-ops forever after a single warning at startup. Note Tests/Runtime/Performance/StateMachinePerformanceTests.cs:43-44 already relies on the collapse accidentally: it adds two distinct `TestState` instances and the second silently replaces the first.

**Düzeltme:** Key on the runtime type: `States[state.GetType()] = state;`, and add a non-generic overload `AddState(TState state)`. If the compile-time key is intentional, at minimum `Debug.LogWarning` when `typeof(T) != state.GetType()`.

## `postprocessor-reads-every-cs`

**CodeGenPostprocessor reads the full text of every changed .cs on the main thread during asset import**

| | |
|---|---|
| Konum | `Editor/CodeGen/CodeGenPostprocessor.cs:55` |
| Kategori | performance · sourcegen |
| Etki | Per import batch, editor-only: O(total bytes of changed .cs) main-thread I/O + 2 substring scans per file, with no early exit. Gated behind StradaCodeGenSettings.AutoRegenEnabled, which defaults to false (line 129). |
| Test | No coverage. Tests/Editor/ contains zero .cs files. |

```csharp
                if (path.EndsWith(".cs") && !path.Contains("Generated"))
                {
                    if (path.StartsWith("Assets/"))
                    {
                        var content = File.ReadAllText(path);
                        if (content.Contains("[StradaSystem") || content.Contains("[Strada.Core"))
```

**Sorun:** Every changed .cs under Assets/ is fully materialised into a string on the editor main thread, then scanned twice with String.Contains. The loop has no early-exit once `systemClassChanged` is already true (lines 61-64 set the flag but the loop keeps reading every remaining file), so the cost is paid for all N files even after the answer is known. The marker `[StradaSystem` is also never used anywhere in this package (`SystemOrderAttribute` is the real attribute, Editor/CodeGen/SystemRegistryGenerator.cs:206), so the first predicate can never match and only the broad `[Strada.Core` substring does any work.

**Senaryo:** A git branch switch or first project import re-imports 3,000 .cs files under Assets/. OnPostprocessAllAssets reads all 3,000 files (say ~4 KB average = ~12 MB of string allocations plus 2 full scans each) synchronously on the main thread, freezing the editor, and then discards the answer after the first match would have sufficed.

**Düzeltme:** Add `if (systemClassChanged) continue;` (or break out of the loop) once the flag is set, and match on the real attribute name. Better: drop the content scan entirely and regenerate whenever any .cs under Assets/ changes — SystemRegistryGenerator already re-scans the AppDomain, so file-content sniffing buys nothing.

## `modulenamevalidator-dead-code`

**ModuleNameValidator is entirely unreferenced dead code that duplicates, and diverges from, the live validator**

| | |
|---|---|
| Konum | `Editor/CodeGen/ModuleNameValidator.cs:46` |
| Kategori | bug · sourcegen |
| Etki | No runtime cost (unreachable); 214 lines of misleading validation surface. |
| Test | No coverage. Tests/Editor/ contains zero .cs files. unit-13 Findings 3 and 6 both analysed this file without noting it has no callers. |

```csharp
        public static ModuleNameValidationResult Validate(string name)
```

**Sorun:** `grep -rn "ModuleNameValidator"` across the repo returns hits only inside ModuleNameValidator.cs itself — zero callers. The module generator uses its own copy instead: StradaModuleGenerator.Validation.cs:11-24 declares a *different* ReservedNames set (C# keywords plus 8 framework names) while ModuleNameValidator.ReservedNames (lines 18-39) is a much larger OrdinalIgnoreCase set including View, Model, Service, Controller, Factory, Entity, World, Data, State. The two disagree about which names are legal. ModuleNameValidator.FindExistingModuleNames (lines 148-185) additionally runs `AssetDatabase.FindAssets("t:Script")` over every script in the project plus `assembly.GetTypes()` over every loaded assembly with an empty `catch { }` (lines 179-181) — all unreachable.

**Senaryo:** A maintainer adds a reserved name to ModuleNameValidator.ReservedNames expecting the Module Generator to reject it. The generator continues to accept it because it consults StradaModuleGenerator.Validation.cs's separate list. Separately, unit-13 Findings 3 and 6 reviewed ModuleNameValidator as if it were a live validation gate; it is not.

**Düzeltme:** Delete Editor/CodeGen/ModuleNameValidator.cs and its .meta, or make StradaModuleGenerator.ValidateModuleName delegate to ModuleNameValidator.Validate so there is exactly one reserved-name list and one PascalCase regex.

## `systemregistry-dead-sanitizer`

**SystemRegistryGenerator's type-name sanitizer and its private GetFullTypeName are both dead code**

| | |
|---|---|
| Konum | `Editor/CodeGen/SystemRegistryGenerator.cs:160` |
| Kategori | bug · sourcegen |
| Etki | Startup/menu-invocation only; the cost is a false sense of validation coverage, not runtime overhead. |
| Test | No coverage. SecurityReports/2026-05-22-low-status-review.md:114 lists unit-13 #1 as accepted-by-design; verified the recommended sanitizer exists in source but is never invoked. |

```csharp
        private static readonly Regex ValidTypeNameRegex = new Regex(@"^[\w.<>,\s]+$", RegexOptions.Compiled);

        private static bool IsValidTypeName(string typeName)
        {
            return !string.IsNullOrEmpty(typeName) && ValidTypeNameRegex.IsMatch(typeName);
        }
```

**Sorun:** `IsValidTypeName` has zero call sites in the file — GenerateRegistryCode emits `typeName` straight into the output at lines 126, 137 and 149 without ever consulting it. The private `GetFullTypeName` at lines 167-190 is likewise unreachable: all three call sites use `StradaCodeGenerator.GetFullTypeName(s.Type)` (lines 125, 136, 148), so the local 24-line duplicate never executes. The regex is exactly the whitelist unit-13 Finding 1 recommended (`^[a-zA-Z_][a-zA-Z0-9_.<>,\s]*$`) — it was written but never wired up, so the defence-in-depth the prior audit asked for is not actually in effect.

**Senaryo:** A reviewer or maintainer reads lines 160-165, concludes generated type names are whitelist-validated, and skips the accessibility/visibility hardening in the sibling finding. Meanwhile any Type.FullName that GetFullTypeName cannot express (nested generic type arguments, a type in an assembly with a '+' or bracket in a name) is emitted verbatim and produces a malformed generated file with no guard.

**Düzeltme:** Either delete both dead members, or call IsValidTypeName on the result of StradaCodeGenerator.GetFullTypeName in GenerateRegistryCode and skip (with Debug.LogWarning) any type that fails. Delete the duplicated private GetFullTypeName either way — StradaCodeGenerator.GetFullTypeName is the live implementation.

## `asmdef-namespace-unvalidated-json`

**AssemblyDefStep injects the raw namespace into asmdef JSON before FileGenerationStep's namespace regex ever runs**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/Pipeline/Steps/AssemblyDefStep.cs:101` |
| Kategori | security · sourcegen |
| Etki | Editor-time only; one malformed or attacker-shaped .asmdef per generation attempt. |
| Test | No coverage. SecurityReports/2026-05-22-medium-fix-plans.md:5 declares unit-12 #3 FIXED on the strength of FileGenerationStep.cs:28 alone — verified INCOMPLETE: the asmdef writer is upstream of that regex and has no validation of its own. |

```csharp
    ""name"": ""{asmName}"",
    ""rootNamespace"": ""{rootNamespace}"",
```

**Sorun:** asmName and rootNamespace are `context.Definition.FullNamespace` (AssemblyDefStep.cs:27) interpolated into a JSON string literal with no escaping and no validation. The only namespace validation in the whole pipeline is FileGenerationStep.cs:28/39-40 (`ValidNamespaceRegex`, `^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*$`), and FileGenerationStep runs at Order 30 — after AssemblyDefStep's Order 20. The UI-side ValidateNamespace (StradaModuleGenerator.Validation.cs:103-117) only checks that each dot-separated segment is non-empty and that `char.IsLetter(part[0])`; it never validates the remaining characters, so `Game.Modules","references":["Foo` passes (segment 2 starts with 'M'). References are likewise unescaped at line 95 (`references.Select(r => $"\"{r}\"")`), and those come from ModuleDiscovery.FindAssemblyForModule.

**Senaryo:** Namespace = `Game.Mod","allowUnsafeCode":true,"x":"` -> AssemblyDefStep writes an .asmdef whose JSON structure the attacker controls (assembly name, references, defineConstraints, autoReferenced). FileGenerationStep then rejects the namespace and the pipeline rolls back — but AssemblyDefStep.Rollback (lines 67-78) only deletes paths already in context.CreatedFiles, and if any step throws instead of returning a failed StepResult (see pipeline-no-exception-handling) Rollback never runs and the crafted .asmdef survives, silently changing how the user's assembly is compiled.

**Düzeltme:** Move the ValidNamespaceRegex check out of FileGenerationStep and into the pipeline entry point (or into a new Order-0 ValidationStep) so it runs before any file is written, and JSON-escape asmName/rootNamespace/references in WriteAsmdef rather than raw-interpolating them.

## `postgen-findtype-simple-name-fallback`

**ModuleGeneratorPostProcessor.FindType falls back to simple-name matching across all assemblies, ignoring namespace and base type**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/StradaModuleGenerator.PostGen.cs:177` |
| Kategori | bug · sourcegen |
| Etki | Editor-time, once per module generation: wrong-type asset creation or an unhandled exception in a script-reload callback. |
| Test | No coverage. Tests/Editor/ contains zero .cs files. |

```csharp
                    type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
```

**Sorun:** The correct lookup is on the preceding line (`assembly.GetType(fullName)` with the namespace-qualified name). When that misses, this fallback matches on the simple name alone across every assembly in AppDomain order, with no namespace check and — critically — no check that the result derives from ScriptableObject or ModuleConfig. The result is handed straight to `ScriptableObject.CreateInstance(configType)` (line 66) and `AssetDatabase.CreateAsset(instance, configPath)` (line 67), inside a [DidReloadScripts]/[InitializeOnLoad] callback with no try/catch anywhere in the method.

**Senaryo:** User generates a module named Player. A pre-existing, unrelated `namespace Legacy { public class PlayerModuleConfig { } }` (a plain class, not a ScriptableObject) lives in an assembly that AppDomain enumerates before the new module's assembly. After recompilation, FindType returns Legacy.PlayerModuleConfig, ScriptableObject.CreateInstance throws ArgumentException ('is not derived from ScriptableObject'), and the exception escapes ProcessPendingModuleConfigAsset on the DidReloadScripts path. Because EditorPrefs.DeleteKey already ran at line 42, the pending record is gone and the user gets no asset and no retry.

**Düzeltme:** Drop the simple-name fallback, or constrain it to `t.Name == typeName && typeof(ModuleConfig).IsAssignableFrom(t)`. Wrap ProcessPendingModuleConfigAsset in try/catch and log a Debug.LogError with the module name so the failure is attributable.

## `templateprocessor-preview-misroutes-i-prefix`

**TemplateProcessor.GeneratePreview shows the interface template for any module whose name starts with 'I'**

| | |
|---|---|
| Konum | `Editor/ModuleGenerator/Utilities/TemplateProcessor.cs:16` |
| Kategori | bug · sourcegen |
| Etki | Editor-time, preview-only: wrong template shown for 1 of ~12 preview entries whenever the module name begins with 'I'. |
| Test | No coverage. Tests/Editor/ contains zero .cs files. |

```csharp
            if (fileName.StartsWith("I") && fileName.Contains("Service"))
                return GenerateServiceInterfacePreview(moduleName, ns, settings);
```

**Sorun:** The dispatcher tests only for a leading 'I' and the substring 'Service'. Module names are constrained to PascalCase starting with an uppercase letter (StradaModuleGenerator.Validation.cs:26 `PascalCaseRegex = new Regex(@"^[A-Z][a-zA-Z0-9]*$")`), so a module named Inventory / Item / Input produces the *implementation* file name `InventoryService.cs` (built at StradaModuleGenerator.UI.cs:688 `if (components.Service) files.Add($"{name}Service.cs");`) which matches this branch before the real `fileName.EndsWith("Service.cs")` test at line 19 can run.

**Senaryo:** User types module name 'Inventory' and opens the Code Preview tab, then selects InventoryService.cs from the dropdown. The preview renders `public interface IInventoryService { void Initialize(); }` instead of the service class that will actually be written. The generated file (FileGenerationStep.GenerateService) is the class — so the preview is simply wrong for every I-prefixed module.

**Düzeltme:** Reorder so the more specific `fileName.EndsWith("Service.cs")` case is checked before the interface case, and make the interface test `fileName.StartsWith("I") && fileName.EndsWith("Service.cs") && char.IsUpper(fileName[1])` — or better, pass the component kind through instead of re-deriving it from the file name string.

## `di-sourcegen-dead-attribute-names`

**StradaDISourceGenerator targets attribute type names that do not exist — it can never emit anything**

| | |
|---|---|
| Konum | `SourceGenerationDI~/StradaDISourceGenerator.cs:13` |
| Kategori | bug · sourcegen |
| Etki | Zero output produced, always. 245 lines of generator code that provably cannot execute past line 57. |
| Test | No coverage. No test asserts that the generator emits StradaGeneratedContainer for an annotated type. |

```csharp
        private const string StradaServiceAttributeName = "Strada.Core.DI.StradaServiceAttribute";
        private const string InjectAttributeName = "Strada.Core.DI.InjectAttribute";
```

**Sorun:** Neither metadata name resolves against the shipped runtime. `grep -rn "StradaServiceAttribute" Runtime` returns nothing at all — the type does not exist anywhere in the package. `InjectAttribute` does exist but at `Strada.Core.DI.Attributes.InjectAttribute` (Runtime/DI/Attributes/InjectAttribute.cs:3 `namespace Strada.Core.DI.Attributes`), not `Strada.Core.DI.InjectAttribute`. Execute compares against these strings with `ad.AttributeClass?.ToDisplayString() == StradaServiceAttributeName` (line 38), so `services` stays empty, and line 56-57 (`if (services.Count == 0) return;`) short-circuits on every single compilation. The same dead constant appears in StradaFactoryGenerator.cs:15 (`"Strada.Core.DI.Attributes.StradaServiceAttribute"`), which is also unresolvable.

**Senaryo:** A user annotates a service, expecting the 'DI auto-binding generation' the README advertises at line 385. Nothing is emitted. `Strada.Core.DI.Generated.StradaGeneratedContainer` never exists, no diagnostic is reported, and the failure is completely silent — the user has no way to tell the generator ran at all.

**Düzeltme:** Either delete SourceGenerationDI~/StradaDISourceGenerator.cs (StradaFactoryGenerator supersedes it), or fix the constants to `Strada.Core.DI.Attributes.AutoRegisterAttribute` / `Strada.Core.DI.Attributes.InjectAttribute` and drop the non-existent StradaServiceAttribute entry from both generators.

## `di-sourcegen-deprecated-isourcegenerator`

**StradaDISourceGenerator is the deprecated ISourceGenerator with an unfiltered syntax receiver — re-runs full semantic analysis on every keystroke**

| | |
|---|---|
| Konum | `SourceGenerationDI~/StradaDISourceGenerator.cs:11` |
| Kategori | performance · sourcegen |
| Etki | Per-keystroke in the IDE and per-domain-reload in Unity: O(attributed-classes) semantic-model constructions, unbounded by any attribute filter. Moot today only because the generator is not shipped (see entityquerygen-not-shipped) and matches nothing (see di-sourcegen-dead-attribute-names). |
| Test | No coverage; there is no generator benchmark or incrementality test in Tests/. |

```csharp
    public class StradaDISourceGenerator : ISourceGenerator
```

**Sorun:** Three compounding problems. (1) `ISourceGenerator` has no incremental caching — `Execute` runs in full on every compilation the IDE produces, i.e. every keystroke. (2) The receiver is unfiltered: `ServiceSyntaxReceiver.OnVisitSyntaxNode` (line 218-221) adds *every* `ClassDeclarationSyntax` that has any attribute list at all — `[Serializable]`, `[CreateAssetMenu]`, `[TestFixture]`, everything — with no attribute-name check. In a mid-size Unity project that is thousands of classes. (3) `Execute` then calls `compilation.GetSemanticModel(candidateClass.SyntaxTree)` (line 31) *inside the loop*, once per candidate; because it is called per-candidate rather than per-tree, a file with N attributed classes builds N semantic models for the same tree. Semantic-model construction is the most expensive Roslyn operation available.

**Senaryo:** Project with 2,000 attributed classes across 800 files. Every keystroke in the IDE: 2,000 iterations, each calling GetSemanticModel + GetAttributes + ToDisplayString. Roslyn binds ~800 trees repeatedly. Typing latency in Rider/VS becomes visibly unusable, and Unity's compilation pipeline pays it again on every domain reload.

**Düzeltme:** Convert to IIncrementalGenerator using `context.SyntaxProvider.ForAttributeWithMetadataName(...)` (available in the referenced Microsoft.CodeAnalysis.CSharp 4.3.0), which does the attribute filtering inside Roslyn's cached index and skips the file entirely when no matching attribute is present. If keeping the syntax-receiver shape, at minimum filter by attribute simple-name in OnVisitSyntaxNode and hoist GetSemanticModel to one-per-SyntaxTree.

## `factorygen-nested-type-name-collision`

**GetFactoryName still collides for nested types sharing a simple name in the same namespace**

| | |
|---|---|
| Konum | `SourceGenerationECS~/StradaFactoryGenerator.cs:327` |
| Kategori | bug · sourcegen |
| Etki | Compile-time only — CS0101 per colliding pair, breaking the assembly. |
| Test | No coverage. unit-16 Finding 5 was addressed for the same-simple-name-different-namespace case; verified the nested-type case is still open. |

```csharp
            sanitized.Append(service.ClassName);
            sanitized.Append("__Factory");
            return sanitized.ToString();
```

**Sorun:** The unit-16 Finding 5 fix prefixes the namespace, but the parts used are `Namespace = symbol.ContainingNamespace.ToDisplayString()` (line 171) and `ClassName = symbol.Name` (line 170). For a nested type, ContainingNamespace skips the containing *type*, so `Game.Outer.Config` and `Game.Other.Config` both yield Namespace="Game", ClassName="Config" and therefore the same `Game_Config__Factory`. The generator emits `internal static class {factoryName}` (line 217) once per service with no dedup, so both land in namespace Strada.Generated.

**Senaryo:** `namespace Game { public class Outer { [AutoRegisterSingleton] public class Config { public Config(){} } } public class Other { [AutoRegisterSingleton] public class Config { public Config(){} } } }` -> the generated file contains `internal static class Game_Config__Factory` twice -> CS0101 'The namespace Strada.Generated already contains a definition for Game_Config__Factory'.

**Düzeltme:** Build the factory name from the full containing chain, e.g. sanitize `symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)` (which already includes containing types) instead of Namespace + Name, or append a short stable hash of the fully-qualified name.

## `bindingscope-track-missing-disposed-guard`

**BindingScope.Track and every Subscribe/Select/Where/CombineLatest/Computed/BindTwoWay helper omit the disposed-guard that Add has, so post-dispose registrations leak**

| | |
|---|---|
| Konum | `Runtime/Sync/BindingScope.cs:12` |
| Kategori | bug · sync-reactive |
| Etki | One permanently-live subscription plus its closure graph per post-dispose registration. |
| Test | NOT COVERED. Tests/Runtime/Sync/ReactiveExtensionsTests.cs:226-262 (BindingScopeTests) disposes the scope and then only mutates the source property to assert no further callbacks; it never calls a scope method after Dispose. |

```csharp
        {
            _disposables.Add(disposable);
            return disposable;
        }
```

**Sorun:** `Add` (lines 23-29) correctly handles late registration: `if (_disposed) { disposable.Dispose(); return disposable; }`. None of the other nine entry points do. `Track<T>` (12-16), `Subscribe` (32-35), `SubscribeAndInvoke` (38-41), `Select` (44-51), `Where` (54-61), `CombineLatest` (64-72), `Computed` x2 (75-93), `BindTwoWay` x2 (96-115) all append to `_disposables` unconditionally. Since Dispose sets `_disposed = true` and then calls `_disposables.Clear()` (lines 120, 125), anything added afterwards sits in a list that will never be walked again — the subscription is live and unreachable. `Track` additionally lacks the null-argument check that `Add` performs at line 25.

**Senaryo:** A MonoBehaviour's OnDestroy disposes its scope; an in-flight async continuation or a queued event handler then runs `_scope.Subscribe(model.Health, OnHealth)`. The subscription is created against model.Health, appended to the dead scope's list, and never removed. The destroyed MonoBehaviour is kept alive by the closure and its handler runs on every health change for the rest of the session, typically throwing MissingReferenceException each time it touches a Unity component.

**Düzeltme:** Route every helper through Add so the guard applies exactly once: replace each `_disposables.Add(x)` with `Add(x)`, and change `Track<T>` to `{ if (disposable == null) throw new ArgumentNullException(nameof(disposable)); if (_disposed) { disposable.Dispose(); return disposable; } _disposables.Add(disposable); return disposable; }`.

## `cb-dirty-latches-true-forever`

**ComponentBinding<TComponent,TProperty> and AutoSyncBinding set _dirty true and never reset it, so IsDirty latches permanently**

| | |
|---|---|
| Konum | `Runtime/Sync/ComponentBinding.cs:99` |
| Kategori | bug · sync-reactive |
| Etki | Correctness. Under the IsDirty-gated path: 1 missed update (the first) then zero filtering. |
| Test | NOT COVERED. Neither Tests/Runtime/Sync/BindingPropertyTests.cs nor Tests/Runtime/Sync/BridgeTests.cs ever reads IsDirty. No test asserts the flag's value before or after Sync on any of the three binding classes. |

```csharp
                    _dirty = true;
```

**Sorun:** `_dirty = true` is assigned at line 99 (ComponentBinding<TComponent,TProperty>.Sync) and line 199 (AutoSyncBinding.Sync); grep across the file shows no `_dirty = false` anywhere. The public `IsDirty` (lines 31 and 161) therefore transitions false->true exactly once and stays true for the object's lifetime. The sibling implementation in EntityView.cs:251 does the opposite (`_dirty = false` in Sync, true only via MarkDirty). Two classes implementing the same `IComponentBinding.IsDirty` contract (EntityView.cs:15) with inverted, mutually incompatible semantics.

**Senaryo:** A mediator-style binding is added to an EntityView via `AddBinding(binding)` (EntityView.cs:109-112). `SyncBindings()` gates on IsDirty (EntityView.cs:72). Before the first change the binding never syncs (IsDirty false — so the first change is missed entirely); after the first change it syncs every frame forever (IsDirty stuck true). The dirty filter therefore delivers the worst of both: it drops the first update and provides no filtering thereafter. Editor/Inspectors/EntityMediatorInspector.cs:186 reads the same flag and will show every binding as permanently dirty.

**Düzeltme:** Pick one protocol and apply it to all three implementations. Given the layer's pull model, the natural one is: `_dirty` means 'the last Sync observed a change', reset at the top of Sync (`_dirty = false;`) and set only when the inequality check succeeds — then IsDirty is a post-sync query, not a pre-sync gate, and EntityView.SyncBindings must stop using it as a gate (see markdirty-never-called-dirtyonly-dead).

## `computed-watchuntyped-silent-noop`

**ComputedProperty.WatchUntypedDependency silently ignores any argument that is not IReadOnlyReactiveProperty<>, producing a computed that never updates**

| | |
|---|---|
| Konum | `Runtime/Sync/ComputedProperty.cs:150` |
| Kategori | api-hazard · sync-reactive |
| Etki | Startup only for the reflection cost; the correctness consequence is permanent for the property's lifetime. |
| Test | NOT COVERED. Tests/Benchmarks/ComputedPropertyBenchmarks.cs:28-38 is the only FromMany caller and passes a correctly-typed `ReactiveProperty<int>[]`. No test passes a non-reactive object or null. |

```csharp
            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyReactiveProperty<>))
                {
```

**Sorun:** `FromMany(Func<T>, params object[] dependencies)` takes `object[]`, so the compiler accepts literally anything. WatchUntypedDependency scans the argument's interfaces and, if no IReadOnlyReactiveProperty<> is found, falls off the end of the loop at line 171 and returns without subscribing, without throwing, and without logging. A null element additionally throws NullReferenceException at line 147 (`dependency.GetType()`) rather than a meaningful ArgumentException.

**Senaryo:** `ComputedProperty<int>.FromMany(() => a.Value + b.Value + cfg.Bonus, a, b, cfg)` where cfg is a plain config object (or where a developer passes `a.Value` instead of `a` — an easy slip given the untyped signature). No exception. The computed is constructed with the correct initial value from the constructor's eager evaluation (line 40), so it looks right at startup and then never updates for that dependency. Debugging this means noticing that one of N inputs stopped propagating.

**Düzeltme:** Fail loudly: after the loop, `throw new ArgumentException($"FromMany dependency of type {type.Name} does not implement IReadOnlyReactiveProperty<>; it cannot be watched.", nameof(dependency));` and null-check the element before GetType. Better, replace the untyped API with typed overloads as recommended in computed-frommany-il2cpp-aot-break, which makes the whole class of mistake unrepresentable.

## `no-recursion-depth-guard-stackoverflow`

**No recursion depth guard anywhere in the reactive graph: a feedback loop produces an uncatchable StackOverflowException**

| | |
|---|---|
| Konum | `Runtime/Sync/ReactiveProperty.cs:105` |
| Kategori | bug · sync-reactive |
| Etki | No steady-state cost (one increment/decrement per notification). Without it: process termination, no log, no recovery. |
| Test | NOT COVERED. No test in Tests/Runtime/Sync/ creates a feedback loop — the closest is TwoWayBinding_SyncsBothDirections (ReactiveExtensionsTests.cs:265-281), which passes precisely because TwoWayBinding has the ad-hoc guard the rest of the graph lacks. |

```csharp
        public void Notify()
        {
            var snapshot = _handlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_value);
        }
```

**Sorun:** Notify invokes handlers synchronously with no depth counter and no cycle detection, and ComputedProperty.Invalidate (ComputedProperty.cs:185-196) recomputes and re-notifies synchronously on the same stack. Nothing in the graph can break a cycle. The authors clearly recognised the hazard for the one case they anticipated — TwoWayBinding's `_updating` flag (BindingScope.cs:137, 155, 163) exists solely to break a two-node cycle — and the prior audit records that the ECS reactive layer received a 'snapshot + depth guard' (2026-05-22-low-status-review.md:129, unit-06 #1/#2/#3), so the mitigation exists elsewhere in the codebase and was not applied here. StackOverflowException cannot be caught in .NET; it terminates the process, taking down the Editor or the player with no stack trace in the log.

**Senaryo:** `var display = ComputedProperty<int>.From(rawScore, v => Mathf.Clamp(v, 0, 9999)); display.Subscribe(v => rawScore.Value = v);` — a clamp-writeback, a very common pattern. `rawScore.Value = 20000` -> Notify -> Invalidate -> display = 9999 -> handler sets rawScore.Value = 9999 -> Notify -> Invalidate -> ... The comparer at ComputedProperty.cs:191 stops it only if the value stabilises; with any non-idempotent transform (increment, accumulate, alternate) it never does and the process dies. A three-node cycle across ReactiveProperties has no guard at all.

**Düzeltme:** Add a shared depth counter: `[ThreadStatic] private static int s_notifyDepth;` in a small internal static class, incremented in Notify/Invalidate under try/finally, and when it exceeds a constant (e.g. 32) log an error naming the property type and return instead of recursing. That converts an uncatchable process kill into a diagnosable log line.

## `rp-comparer-boxes-for-non-iequatable-structs`

**ReactiveProperty's Value setter uses EqualityComparer<T>.Default through an instance field, boxing on every set for struct T without IEquatable<T>**

| | |
|---|---|
| Konum | `Runtime/Sync/ReactiveProperty.cs:41` |
| Kategori | allocation · sync-reactive |
| Etki | One boxed T (sizeof(T)+16 bytes) per Value assignment for struct T lacking IEquatable<T>. Zero for primitives and Unity math types. |
| Test | NOT COVERED. Tests/Runtime/Sync/ReactivePropertyTests.cs and ReactivePropertyPropertyTests.cs exercise only `ReactiveProperty<int>` and `ReactiveProperty<string>` — both of which implement IEquatable and take the fast path — so the boxing case is never instantiated by any test. |

```csharp
                if (_comparer.Equals(_value, value))
                    return;
```

**Sorun:** For a struct T that does not implement IEquatable<T>, `EqualityComparer<T>.Default` (captured into the instance field at line 24) resolves to ObjectEqualityComparer<T>, whose Equals(T,T) dispatches through `object.Equals(object)` and must box the argument. Every assignment to Value pays this — including assignments of the SAME value, which is the case the check exists to make free. Storing the comparer in an instance field rather than reading the static property also guarantees a non-devirtualizable interface call on Mono/IL2CPP and adds 8 bytes per ReactiveProperty instance. This is strictly less severe than the object.Equals form in ComponentBinding.cs:96 because primitives, string and Unity's Vector2/3/4/Quaternion all implement IEquatable<T> and take the non-boxing path.

**Senaryo:** `public struct AmmoState { public int Clip; public int Reserve; }` with no IEquatable implementation — an entirely ordinary game struct. `_ammo.Value = newState;` from a weapon system running every frame boxes one AmmoState (16 bytes payload + 16 header = 32 bytes) per assignment, whether or not the value changed. 30 such properties updated per frame = ~960 bytes/frame = 57 KB/s of pure garbage on the 'no-op' path.

**Düzeltme:** Make the comparer a `private static readonly` field (shared per closed generic, removes the per-instance 8 bytes and lets the runtime hoist it), and document/analyzer-enforce that struct payloads implement IEquatable<T>. For a full fix, add an optional constructor overload accepting an `IEqualityComparer<T>` so callers can supply a non-boxing comparer for legacy structs.

## `vp-spawn-does-not-reset-transform`

**ViewPool.Spawn(Entity, Transform) reparents with worldPositionStays:false and never resets local TRS, so a reused view appears at its previous position**

| | |
|---|---|
| Konum | `Runtime/Sync/ViewPool.cs:80` |
| Kategori | bug · sync-reactive |
| Etki | Three transform writes per spawn (negligible). Without it: one frame of visible mispositioning per reuse, or permanent mispositioning for views with no position binding. |
| Test | MASKED BY TEST. Tests/Runtime/Sync/ViewPoolTests.cs:182-198 (ViewPool_SpawnWithPosition_SetsTransform) tests only the overload that explicitly sets position, so it passes while the bug lives in the other overload. ViewPool_RespawnFromPool_ReusesInstance (lines 113-130) reuses an instance but asserts only AreSame/TotalCreated, never the transform. |

```csharp
            view.transform.SetParent(parent ?? _activeRoot, false);
```

**Sorun:** `SetParent(parent, false)` preserves the transform's existing local position, rotation and scale. Despawn does the same on the way in (line 132, `view.transform.SetParent(_poolRoot, false)`), so whatever local TRS the view had when it died is carried through the pool and back out. The parameterless Spawn overload never writes a position, so the caller receives a view sitting wherever the previous user left it. Only the `Spawn(entity, position, rotation, parent)` overload (lines 93-98) fixes this, and it does so by writing world position AFTER the reparent. Prior audit POOL-01 flagged 'any MonoBehaviour state not explicitly reset in OnBind will persist' and was accepted as OPEN-BY-DESIGN (2026-05-22-medium-status-review.md, OPEN-BY-DESIGN #4) on the grounds that reset semantics belong to the user — but transform state is the pool's own doing here, since the pool is what reparents.

**Senaryo:** An enemy dies at (150, 0, 320) and is despawned. Later `pool.Spawn(newEntity)` is called and the ECS system is expected to drive position on the next sync. For the one frame between Spawn and the first sync — and permanently if the view has no position binding, e.g. a UI worldspace nameplate positioned by its parent — the enemy renders at (150, 0, 320) instead of its intended spawn point. Players see a one-frame teleport artifact at the previous corpse's location on every reuse.

**Düzeltme:** Reset local TRS on spawn before Bind: after line 80 add `view.transform.localPosition = Vector3.zero; view.transform.localRotation = Quaternion.identity; view.transform.localScale = _prefab.transform.localScale;`. The position overload then overwrites world pose as it already does.

## `vp-duplicate-entity-overwrites-index-map`

**ViewPool.Spawn overwrites the entity->index map when two views are spawned for one entity, corrupting the swap-remove bookkeeping**

| | |
|---|---|
| Konum | `Runtime/Sync/ViewPool.cs:86` |
| Kategori | bug · sync-reactive |
| Etki | No allocation. Silent active-list corruption: a live view enters the free pool while still parented and rendering. |
| Test | NOT COVERED. Tests/Runtime/Sync/ViewPoolTests.cs always creates a fresh entity per spawn (lines 53, 70, 86, 119/123, 154, 188, 207). No test spawns twice for the same entity. |

```csharp
            var entityKey = GetEntityKey(entity);
            _entityToActiveIndex[entityKey] = _active.Count;
            _active.Add(view);
```

**Sorun:** `_entityToActiveIndex` is a one-to-one map keyed by entity, but nothing prevents two Spawn calls with the same Entity. The second call silently overwrites the first view's index. The first view is now in `_active` with no map entry, and `Despawn(firstView)` (line 113) looks up the shared key, finds the SECOND view's index, and swap-removes the wrong element — leaving the second view's map entry pointing at whatever got swapped in. `Despawn(Entity)` at line 147 then indexes `_active[index]` from that corrupted map.

**Senaryo:** An entity is given both a body view and a healthbar view from two pools — or, more commonly, a spawn is issued twice for the same entity due to a duplicated event. `pool.Spawn(e); pool.Spawn(e);` -> `_entityToActiveIndex[key] = 1`, `_active = [v1, v2]`. `pool.Despawn(v1)` -> lookup yields index 1 -> since 1 == lastIndex it just RemoveAt(1), removing v2 from `_active` while v1 stays active and v2 (still visible, still bound) is pushed into `_available`. v2 is subsequently handed out by Spawn while its GameObject is still parented under the active root and rendering.

**Düzeltme:** Reject or handle duplicates explicitly: `if (_entityToActiveIndex.ContainsKey(entityKey)) throw new InvalidOperationException($"ViewPool<{typeof(TView).Name}> already has an active view for {entity}");` — or, if multiple views per entity is intended, change the map to `Dictionary<long, List<int>>` and make Despawn(Entity) despawn all of them. Independently, Despawn(TView) should locate the view by identity (`_active.IndexOf(view)`) rather than trusting the entity key.

## `no-blackhole-consume-pattern`

**No benchmark consumes its result — the ns-scale measurements have no protection against dead-code elimination**

| | |
|---|---|
| Konum | `Tests/Benchmarks/ComputedPropertyBenchmarks.cs:90` |
| Kategori | test-gap · tests-bench |
| Etki | Affects every sub-20ns published figure: 4ns dispatch, ~5ns cached ComputedProperty read, 6.6ns/entity query, ~2ns ReactiveProperty read. These are the numbers the competitive claims rest on. |
| Test | ComputedProperty_ValueAccess_Performance (line 74) has zero assertions. Benchmark_Query_SingleComponent_100k asserts only `iterCount`, never `sum`. Benchmark_GetComponent_100k asserts neither. |

```csharp
            int sum = 0;
            for (int i = 0; i < iterations; i++)
            {
                sum += computed.Value;
            }
```

**Sorun:** `sum` is written 10,000 times and then never read — it is a provably dead store, and the loop body's arithmetic is legally removable. The method contains no assertion at all (0 `Assert.` in the file), so nothing roots the value. The same shape recurs across the suite: ECSPerformanceTests.cs:155/161 declares `float sum = 0;` and accumulates `sum += p.X + p.Y + p.Z;` inside the very ForEach that produces the published 6.6ns/entity, and `sum` is never asserted (only `iterCount` is); ECSPerformanceTests.cs:432/438 does the same for the 67ns GetComponent number; ContainerBenchmarks.cs:32 discards `_container.Resolve<TestService>()` entirely; EventBusBenchmarks.cs:27 subscribes `e => { }`. There is no blackhole/consume helper anywhere in Tests/.

**Senaryo:** Documentation~/Benchmarks.md:308 publishes "ComputedProperty Read (cached) ~5ns". A 5ns figure for a property read that walks a cache-validity flag on a class instance is precisely the range in which an eliminated loop body lands. Because nothing consumes `sum`, the number cannot be distinguished from a partially-eliminated loop, and the same doubt attaches to the 4ns MessageBus dispatch (README:49) and 6.6ns/entity query claims. (Mono's JIT does not do aggressive interprocedural DCE, so total elimination of a cross-method call is unlikely — but the suite provides zero evidence either way, and the published numbers sit exactly where that distinction matters.)

**Düzeltme:** Add a `Blackhole` sink — e.g. `[MethodImpl(MethodImplOptions.NoInlining)] public static void Consume<T>(T v) { if (v == null && DateTime.UtcNow.Ticks == 42) throw new Exception(); }` — and pass every benchmark result to it, or assert on the accumulated value (`Assert.AreNotEqual(0f, sum)`), which also roots it. Do this for at least the ComputedProperty read, ECS query, GetComponent and MessageBus dispatch benchmarks.

## `property-tests-no-replay-seed`

**Property tests have no Replay seed — a failure (once the runner is fixed) cannot be reproduced**

| | |
|---|---|
| Konum | `Tests/Runtime/Generators/StradaArbitraries.cs:54` |
| Kategori | test-gap · tests-bench |
| Etki | All 71 property tests; no reproduction path for any property failure. |
| Test | No test asserts reproducibility. |

```csharp
        public static Configuration CreateConfig(int maxTest = DefaultMaxTest)
```

**Sorun:** `CreateConfig` sets only `MaxNbOfTest`. FsCheck's `Configuration.Replay` (an `FsCheck.Random.StdGen`) is left null, so each run seeds from the clock. There is also no `StartSize`/`EndSize` control and no per-test seed override. Combined with the non-throwing runner (see fscheck-nonthrowing-runner), a falsification today prints a seed to the console that nobody reads; once the runner is fixed, an intermittent failure will be irreproducible from the CI log alone unless the seed is captured.

**Senaryo:** After fixing the runner, `ComponentPropertyTests` fails once in CI on FsCheck case 47 with a specific generated TestComponent. The engineer re-runs locally, gets a different seed, and the failure does not reproduce. The bug is closed as flaky.

**Düzeltme:** Accept an optional seed and default it deterministically: `public static Configuration CreateConfig(int maxTest = DefaultMaxTest, int? seed = null) => new Configuration { MaxNbOfTest = maxTest, Runner = Configuration.QuickThrowOnFailure.Runner, Replay = seed.HasValue ? FsCheck.Random.StdGen.NewStdGen(seed.Value, 0) : null };` and log the effective seed on failure so a CI failure is replayable.

## `parallel-async-benchmark-is-not-parallel`

**Benchmark_ParallelAsyncSignals dispatches ten synchronous handlers on the calling thread — nothing runs in parallel**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/AsyncPerformanceTests.cs:156` |
| Kategori | test-gap · tests-bench |
| Etki | 0 of 10,000 dispatches in this benchmark execute concurrently or allocate an async state machine. |
| Test | Tests/Runtime/Communication/AsyncEventBusTests.cs covers async functionality (it uses `await Task.Delay(100, ct)`), but no performance test measures a yielding handler. |

```csharp
                    tasks[j] = _bus.SendAsync(new AsyncSignal { Value = 1 });
```

**Sorun:** The registered handler (lines 144-148) is `(s, ct) => { Interlocked.Increment(ref counter); return default; }` — `default` is an already-completed `ValueTask`. So each `SendAsync` runs its handler synchronously on the calling thread and returns a completed ValueTask before the loop advances. By the time the `await tasks[j]` loop at lines 158-161 runs, all ten have already finished. No continuation is ever scheduled, no thread pool work item is queued, and the `Interlocked.Increment` guards against a race that cannot occur. The benchmark is named "Parallel SendAsync" and logged as such (line 167).

**Senaryo:** A reader takes "Parallel SendAsync: 1000 batches of 10" as evidence that concurrent async dispatch is cheap and safe. In reality neither the concurrent-dispatch path nor the async state-machine path (a handler that actually yields) is measured anywhere — `Benchmark_100k_AsyncSignalDispatches_Synchronous` and `Benchmark_10k_AsyncQueryDispatches_Synchronous` are both explicitly synchronous too, as their names admit. There is no benchmark of a genuinely-yielding async handler.

**Düzeltme:** Rename to `Benchmark_BatchedAsyncSignals_SyncHandler`, and add a companion benchmark whose handler actually yields (`await Task.Yield();` or `await Task.Delay(0, ct)`) so the state-machine allocation and continuation-scheduling cost of the async path is measured. Drive genuine concurrency with `Task.Run` if a parallel-dispatch benchmark is wanted.

## `nativearray-temp-leak-on-exception`

**Simulation_CacheThrashing allocates a 400 KB Allocator.Temp NativeArray with no try/finally — an exception in the loop leaks it and trips Unity's leak detector for the rest of the run**

| | |
|---|---|
| Konum | `Tests/Runtime/Performance/RealisticSimulationTests.cs:65` |
| Kategori | bug · tests-bench |
| Etki | 400 KB leaked on any failure path; the 500 ms assertion permits 5 μs per random component access (roughly 75x the published 67ns GetComponent figure). |
| Test | Simulation_CacheThrashing is the only random-access ECS benchmark; Simulation_MixedReadWrite (line 40) has the same n=1/no-warmup profile with a 50 ms bound. |

```csharp
            var randomIndices = new NativeArray<int>(EntityCount, Allocator.Temp);
```

**Sorun:** `EntityCount` is 100_000, so this is a 400 KB `Allocator.Temp` allocation — far above what Temp's small-block fast path is designed for. `randomIndices.Dispose()` is called unconditionally at line 88, after the measured loop, with no `using` and no `try/finally`. Any throw inside the 100,000-iteration loop at lines 73-85 (e.g. `GetComponentRef` throwing on a stale entity) skips the Dispose. The test also asserts nothing about correctness — `p.X += 1` at line 82 is never verified, and the only assertion is `Assert.Less(stopwatch.Elapsed.TotalMilliseconds, 500.0)` (5μs per random access, a bound so loose it cannot fail).

**Senaryo:** A regression makes `GetComponentRef<Position>` throw for one of the 100k random indices. The test fails with that exception, the NativeArray is never disposed, and Unity's native leak detection reports "A Native Collection has not been disposed" — a diagnostic that then attaches to whichever test happens to run next, misdirecting the investigation.

**Düzeltme:** Use `using var randomIndices = new NativeArray<int>(EntityCount, Allocator.Persistent);` (Persistent is the right allocator for a 400 KB buffer held across a long synchronous loop) and add a correctness assertion, e.g. accumulate the touched indices and assert the expected number of `Exists` hits.
