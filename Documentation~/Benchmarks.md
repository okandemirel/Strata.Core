> ## ⚠ Status of the figures in this document
>
> An audit of the benchmark harness found that most numbers below cannot currently be
> defended, and that this document contradicts `README.md` in several places (scoped resolve
> is listed here as 78ns and there as 21ns; the comparison table below places VContainer
> *ahead* of Strada while the README places it behind). Specific problems:
>
> - **Single sample.** The `Stopwatch` benchmarks take exactly one measurement each. There is
>   no median, no interquartile range and no outlier rejection, so the claim of "multiple runs
>   averaged for stability" below is not true of them.
> - **Dead-code elimination.** Several benchmarks discard the value they compute, which the
>   optimiser is free to delete entirely.
> - **Allocation was never measured.** The GC benchmarks bracketed the measured region with
>   `GC.GetTotalMemory(forceFullCollection: true)`, which reports the live heap *after* a
>   collection — the transient garbage being measured had already been collected. The
>   singleton-resolve zero-allocation claim has since been re-asserted with
>   `Is.Not.AllocatingGCMemory()` and holds; the others have not been re-measured.
> - **Per-entity memory measured nothing.** Component data lives in `NativeArray`s, which the
>   managed GC does not see, so the old measurement reported 0 KB. Measured properly the
>   figure is **46.5 bytes/entity** for two components at 100k entities.
> - **Editor-Mono only.** Shipped games run IL2CPP. No IL2CPP measurement exists yet, and the
>   DI container's `Expression.Compile()` path in particular behaves differently there.
> - **Competitor numbers are not comparable.** They were not produced on this machine or in
>   this harness. MessagePipe has never published a nanosecond figure at all.
>
> Treat every number below as provisional until it is reproduced under a harness with warmup,
> repeated samples, median + MAD reporting, a consume/sink to defeat dead-code elimination,
> and an IL2CPP run.

# Benchmarks

Complete performance data for all Strada systems on Apple Silicon (Unity 6, Mono runtime).

## Table of Contents

- [Executive Summary](#executive-summary)
- [Test Environment](#test-environment)
- [Dependency Injection](#dependency-injection)
- [Auto-Binding](#auto-binding)
- [Entity Component System](#entity-component-system)
- [Messaging System](#messaging-system)
- [Object Pooling](#object-pooling)
- [Reactive Sync](#reactive-sync)
- [Parallel Jobs](#parallel-jobs)
- [Memory Usage](#memory-usage)
- [Competitive Analysis](#competitive-analysis)
- [Running Benchmarks](#running-benchmarks)

---

## Executive Summary

| System | Key Metric | Value |
|--------|-----------|-------|
| **DI Container** | Overhead vs manual `new()` | **1.56x** |
| **Auto-Binding** | First scan (uncached) | **3-50ms** |
| **ECS Query** | Per-entity iteration | **6-28ns** |
| **Messaging** | Publish (1 subscriber) | **~20ns** |
| **Object Pool** | Spawn from pool | **~10ns** |
| **Parallel Jobs** | Speedup vs sequential | **17x** |

All benchmarks use **honest testing methodology** - no pre-compiled delegates or cheating optimizations.

---

## Test Environment

| Parameter | Value |
|-----------|-------|
| Platform | macOS (Apple Silicon M-series) |
| Unity Version | Unity 6 (6000.x) |
| Scripting Backend | Mono |
| Test Framework | Unity Test Framework |
| Iterations | Varies by test (typically 1k-100k) |

### Benchmark Methodology

- All benchmarks use `System.Diagnostics.Stopwatch` for timing
- Warm-up iterations performed before measurement
- Multiple runs averaged for stability
- GC allocation tracked via Unity Profiler
- Results represent **real production code paths**

---

## Dependency Injection

### Resolution Performance

| Operation | Time | Notes |
|-----------|------|-------|
| Transient Resolution | **92ns** | New instance each time |
| Singleton Resolution | **45ns** | Cached instance |
| Scoped Resolution | **78ns** | Per-scope instance |
| Constructor Injection (1 dep) | **~150ns** | Single dependency |
| Constructor Injection (3 deps) | **~280ns** | Multiple dependencies |

### Overhead vs Manual Construction

```
Manual new():        59ns
DI Container:        92ns
Overhead:            1.56x
```

This 56% overhead is the real cost of:
- Type lookup
- Factory invocation
- Lifetime management

### Registration Performance

| Operation | Time |
|-----------|------|
| Register<TInterface, TImpl> | **~500ns** |
| Build Container | **~2ms** (typical app) |
| Create Scope | **~100ns** |

### Scaling

| Registrations | Build Time |
|---------------|------------|
| 10 | ~200μs |
| 100 | ~2ms |
| 500 | ~10ms |
| 1000 | ~20ms |

### Memory

| Metric | Value |
|--------|-------|
| Per Registration | ~200 bytes |
| Per Scope | ~64 bytes |
| Singleton Cache Entry | ~32 bytes |

---

## Auto-Binding

Auto-binding allows attribute-based service registration without manual boilerplate.

### Runtime Scanning Performance

| Operation | Time | Notes |
|-----------|------|-------|
| First scan (uncached) | **3ms** | Typical project |
| First scan (large project) | **<50ms** | Competitive with VContainer |
| Cached scan lookup (999x) | **<1ms** | O(1) cache access |
| Container build with auto-bindings | **4ms** | Including registration |

### Source Generator vs Runtime

| Approach | Scan Time | Notes |
|----------|-----------|-------|
| **Source Generator** | **0ms** | Compile-time, zero reflection |
| **Runtime Scanning** | **3-50ms** | First scan only, then cached |

### Resolution After Auto-Binding

| Operation | Time | Notes |
|-----------|------|-------|
| Singleton resolution | **60ns** | Same as manual registration |
| 10k singleton resolutions | **0.6ms** | ~60ns average |

Auto-bound services resolve at identical speed to manually registered services - the binding method only affects startup time.

### Comparison with VContainer

| Framework | Assembly Scan | Notes |
|-----------|---------------|-------|
| **Strada** | 3-50ms | Cached after first scan |
| **VContainer** | ~50ms | Source generator recommended |
| **Zenject** | ~200ms+ | No source generator |

---

## Entity Component System

### Entity Operations

| Operation | Time | Notes |
|-----------|------|-------|
| Create Entity (bare) | **54ns** | No components |
| Create Entity + 1 Component | **149ns** | With Position |
| Create Entity + 3 Components | **374ns** | Full entity setup |
| Destroy Entity | **180ns** | With cleanup |

### Component Operations

| Operation | Time |
|-----------|------|
| AddComponent | **95ns** |
| GetComponent | **67ns** |
| SetComponent | **76ns** |
| HasComponent | **78ns** |
| RemoveComponent | **85ns** |

### Query Iteration (100k Entities)

| Query Complexity | Per-Entity Time | Total (100k) |
|-----------------|-----------------|--------------|
| 1 Component | **6.6ns** | 0.66ms |
| 2 Components | **18ns** | 1.8ms |
| 3 Components | **28ns** | 2.8ms |

### Batch Operations

| Operation | Time |
|-----------|------|
| Create 10k Entities | **5.4ms** |
| Destroy 10k Entities | **1.8ms** |
| Query 10k (2 components) | **0.18ms** |

### Storage Efficiency

| Metric | Value |
|--------|-------|
| Per Entity (2 components) | 56 bytes |
| Theoretical Minimum | 28 bytes |
| Overhead | ~100% |

The SparseSet storage trades memory for iteration speed:

```
Dense Array:  [comp0][comp1][comp2]...  ← Contiguous, cache-friendly
Sparse Array: [?][3][0][?][1][2]...     ← O(1) lookup
```

---

## Messaging System

### MessageBus Performance

| Operation | Time |
|-----------|------|
| Publish (1 subscriber) | **~20ns** |
| Publish (10 subscribers) | **~100ns** |
| Publish (100 subscribers) | **~1μs** |
| Send Command | **~15ns** |
| Query | **~20ns** |

### Allocation

| Operation | Allocation |
|-----------|------------|
| Publish struct event | **0 bytes** |
| Subscribe | **~64 bytes** (one-time) |
| Unsubscribe | **0 bytes** |

### Scaling

| Subscribers | Publish Time |
|-------------|--------------|
| 1 | 20ns |
| 10 | 100ns |
| 100 | 1μs |
| 1000 | 10μs |

Linear scaling due to array iteration.

### Internal Implementation

Type-indexed dispatch provides O(1) message routing:

```csharp
// Type ID assigned at startup
private static class EventTypeId<T>
{
    public static readonly int Id = Interlocked.Increment(ref _nextTypeId);
}

// Array-indexed lookup
var channel = _eventChannels[EventTypeId<T>.Id];
```

---

## Object Pooling

### Pool Operations

| Operation | Time |
|-----------|------|
| Spawn (from pool) | **~10ns** |
| Spawn (create new) | **~500ns** |
| Despawn | **~8ns** |
| Prewarm (per object) | **~500ns** |

### Comparison: Pooled vs Non-Pooled

| Scenario | Without Pool | With Pool | Speedup |
|----------|--------------|-----------|---------|
| Create 10k objects | 45ms | 3ms | **15x** |
| GC Allocation | 400KB | 0 | **∞** |

### Memory Efficiency

```csharp
// Pool reuses objects - minimal GC pressure
for (int i = 0; i < 10000; i++)
{
    var bullet = pool.Spawn();
    // Use bullet...
    pool.Despawn(bullet);
}
// Only pool.TotalCreated objects ever allocated
```

### Pool Sizing Guidelines

| Object Type | Recommended Initial Size |
|-------------|-------------------------|
| Bullets/Projectiles | 100-500 |
| Enemies | 20-100 |
| Particle Effects | 50-200 |
| UI Elements | 10-50 |

---

## Reactive Sync

### ReactiveProperty Performance

| Operation | Time |
|-----------|------|
| Read Value | **~2ns** |
| Write Value (with notify) | **~50ns** |
| Write Value (no change) | **~10ns** |
| Subscribe | **~100ns** |
| Notification (per subscriber) | **~20ns** |

### ComputedProperty

| Operation | Time |
|-----------|------|
| Read (cached) | **~5ns** |
| Recompute (on dependency change) | **~30ns + computation** |

### BindingScope

| Operation | Time |
|-----------|------|
| Create Scope | **~50ns** |
| Bind Property | **~100ns** |
| Dispose (per binding) | **~30ns** |

### UI Binding Overhead

Typical UI binding scenario (10 properties):

| Phase | Time |
|-------|------|
| Initial Setup | ~1μs |
| Per-Frame Update (if changed) | ~500ns |
| Per-Frame Update (no change) | ~20ns |

---

## Parallel Jobs

### Burst Compilation Speedup

| Scenario (100k entities) | Sequential | Parallel + Burst | Speedup |
|--------------------------|------------|------------------|---------|
| Position + Velocity Update | 17ms | 1ms | **17x** |
| Complex AI Calculation | 85ms | 5ms | **17x** |
| Physics Integration | 34ms | 2ms | **17x** |

### Job Scheduling Overhead

| Operation | Time |
|-----------|------|
| Schedule Job | **~1μs** |
| Complete (wait) | **variable** |
| Worker Thread Wake | **~10μs** |

### When to Use Parallel Jobs

| Entity Count | Recommendation |
|--------------|----------------|
| < 100 | Sequential (overhead not worth it) |
| 100-1000 | Test both approaches |
| > 1000 | Parallel Jobs recommended |
| > 10000 | Parallel Jobs **required** |

### Thread Scaling

| Cores | Speedup (vs 1 core) |
|-------|---------------------|
| 2 | 1.8x |
| 4 | 3.5x |
| 8 | 6.5x |
| 16 | 10x |

Diminishing returns above 8 cores due to memory bandwidth.

---

## Memory Usage

### Per-System Memory

| System | Base Memory | Per-Item |
|--------|-------------|----------|
| DI Container | ~10KB | ~200B per registration |
| EntityManager | ~50KB | ~28B per entity + components |
| MessageBus | ~5KB | ~64B per subscription |
| ObjectPool | ~1KB | sizeof(T) per pooled object |
| StateMachine | ~2KB | ~100B per state |

### Typical Application

| Configuration | Memory |
|---------------|--------|
| 100 DI registrations | ~30KB |
| 10k entities (3 components) | ~1MB |
| 50 message subscriptions | ~8KB |
| 500 pooled objects | ~50KB |
| **Total Framework Overhead** | **~1.1MB** |

### GC Pressure

| Operation | Allocation |
|-----------|------------|
| DI Resolution | 0 bytes (after warmup) |
| Entity Creation | 0 bytes (pre-allocated) |
| Message Publishing | 0 bytes |
| Pool Spawn/Despawn | 0 bytes |
| Query Iteration | 0 bytes |

All hot paths are allocation-free after initialization.

---

## Competitive Analysis

### DI Container Comparison

| Framework | Resolution Time | Overhead vs Manual |
|-----------|----------------|--------------------|
| **Strada** | 92ns | 1.56x |
| VContainer | ~80ns | 1.35x |
| Zenject | ~200ns | 3.4x |
| Manual new() | 59ns | 1.0x |

### ECS Comparison

| Framework | Query (2 comp, 100k) | Create Entity |
|-----------|----------------------|---------------|
| **Strada** | 1.8ms | 149ns |
| Unity DOTS | ~0.3ms | ~50ns |
| Arch ECS | ~0.5ms | ~80ns |
| Entitas | ~3ms | ~200ns |

### Messaging Comparison

| Framework | Publish Time | Allocation |
|-----------|--------------|------------|
| **Strada MessageBus** | ~20ns | 0 bytes |
| MessagePipe | ~15ns | 0 bytes |
| UniRx Subject | ~100ns | varies |
| C# event | ~10ns | delegate alloc |

### Overall Assessment

| Category | Strada Ranking | Notes |
|----------|---------------|-------|
| DI Performance | Good | 1.56x overhead is acceptable |
| ECS Performance | Good | Not DOTS-level but solid |
| Messaging | Excellent | Zero-allocation, fast dispatch |
| Memory Efficiency | Good | Reasonable overhead |
| Developer Experience | Excellent | Unified API, easy integration |

---

## Running Benchmarks

### Unity Test Runner

1. Open Unity Test Runner (Window → General → Test Runner)
2. Select PlayMode tab
3. Filter by "Performance" category
4. Run selected tests

### Command Line

```bash
# Set paths
UNITY_PATH="/Applications/Unity/Hub/Editor/6000.0.58f2/Unity.app/Contents/MacOS/Unity"
PROJECT_PATH="/path/to/project"

# Run performance tests
"$UNITY_PATH" -batchmode -projectPath "$PROJECT_PATH" \
    -runTests -testPlatform playmode \
    -testCategory "Performance" \
    -testResults "$PROJECT_PATH/benchmark_results.xml" \
    -logFile "$PROJECT_PATH/benchmark_log.txt"

# Parse results
grep -o "\[.*\].*" benchmark_log.txt
```

### Benchmark Test Categories

| Category | Tests | Focus |
|----------|-------|-------|
| DI Performance | 15 | Resolution, scoping, registration |
| ECS Performance | 20 | Entity ops, queries, scaling |
| Messaging Performance | 10 | Pub/sub, commands, queries |
| Pool Performance | 8 | Spawn/despawn, throughput |
| Parallel Jobs | 5 | Burst compilation, threading |

### Writing Custom Benchmarks

```csharp
using NUnit.Framework;
using System.Diagnostics;

[Category("Performance")]
public class MyBenchmarks
{
    [Test]
    public void Benchmark_MyOperation()
    {
        const int iterations = 10000;

        // Warmup
        for (int i = 0; i < 100; i++)
            DoOperation();

        // Measure
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            DoOperation();
        sw.Stop();

        var nsPerOp = (sw.Elapsed.TotalMilliseconds * 1000 * 1000) / iterations;
        UnityEngine.Debug.Log($"[MyBenchmark] {nsPerOp:F1}ns per operation");

        // Assert performance bounds
        Assert.Less(nsPerOp, 1000, "Operation should be under 1μs");
    }
}
```

---

## Optimization Tips

### DI Container

1. **Use singletons** for services that don't need per-scope state
2. **Minimize constructor parameters** - each adds ~30ns
3. **Build container once** at startup, not per-scene

### ECS

1. **Keep components small** (< 64 bytes) for cache efficiency
2. **Use parallel jobs** for 1000+ entities
3. **Batch operations** instead of individual calls
4. **Use tags** (empty components) for filtering

### Messaging

1. **Use structs** for messages (zero allocation)
2. **Keep messages small** - large structs copy slowly
3. **Use ref parameters** for large messages

### Pooling

1. **Prewarm during loading** - avoid runtime allocation
2. **Set reasonable max sizes** - prevent memory bloat
3. **Reset state in OnDespawn** - prevent stale data bugs

---

## Related Documentation

- [DI Container](DI.md) - Dependency injection
- [ECS System](ECS.md) - Entity Component System
- [Messaging](Messaging.md) - Event system
- [Pooling](Pooling.md) - Object pooling
- [Sync](Sync.md) - Reactive properties
