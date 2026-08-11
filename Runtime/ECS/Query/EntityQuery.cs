using System.Runtime.CompilerServices;
using Strada.Core.ECS.Jobs;
using Strada.Core.ECS.Storage;

namespace Strada.Core.ECS.Query
{
    public readonly struct EntityQuery<T1>
        where T1 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;

        internal EntityQuery(ComponentStorage<T1> storage1)
        {
            _storage1 = storage1;
        }

        public int Count => _storage1.Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1> action)
        {
            ref var sparseSet = ref _storage1.GetSparseSet();
            int count = sparseSet.Count;

            unsafe
            {
                int* entities = sparseSet.GetDenseEntityPtr();
                T1* data = sparseSet.GetDataPtr();
                int guard = sparseSet.StructuralVersion;

                for (int i = 0; i < count; i++)
                {
                    // The bound is re-checked against the live count because a removal from
                    // inside the callback swap-removes: the entity at the end moves down into
                    // the vacated slot and Count drops. Running on to the snapshotted count
                    // would visit the now-dead tail slot, invoke the callback a second time
                    // for an entity the loop has already passed, and silently discard whatever
                    // that callback wrote through the ref. Editor and Development builds throw
                    // from the guard below before getting here; release builds have no guard.
                    if (i >= sparseSet.Count) break;

                    action(entities[i], ref data[i]);
                    QueryGuard.Check(guard, sparseSet.StructuralVersion);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEachReadOnly(QueryDelegateReadOnly<T1> action)
        {
            ref var sparseSet = ref _storage1.GetSparseSet();
            int count = sparseSet.Count;

            unsafe
            {
                int* entities = sparseSet.GetDenseEntityReadOnlyPtr();
                T1* data = sparseSet.GetDataReadOnlyPtr();
                int guard = sparseSet.StructuralVersion;

                for (int i = 0; i < count; i++)
                {
                    // See ForEach: the live count keeps a swap-remove made from inside the
                    // callback from making the loop revisit the slot the removed entity left.
                    if (i >= sparseSet.Count) break;

                    action(entities[i], in data[i]);
                    QueryGuard.Check(guard, sparseSet.StructuralVersion);
                }
            }
        }

        /// <summary>
        /// Runs <typeparamref name="TJob"/> over the matching entities on the calling thread,
        /// without the delegate.
        /// </summary>
        /// <remarks>
        /// <see cref="QueryDelegate{T1}"/> is a MulticastDelegate: the per-entity call is an
        /// indirect callvirt that neither Mono nor IL2CPP can inline through, and because it
        /// is opaque to the optimiser it also stops the loop's base pointers from staying in
        /// registers. Callers pay for it too — a lambda that captures anything allocates a
        /// display class and a delegate object, once per frame for a system that queries every
        /// update. The struct constraint gives each TJob its own specialisation, so Execute is
        /// a direct call that inlines and the captured state lives in TJob's fields instead.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach<TJob>(ref TJob job) where TJob : struct, IJobComponent<T1>
        {
            ref var sparseSet = ref _storage1.GetSparseSet();
            int count = sparseSet.Count;

            unsafe
            {
                int* entities = sparseSet.GetDenseEntityPtr();
                T1* data = sparseSet.GetDataPtr();
                int guard = sparseSet.StructuralVersion;

                for (int i = 0; i < count; i++)
                {
                    if (i >= sparseSet.Count) break;

                    job.Execute(entities[i], ref data[i]);
                    QueryGuard.Check(guard, sparseSet.StructuralVersion);
                }
            }
        }
    }

    public readonly struct EntityQuery<T1, T2>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;

        internal EntityQuery(ComponentStorage<T1> storage1, ComponentStorage<T2> storage2)
        {
            _storage1 = storage1;
            _storage2 = storage2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();

            unsafe
            {
                // Hoisted out of the loop: these were re-fetched once per component per
                // entity, and each fetch is a safety-handle check in Editor/Development.
                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;

                // Two loop bodies, one per driving set. The sparse-set invariant is
                // _sparse[_dense[i]] == i, so the driving set's own GetDenseIndex probe can
                // only ever return i: keeping it symmetric spent a random read into an array
                // of up to a million ints, plus a branch that is never taken, on every entity.
                if (set1.Count <= set2.Count)
                {
                    int* entities = set1.GetDenseEntityPtr();
                    int count = set1.Count;

                    for (int i = 0; i < count; i++)
                    {
                        // The live count shrinks the loop if the callback removes a component,
                        // rather than letting it revisit the slot the swap-remove vacated.
                        if (i >= set1.Count) break;

                        int entityIndex = entities[i];
                        int idx2 = set2.GetDenseIndex(entityIndex);
                        if (idx2 < 0)
                            continue;

                        action(entityIndex, ref data1[i], ref data2[idx2]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                    }
                }
                else
                {
                    int* entities = set2.GetDenseEntityPtr();
                    int count = set2.Count;

                    for (int i = 0; i < count; i++)
                    {
                        if (i >= set2.Count) break;

                        int entityIndex = entities[i];
                        int idx1 = set1.GetDenseIndex(entityIndex);
                        if (idx1 < 0)
                            continue;

                        action(entityIndex, ref data1[idx1], ref data2[i]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                    }
                }
            }
        }

        /// <summary>
        /// Struct-typed alternative to the delegate overload: no closure allocation, and the
        /// job body inlines into the loop. See the one-component overload for why.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach<TJob>(ref TJob job) where TJob : struct, IJobComponent<T1, T2>
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();

            unsafe
            {
                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;

                if (set1.Count <= set2.Count)
                {
                    int* entities = set1.GetDenseEntityPtr();
                    int count = set1.Count;

                    for (int i = 0; i < count; i++)
                    {
                        if (i >= set1.Count) break;

                        int entityIndex = entities[i];
                        int idx2 = set2.GetDenseIndex(entityIndex);
                        if (idx2 < 0)
                            continue;

                        job.Execute(entityIndex, ref data1[i], ref data2[idx2]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                    }
                }
                else
                {
                    int* entities = set2.GetDenseEntityPtr();
                    int count = set2.Count;

                    for (int i = 0; i < count; i++)
                    {
                        if (i >= set2.Count) break;

                        int entityIndex = entities[i];
                        int idx1 = set1.GetDenseIndex(entityIndex);
                        if (idx1 < 0)
                            continue;

                        job.Execute(entityIndex, ref data1[idx1], ref data2[i]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                    }
                }
            }
        }
    }

    public readonly struct EntityQuery<T1, T2, T3>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private readonly ComponentStorage<T3> _storage3;

        internal EntityQuery(
            ComponentStorage<T1> storage1,
            ComponentStorage<T2> storage2,
            ComponentStorage<T3> storage3)
        {
            _storage1 = storage1;
            _storage2 = storage2;
            _storage3 = storage3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2, T3> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();

            int count1 = set1.Count;
            int count2 = set2.Count;
            int count3 = set3.Count;

            unsafe
            {
                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;
                int guard3 = set3.StructuralVersion;

                // One loop body per driving set: the driving set's dense index is the loop
                // index by the sparse-set invariant, so probing its own sparse array is a
                // random read that can only ever return i. See EntityQuery<T1, T2>.ForEach.
                if (count1 <= count2 && count1 <= count3)
                {
                    int* entities = set1.GetDenseEntityPtr();

                    for (int i = 0; i < count1; i++)
                    {
                        // The live count shrinks the loop if the callback removes a component,
                        // rather than letting it revisit the slot the swap-remove vacated.
                        if (i >= set1.Count) break;

                        int entityIndex = entities[i];
                        int idx2 = set2.GetDenseIndex(entityIndex);
                        int idx3 = set3.GetDenseIndex(entityIndex);
                        if (idx2 < 0 || idx3 < 0)
                            continue;

                        action(entityIndex, ref data1[i], ref data2[idx2], ref data3[idx3]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                        QueryGuard.Check(guard3, set3.StructuralVersion);
                    }
                }
                else if (count2 <= count3)
                {
                    int* entities = set2.GetDenseEntityPtr();

                    for (int i = 0; i < count2; i++)
                    {
                        if (i >= set2.Count) break;

                        int entityIndex = entities[i];
                        int idx1 = set1.GetDenseIndex(entityIndex);
                        int idx3 = set3.GetDenseIndex(entityIndex);
                        if (idx1 < 0 || idx3 < 0)
                            continue;

                        action(entityIndex, ref data1[idx1], ref data2[i], ref data3[idx3]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                        QueryGuard.Check(guard3, set3.StructuralVersion);
                    }
                }
                else
                {
                    int* entities = set3.GetDenseEntityPtr();

                    for (int i = 0; i < count3; i++)
                    {
                        if (i >= set3.Count) break;

                        int entityIndex = entities[i];
                        int idx1 = set1.GetDenseIndex(entityIndex);
                        int idx2 = set2.GetDenseIndex(entityIndex);
                        if (idx1 < 0 || idx2 < 0)
                            continue;

                        action(entityIndex, ref data1[idx1], ref data2[idx2], ref data3[i]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                        QueryGuard.Check(guard3, set3.StructuralVersion);
                    }
                }
            }
        }

        /// <summary>
        /// Struct-typed alternative to the delegate overload: no closure allocation, and the
        /// job body inlines into the loop. See the one-component overload for why.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach<TJob>(ref TJob job) where TJob : struct, IJobComponent<T1, T2, T3>
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();

            int count1 = set1.Count;
            int count2 = set2.Count;
            int count3 = set3.Count;

            unsafe
            {
                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;
                int guard3 = set3.StructuralVersion;

                if (count1 <= count2 && count1 <= count3)
                {
                    int* entities = set1.GetDenseEntityPtr();

                    for (int i = 0; i < count1; i++)
                    {
                        if (i >= set1.Count) break;

                        int entityIndex = entities[i];
                        int idx2 = set2.GetDenseIndex(entityIndex);
                        int idx3 = set3.GetDenseIndex(entityIndex);
                        if (idx2 < 0 || idx3 < 0)
                            continue;

                        job.Execute(entityIndex, ref data1[i], ref data2[idx2], ref data3[idx3]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                        QueryGuard.Check(guard3, set3.StructuralVersion);
                    }
                }
                else if (count2 <= count3)
                {
                    int* entities = set2.GetDenseEntityPtr();

                    for (int i = 0; i < count2; i++)
                    {
                        if (i >= set2.Count) break;

                        int entityIndex = entities[i];
                        int idx1 = set1.GetDenseIndex(entityIndex);
                        int idx3 = set3.GetDenseIndex(entityIndex);
                        if (idx1 < 0 || idx3 < 0)
                            continue;

                        job.Execute(entityIndex, ref data1[idx1], ref data2[i], ref data3[idx3]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                        QueryGuard.Check(guard3, set3.StructuralVersion);
                    }
                }
                else
                {
                    int* entities = set3.GetDenseEntityPtr();

                    for (int i = 0; i < count3; i++)
                    {
                        if (i >= set3.Count) break;

                        int entityIndex = entities[i];
                        int idx1 = set1.GetDenseIndex(entityIndex);
                        int idx2 = set2.GetDenseIndex(entityIndex);
                        if (idx1 < 0 || idx2 < 0)
                            continue;

                        job.Execute(entityIndex, ref data1[idx1], ref data2[idx2], ref data3[i]);

                        QueryGuard.Check(guard1, set1.StructuralVersion);
                        QueryGuard.Check(guard2, set2.StructuralVersion);
                        QueryGuard.Check(guard3, set3.StructuralVersion);
                    }
                }
            }
        }
    }

    public delegate void QueryDelegate<T1>(int entityIndex, ref T1 c1) where T1 : unmanaged;
    public delegate void QueryDelegateReadOnly<T1>(int entityIndex, in T1 c1) where T1 : unmanaged;
    public delegate void QueryDelegate<T1, T2>(int entityIndex, ref T1 c1, ref T2 c2)
        where T1 : unmanaged where T2 : unmanaged;
    public delegate void QueryDelegate<T1, T2, T3>(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;
}
