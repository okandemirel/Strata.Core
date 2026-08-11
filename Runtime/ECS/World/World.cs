using System;
using Strada.Core.Communication;
using Strada.Core.ECS.Core;

namespace Strada.Core.ECS.World
{
    public sealed class World : IDisposable
    {
        private static volatile World _current;

        private readonly EntityManager _entities;
        private readonly SystemScheduler _scheduler;
        private readonly EventBus _bus;
        private bool _initialized;
        private bool _disposed;

        /// <summary>
        /// Gets or sets the current active World instance.
        /// Used by editor tools and debugging utilities.
        /// </summary>
        public static World Current
        {
            get => _current;
            internal set => _current = value;
        }

        /// <summary>
        /// Publishes this World as <see cref="Current"/>.
        /// </summary>
        /// <remarks>
        /// The setter is internal so nothing outside the package can hijack the global, but that
        /// left every World not built by GameBootstrapper — which is every World the builder
        /// produces, and every World in the tests and benchmarks — permanently invisible to the
        /// editor tooling that reads <see cref="Current"/>. This is the explicit, opt-in way in.
        /// </remarks>
        public void MakeCurrent()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(World));
            _current = this;
        }

        /// <summary>
        /// Gets the EntityManager responsible for creating, destroying, and managing entities.
        /// </summary>
        public EntityManager EntityManager => _entities;

        /// <summary>
        /// Gets the SystemScheduler responsible for executing systems in the correct order.
        /// </summary>
        public SystemScheduler SystemScheduler => _scheduler;

        /// <summary>
        /// Gets the EventBus for publish/subscribe communication.
        /// </summary>
        public EventBus EventBus => _bus;

        /// <summary>
        /// Gets a value indicating whether the World has been initialized.
        /// </summary>
        public bool IsInitialized => _initialized;

        internal World(EntityManager entities, SystemScheduler scheduler, EventBus bus)
        {
            _entities = entities;
            _scheduler = scheduler;
            _bus = bus;
        }

        /// <summary>
        /// Initializes the World and its subsystems.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            // Flag set after the scheduler has actually run, so IsInitialized cannot report true
            // for a World whose initialization never completed.
            _scheduler.Initialize();
            _initialized = true;
        }

        /// <summary>
        /// Updates the World's systems (Variable Time Step).
        /// </summary>
        /// <param name="deltaTime">Time since last frame.</param>
        public void Update(float deltaTime)
        {
            // Drives UpdatePhase.Initialization, which previously had no caller anywhere: systems
            // registered into it were injected and Initialize()d — so the registration looked
            // live — but never updated. Runs ahead of Update, mirroring Unity's PlayerLoop.
            _scheduler.InitializationUpdate(deltaTime);
            _scheduler.Update(deltaTime);
        }

        /// <summary>
        /// Updates the World's systems (Late Update).
        /// </summary>
        /// <param name="deltaTime">Time since last frame.</param>
        public void LateUpdate(float deltaTime)
        {
            _scheduler.LateUpdate(deltaTime);
        }

        /// <summary>
        /// Updates the World's systems (Fixed Time Step).
        /// </summary>
        /// <param name="fixedDeltaTime">Fixed time step duration.</param>
        public void FixedUpdate(float fixedDeltaTime)
        {
            _scheduler.FixedUpdate(fixedDeltaTime);
        }

        /// <summary>
        /// Creates a new Entity in this World.
        /// </summary>
        public Entity CreateEntity() => _entities.CreateEntity();

        /// <summary>
        /// Destroys an Entity and recycles its ID.
        /// </summary>
        public void DestroyEntity(Entity entity) => _entities.DestroyEntity(entity);

        public void AddComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
            => _entities.AddComponent(entity, component);

        public T GetComponent<T>(Entity entity) where T : unmanaged, IComponent
            => _entities.GetComponent<T>(entity);

        public void SetComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
            => _entities.SetComponent(entity, component);

        public bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent
            => _entities.HasComponent<T>(entity);

        public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent
            => _entities.RemoveComponent<T>(entity);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_current == this)
                _current = null;

            // The native memory below must be released even if a system's Dispose throws its way
            // out of the scheduler; otherwise the EntityManager's persistent NativeArrays and
            // every ComponentStorage leak for the process lifetime.
            try
            {
                _scheduler.Dispose();
            }
            finally
            {
                _entities.Dispose();
                _bus?.Dispose();
            }
        }
    }
}
