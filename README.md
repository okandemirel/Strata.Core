# Strada Framework

**A high-performance Unity framework unifying Patterns architecture with ECS simulation**

> **Language**: [English](README.md) | [日本語](README.ja.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [한국어](README.ko.md)

[![Tests](https://img.shields.io/badge/tests-564%20passing-brightgreen)]()
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-blue)]()
[![.NET](https://img.shields.io/badge/.NET-Standard%202.1-purple)]()

Strada combines enterprise-grade dependency injection with performance-critical ECS, wrapped in a clean modular architecture. Build UI with familiar patterns while using ECS for high-performance simulation—without choosing between paradigms.

---

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Performance](#performance)
- [Documentation](#documentation)
- [Architecture](#architecture)
- [API Reference](#api-reference)
- [Testing](#testing)
- [License](#license)

---

## Features

### Dependency Injection ([docs](Documentation~/DI.md))
- **Container**: Expression tree compiled factories (1.56x manual `new()` overhead)
- **Lifetimes**: Singleton, Transient, Scoped with thread-safe initialization
- **Disposal**: LIFO disposal order (dependents disposed before dependencies)
- **Auto-Binding**: Attribute-based service registration with `[AutoRegister]`, `[AutoRegisterSingleton]`, etc.
- **Circular Detection**: Build-time cycle detection prevents runtime errors
- **Zero-alloc Resolution**: No GC allocation for singleton/scoped paths
- **Thread-Safe**: Volatile reads, ConcurrentDictionary, and lock-based disposal safety

### Entity Component System ([docs](Documentation~/ECS.md))
- **SparseSet Storage**: Cache-friendly component iteration (6-28ns per entity)
- **Query System**: `ForEach<T1...T16>()` - up to 8 hand-written, 9-16 source-generated
- **Safety**: `EntityCommandBuffer` for safe structural changes during iteration
- **Parallel Jobs**: Burst-compiled jobs with 17x speedup over sequential
- **Entity Recycling**: Automatic index reuse with version tracking
- **Source Generation**: Compile-time query generation for 9-16 components

### Messaging ([docs](Documentation~/Messaging.md))
- **MessageBus**: Unified command/query/event bus with array-indexed dispatch (~15-20ns/dispatch)
- **Pooled Commands**: Execute ICommand objects with automatic pool return
- **Zero-alloc Publish**: Struct-based messages, no boxing
- **Exception Isolation**: Handler failures don't interrupt other subscribers

### Patterns-ECS Sync ([docs](Documentation~/Sync.md))
- **Event-Driven Integration**: ECS systems publish ComponentChanged events, Patterns controllers subscribe
- **EntityMediator**: Binds ECS entities to UI views with auto-sync and MessageBus integration
- **Bidirectional Flow**: Controllers send commands to ECS via MessageBus, receive events back

### Reactive Bindings ([docs](Documentation~/Sync.md))
- **ReactiveProperty**: Observable values with change notification
- **ReactiveCollection**: Observable lists with add/remove/clear events
- **ComputedProperty**: Derived values with automatic dependency tracking

### Modular Architecture ([docs](Documentation~/Modules.md))
- **ModuleConfig**: ScriptableObject-based module configuration
- **Inspector Systems**: Configure ECS systems via drag-and-drop
- **IModuleBuilder**: VContainer-like fluent API for DI registration
- **System Discovery**: Auto-find systems with `[StradaSystem]` attribute
- **Priority Ordering**: Control module initialization order

### Utilities
- **ObjectPool**: Generic pooling with lifecycle hooks (Spawn/Despawn)
- **StateMachine**: Type-safe FSM with conditional transitions
- **TimerService**: Managed timers with pause/resume support

---

## Installation

Add to your Unity project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.strada.core": "file:../Packages/com.strada.core"
  }
}
```

Or copy the `Packages/com.strada.core` folder directly into your project.

**Requirements:**
- Unity 6000.0+ (Unity 6)
- .NET Standard 2.1

---

## Quick Start

### Dependency Injection

```csharp
using Strada.Core.DI;
using Strada.Core.DI.Attributes;

// Option 1: Manual registration
var builder = new ContainerBuilder();
builder.Register<IPlayerService, PlayerService>(Lifetime.Singleton);
builder.Register<IInputService, InputService>(Lifetime.Singleton);
using var container = builder.Build();

// Option 2: Auto-binding with attributes
[AutoRegisterSingleton(As = typeof(IPlayerService))]
public class PlayerService : IPlayerService { }

[AutoRegisterTransient]
public class EnemyController { }

// Auto-register all attributed types
var builder = new ContainerBuilder();
builder.RegisterAutoBindings();  // Scans for [AutoRegister*] attributes
using var container = builder.Build();
```

### ECS System

```csharp
using Strada.Core.ECS;
using Strada.Core.ECS.Query;

// Define components (must be unmanaged structs)
public struct Position : IComponent { public float X, Y, Z; }
public struct Velocity : IComponent { public float X, Y, Z; }
public struct Health : IComponent { public int Current, Max; }
public struct Damage : IComponent { public int Value; }

// Query up to 8 components (hand-written, optimal performance)
entityManager.ForEach<Position, Velocity, Health, Damage>(
    (int entity, ref Position pos, ref Velocity vel, ref Health hp, ref Damage dmg) =>
    {
        pos.X += vel.X * deltaTime;
    });

// Query 9-16 components (source-generated)
entityManager.ForEach<T1, T2, T3, T4, T5, T6, T7, T8, T9>(...);

// Or use SystemBase for cleaner code
public class MovementSystem : SystemBase<Position, Velocity>
{
    protected override void OnUpdateEntity(int entity, ref Position pos, ref Velocity vel, float dt)
    {
        pos.X += vel.X * dt;
        pos.Y += vel.Y * dt;
        pos.Z += vel.Z * dt;
    }
}
```

### Messaging

```csharp
using Strada.Core.Communication;

// Define messages as structs
public struct PlayerDamaged { public int EntityId; public int Damage; }
public struct SpawnEnemy { public float X, Y; }

// Setup bus
var bus = new MessageBus();

// Subscribe to events
bus.Subscribe<PlayerDamaged>(e => Debug.Log($"Player took {e.Damage} damage"));

// Publish events (zero allocation)
bus.Publish(new PlayerDamaged { EntityId = 1, Damage = 10 });

// Register command handlers
bus.RegisterCommandHandler<SpawnEnemy>(cmd => SpawnEnemyAt(cmd.X, cmd.Y));
bus.Send(new SpawnEnemy { X = 10, Y = 20 });
```

### Reactive Properties

```csharp
using Strada.Core.Sync;

// Create reactive property
var health = new ReactiveProperty<int>(100);

// Subscribe to changes
health.Subscribe(value => healthBar.SetValue(value));

// Changes automatically notify subscribers
health.Value = 75; // healthBar updates automatically
```

---

## Performance

Measured on Apple Silicon, Unity 6, **Mono, in the Editor**.

> **Read these numbers with the following caveats.** They are Editor-Mono figures; shipped
> games run IL2CPP, where the DI container's `Expression.Compile()` path behaves differently
> and no IL2CPP measurement has been taken yet. Most timings below come from single-sample
> `Stopwatch` benchmarks with no median or outlier rejection, so they carry unknown variance.
> Only the entries explicitly marked as asserted are currently backed by a measurement that
> can fail. See `Documentation~/Benchmarks.md` for per-claim status.

### DI Container

| Operation | Time | Notes |
|-----------|------|-------|
| Simple Transient | **0.11μs** | Single class, no dependencies |
| 4-Level Deep Chain | **0.27μs** | A→B→C→D dependency chain |
| Wide Service (5 deps) | **0.42μs** | Class with 5 injected dependencies |
| Singleton Lookup | **61ns** | Already-created singleton |
| Scoped Lookup | **21ns** | Within existing scope |
| Container Build (100 types) | **~2ms** | ~20μs per registration |
| **vs Manual `new()`** | **1.56x** | Competitive with best Unity DI |

### ECS

| Operation | Time | Notes |
|-----------|------|-------|
| Entity Creation | **54ns** | Bare entity |
| Entity + 3 Components | **374ns** | Full entity setup |
| Single Component Query | **6.6ns/entity** | 100k entities |
| Two Component Query | **18ns/entity** | 100k entities |
| Three Component Query | **28ns/entity** | 100k entities |
| GetComponent | **67ns** | Random access |
| Simulation (100k, 10 frames) | **1.62ms/frame** | Position += Velocity |
| **Parallel Job Speedup** | **17x** | vs sequential ForEach |

### Memory

| Metric | Value |
|--------|-------|
| Memory per Entity (2 components) | **46.5 bytes** (native storage; managed heap contributes 0) |
| GC Allocation (Singleton resolve) | **0 bytes** (asserted with `Is.Not.AllocatingGCMemory`) |
| GC Allocation (Scoped resolve) | not yet re-measured — see note below |

### Comparison

**No cross-framework comparison is published here at present.** The table that used to sit in
this section quoted competitor figures that were not measured on the same machine, in the same
harness, or in some cases measured at all — Reflex publishes a directly comparable benchmark
(10k transient resolves through a 4-level chain) whose number is *faster* than the figure that
was attributed to it here, and `Documentation~/Benchmarks.md` simultaneously listed VContainer
ahead of Strada. Quoting a competitor's worst case against your own best case is not a
comparison.

A comparative suite that runs Strada, VContainer, Reflex, Zenject and manual `new()` through
one adapter interface, on one machine, in one interleaved run, is the prerequisite for making
this claim again. Until it exists and its raw output is published, treat any relative ordering
as unmeasured.

---

## Documentation

| Document | Description |
|----------|-------------|
| [Modules](Documentation~/Modules.md) | Modular architecture, ModuleConfig, Inspector-configurable systems |
| [DI Container](Documentation~/DI.md) | Dependency injection, lifetimes, scopes |
| [ECS System](Documentation~/ECS.md) | Entities, components, queries, systems |
| [Messaging](Documentation~/Messaging.md) | MessageBus, commands, events, queries |
| [Sync](Documentation~/Sync.md) | Reactive properties, bindings, EntityMediator |
| [Pooling](Documentation~/Pooling.md) | Object pools, lifecycle hooks |
| [StateMachine](Documentation~/StateMachine.md) | FSM with transitions |
| [TimerService](Documentation~/TimerService.md) | Managed timers with pause/resume |
| [Debugging](Documentation~/Debugging.md) | Troubleshooting, common issues, debugging tools |
| [Benchmarks](Documentation~/Benchmarks.md) | Full performance data |

---

## Architecture

### System Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          STRADA FRAMEWORK                                │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│   ┌─────────────────────────┐     ┌─────────────────────────┐          │
│   │    PATTERNS LAYER       │     │      ECS LAYER          │          │
│   │                         │     │                         │          │
│   │  ┌─────────────────┐    │     │  ┌─────────────────┐    │          │
│   │  │     Views       │    │     │  │    Systems      │    │          │
│   │  │ (MonoBehaviour) │    │     │  │  (SystemBase)   │    │          │
│   │  └────────┬────────┘    │     │  └────────┬────────┘    │          │
│   │           │             │     │           │             │          │
│   │  ┌────────▼────────┐    │     │  ┌────────▼────────┐    │          │
│   │  │   Controllers   │    │     │  │    Entities     │    │          │
│   │  │  (Controller)   │    │     │  │  (EntityManager)│    │          │
│   │  └────────┬────────┘    │     │  └────────┬────────┘    │          │
│   │           │             │     │           │             │          │
│   │  ┌────────▼────────┐    │     │  ┌────────▼────────┐    │          │
│   │  │    Services     │    │     │  │   Components    │    │          │
│   │  │    (Service)    │    │     │  │  (IComponent)   │    │          │
│   │  └────────┬────────┘    │     │  └─────────────────┘    │          │
│   │           │             │     │                         │          │
│   │  ┌────────▼────────┐    │     │                         │          │
│   │  │     Models      │    │     │                         │          │
│   │  │    (Model)      │    │     │                         │          │
│   │  └─────────────────┘    │     │                         │          │
│   └────────────┬────────────┘     └────────────┬────────────┘          │
│                │                               │                        │
│                └───────────┬───────────────────┘                        │
│                            │                                            │
│                ┌───────────▼───────────┐                                │
│                │      MessageBus       │                                │
│                │  (Events/Commands/    │                                │
│                │       Queries)        │                                │
│                └───────────┬───────────┘                                │
│                            │                                            │
│    ┌───────────────────────┼───────────────────────┐                    │
│    │                       │                       │                    │
│    ▼                       ▼                       ▼                    │
│ ┌──────────────┐   ┌──────────────┐   ┌──────────────────────┐         │
│ │     DI       │   │   Reactive   │   │   Sync/Mediator      │         │
│ │  Container   │   │  Properties  │   │      Registry        │         │
│ │ (Container)  │   │(ReactiveProperty) │  (EntityMediator)    │         │
│ └──────────────┘   └──────────────┘   └──────────────────────┘         │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Data Flow

```
┌──────────────┐   Commands    ┌──────────────┐   Component     ┌──────────┐
│  Controller  │──────────────▶│  MessageBus  │───Updates──────▶│   ECS    │
│              │               │              │                 │  System  │
└──────────────┘               └──────────────┘                 └──────────┘
       ▲                              │                               │
       │                              │                               │
       │         ComponentChanged     │      Publish Events           │
       └──────────────────────────────┴───────────────────────────────┘
```

### Folder Structure

```
Packages/com.strada.core/
├── Runtime/
│   ├── DI/                    # Dependency Injection
│   │   ├── ContainerBuilder.cs
│   │   ├── Container.cs
│   │   ├── ContainerScope.cs
│   │   ├── Lifetime.cs
│   │   ├── Attributes/        # Auto-binding attributes
│   │   │   └── AutoRegisterAttribute.cs
│   │   └── AutoBinding/       # Runtime scanner
│   │       └── RuntimeAutoBindingScanner.cs
│   ├── ECS/                   # Entity Component System
│   │   ├── Core/EntityManager.cs
│   │   ├── Storage/SparseSet.cs
│   │   ├── Query/QueryBuilder.cs
│   │   ├── Systems/SystemBase.cs
│   │   └── Jobs/ParallelComponentJob.cs
│   ├── Communication/         # Unified Messaging
│   │   └── MessageBus.cs
│   ├── Commands/              # Command Pattern
│   │   ├── ICommand.cs
│   │   ├── CommandPool.cs
│   │   └── CommandSequencer.cs
│   ├── Sync/                  # Patterns-ECS Integration
│   │   ├── ReactiveProperty.cs
│   │   ├── ComputedProperty.cs
│   │   ├── EntityMediator.cs
│   │   └── SyncEvents.cs
│   ├── Modules/               # Modular Architecture
│   │   ├── ModuleConfig.cs        # Base module ScriptableObject
│   │   ├── IModuleBuilder.cs      # Fluent registration API
│   │   ├── ModuleBuilder.cs       # Builder implementation
│   │   ├── SystemRunner.cs        # Config-driven system execution
│   │   ├── SystemEntry.cs         # System configuration
│   │   ├── ServiceEntry.cs        # Service configuration
│   │   └── SystemAttributes.cs    # [StradaSystem] attribute
│   ├── Bootstrap/             # Application Bootstrap
│   │   ├── GameBootstrapper.cs    # Main entry point
│   │   └── GameBootstrapperConfig.cs  # Central orchestrator
│   ├── Pooling/               # Object Pooling
│   │   └── ObjectPool.cs
│   └── StateMachine/          # FSM
│       └── StateMachine.cs
├── Documentation~/            # Detailed Documentation
│   ├── Modules.md             # Modular architecture guide
│   ├── DI.md                  # Dependency injection guide
│   ├── ECS.md                 # Entity Component System guide
│   ├── Messaging.md           # MessageBus messaging guide
│   ├── Sync.md                # Reactive bindings guide
│   ├── Pooling.md             # Object pooling guide
│   ├── StateMachine.md        # FSM guide
│   └── Benchmarks.md          # Performance benchmarks
├── SourceGenerationDI~/       # DI Roslyn Source Generators
│   └── StradaDISourceGenerator.cs  # DI auto-binding generation
├── SourceGenerationECS~/      # ECS Roslyn Source Generators
│   ├── StradaFactoryGenerator.cs   # Factory generation
│   └── EntityQueryGenerator.cs     # Query T9-T16 generation
├── Editor/                    # Editor Tools
└── Tests/                     # Test Suite (564 tests)
    ├── Runtime/               # Functional tests (457)
    │   └── Performance/       # Unity PerformanceTesting benchmarks (85)
    ├── Benchmarks/            # Scaling benchmarks (18)
    └── Stress/                # Stress tests (4)
```

---

## API Reference

### ContainerBuilder

```csharp
// Register interface → implementation
builder.Register<IService, ServiceImpl>(Lifetime.Singleton);

// Register concrete type
builder.Register<MyService>(Lifetime.Transient);

// Register factory
builder.RegisterFactory<IService>(c => new ServiceImpl(c.Resolve<IDep>()));

// Register instance
builder.RegisterInstance<IConfig>(configInstance);

// Build container
IContainer container = builder.Build();
```

### IContainer

```csharp
T Resolve<T>() where T : class;
object Resolve(Type type);
bool TryResolve<T>(out T instance) where T : class;
bool IsRegistered<T>() where T : class;
IContainerScope CreateScope();
```

### EntityManager

```csharp
Entity CreateEntity();
void DestroyEntity(Entity entity);
bool Exists(Entity entity);

void AddComponent<T>(Entity entity, T component) where T : unmanaged, IComponent;
void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent;
bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent;
T GetComponent<T>(Entity entity) where T : unmanaged, IComponent;
void SetComponent<T>(Entity entity, T component) where T : unmanaged, IComponent;
```

### MessageBus

```csharp
// Events (pub/sub)
void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
void Publish<TEvent>(TEvent evt) where TEvent : struct;

// Struct Commands (request/response)
void RegisterCommandHandler<TCommand>(Action<TCommand> handler) where TCommand : struct;
void Send<TCommand>(TCommand command) where TCommand : struct;

// Object Commands (pooled, async)
void Execute(ICommand command);          // Auto-returns pooled commands
void ExecuteAsync(IAsyncCommand command, Action onComplete = null);

// Queries (request/response with return)
void RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler);
TResult Query<TQuery, TResult>(TQuery query) where TQuery : struct, IQuery<TResult>;
```

### ReactiveProperty

```csharp
var prop = new ReactiveProperty<int>(initialValue);

prop.Value;                          // Get current value
prop.Value = newValue;               // Set and notify
prop.SetWithoutNotify(value);        // Set without notification
prop.Subscribe(handler);             // Subscribe to changes
prop.SubscribeAndInvoke(handler);    // Subscribe and call immediately
prop.Unsubscribe(handler);           // Remove subscription
```

### ObjectPool

```csharp
var pool = new ObjectPool<Enemy>(
    factory: () => new Enemy(),
    onSpawn: e => e.Reset(),
    onDespawn: e => e.Cleanup(),
    initialSize: 10,
    maxSize: 100
);

Enemy enemy = pool.Spawn();
pool.Despawn(enemy);
pool.Prewarm(20);
pool.Clear();
```

### StateMachine

```csharp
var fsm = new StateMachine<IState>();

fsm.AddState(new IdleState());
fsm.AddState(new WalkState());
fsm.AddState(new AttackState());

fsm.AddTransition<IdleState, WalkState>(() => input.IsMoving);
fsm.AddTransition<WalkState, IdleState>(() => !input.IsMoving);
fsm.AddAnyTransition<AttackState>(() => input.IsAttacking);

fsm.Start<IdleState>();
fsm.Update(deltaTime);
```

---

## Testing

This repository is a Unity *package*: it has no `Assets/` or `ProjectSettings/`, so no Unity
binary can open it directly. The scripts below synthesise the host project the Test Runner
needs (including the mandatory `testables` entry, without which zero tests are discovered).

Check what your machine can do first:

```bash
./Tools/ci/doctor.sh
```

The toolchain is resolved by `Tools/ci/unity-env.sh`. It uses Unity's own CLI (`unity`,
shipped July 2026) when it is installed — for locating the editor and, in CI, for
provisioning it with `unity install` — and otherwise falls back to a Hub installation or an
explicit `UNITY` path. Nothing here *requires* the CLI.

```bash
brew install --cask unity-cli     # optional; see docs.unity.com/en-us/unity-cli
unity install 6000.5.7f1          # the version this package targets
```

The compile and test invocations still drive the editor executable in batchmode rather than
`unity test`. The CLI has a test subcommand, but it is marked experimental and its flags are
not in the published reference — Unity's own docs name `unity --help` as the authority. The
switch is a small, isolated change in `run-tests.sh`, noted inline there.

```bash
# Build the host project once, then compile and run the suite
./Tools/ci/assemble-bench-project.sh "$PWD" /tmp/StradaBench
./Tools/ci/compile.sh   /tmp/StradaBench            # fails on any compiler error
./Tools/ci/run-tests.sh /tmp/StradaBench playmode   # prints total/passed/failed

# Functional tests only
./Tools/ci/run-tests.sh /tmp/StradaBench playmode -testCategory "!Performance;!Benchmark"
```

The tests live in an assembly that targets all platforms, so they run under
`-testPlatform playmode`; `editmode` discovers none of them.

Set `UNITY` to pin an exact editor executable, or `STRADA_UNITY_VERSION` to pick a different
installed version, e.g. `STRADA_UNITY_VERSION=6000.0.58f1 ./Tools/ci/compile.sh /tmp/StradaBench`.

**Test Coverage:** 564 tests, all passing.

---

## License

Proprietary - All rights reserved

---

## Contributing

This is a private framework. For bug reports or feature requests, contact the maintainer.

---

*Built for Unity 6 with performance and clean architecture in mind.*
