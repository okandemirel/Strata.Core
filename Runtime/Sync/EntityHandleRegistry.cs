using System;
using System.Collections.Generic;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;

namespace Strada.Core.Sync
{
    public sealed class EntityHandleRegistry
    {
        private readonly Dictionary<int, Entity> _handleToEntity = new(256);
        private readonly Dictionary<long, int> _entityToHandle = new(256);
        // Liveness cannot come from the stored Entity: it is a snapshot taken at Register time,
        // so comparing its Version against the handle's Version only ever compares a copy with
        // itself and reports valid forever, including after the entity is destroyed and its
        // index recycled. Only the EntityManager knows the current version of an index.
        private readonly EntityManager _entities;
        private int _nextHandleId = 1;

        /// <summary>
        /// Creates a registry with no liveness checking: handles stay valid until they are
        /// unregistered explicitly. Prefer the overload that takes an
        /// <see cref="EntityManager"/>.
        /// </summary>
        public EntityHandleRegistry()
        {
        }

        /// <summary>
        /// Creates a registry that validates handles against live entity state, so a handle to
        /// a destroyed entity reports invalid.
        /// </summary>
        public EntityHandleRegistry(EntityManager entities)
        {
            _entities = entities;
        }

        public EntityHandle Register(Entity entity)
        {
            long entityKey = GetEntityKey(entity);
            if (_entityToHandle.TryGetValue(entityKey, out int existingHandleId))
                return new EntityHandle(existingHandleId, entity.Version);

            if (_nextHandleId == int.MaxValue)
                throw new InvalidOperationException(
                    "EntityHandleRegistry handle ID space exhausted (int.MaxValue handles allocated).");
            int handleId = _nextHandleId++;
            _handleToEntity[handleId] = entity;
            _entityToHandle[entityKey] = handleId;
            return new EntityHandle(handleId, entity.Version);
        }

        public Entity Resolve(EntityHandle handle)
        {
            if (!handle.IsValid)
                return Entity.Null;

            if (_handleToEntity.TryGetValue(handle.Id, out Entity entity))
            {
                if (entity.Version == handle.Version && IsEntityAlive(handle.Id, entity))
                    return entity;
            }
            return Entity.Null;
        }

        public void Unregister(EntityHandle handle)
        {
            if (!handle.IsValid)
                return;

            if (_handleToEntity.TryGetValue(handle.Id, out Entity entity))
            {
                _entityToHandle.Remove(GetEntityKey(entity));
                _handleToEntity.Remove(handle.Id);
            }
        }

        public bool IsValid(EntityHandle handle)
        {
            if (!handle.IsValid)
                return false;

            if (_handleToEntity.TryGetValue(handle.Id, out Entity entity))
                return entity.Version == handle.Version && IsEntityAlive(handle.Id, entity);

            return false;
        }

        /// <summary>
        /// Returns false once the entity has been destroyed, and drops the mapping while it is
        /// here: nothing else prunes the registry, so a registry that is only ever queried
        /// would otherwise grow by two dictionary entries per entity for the whole session.
        /// </summary>
        private bool IsEntityAlive(int handleId, Entity entity)
        {
            if (_entities == null)
                return true;

            if (_entities.Exists(entity))
                return true;

            _handleToEntity.Remove(handleId);
            _entityToHandle.Remove(GetEntityKey(entity));
            return false;
        }

        public void Clear()
        {
            _handleToEntity.Clear();
            _entityToHandle.Clear();
            _nextHandleId = 1;
        }

        private static long GetEntityKey(Entity entity) => ((long)entity.Index << 32) | (uint)entity.Version;
    }
}
