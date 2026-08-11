using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Strada.Core.ECS.Storage
{
    public unsafe struct SparseSet<T> : IDisposable where T : unmanaged
    {
        private NativeArray<int> _sparse;
        private NativeArray<int> _dense;
        private NativeArray<T> _data;
        private int _count;
        private Allocator _allocator;

        public int Count => _count;
        public int Capacity => _dense.Length;
        public int SparseCapacity => _sparse.Length;
        public bool IsCreated => _sparse.IsCreated;

        public SparseSet(int sparseCapacity, int denseCapacity, Allocator allocator)
        {
            _allocator = allocator;
            _sparse = new NativeArray<int>(sparseCapacity, allocator);
            _dense = new NativeArray<int>(denseCapacity, allocator);
            _data = new NativeArray<T>(denseCapacity, allocator);
            _count = 0;

            UnsafeUtility.MemSet(_sparse.GetUnsafePtr(), 0xFF, sparseCapacity * sizeof(int));
        }

        public void Add(int entityIndex, T component)
        {
            if (entityIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(entityIndex),
                    $"Entity index must be non-negative (got {entityIndex}).");

            EnsureSparseCapacity(entityIndex + 1);

            if (_sparse[entityIndex] >= 0)
            {
                _data[_sparse[entityIndex]] = component;
                return;
            }

            EnsureDenseCapacity(_count + 1);

            _dense[_count] = entityIndex;
            _data[_count] = component;
            _sparse[entityIndex] = _count;
            _count++;
        }

        public bool Remove(int entityIndex)
        {
            if ((uint)entityIndex >= (uint)_sparse.Length || _sparse[entityIndex] < 0)
                return false;

            int denseIndex = _sparse[entityIndex];
            int lastIndex = _count - 1;

            if (denseIndex != lastIndex)
            {
                int lastEntityIndex = _dense[lastIndex];
                _dense[denseIndex] = lastEntityIndex;
                _data[denseIndex] = _data[lastIndex];
                _sparse[lastEntityIndex] = denseIndex;
            }

            _sparse[entityIndex] = -1;
            _count--;
            return true;
        }

        public bool Contains(int entityIndex)
        {
            return (uint)entityIndex < (uint)_sparse.Length && _sparse[entityIndex] >= 0;
        }

        public T Get(int entityIndex)
        {
            if ((uint)entityIndex >= (uint)_sparse.Length || _sparse[entityIndex] < 0)
                throw new InvalidOperationException($"Entity {entityIndex} does not exist in sparse set");
            return _data[_sparse[entityIndex]];
        }

        public ref T GetRef(int entityIndex)
        {
            if (entityIndex < 0 || entityIndex >= _sparse.Length)
                throw new ArgumentOutOfRangeException(nameof(entityIndex), $"Entity index {entityIndex} is out of range [0, {_sparse.Length})");

            int denseIndex = _sparse[entityIndex];
            if (denseIndex < 0 || denseIndex >= _count)
                throw new InvalidOperationException($"Entity {entityIndex} does not exist in sparse set");

            return ref ((T*)_data.GetUnsafePtr())[denseIndex];
        }

        public bool TryGet(int entityIndex, out T component)
        {
            if ((uint)entityIndex < (uint)_sparse.Length)
            {
                int denseIndex = _sparse[entityIndex];
                if (denseIndex >= 0 && denseIndex < _count)
                {
                    component = _data[denseIndex];
                    return true;
                }
            }

            component = default;
            return false;
        }

        public void Set(int entityIndex, T component)
        {
            if ((uint)entityIndex >= (uint)_sparse.Length || _sparse[entityIndex] < 0)
                throw new InvalidOperationException($"Entity {entityIndex} does not exist in sparse set");
            _data[_sparse[entityIndex]] = component;
        }

        public int* GetDenseEntityPtr() => (int*)_dense.GetUnsafePtr();
        public T* GetDataPtr() => (T*)_data.GetUnsafePtr();
        public int* GetDenseEntityReadOnlyPtr() => (int*)_dense.GetUnsafeReadOnlyPtr();
        public T* GetDataReadOnlyPtr() => (T*)_data.GetUnsafeReadOnlyPtr();
        public int* GetSparsePtr() => (int*)_sparse.GetUnsafePtr();
        public int GetDenseIndex(int entityIndex) => (uint)entityIndex < (uint)_sparse.Length ? _sparse[entityIndex] : -1;

        public NativeSlice<T> GetDataSlice() => new NativeSlice<T>(_data, 0, _count);
        public NativeSlice<int> GetEntitySlice() => new NativeSlice<int>(_dense, 0, _count);

        public void Reserve(int capacity)
        {
            EnsureDenseCapacity(capacity);
            EnsureSparseCapacity(capacity);
        }

        public void AddRange(NativeArray<int> entityIndices, NativeArray<T> components)
        {
            int addCount = entityIndices.Length;
            EnsureDenseCapacity(_count + addCount);

            int maxEntity = 0;
            for (int i = 0; i < addCount; i++)
                if (entityIndices[i] > maxEntity) maxEntity = entityIndices[i];

            EnsureSparseCapacity(maxEntity + 1);

            for (int i = 0; i < addCount; i++)
            {
                int entityIndex = entityIndices[i];
                if (_sparse[entityIndex] >= 0)
                {
                    _data[_sparse[entityIndex]] = components[i];
                    continue;
                }

                _dense[_count] = entityIndex;
                _data[_count] = components[i];
                _sparse[entityIndex] = _count;
                _count++;
            }
        }

        public void RemoveRange(NativeArray<int> entityIndices)
        {
            for (int i = 0; i < entityIndices.Length; i++)
                Remove(entityIndices[i]);
        }

        public void Clear()
        {
            if (!_sparse.IsCreated) return;

            for (int i = 0; i < _count; i++)
            {
                _sparse[_dense[i]] = -1;
            }
            _count = 0;
        }

        public void Dispose()
        {
            if (_sparse.IsCreated) _sparse.Dispose();
            if (_dense.IsCreated) _dense.Dispose();
            if (_data.IsCreated) _data.Dispose();
            _count = 0;
        }

        private const int MaxSparseCapacity = 1_048_576;

        private void EnsureSparseCapacity(int required)
        {
            if (required <= _sparse.Length) return;

            if (required > MaxSparseCapacity)
                throw new InvalidOperationException(
                    $"Entity index requires sparse capacity {required} which exceeds maximum {MaxSparseCapacity}");

            // Use long arithmetic to prevent int overflow when _sparse.Length is large,
            // then clamp to MaxSparseCapacity.
            long grown = (long)_sparse.Length * 3 / 2;
            long target = Math.Max(required, grown);
            int newCapacity = (int)Math.Min(target, MaxSparseCapacity);
            var newSparse = new NativeArray<int>(newCapacity, _allocator);

            NativeArray<int>.Copy(_sparse, newSparse, _sparse.Length);

            int* ptr = (int*)newSparse.GetUnsafePtr();
            UnsafeUtility.MemSet(ptr + _sparse.Length, 0xFF, (newCapacity - _sparse.Length) * sizeof(int));

            _sparse.Dispose();
            _sparse = newSparse;
        }

        private void EnsureDenseCapacity(int required)
        {
            if (required <= _dense.Length) return;

            int newCapacity = Math.Max(required, _dense.Length * 3 / 2);

            var newDense = new NativeArray<int>(newCapacity, _allocator);
            NativeArray<int>.Copy(_dense, newDense, _count);
            _dense.Dispose();
            _dense = newDense;

            var newData = new NativeArray<T>(newCapacity, _allocator);
            NativeArray<T>.Copy(_data, newData, _count);
            _data.Dispose();
            _data = newData;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        public struct Enumerator
        {
            private readonly SparseSet<T> _set;
            private int _index;

            internal Enumerator(SparseSet<T> set)
            {
                _set = set;
                _index = -1;
            }

            public bool MoveNext()
            {
                _index++;
                return _index < _set._count;
            }

            public (int entityIndex, T component) Current => (_set._dense[_index], _set._data[_index]);

            public void Reset() => _index = -1;
        }
    }
}
