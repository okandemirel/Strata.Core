using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;

namespace Strada.Core.ECS.Storage
{
    public interface IComponentStorage : IDisposable
    {
        bool Contains(int entityIndex);
        bool Remove(int entityIndex);
        void Clear();
        int Count { get; }
        IReadOnlyList<int> GetEntityIndices();

        /// <summary>
        /// Bytes of native memory held by this storage.
        /// </summary>
        /// <remarks>
        /// Component data lives in NativeArrays, which the managed GC does not see at all —
        /// GC.GetTotalMemory reports none of it. Any per-entity memory figure derived from GC
        /// statistics alone is therefore measuring managed overhead, not the storage.
        /// </remarks>
        long AllocatedBytes { get; }

        /// <summary>
        /// Blocks until every scheduled job reading or writing this storage has finished.
        /// </summary>
        /// <remarks>
        /// Jobs capture raw pointers into this storage's native arrays. A structural change
        /// reallocates or reorders those arrays, so it must not happen while a job is still
        /// running — the job would keep writing into freed or shuffled memory, and because
        /// the pointers are marked [NativeDisableUnsafePtrRestriction] the Job Safety System
        /// cannot catch it.
        /// </remarks>
        void CompletePendingJobs();
    }

    public class ComponentStorage<T> : IComponentStorage where T : unmanaged, IComponent
    {
        private SparseSet<T> _sparseSet;
        private JobHandle _pendingJobs;

        public int Count => _sparseSet.Count;

        /// <summary>
        /// Records a job that holds pointers into this storage, so that a later structural
        /// change can wait for it. Combined with any handle already registered.
        /// </summary>
        public void AddDependency(JobHandle handle)
        {
            _pendingJobs = JobHandle.CombineDependencies(_pendingJobs, handle);
        }

        /// <summary>
        /// Bytes of native memory this storage currently holds (sparse + dense + component
        /// data), based on allocated capacity rather than live count.
        /// </summary>
        public long AllocatedBytes => _sparseSet.AllocatedBytes;

        /// <inheritdoc/>
        public void CompletePendingJobs()
        {
            _pendingJobs.Complete();
            _pendingJobs = default;
        }

        public ComponentStorage(int sparseCapacity = 1024, int denseCapacity = 256)
        {
            _sparseSet = new SparseSet<T>(sparseCapacity, denseCapacity, Allocator.Persistent);
        }

        public void Add(int entityIndex, T component)
        {
            CompletePendingJobs();
            _sparseSet.Add(entityIndex, component);
        }

        public bool Remove(int entityIndex)
        {
            CompletePendingJobs();
            return _sparseSet.Remove(entityIndex);
        }

        public bool Contains(int entityIndex)
        {
            return _sparseSet.Contains(entityIndex);
        }

        public T Get(int entityIndex)
        {
            return _sparseSet.Get(entityIndex);
        }

        public ref T GetRef(int entityIndex)
        {
            return ref _sparseSet.GetRef(entityIndex);
        }

        public bool TryGet(int entityIndex, out T component)
        {
            return _sparseSet.TryGet(entityIndex, out component);
        }

        public void Set(int entityIndex, T component)
        {
            _sparseSet.Set(entityIndex, component);
        }

        public ref SparseSet<T> GetSparseSet()
        {
            return ref _sparseSet;
        }

        public IReadOnlyList<int> GetEntityIndices()
        {
            var indices = new List<int>(_sparseSet.Count);
            GetEntityIndices(indices);
            return indices;
        }

        /// <summary>
        /// Non-allocating overload that fills the provided list with entity indices.
        /// The list will be cleared before filling.
        /// </summary>
        public void GetEntityIndices(List<int> outputList)
        {
            outputList.Clear();
            unsafe
            {
                int* densePtr = _sparseSet.GetDenseEntityReadOnlyPtr();
                int count = _sparseSet.Count;
                for (int i = 0; i < count; i++)
                {
                    outputList.Add(densePtr[i]);
                }
            }
        }

        /// <summary>
        /// Copies entity indices to the provided array.
        /// Returns the number of indices copied.
        /// </summary>
        public int GetEntityIndices(int[] outputArray, int startIndex = 0)
        {
            int count = _sparseSet.Count;
            int copyCount = Math.Min(count, outputArray.Length - startIndex);
            unsafe
            {
                int* densePtr = _sparseSet.GetDenseEntityReadOnlyPtr();
                for (int i = 0; i < copyCount; i++)
                {
                    outputArray[startIndex + i] = densePtr[i];
                }
            }
            return copyCount;
        }

        public void Clear()
        {
            CompletePendingJobs();
            _sparseSet.Clear();
        }

        public void Dispose()
        {
            CompletePendingJobs();
            _sparseSet.Dispose();
        }
    }

    public class ComponentStore : IDisposable
    {
        private readonly Dictionary<Type, IComponentStorage> _storages;
        private readonly int _defaultSparseCapacity;
        private readonly int _defaultDenseCapacity;

        public ComponentStore(int defaultSparseCapacity = 1024, int defaultDenseCapacity = 256)
        {
            _storages = new Dictionary<Type, IComponentStorage>();
            _defaultSparseCapacity = defaultSparseCapacity;
            _defaultDenseCapacity = defaultDenseCapacity;
        }

        public ComponentStorage<T> GetOrCreateStorage<T>() where T : unmanaged, IComponent
        {
            Type type = typeof(T);
            if (!_storages.TryGetValue(type, out var storage))
            {
                storage = new ComponentStorage<T>(_defaultSparseCapacity, _defaultDenseCapacity);
                _storages[type] = storage;
            }
            return (ComponentStorage<T>)storage;
        }

        public bool HasStorage<T>() where T : unmanaged, IComponent
        {
            return _storages.ContainsKey(typeof(T));
        }

        /// <summary>
        /// Returns the storage for <typeparamref name="T"/>, or null if none exists yet,
        /// WITHOUT creating one.
        /// </summary>
        /// <remarks>
        /// Read-only paths must use this. GetOrCreateStorage allocates three persistent
        /// NativeArrays (~5 KB) that are never freed, so merely asking "does this entity have
        /// component T?" for a type nobody ever added used to permanently allocate a storage
        /// for it — and every such phantom storage is then walked on every DestroyEntity and
        /// shows up in every editor snapshot.
        /// </remarks>
        public ComponentStorage<T> TryGetStorage<T>() where T : unmanaged, IComponent
        {
            return _storages.TryGetValue(typeof(T), out var storage)
                ? (ComponentStorage<T>)storage
                : null;
        }

        /// <summary>
        /// Total native memory held across every component storage in this store.
        /// </summary>
        public long AllocatedBytes
        {
            get
            {
                long total = 0;
                foreach (var storage in _storages.Values)
                    total += storage.AllocatedBytes;
                return total;
            }
        }

        public void RemoveEntity(int entityIndex)
        {
            foreach (var storage in _storages.Values)
            {
                storage.Remove(entityIndex);
            }
        }

        public void Clear()
        {
            foreach (var storage in _storages.Values)
            {
                storage.Clear();
            }
        }

        public void Dispose()
        {
            foreach (var storage in _storages.Values)
            {
                storage.Dispose();
            }
            _storages.Clear();
        }

        public IEnumerable<Type> GetComponentTypes()
        {
            return _storages.Keys;
        }

        public int GetEntityComponentCount(int entityIndex)
        {
            int count = 0;
            foreach (var storage in _storages.Values)
            {
                if (storage.Contains(entityIndex))
                    count++;
            }
            return count;
        }

        public bool HasComponent(int entityIndex, Type componentType)
        {
            return _storages.TryGetValue(componentType, out var storage) && storage.Contains(entityIndex);
        }

        public object GetComponentBoxed(int entityIndex, Type componentType)
        {
            if (!_storages.TryGetValue(componentType, out var storage))
                return null;

            var method = storage.GetType().GetMethod("Get");
            if (method == null) return null;

            try
            {
                return method.Invoke(storage, new object[] { entityIndex });
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Strada] Failed to get component {componentType.Name} for entity {entityIndex}: {e.Message}");
                return null;
            }
        }

        public void SetComponentBoxed(int entityIndex, Type componentType, object value)
        {
            if (!_storages.TryGetValue(componentType, out var storage))
                return;

            var method = storage.GetType().GetMethod("Set");
            if (method == null) return;

            try
            {
                method.Invoke(storage, new object[] { entityIndex, value });
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Strada] Failed to set component {componentType.Name} for entity {entityIndex}: {e.Message}");
            }
        }
    }
}
