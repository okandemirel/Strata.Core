using System;
using System.Collections.Generic;
using Strada.Core.Communication;
using Strada.Core.ECS.Core;

namespace Strada.Core.ECS.World
{
    public sealed class ECSBuilder
    {
        private readonly List<(Type systemType, UpdatePhase phase, Func<World, ISystem> factory)> _systemFactories = new();
        private int _initialEntityCapacity = 1024;
        private EventBus _eventBus;
        private bool _makeCurrent;

        public ECSBuilder WithInitialEntityCapacity(int capacity)
        {
            // Validated here rather than only inside the EntityManager constructor, so the bad
            // argument is reported at the call that supplied it.
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity),
                    $"Initial entity capacity must be positive (got {capacity}).");

            _initialEntityCapacity = capacity;
            return this;
        }

        /// <summary>
        /// Publishes the built World as <see cref="World.Current"/>.
        /// </summary>
        /// <remarks>
        /// Opt-in rather than automatic: <c>World.Current</c> is a process-wide global, and a test
        /// or tool that builds a second World must be able to do so without displacing the one the
        /// application is running. Without this, builder-created Worlds were unreachable from the
        /// editor tooling entirely, because the setter is internal to the package.
        /// </remarks>
        public ECSBuilder AsCurrent()
        {
            _makeCurrent = true;
            return this;
        }

        public ECSBuilder WithEventBus(EventBus eventBus)
        {
            _eventBus = eventBus;
            return this;
        }

        public ECSBuilder WithSystem<T>(UpdatePhase phase = UpdatePhase.Update) where T : ISystem, new()
        {
            EnsureNotRegistered(typeof(T));
            _systemFactories.Add((typeof(T), phase, _ => new T()));
            return this;
        }

        public ECSBuilder WithSystem<T>(Func<World, T> factory, UpdatePhase phase = UpdatePhase.Update) where T : ISystem
        {
            EnsureNotRegistered(typeof(T));
            _systemFactories.Add((typeof(T), phase, w => factory(w)));
            return this;
        }

        private void EnsureNotRegistered(Type systemType)
        {
            for (int i = 0; i < _systemFactories.Count; i++)
            {
                if (_systemFactories[i].systemType == systemType)
                    throw new InvalidOperationException(
                        $"System '{systemType.Name}' is already registered on this ECSBuilder.");
            }
        }

        public World Build()
        {
            // WithInitialEntityCapacity was previously accepted and then silently ignored here.
            var entities = new EntityManager(_initialEntityCapacity);
            var scheduler = new SystemScheduler();
            var bus = _eventBus ?? new EventBus();
            var handleRegistry = new Sync.EntityHandleRegistry();

            var world = new World(entities, scheduler, bus);

            foreach (var (_, phase, factory) in _systemFactories)
            {
                var system = factory(world);

                // Without this a builder-constructed system has a null EntityManager and throws
                // on its first update, every frame. SystemRunner.InjectSystem does the same.
                if (system is Systems.SystemBase systemBase)
                    systemBase.Inject(entities, bus, handleRegistry);

                scheduler.AddSystem(system, phase);
            }

            if (_makeCurrent)
                world.MakeCurrent();

            return world;
        }
    }
}
