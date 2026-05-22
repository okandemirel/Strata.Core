# Messaging System

Strada provides zero-allocation messaging through `MessageBus` - the unified messaging system for events, commands, and queries.

## Table of Contents

- [Quick Start](#quick-start)
- [MessageBus](#stradabus)
  - [Events (Pub/Sub)](#events-pubsub)
  - [Struct Commands](#struct-commands)
  - [Object Commands](#object-commands)
  - [Queries](#queries)
- [Pooled Commands](#pooled-commands)
- [Performance](#performance)
- [Best Practices](#best-practices)

---

## Quick Start

```csharp
using Strada.Core.Communication;

// Create bus
var bus = new MessageBus();

// Define messages as structs (zero allocation)
public struct PlayerDamaged { public int EntityId; public int Damage; }
public struct SpawnEnemy { public float X, Y; }

// Subscribe to events
bus.Subscribe<PlayerDamaged>(e => HandleDamage(e));

// Publish events
bus.Publish(new PlayerDamaged { EntityId = 1, Damage = 10 });

// Register command handlers
bus.RegisterCommandHandler<SpawnEnemy>(cmd => SpawnAt(cmd.X, cmd.Y));

// Send commands
bus.Send(new SpawnEnemy { X = 100, Y = 50 });
```

---

## MessageBus

The unified messaging bus supporting events, commands, and queries.

### Events (Pub/Sub)

Events are fire-and-forget messages with multiple subscribers.

```csharp
// Define event
public struct EnemyKilled
{
    public int EntityId;
    public int ScoreValue;
    public float X, Y;
}

// Subscribe
bus.Subscribe<EnemyKilled>(OnEnemyKilled);

void OnEnemyKilled(EnemyKilled e)
{
    score += e.ScoreValue;
    SpawnParticles(e.X, e.Y);
}

// Multiple subscribers allowed
bus.Subscribe<EnemyKilled>(e => PlaySound("kill"));
bus.Subscribe<EnemyKilled>(e => UpdateKillCount());

// Publish to all subscribers
bus.Publish(new EnemyKilled
{
    EntityId = 42,
    ScoreValue = 100,
    X = 15.5f,
    Y = 20.0f
});

// Unsubscribe when done
bus.Unsubscribe<EnemyKilled>(OnEnemyKilled);
```

### Struct Commands

Struct commands are zero-allocation single-handler messages for direct actions.

```csharp
// Define command as struct
public struct DealDamage
{
    public int TargetId;
    public int Amount;
    public bool IsCritical;
}

// Register single handler
bus.RegisterCommandHandler<DealDamage>(HandleDamage);

void HandleDamage(DealDamage cmd)
{
    var health = GetHealth(cmd.TargetId);
    health.Current -= cmd.IsCritical ? cmd.Amount * 2 : cmd.Amount;
}

// Send command
bus.Send(new DealDamage
{
    TargetId = 1,
    Amount = 25,
    IsCritical = true
});
```

### Object Commands

For complex commands with async support or pooling, use ICommand/IAsyncCommand:

```csharp
using Strada.Core.Commands;

// Synchronous command
public class LoadLevelCommand : ICommand
{
    public string LevelName { get; set; }

    public void Execute()
    {
        SceneManager.LoadScene(LevelName);
    }
}

// Execute via MessageBus
bus.Execute(new LoadLevelCommand { LevelName = "Level1" });

// Async command
public class LoadAssetsCommand : IAsyncCommand
{
    public string[] AssetPaths { get; set; }

    public void Execute(Action onComplete)
    {
        LoadAssetsAsync(AssetPaths, onComplete);
    }
}

bus.ExecuteAsync(new LoadAssetsCommand { AssetPaths = paths }, () =>
{
    Debug.Log("Assets loaded!");
});
```

### Queries

Queries return values from handlers.

```csharp
// Define query with result type
public struct GetPlayerHealth : IQuery<int>
{
    public int PlayerId;
}

// Register query handler (interface style)
public class HealthQueryHandler : IQueryHandler<GetPlayerHealth, int>
{
    public int Handle(ref GetPlayerHealth query)
    {
        return _healthSystem.GetHealth(query.PlayerId);
    }
}

bus.RegisterQueryHandler<GetPlayerHealth, int>(new HealthQueryHandler());

// Or use delegate style
bus.RegisterQueryHandler<GetPlayerHealth, int>(q => _health[q.PlayerId]);

// Execute query
int health = bus.Query<GetPlayerHealth, int>(new GetPlayerHealth { PlayerId = 1 });
```

### Ref Parameters

For maximum performance, use ref parameters to avoid struct copies:

```csharp
// Publish by ref (zero copy)
var evt = new LargeEvent { /* lots of data */ };
bus.Publish(ref evt);

// Send by ref
var cmd = new LargeCommand { /* lots of data */ };
bus.Send(ref cmd);

// Query by ref
var query = new ComplexQuery { /* parameters */ };
var result = bus.Query<ComplexQuery, Result>(ref query);
```

---

## Pooled Commands

Reuse command objects to avoid allocation. MessageBus automatically returns pooled commands after execution.

### Using PooledCommandBase

```csharp
using Strada.Core.Commands;

// Inherit from PooledCommandBase for automatic pooling
public class SpawnEnemyCommand : PooledCommandBase<SpawnEnemyCommand>
{
    public float X, Y;
    public int EnemyType;

    protected override void OnExecute()
    {
        EnemySpawner.SpawnAt(X, Y, EnemyType);
    }

    public override void Reset()
    {
        X = 0;
        Y = 0;
        EnemyType = 0;
    }
}

// Rent from pool
var cmd = SpawnEnemyCommand.Rent();
cmd.X = 10;
cmd.Y = 20;
cmd.EnemyType = 1;

// Execute - automatically returns to pool when done
bus.Execute(cmd);
```

### CommandPool

Pre-warm pools for better performance:

```csharp
// Access the pool
CommandPool<SpawnEnemyCommand>.Instance.Prewarm(50);

// Manual rent/return (if not using MessageBus.Execute)
var cmd = CommandPool<SpawnEnemyCommand>.Instance.Rent();
// ... use command ...
CommandPool<SpawnEnemyCommand>.Instance.Return(cmd);
```

### Custom Pooled Commands

Implement IPooledCommand for custom pooling:

```csharp
public class CustomCommand : ICommand, IPooledCommand
{
    public int Value;

    public void Execute()
    {
        DoSomething(Value);
    }

    public void ReturnToPool()
    {
        Value = 0;
        MyCustomPool.Return(this);
    }
}

// MessageBus calls ReturnToPool() automatically after Execute()
bus.Execute(myCommand);
```

---

## Performance

### Benchmarks

MessageBus uses array-indexed dispatch for O(1) message routing:

| Operation | Time |
|-----------|------|
| Publish (1 subscriber) | ~20ns |
| Publish (10 subscribers) | ~100ns |
| Send Command | ~15ns |
| Query | ~20ns |

### Exception Isolation

EventBus isolates handler exceptions — if one subscriber throws, other subscribers still receive the event. Exceptions are caught and logged without interrupting the publish loop.

### Zero Allocation

All message types must be structs:

```csharp
// GOOD - struct, no allocation
public struct DamageEvent { public int Amount; }

// BAD - class, allocates
public class DamageEvent { public int Amount; } // Don't do this
```

### Internal Architecture

```csharp
// Type ID system for O(1) lookup
private static class EventTypeId<T>
{
    public static readonly int Id = Interlocked.Increment(ref _nextTypeId);
}

// Array-indexed dispatch
public void Publish<T>(ref T evt)
{
    var id = EventTypeId<T>.Id;
    var channel = _eventChannels[id];
    channel?.Publish(ref evt);
}
```

---

## Best Practices

### 1. Use Structs for Messages

```csharp
// Good - struct
public struct PlayerJumped { public int PlayerId; }

// Avoid - class (allocates)
public class PlayerJumped { public int PlayerId; }
```

### 2. Keep Messages Small

```csharp
// Good - minimal data
public struct ItemCollected
{
    public int ItemId;
    public int PlayerId;
}

// Avoid - too much data
public struct ItemCollected
{
    public int ItemId;
    public int PlayerId;
    public string ItemName;    // Don't include strings
    public ItemData FullData;  // Don't include large objects
}
```

### 3. Unsubscribe to Prevent Leaks

```csharp
public class EnemyController : MonoBehaviour
{
    private MessageBus _bus;
    private Action<DamageEvent> _handler;

    void OnEnable()
    {
        _handler = OnDamage;
        _bus.Subscribe(_handler);
    }

    void OnDisable()
    {
        _bus.Unsubscribe(_handler);
    }
}
```

### 4. Use Commands for Side Effects

```csharp
// Events - notify about something that happened
bus.Publish(new EnemyDied { EntityId = 42 });

// Commands - request an action
bus.Send(new SpawnReward { Position = pos });
```

### 5. Use Queries for Data Retrieval

```csharp
// Query - get data without side effects
var health = bus.Query<GetHealth, int>(new GetHealth { EntityId = id });

// Don't use events/commands for getting data
```

---

## API Reference

### IMessageBus

```csharp
// Events
void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
void Publish<TEvent>(TEvent evt) where TEvent : struct;
void Publish<TEvent>(ref TEvent evt) where TEvent : struct;
int GetSubscriberCount<TEvent>() where TEvent : struct;

// Struct Commands
void RegisterCommandHandler<TCommand>(Action<TCommand> handler) where TCommand : struct;
void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler) where TCommand : struct;
void Send<TCommand>(TCommand command) where TCommand : struct;
void Send<TCommand>(ref TCommand command) where TCommand : struct;

// Object Commands (pooled/async)
void Execute(ICommand command);                              // Auto-returns pooled
void ExecuteAsync(IAsyncCommand command, Action onComplete); // Auto-returns pooled

// Queries
void RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler)
    where TQuery : struct, IQuery<TResult>;
void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
    where TQuery : struct, IQuery<TResult>;
TResult Query<TQuery, TResult>(TQuery query) where TQuery : struct, IQuery<TResult>;
TResult Query<TQuery, TResult>(ref TQuery query) where TQuery : struct, IQuery<TResult>;

// Lifecycle
void Clear();
void Dispose();
```

### Interfaces

```csharp
public interface IQuery<TResult> { }

public interface IQueryHandler<TQuery, TResult> where TQuery : struct, IQuery<TResult>
{
    TResult Handle(ref TQuery query);
}

public interface ICommand
{
    void Execute();
}

public interface IAsyncCommand
{
    void Execute(Action onComplete);
}

public interface IPooledCommand
{
    void ReturnToPool();
}

public interface ICommandHandler<TCommand> where TCommand : struct
{
    void Handle(TCommand command);
}
```

---

## Migration: legacy Unsubscribe → SubscriptionToken

`EventBus` now returns a `Strada.Core.SubscriptionToken` from every
`Subscribe` / `RegisterSignalHandler` / `RegisterQueryHandler` overload.
Disposing the token removes the handler it represents — disposal is
idempotent and thread-safe, and the signal/query overloads use
`ReferenceEquals` so a stale token cannot clear a slot that has already
been replaced.

The legacy `Unsubscribe(handler)` / `UnregisterSignalHandler<T>()` /
`UnregisterQueryHandler<TQuery,TResult>()` methods are marked
`[Obsolete]` (warning-only) and remain functional during the deprecation
period. They are scheduled for removal in the next major release.

| Before (legacy) | After (token) |
|-----------------|---------------|
| `bus.Subscribe(handler); ... bus.Unsubscribe(handler);` | `var t = bus.Subscribe(handler); ... t.Dispose();` |
| `bus.RegisterSignalHandler(h); ... bus.UnregisterSignalHandler<TSignal>();` | `var t = bus.RegisterSignalHandler(h); ... t.Dispose();` |
| `bus.RegisterQueryHandler(h); ... bus.UnregisterQueryHandler<TQuery,TResult>();` | `var t = bus.RegisterQueryHandler(h); ... t.Dispose();` |

Aggregate tokens with a `BindingScope`:

```csharp
var scope = new BindingScope();
scope.Add(bus.Subscribe<HealthChanged>(OnHealthChanged));
scope.Add(bus.RegisterSignalHandler<JumpRequest>(OnJump));
// ... later (eg. in your controller's Dispose) ...
scope.Dispose();   // releases both subscriptions in LIFO order
```

Pattern subclasses (`Patterns/Base`, `ECS/SystemBase`,
`Sync/EntityMediator`) already capture the returned tokens internally
and dispose them at the right point in their lifecycle — application
code that uses those base classes does not need to manage tokens
explicitly.

---

## Related Documentation

- [DI Container](DI.md) - Dependency injection
- [ECS System](ECS.md) - Entity Component System
- [Sync](Sync.md) - Reactive properties
