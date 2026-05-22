# Reactive Sync

Strada's Sync system provides reactive data binding and true Patterns-ECS integration through event-driven architecture.

## Table of Contents

- [Quick Start](#quick-start)
- [Patterns-ECS Integration](#patterns-ecs-integration)
  - [Sync Events](#sync-events)
  - [EntityMediator](#entitymediator)
- [ReactiveProperty](#reactiveproperty)
- [ReactiveCollection](#reactivecollection)
- [ComputedProperty](#computedproperty)
- [BindingScope](#bindingscope)
- [Integration Patterns](#integration-patterns)
- [Best Practices](#best-practices)

---

## Quick Start

```csharp
using Strada.Core.Sync;

// Create reactive property
var health = new ReactiveProperty<int>(100);

// Subscribe to changes
health.Subscribe(value => healthBar.SetFillAmount(value / 100f));

// Changes automatically notify subscribers
health.Value = 75; // UI updates automatically

// Subscribe and get current value immediately
health.SubscribeAndInvoke(value => Debug.Log($"Health: {value}"));
```

---

## Patterns-ECS Integration

Strada provides true Patterns-ECS integration through event-driven communication via MessageBus.

### Sync Events

ECS systems publish these events when entity state changes:

```csharp
using Strada.Core.Sync;
using Strada.Core.ECS;

// Published when a component value changes
public readonly struct ComponentChanged<T> where T : unmanaged, IComponent
{
    public readonly Entity Entity;
    public readonly T OldValue;
    public readonly T NewValue;
}

// Published when a component is added
public readonly struct ComponentAdded<T> where T : unmanaged, IComponent
{
    public readonly Entity Entity;
    public readonly T Value;
}

// Published when a component is removed
public readonly struct ComponentRemoved<T> where T : unmanaged, IComponent
{
    public readonly Entity Entity;
    public readonly T Value;
}

// Entity lifecycle events
public readonly struct EntityCreated { public readonly Entity Entity; }
public readonly struct EntityDestroyed { public readonly Entity Entity; }
```

### Publishing from ECS Systems

```csharp
public class HealthSystem : SystemBase<Health>
{
    private readonly IMessageBus _bus;

    public HealthSystem(IMessageBus bus) => _bus = bus;

    protected override void OnUpdateEntity(int entity, ref Health health, float dt)
    {
        var oldHealth = health;
        health.Current -= health.DamagePerSecond * dt;

        // Publish change event for Patterns controllers to react
        if (health.Current != oldHealth.Current)
        {
            _bus.Publish(new ComponentChanged<Health>(
                new Entity(entity, 0),
                oldHealth,
                health
            ));
        }
    }
}
```

### EntityMediator

EntityMediator binds ECS entities to UI views with automatic MessageBus integration:

```csharp
using Strada.Core.Sync;
using Strada.Core.Patterns;

public class PlayerHealthMediator : EntityMediator<PlayerHealthView>
{
    protected override void OnBind()
    {
        // Subscribe to component changes for this entity
        OnComponentChanged<Health>(e =>
        {
            View.SetHealth(e.NewValue.Current, e.NewValue.Max);
        });

        // Subscribe to any event via MessageBus
        Subscribe<PlayerDied>(e =>
        {
            if (e.EntityId == Entity.Index)
                View.ShowDeathScreen();
        });

        // Initial sync from ECS component
        if (Entities.HasComponent<Health>(Entity))
        {
            var health = Entities.GetComponent<Health>(Entity);
            View.SetHealth(health.Current, health.Max);
        }
    }

    protected override void OnUnbind()
    {
        // All subscriptions auto-unsubscribe
    }

    // Send commands to ECS via MessageBus
    public void OnHealButtonClicked()
    {
        SendCommand(new HealPlayer { EntityId = Entity.Index, Amount = 50 });
    }
}
```

### EntityMediator API

```csharp
public abstract class EntityMediator<TView> : IDisposable where TView : View
{
    // Available in OnBind/OnUnbind
    protected EntityManager Entities { get; }
    protected IContainer Container { get; }
    protected IMessageBus Bus { get; }
    protected TView View { get; }
    protected Entity Entity { get; }

    // Lifecycle
    protected abstract void OnBind();
    protected abstract void OnUnbind();

    // MessageBus integration (auto-unsubscribe on Unbind)
    protected void OnComponentChanged<T>(Action<ComponentChanged<T>> handler);
    protected void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
    protected void Publish<TEvent>(TEvent evt) where TEvent : struct;
    protected void SendCommand<TCommand>(TCommand command) where TCommand : struct;

    // Component bindings
    protected ComponentBinding<TComponent, TProperty> Bind<TComponent, TProperty>(...);
    protected AutoSyncBinding<TComponent> AutoSync<TComponent>(...);

    // Sync all bindings
    public void SyncBindings();
    public void PushBindings();
}
```

### MediatorRegistry

Manages EntityMediator lifecycle with pooling:

```csharp
// Setup
var registry = new MediatorRegistry(container);

// Create mediator for entity-view pair
var mediator = registry.Create<PlayerHealthMediator, PlayerHealthView>(entity, view);

// Sync all active mediators
registry.SyncAll();

// Release specific mediator (returns to pool)
registry.Release<PlayerHealthMediator, PlayerHealthView>(mediator);

// Release all mediators
registry.ReleaseAll();
```

---

## ReactiveProperty

Observable value that notifies subscribers on change.

### Basic Usage

```csharp
// Create with initial value
var score = new ReactiveProperty<int>(0);
var playerName = new ReactiveProperty<string>("Player1");
var isAlive = new ReactiveProperty<bool>(true);

// Read value
int currentScore = score.Value;

// Write value (notifies subscribers if changed)
score.Value = 100;

// Write without notification
score.SetWithoutNotify(200);
```

### Subscribing

```csharp
// Simple subscription
score.Subscribe(value => UpdateScoreUI(value));

// Subscribe and invoke immediately with current value
score.SubscribeAndInvoke(value =>
{
    // Called immediately with current value
    // Then called on every change
    scoreText.text = value.ToString();
});

// Store reference for unsubscription
Action<int> handler = OnScoreChanged;
score.Subscribe(handler);

// Later...
score.Unsubscribe(handler);
```

### Thread Safety

ReactiveProperty uses snapshot iteration (`ToArray()`) when notifying handlers, making it safe to subscribe/unsubscribe from within a handler callback. ReactiveCollection also supports unsubscription via `OffAdd()`, `OffRemove()`, and `OffClear()` methods.

### Change Detection

ReactiveProperty only notifies when value actually changes:

```csharp
var health = new ReactiveProperty<int>(100);
int notifyCount = 0;
health.Subscribe(_ => notifyCount++);

health.Value = 100; // No notification (same value)
health.Value = 99;  // Notifies (changed)
health.Value = 99;  // No notification (same value)

Assert.AreEqual(1, notifyCount);
```

### Manual Notification

```csharp
// Force notification even without change
health.SetWithoutNotify(50);
health.Notify(); // Manually trigger notification
```

### Implicit Conversion

```csharp
var health = new ReactiveProperty<int>(100);

// Implicit conversion to value type
int current = health; // Same as health.Value
```

### Disposal

```csharp
var prop = new ReactiveProperty<int>(0);

// Clear all subscribers
prop.Clear();

// Or dispose (also clears)
prop.Dispose();
```

---

## ReactiveCollection

Observable list with add/remove/clear notifications.

### Basic Usage

```csharp
var inventory = new ReactiveCollection<Item>();

// Subscribe to events
inventory.OnAdd(item => Debug.Log($"Added: {item.Name}"));
inventory.OnRemove(item => Debug.Log($"Removed: {item.Name}"));
inventory.OnClear(() => Debug.Log("Inventory cleared"));

// Modify collection
inventory.Add(sword);    // Triggers OnAdd
inventory.Remove(sword); // Triggers OnRemove
inventory.Clear();       // Triggers OnClear
```

### Collection Operations

```csharp
var items = new ReactiveCollection<string>();

// Add items
items.Add("Sword");
items.Add("Shield");

// Access by index
string first = items[0];
int count = items.Count;

// Remove operations
items.Remove("Sword");    // By value
items.RemoveAt(0);        // By index

// Clear all
items.Clear();
```

### UI Binding Example

```csharp
public class InventoryUI : MonoBehaviour
{
    public Transform itemContainer;
    public GameObject itemPrefab;

    private ReactiveCollection<Item> _inventory;
    private Dictionary<Item, GameObject> _itemViews = new();

    void Start()
    {
        _inventory = GameModel.Instance.Inventory;

        _inventory.OnAdd(item =>
        {
            var view = Instantiate(itemPrefab, itemContainer);
            view.GetComponent<ItemView>().Setup(item);
            _itemViews[item] = view;
        });

        _inventory.OnRemove(item =>
        {
            if (_itemViews.TryGetValue(item, out var view))
            {
                Destroy(view);
                _itemViews.Remove(item);
            }
        });

        _inventory.OnClear(() =>
        {
            foreach (var view in _itemViews.Values)
                Destroy(view);
            _itemViews.Clear();
        });
    }
}
```

---

## ComputedProperty

Derived value that automatically updates when dependencies change.

### Basic Usage

```csharp
var health = new ReactiveProperty<int>(100);
var maxHealth = new ReactiveProperty<int>(100);

// Computed property derived from two sources
var healthPercent = new ComputedProperty<float>(
    () => (float)health.Value / maxHealth.Value,
    health, maxHealth // Dependencies
);

// Subscribe to computed value
healthPercent.Subscribe(pct => healthBar.fillAmount = pct);

// When either dependency changes, computed updates
health.Value = 50;     // healthPercent becomes 0.5
maxHealth.Value = 200; // healthPercent becomes 0.25
```

### Complex Computations

```csharp
var baseAttack = new ReactiveProperty<int>(10);
var weaponBonus = new ReactiveProperty<int>(5);
var buffMultiplier = new ReactiveProperty<float>(1.0f);

var totalDamage = new ComputedProperty<int>(
    () => (int)((baseAttack.Value + weaponBonus.Value) * buffMultiplier.Value),
    baseAttack, weaponBonus, buffMultiplier
);

// totalDamage auto-updates when any input changes
```

### Chained Computations

```csharp
var a = new ReactiveProperty<int>(1);
var b = new ReactiveProperty<int>(2);

var sum = new ComputedProperty<int>(() => a.Value + b.Value, a, b);
var doubled = new ComputedProperty<int>(() => sum.Value * 2, sum);

// doubled updates when a or b changes
a.Value = 5; // sum=7, doubled=14
```

---

## BindingScope

Manages subscriptions with automatic cleanup.

### Basic Usage

```csharp
public class PlayerUI : MonoBehaviour
{
    private BindingScope _scope;

    void Start()
    {
        _scope = new BindingScope();

        var model = GameModel.Instance;

        // All subscriptions tracked by scope
        _scope.Bind(model.Health, v => healthBar.value = v);
        _scope.Bind(model.Score, v => scoreText.text = v.ToString());
        _scope.Bind(model.Level, v => levelText.text = $"Level {v}");
    }

    void OnDestroy()
    {
        // Automatically unsubscribes all
        _scope.Dispose();
    }
}
```

### One-Way Binding

```csharp
// Property → UI
_scope.Bind(health, value => healthText.text = value.ToString());

// Property → Multiple targets
_scope.Bind(score, value =>
{
    scoreText.text = value.ToString();
    highScoreText.text = value > highScore ? "NEW HIGH!" : "";
});
```

### Two-Way Binding

```csharp
// For input fields
_scope.BindTwoWay(
    playerName,
    () => inputField.text,
    value => inputField.text = value,
    inputField.onValueChanged
);
```

---

## Integration Patterns

### Model-View Binding

```csharp
// Model
public class PlayerModel
{
    public ReactiveProperty<int> Health { get; } = new(100);
    public ReactiveProperty<int> MaxHealth { get; } = new(100);
    public ReactiveProperty<int> Gold { get; } = new(0);

    public ComputedProperty<float> HealthPercent { get; }

    public PlayerModel()
    {
        HealthPercent = new ComputedProperty<float>(
            () => (float)Health.Value / MaxHealth.Value,
            Health, MaxHealth
        );
    }
}

// View
public class PlayerView : MonoBehaviour
{
    public Slider healthBar;
    public Text goldText;

    private BindingScope _scope;

    public void Bind(PlayerModel model)
    {
        _scope = new BindingScope();
        _scope.Bind(model.HealthPercent, v => healthBar.value = v);
        _scope.Bind(model.Gold, v => goldText.text = $"Gold: {v}");
    }

    void OnDestroy() => _scope?.Dispose();
}
```

### ECS → Patterns via ComponentChanged Events

```csharp
// ECS system publishes component changes
public class HealthSystem : SystemBase<Health>
{
    private readonly IMessageBus _bus;

    public HealthSystem(IMessageBus bus) => _bus = bus;

    protected override void OnUpdateEntity(int entity, ref Health health, float dt)
    {
        var old = health;
        health.Current = Math.Max(0, health.Current - health.Decay * dt);

        if (health.Current != old.Current)
        {
            _bus.Publish(new ComponentChanged<Health>(
                new Entity(entity, 0), old, health));
        }
    }
}

// Controller subscribes to changes
public class PlayerController
{
    public PlayerController(IMessageBus bus, PlayerModel model)
    {
        bus.Subscribe<ComponentChanged<Health>>(e =>
        {
            if (e.Entity.Index == model.EntityId)
                model.Health.Value = e.NewValue.Current;
        });
    }
}
```

### Patterns → ECS via Commands

```csharp
// Define command struct
public struct DealDamage
{
    public int TargetEntity;
    public int Amount;
}

// ECS system handles command
public class DamageSystem : SystemBase<Health>
{
    public DamageSystem(IMessageBus bus)
    {
        bus.RegisterCommandHandler<DealDamage>(HandleDamage);
    }

    private void HandleDamage(DealDamage cmd)
    {
        if (Entities.HasComponent<Health>(cmd.TargetEntity))
        {
            var health = Entities.GetComponent<Health>(cmd.TargetEntity);
            health.Current -= cmd.Amount;
            Entities.SetComponent(cmd.TargetEntity, health);
        }
    }
}

// Controller sends command
public class CombatController
{
    private readonly IMessageBus _bus;

    public void Attack(int targetEntity, int damage)
    {
        _bus.Send(new DealDamage { TargetEntity = targetEntity, Amount = damage });
    }
}
```

### Reactive Properties with MessageBus

```csharp
// Update reactive properties from bus events
bus.Subscribe<ComponentChanged<Health>>(e =>
{
    model.Health.Value = e.NewValue.Current;
});

bus.Subscribe<GoldCollected>(e =>
{
    model.Gold.Value += e.Amount;
});
```

---

## Best Practices

### 1. Use BindingScope for Cleanup

```csharp
// Good - automatic cleanup
private BindingScope _scope;

void OnEnable()
{
    _scope = new BindingScope();
    _scope.Bind(health, OnHealthChanged);
}

void OnDisable()
{
    _scope.Dispose();
}

// Avoid - manual management
void OnEnable()
{
    health.Subscribe(_handler); // Easy to forget cleanup
}
```

### 2. Keep Computed Logic Simple

```csharp
// Good - simple computation
var total = new ComputedProperty<int>(() => a.Value + b.Value, a, b);

// Avoid - complex side effects
var bad = new ComputedProperty<int>(() =>
{
    SendAnalytics(); // Don't do side effects
    return a.Value + b.Value;
}, a, b);
```

### 3. Dispose When Done

```csharp
public class GameUI : MonoBehaviour
{
    private List<IDisposable> _disposables = new();

    void Start()
    {
        var prop = new ReactiveProperty<int>(0);
        _disposables.Add(prop);
    }

    void OnDestroy()
    {
        foreach (var d in _disposables)
            d.Dispose();
    }
}
```

### 4. Use SetWithoutNotify for Batch Updates

```csharp
// Avoid multiple notifications
model.Health.SetWithoutNotify(newHealth);
model.MaxHealth.SetWithoutNotify(newMaxHealth);
model.Shield.SetWithoutNotify(newShield);

// Single notification at end
model.Health.Notify();
```

---

## API Reference

### ReactiveProperty<T>

```csharp
ReactiveProperty()
ReactiveProperty(T initialValue)

T Value { get; set; }
void SetWithoutNotify(T value)
void Subscribe(Action<T> handler)
void SubscribeAndInvoke(Action<T> handler)
void Unsubscribe(Action<T> handler)
void Notify()
void Clear()
void Dispose()

static implicit operator T(ReactiveProperty<T> property)
```

### ReactiveCollection<T>

```csharp
int Count { get; }
T this[int index] { get; }

void Add(T item)
bool Remove(T item)
void RemoveAt(int index)
void Clear()

void OnAdd(Action<T> handler)
void OnRemove(Action<T> handler)
void OnClear(Action handler)

// Unsubscribe methods
void OffAdd(Action<T> handler)
void OffRemove(Action<T> handler)
void OffClear(Action handler)

void Dispose()
```

### ComputedProperty<T>

```csharp
ComputedProperty(Func<T> compute, params IReadOnlyReactiveProperty<object>[] dependencies)

T Value { get; }
void Subscribe(Action<T> handler)
void Unsubscribe(Action<T> handler)
void Dispose()
```

### BindingScope

```csharp
void Bind<T>(IReadOnlyReactiveProperty<T> source, Action<T> target)
void BindTwoWay<T>(ReactiveProperty<T> property, Func<T> getter, Action<T> setter, UnityEvent<T> changeEvent)
void Dispose()
```

---

## Migration: legacy Unsubscribe → SubscriptionToken

`ReactiveProperty<T>.Subscribe` and `SubscribeAndInvoke` now return a
`Strada.Core.SubscriptionToken`. Disposing the token removes the
handler — disposal is idempotent. `IReadOnlyReactiveProperty<T>.Unsubscribe`
is marked `[Obsolete]` and scheduled for removal in the next major
release.

| Before (legacy) | After (token) |
|-----------------|---------------|
| `property.Subscribe(h); ... property.Unsubscribe(h);` | `var t = property.Subscribe(h); ... t.Dispose();` |
| `property.SubscribeAndInvoke(h); ... property.Unsubscribe(h);` | `var t = property.SubscribeAndInvoke(h); ... t.Dispose();` |

If you have a property typed as the read-only interface
(`IReadOnlyReactiveProperty<T>`), use the `SubscribeToken` extension to
get a token regardless of the underlying object's concrete type:

```csharp
IReadOnlyReactiveProperty<int> health = enemy.Health;
var token = health.SubscribeToken(OnHealthChanged);
// ...
token.Dispose();
```

Or aggregate with a `BindingScope`:

```csharp
var scope = new BindingScope();
scope.Subscribe(player.Position, OnMove);
scope.Add(enemy.Health.SubscribeToken(OnHealth));
// ...
scope.Dispose();   // releases all subscriptions in LIFO order
```

Derived reactive types (`MappedProperty`, `FilteredProperty`,
`CombinedProperty`, `ThrottledProperty`, `DistinctProperty`,
`PropertyBinding`, `ConvertedBinding`, `TwoWayBinding`,
`ValidatedBinding`, `ComputedProperty`) already capture tokens
internally and dispose them when their own `Dispose` runs.

---

## Related Documentation

- [DI Container](DI.md) - Dependency injection
- [ECS System](ECS.md) - Entity Component System
- [Messaging](Messaging.md) - Event system
