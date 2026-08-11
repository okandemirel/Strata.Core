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
        // Bumped by every operation that can reallocate the native arrays or reorder the
        // dense arrays. Queries hoist raw pointers into their loop, so a structural change
        // from inside a ForEach callback leaves those pointers dangling; the guard turns
        // that silent heap corruption into a thrown exception in Editor/Development builds.
        private int _structuralVersion;
        // Ownership sentinel, mirroring EntityCommandBuffer. NativeArray.Dispose() clears the
        // buffer pointer only on the instance it is called on, so a second Dispose() of the
        // same set — a storage torn down twice, say — would sail past an IsCreated check made
        // on a stale copy and free the same three allocations again.
        private byte _isCreated;

        public int StructuralVersion => _structuralVersion;

        /// <summary>
        /// Native bytes held by the three backing arrays, by allocated capacity.
        /// </summary>
        public long AllocatedBytes =>
            (long)_sparse.Length * sizeof(int)
            + (long)_dense.Length * sizeof(int)
            + (long)_data.Length * UnsafeUtility.SizeOf<T>();

        public int Count => _count;
        public int Capacity => _dense.Length;
        public int SparseCapacity => _sparse.Length;
        public bool IsCreated => _sparse.IsCreated;

        public SparseSet(int sparseCapacity, int denseCapacity, Allocator allocator)
        {
            // The same ceilings EnsureSparseCapacity/EnsureDenseCapacity enforce on growth.
            // Without them the constructor is a documented-cap bypass: a set could be born
            // larger than growing to that size is allowed to make it.
            if (sparseCapacity < 0 || sparseCapacity > MaxSparseCapacity)
                throw new ArgumentOutOfRangeException(nameof(sparseCapacity),
                    $"Sparse capacity must be in [0, {MaxSparseCapacity}] (got {sparseCapacity}).");
            if (denseCapacity < 0 || denseCapacity > MaxDenseCapacity)
                throw new ArgumentOutOfRangeException(nameof(denseCapacity),
                    $"Dense capacity must be in [0, {MaxDenseCapacity}] (got {denseCapacity}).");

            _allocator = allocator;
            _count = 0;
            _structuralVersion = 0;
            _isCreated = 1;

            // Allocate into locals and free what already succeeded if a later allocation
            // throws. Assigning the fields one at a time would leave a half-built set whose
            // earlier Persistent allocations no caller has a reference to any more.
            var sparse = new NativeArray<int>(sparseCapacity, allocator);
            NativeArray<int> dense;
            NativeArray<T> data;
            try
            {
                dense = new NativeArray<int>(denseCapacity, allocator);
            }
            catch
            {
                sparse.Dispose();
                throw;
            }
            try
            {
                data = new NativeArray<T>(denseCapacity, allocator);
            }
            catch
            {
                sparse.Dispose();
                dense.Dispose();
                throw;
            }

            _sparse = sparse;
            _dense = dense;
            _data = data;

            // Widen before multiplying: `sparseCapacity * sizeof(int)` is an int expression,
            // so at large capacities it overflows negative and reaches memset as a huge
            // unsigned byte count.
            UnsafeUtility.MemSet(_sparse.GetUnsafePtr(), 0xFF, (long)sparseCapacity * sizeof(int));
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

            _structuralVersion++;
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

            // GetRef and TryGet both reject a dense index at or past the live region; Remove
            // is the method that writes, so it has to reject it too. A sparse entry pointing
            // into the dead region would otherwise make the swap-and-pop below clobber
            // memory past _count, or read _dense[-1]/_data[-1] when the set is empty.
            if (denseIndex >= _count)
                return false;

            _structuralVersion++;

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

        /// <summary>
        /// Dense index of <paramref name="entityIndex"/>, or -1 if this set does not hold it.
        /// </summary>
        /// <remarks>
        /// Queries add the returned index to a raw <c>T*</c> and hand the caller a ref to the
        /// result, testing only for -1. So the upper bound is checked here rather than there:
        /// a sparse entry that survived past a swap-remove would otherwise resolve to a live
        /// pointer into the dead region and become an out-of-range write for the caller.
        /// </remarks>
        public int GetDenseIndex(int entityIndex)
        {
            if ((uint)entityIndex >= (uint)_sparse.Length) return -1;
            int denseIndex = _sparse[entityIndex];
            return denseIndex < _count ? denseIndex : -1;
        }

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

            // The loop below reads components[i] for every entity index, and both arrays are
            // indexed unchecked in release builds.
            if (components.Length < addCount)
                throw new ArgumentException(
                    $"components must hold at least {addCount} elements (got {components.Length}).",
                    nameof(components));

            // Checked in long arithmetic: an int overflow here would wrap to a negative
            // required capacity, EnsureDenseCapacity would return without growing, and the
            // adds would run off the end of the dense arrays.
            if ((long)_count + addCount > MaxDenseCapacity)
                throw new InvalidOperationException(
                    $"AddRange would grow the set to {(long)_count + addCount} elements, " +
                    $"which exceeds the maximum of {MaxDenseCapacity}.");

            EnsureDenseCapacity(_count + addCount);

            // maxEntity only ever grows, so a negative index would slip past the sparse
            // capacity check and be used as a negative offset into _sparse. Rejecting it
            // rides on the scan that has to happen anyway.
            int maxEntity = 0;
            for (int i = 0; i < addCount; i++)
            {
                int entity = entityIndices[i];
                if (entity < 0)
                    throw new ArgumentOutOfRangeException(nameof(entityIndices),
                        $"Entity index must be non-negative (got {entity} at position {i}).");
                if (entity > maxEntity) maxEntity = entity;
            }

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
            _structuralVersion++;
        }

        public void Dispose()
        {
            if (_isCreated == 0) return;
            _isCreated = 0;

            if (_sparse.IsCreated) _sparse.Dispose();
            if (_dense.IsCreated) _dense.Dispose();
            if (_data.IsCreated) _data.Dispose();
            _count = 0;
        }

        /// <summary>
        /// Ceiling on the sparse array, and therefore on the entity index space a component
        /// storage can address.
        /// </summary>
        /// <remarks>
        /// Internal rather than private so that entity creation can enforce the same ceiling
        /// up front. Today an entity minted with an index at or above this limit is only
        /// rejected when its first component is added, as a "sparse capacity" error that names
        /// neither the entity nor the real constraint.
        /// </remarks>
        internal const int MaxSparseCapacity = 1_048_576;

        // The dense arrays hold one entry per distinct entity index, so they can never need
        // more room than the sparse array is allowed to have.
        private const int MaxDenseCapacity = MaxSparseCapacity;

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
            _structuralVersion++;
        }

        private void EnsureDenseCapacity(int required)
        {
            if (required <= _dense.Length) return;

            if (required > MaxDenseCapacity)
                throw new InvalidOperationException(
                    $"Dense capacity {required} exceeds maximum {MaxDenseCapacity}");

            // Long arithmetic for the same reason as EnsureSparseCapacity: `_dense.Length * 3`
            // wraps negative once the array passes ~715M entries, Math.Max would then pick
            // `required`, and growth would degenerate from 1.5x-amortised to one reallocation
            // and full copy of both arrays per Add.
            long grown = (long)_dense.Length * 3 / 2;
            long target = Math.Max(required, grown);
            int newCapacity = (int)Math.Min(target, MaxDenseCapacity);

            // Both arrays are allocated before either field is replaced. _dense.Length is the
            // capacity check for both, so committing _dense first and then failing to
            // allocate _data would permanently leave the set believing it has room in an
            // array that never grew.
            var newDense = new NativeArray<int>(newCapacity, _allocator);
            NativeArray<T> newData;
            try
            {
                newData = new NativeArray<T>(newCapacity, _allocator);
            }
            catch
            {
                newDense.Dispose();
                throw;
            }

            NativeArray<int>.Copy(_dense, newDense, _count);
            NativeArray<T>.Copy(_data, newData, _count);

            _dense.Dispose();
            _dense = newDense;
            _data.Dispose();
            _data = newData;
            _structuralVersion++;
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
