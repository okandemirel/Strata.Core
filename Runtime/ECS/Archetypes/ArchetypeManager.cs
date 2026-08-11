using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Core;

namespace Strada.Core.ECS.Archetypes
{
    public sealed class ArchetypeManager : IDisposable
    {
        /// <summary>
        /// Below this many pending destructions a sweep is not worth walking every list.
        /// </summary>
        private const int MinSweepBatch = 64;

        private readonly EntityManager _entities;
        private readonly Dictionary<Type, IEntityDescriptor> _descriptors = new(32);
        private readonly Dictionary<Type, List<Entity>> _entitiesByArchetype = new(32);

        // The tracking lists are a cache of entity handles, not the source of truth: an entity
        // created here can be destroyed through EntityManager.DestroyEntity or World.DestroyEntity,
        // neither of which knows this class exists. These two fields let a sweep that drops the
        // dead handles be scheduled lazily, so the common path stays O(1). Comparing the
        // EntityManager's destroy counter against the last swept value tells us in one integer
        // compare whether anything can have died since the lists were last validated.
        private int _lastSweptDestroyVersion;
        private int _trackedCount;

        private bool _disposed;

        public ArchetypeManager(EntityManager entities)
        {
            _entities = entities ?? throw new ArgumentNullException(nameof(entities));
            _lastSweptDestroyVersion = entities.DestroyVersion;
        }

        public void RegisterDescriptor<T>() where T : IEntityDescriptor, new()
        {
            _descriptors[typeof(T)] = new T();
            EnsureTrackingList(typeof(T));
        }

        public void RegisterDescriptor<T>(T descriptor) where T : IEntityDescriptor
        {
            _descriptors[typeof(T)] = descriptor;
            EnsureTrackingList(typeof(T));
        }

        /// <summary>
        /// Returns the tracking list for <paramref name="archetype"/>, creating it only if absent.
        /// </summary>
        /// <remarks>
        /// Re-registering a descriptor used to install a fresh list, which orphaned every entity
        /// created under the previous one: Clear and Dispose only walk the lists still in the
        /// dictionary, so those entities were never destroyed and kept their EntityManager slot
        /// and every component they held for the lifetime of the World.
        /// </remarks>
        private List<Entity> EnsureTrackingList(Type archetype)
        {
            if (!_entitiesByArchetype.TryGetValue(archetype, out var list))
            {
                list = new List<Entity>(256);
                _entitiesByArchetype[archetype] = list;
            }
            return list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IEntityDescriptor EnsureDescriptor<T>() where T : IEntityDescriptor, new()
        {
            if (!_descriptors.TryGetValue(typeof(T), out var descriptor))
            {
                RegisterDescriptor<T>();
                descriptor = _descriptors[typeof(T)];
            }
            return descriptor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Entity CreateEntity<T>() where T : IEntityDescriptor, new()
        {
            var descriptor = EnsureDescriptor<T>();
            SweepIfBacklogged();

            var entity = _entities.CreateEntity();
            descriptor.InitializeComponents(_entities, entity);
            EnsureTrackingList(typeof(T)).Add(entity);
            _trackedCount++;
            return entity;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CreateEntities<T>(Span<Entity> buffer) where T : IEntityDescriptor, new()
        {
            var descriptor = EnsureDescriptor<T>();
            SweepIfBacklogged();
            var list = EnsureTrackingList(typeof(T));

            for (int i = 0; i < buffer.Length; i++)
            {
                var entity = _entities.CreateEntity();
                descriptor.InitializeComponents(_entities, entity);
                list.Add(entity);
                buffer[i] = entity;
            }
            _trackedCount += buffer.Length;
        }

        public Entity[] CreateEntities<T>(int count) where T : IEntityDescriptor, new()
        {
            var entities = new Entity[count];
            CreateEntities<T>(entities);
            return entities;
        }

        public void DestroyEntity<T>(Entity entity) where T : IEntityDescriptor
        {
            // The tracking list is not touched here. List<Entity>.Remove is a linear scan plus
            // an Array.Copy of everything after the hole, so destroying n entities of an
            // archetype one by one cost n^2/2 comparisons and n^2/2 element moves. The handle
            // is dropped instead by the sweep below, which recognises it because
            // EntityManager.Exists now rejects it — and which therefore also drops handles
            // destroyed through EntityManager or World directly, something the old Remove
            // could never see.
            _entities.DestroyEntity(entity);
            SweepIfBacklogged();
        }

        public IReadOnlyList<Entity> GetEntities<T>() where T : IEntityDescriptor
        {
            SweepIfStale();
            return _entitiesByArchetype.TryGetValue(typeof(T), out var list) ? list : Array.Empty<Entity>();
        }

        public int GetEntityCount<T>() where T : IEntityDescriptor
        {
            SweepIfStale();
            return _entitiesByArchetype.TryGetValue(typeof(T), out var list) ? list.Count : 0;
        }

        /// <summary>
        /// Drops handles whose entity no longer exists, if any entity has been destroyed since
        /// the last sweep. Callers that hand tracked entities out must use this.
        /// </summary>
        private void SweepIfStale()
        {
            if (_entities.DestroyVersion != _lastSweptDestroyVersion)
                Sweep();
        }

        /// <summary>
        /// Sweeps only once enough entities have died to be worth it, so the create/destroy path
        /// stays O(1) amortised while the lists still cannot grow without bound.
        /// </summary>
        private void SweepIfBacklogged()
        {
            int pending = _entities.DestroyVersion - _lastSweptDestroyVersion;
            if (pending >= MinSweepBatch && pending >= (_trackedCount >> 1))
                Sweep();
        }

        private void Sweep()
        {
            _lastSweptDestroyVersion = _entities.DestroyVersion;

            int live = 0;
            foreach (var list in _entitiesByArchetype.Values)
            {
                int write = 0;
                for (int read = 0; read < list.Count; read++)
                {
                    var entity = list[read];
                    if (!_entities.Exists(entity))
                        continue;
                    list[write++] = entity;
                }
                if (write < list.Count)
                    list.RemoveRange(write, list.Count - write);
                live += write;
            }
            _trackedCount = live;
        }

        public bool HasDescriptor<T>() where T : IEntityDescriptor
        {
            return _descriptors.ContainsKey(typeof(T));
        }

        public void Clear()
        {
            foreach (var list in _entitiesByArchetype.Values)
            {
                foreach (var entity in list)
                    _entities.DestroyEntity(entity);
                list.Clear();
            }
            _trackedCount = 0;
            _lastSweptDestroyVersion = _entities.DestroyVersion;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
            _descriptors.Clear();
            _entitiesByArchetype.Clear();
        }
    }
}
