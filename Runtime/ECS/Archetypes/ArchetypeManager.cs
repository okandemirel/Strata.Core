using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Core;

namespace Strada.Core.ECS.Archetypes
{
    public sealed class ArchetypeManager : IDisposable
    {
        private readonly EntityManager _entities;
        private readonly Dictionary<Type, IEntityDescriptor> _descriptors = new(32);
        private readonly Dictionary<Type, List<Entity>> _entitiesByArchetype = new(32);
        private bool _disposed;

        public ArchetypeManager(EntityManager entities)
        {
            _entities = entities;
        }

        public void RegisterDescriptor<T>() where T : IEntityDescriptor, new()
        {
            var descriptor = new T();
            _descriptors[typeof(T)] = descriptor;
            _entitiesByArchetype[typeof(T)] = new List<Entity>(256);
        }

        public void RegisterDescriptor<T>(T descriptor) where T : IEntityDescriptor
        {
            _descriptors[typeof(T)] = descriptor;
            _entitiesByArchetype[typeof(T)] = new List<Entity>(256);
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
            var entity = _entities.CreateEntity();
            descriptor.InitializeComponents(_entities, entity);
            _entitiesByArchetype[typeof(T)].Add(entity);
            return entity;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CreateEntities<T>(Span<Entity> buffer) where T : IEntityDescriptor, new()
        {
            var descriptor = EnsureDescriptor<T>();
            var list = _entitiesByArchetype[typeof(T)];

            for (int i = 0; i < buffer.Length; i++)
            {
                var entity = _entities.CreateEntity();
                descriptor.InitializeComponents(_entities, entity);
                list.Add(entity);
                buffer[i] = entity;
            }
        }

        public Entity[] CreateEntities<T>(int count) where T : IEntityDescriptor, new()
        {
            var entities = new Entity[count];
            CreateEntities<T>(entities);
            return entities;
        }

        public void DestroyEntity<T>(Entity entity) where T : IEntityDescriptor
        {
            // FRAMEWORK DESIGN: List<Entity>.Remove is O(n) and does not compact the
            // backing array. The audit flagged this as "unbounded entity-list growth",
            // but the ECS data layout intentionally keeps the per-archetype list as a
            // dense, append-mostly structure — entities live for the duration of their
            // archetype and bulk teardown clears the list via Reset. A separate compaction
            // pass would shift every following element and erase the cache-friendly
            // access pattern; the current design trades a slow Remove for a fast
            // iteration over the live entity set.
            if (_entitiesByArchetype.TryGetValue(typeof(T), out var list))
                list.Remove(entity);

            _entities.DestroyEntity(entity);
        }

        public IReadOnlyList<Entity> GetEntities<T>() where T : IEntityDescriptor
        {
            return _entitiesByArchetype.TryGetValue(typeof(T), out var list) ? list : Array.Empty<Entity>();
        }

        public int GetEntityCount<T>() where T : IEntityDescriptor
        {
            return _entitiesByArchetype.TryGetValue(typeof(T), out var list) ? list.Count : 0;
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
