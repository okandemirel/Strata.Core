using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Strada.Core.ECS.Storage;

namespace Strada.Core.ECS.Core
{
    /// <remarks>
    /// FRAMEWORK DESIGN: EntityManager is intentionally not thread-safe. Strada's ECS
    /// follows the Unity main-thread model — entity creation, destruction, and component
    /// add/remove must happen on the main thread or be deferred through an
    /// <see cref="Strada.Core.ECS.Jobs.EntityCommandBuffer"/> recorded inside a job and
    /// played back on the main thread. Adding locks to the hot path would erase the
    /// SparseSet's cache-friendly performance characteristics.
    /// </remarks>
    public sealed class EntityManager : IDisposable
    {
        private const int InitialCapacity = 1024;

        /// <summary>
        /// Upper bound on the entity index space. Capacity growth is clamped to this, and any
        /// request beyond it throws rather than overflowing into a negative allocation size.
        /// </summary>
        public const int MaxEntityCapacity = int.MaxValue / 2;

        private NativeArray<int> _versions;
        private NativeArray<byte> _active;
        private NativeList<int> _recycledIndices;
        private int _nextEntityIndex;
        private int _entityCount;
        private readonly ComponentStore _store;
        private bool _disposed;

        public int EntityCount => _entityCount;
        public ComponentStore Store => _store;

        public EntityManager() : this(InitialCapacity) { }

        public EntityManager(int initialCapacity)
        {
            // A zero or negative capacity would make the doubling growth in EnsureCapacity
            // unable to ever reach the requested size.
            if (initialCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity),
                    $"Initial entity capacity must be positive (got {initialCapacity}).");
            if (initialCapacity > MaxEntityCapacity)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity),
                    $"Initial entity capacity {initialCapacity} exceeds the maximum of {MaxEntityCapacity}.");

            _versions = new NativeArray<int>(initialCapacity, Allocator.Persistent);
            _active = new NativeArray<byte>(initialCapacity, Allocator.Persistent);
            _recycledIndices = new NativeList<int>(256, Allocator.Persistent);
            _nextEntityIndex = 1;
            _entityCount = 0;
            _store = new ComponentStore();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Entity CreateEntity()
        {
            int index;
            int version;

            if (_recycledIndices.Length > 0)
            {
                index = _recycledIndices[_recycledIndices.Length - 1];
                _recycledIndices.RemoveAt(_recycledIndices.Length - 1);
                version = _versions[index] + 1;
            }
            else
            {
                index = _nextEntityIndex++;
                EnsureCapacity(index + 1);
                version = 1;
            }

            _versions[index] = version;
            _active[index] = 1;
            _entityCount++;

            return new Entity(index, version);
        }

        /// <summary>
        /// Creates multiple entities at once for better performance when spawning many entities.
        /// </summary>
        public void CreateEntities(NativeArray<Entity> entities)
        {
            int count = entities.Length;
            if (count < 0 || _nextEntityIndex > int.MaxValue - count)
                throw new ArgumentException("Entity count overflow");

            EnsureCapacity(_nextEntityIndex + count);

            for (int i = 0; i < count; i++)
            {
                int index;
                int version;

                if (_recycledIndices.Length > 0)
                {
                    index = _recycledIndices[_recycledIndices.Length - 1];
                    _recycledIndices.RemoveAt(_recycledIndices.Length - 1);
                    version = _versions[index] + 1;
                }
                else
                {
                    index = _nextEntityIndex++;
                    version = 1;
                }

                _versions[index] = version;
                _active[index] = 1;
                entities[i] = new Entity(index, version);
            }

            _entityCount += count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DestroyEntity(Entity entity)
        {
            if (!Exists(entity))
                return;

            _store.RemoveEntity(entity.Index);
            _active[entity.Index] = 0;
            _recycledIndices.Add(entity.Index);
            _entityCount--;
        }

        /// <summary>
        /// Destroys multiple entities at once for better performance.
        /// </summary>
        public void DestroyEntities(NativeArray<Entity> entities)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                DestroyEntity(entities[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(Entity entity)
        {
            if (entity.Index <= 0 || entity.Index >= _versions.Length)
                return false;

            return _active[entity.Index] == 1 && _versions[entity.Index] == entity.Version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Entity GetEntity(int index)
        {
            if (index <= 0 || index >= _versions.Length || _active[index] == 0)
                return Entity.Null;

            return new Entity(index, _versions[index]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddComponent<T>(Entity entity) where T : unmanaged, IComponent
        {
            if (!Exists(entity))
                return;

            var storage = _store.GetOrCreateStorage<T>();
            storage.Add(entity.Index, default);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
        {
            if (!Exists(entity))
                return;

            var storage = _store.GetOrCreateStorage<T>();
            storage.Add(entity.Index, component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent
        {
            if (!Exists(entity))
                return;

            var storage = _store.TryGetStorage<T>();
            storage?.Remove(entity.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent
        {
            if (!Exists(entity))
                return false;

            var storage = _store.TryGetStorage<T>();
            return storage != null && storage.Contains(entity.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetComponent<T>(Entity entity) where T : unmanaged, IComponent
        {
            if (!Exists(entity))
                ThrowEntityNotExists(entity);

            var storage = _store.TryGetStorage<T>()
                          ?? throw new InvalidOperationException(
                              $"Entity {entity.Index} does not have component {typeof(T).Name}");
            return storage.Get(entity.Index);
        }

        /// <summary>
        /// Gets a reference to a component, allowing direct modification without copy.
        /// Includes entity version validation for safety.
        /// WARNING: The reference becomes invalid if the entity is destroyed or component removed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponentRef<T>(Entity entity) where T : unmanaged, IComponent
        {
            if (!Exists(entity))
                ThrowEntityNotExists(entity);

            var storage = _store.TryGetStorage<T>();
            if (storage == null || !storage.Contains(entity.Index))
                ThrowComponentNotFound<T>(entity);

            return ref storage.GetRef(entity.Index);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowEntityNotExists(Entity entity) =>
            throw new InvalidOperationException($"Entity {entity.Index}:{entity.Version} does not exist or version mismatch");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowComponentNotFound<T>(Entity entity) =>
            throw new InvalidOperationException($"Entity {entity.Index} does not have component {typeof(T).Name}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
        {
            if (!Exists(entity))
                return;

            var storage = _store.TryGetStorage<T>()
                          ?? throw new InvalidOperationException(
                              $"Entity {entity.Index} does not have component {typeof(T).Name}");
            storage.Set(entity.Index, component);
        }

        /// <summary>
        /// Gets all active entity indices. Allocates a managed list for compatibility.
        /// For performance-critical code, use GetActiveEntitiesNonAlloc instead.
        /// </summary>
        public IEnumerable<int> GetAllEntities()
        {
            var result = new List<int>(_entityCount);
            for (int i = 1; i < _nextEntityIndex; i++)
            {
                if (_active[i] == 1)
                    result.Add(i);
            }
            return result;
        }

        /// <summary>
        /// Gets active entity indices without allocation. Caller provides the output array.
        /// Returns the number of active entities written.
        /// </summary>
        public int GetActiveEntitiesNonAlloc(NativeArray<int> output)
        {
            int written = 0;
            int maxWrite = output.Length;

            for (int i = 1; i < _nextEntityIndex && written < maxWrite; i++)
            {
                if (_active[i] == 1)
                    output[written++] = i;
            }

            return written;
        }

        public void Clear()
        {
            _store.Clear();

            unsafe
            {
                UnsafeUtility.MemClear(_versions.GetUnsafePtr(), _versions.Length * sizeof(int));
                UnsafeUtility.MemClear(_active.GetUnsafePtr(), _active.Length * sizeof(byte));
            }

            _recycledIndices.Clear();
            _nextEntityIndex = 1;
            _entityCount = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _store.Dispose();

            if (_versions.IsCreated) _versions.Dispose();
            if (_active.IsCreated) _active.Dispose();
            if (_recycledIndices.IsCreated) _recycledIndices.Dispose();
        }

        /// <summary>
        /// O(1) existence check by raw entity index, without the version component.
        /// </summary>
        /// <remarks>
        /// Tooling that tracks bare indices needs this. The alternative was
        /// GetAllEntities().Contains(index), which allocates a list of every live entity and
        /// scans it linearly — quadratic when called once per tracked entity.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ExistsIndex(int index) => IsActiveIndex(index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsActiveIndex(int index)
        {
            return index > 0 && index < _active.Length && _active[index] == 1;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _versions.Length)
                return;

            if (required > MaxEntityCapacity)
                throw new ArgumentOutOfRangeException(nameof(required),
                    $"Requested entity capacity {required} exceeds the maximum of {MaxEntityCapacity}.");

            // Grow in long so the doubling cannot overflow into a negative value, and clamp
            // inside the loop. A clamp placed after the loop is unreachable: an overflowed
            // (or zero) capacity never satisfies the loop condition, so the loop never exits.
            long newCapacity = _versions.Length > 0 ? _versions.Length : 1;
            while (newCapacity < required)
            {
                newCapacity *= 2;
                if (newCapacity > MaxEntityCapacity)
                {
                    newCapacity = MaxEntityCapacity;
                    break;
                }
            }

            int capacity = (int)newCapacity;
            var newVersions = new NativeArray<int>(capacity, Allocator.Persistent);
            var newActive = new NativeArray<byte>(capacity, Allocator.Persistent);

            NativeArray<int>.Copy(_versions, newVersions, _versions.Length);
            NativeArray<byte>.Copy(_active, newActive, _active.Length);

            _versions.Dispose();
            _active.Dispose();

            _versions = newVersions;
            _active = newActive;
        }
        public void RestoreState(int nextEntityIndex, int[] activeIndices, int[] versions)
        {
            if (activeIndices == null) throw new ArgumentNullException(nameof(activeIndices));
            if (versions == null) throw new ArgumentNullException(nameof(versions));
            if (nextEntityIndex < 1 || nextEntityIndex > MaxEntityCapacity)
                throw new ArgumentOutOfRangeException(nameof(nextEntityIndex),
                    $"nextEntityIndex must be in [1, {MaxEntityCapacity}] (got {nextEntityIndex}).");

            Clear();

            EnsureCapacity(nextEntityIndex);
            _nextEntityIndex = nextEntityIndex;

            for (int i = 0; i < activeIndices.Length; i++)
            {
                int idx = activeIndices[i];
                // Unsigned compare rejects negatives and the upper bound in one branch — a
                // negative index here would otherwise be an arbitrary-offset 1-byte write.
                if ((uint)idx < (uint)_active.Length)
                {
                    _active[idx] = 1;
                    _entityCount++;
                }
            }

            for (int i = 0; i < versions.Length; i++)
            {
                if (i < _versions.Length)
                {
                    _versions[i] = versions[i];
                }
            }
        }
        public void CaptureState(out int nextEntityIndex, out int[] activeIndices, out int[] versions)
        {
            nextEntityIndex = _nextEntityIndex;
            
            var activeList = new List<int>(_entityCount);
            for (int i = 1; i < _nextEntityIndex; i++)
            {
                if (_active[i] == 1) activeList.Add(i);
            }
            activeIndices = activeList.ToArray();

            versions = _versions.ToArray();
        }
    }
}
