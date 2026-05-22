using System;
using System.Collections.Generic;
using Strada.Core.ECS;

namespace Strada.Core.Sync
{
    public sealed class EntityHandleRegistry
    {
        private readonly Dictionary<int, Entity> _handleToEntity = new(256);
        private readonly Dictionary<long, int> _entityToHandle = new(256);
        private int _nextHandleId = 1;

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
                if (entity.Version == handle.Version)
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
                return entity.Version == handle.Version;

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
