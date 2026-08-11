using System.Runtime.CompilerServices;
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
                    action(entities[i], in data[i]);
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

            bool useSet1 = set1.Count <= set2.Count;

            unsafe
            {
                int* entities = useSet1 ? set1.GetDenseEntityPtr() : set2.GetDenseEntityPtr();
                int count = useSet1 ? set1.Count : set2.Count;

                // Hoisted out of the loop: these were re-fetched once per component per
                // entity, and each fetch is a safety-handle check in Editor/Development.
                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;

                for (int i = 0; i < count; i++)
                {
                    int entityIndex = entities[i];

                    int idx1 = set1.GetDenseIndex(entityIndex);
                    int idx2 = set2.GetDenseIndex(entityIndex);

                    if (idx1 < 0 || idx2 < 0)
                        continue;

                    action(entityIndex, ref data1[idx1], ref data2[idx2]);

                    QueryGuard.Check(guard1, set1.StructuralVersion);
                    QueryGuard.Check(guard2, set2.StructuralVersion);
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
                int minCount;
                int* entities;

                if (count1 <= count2 && count1 <= count3)
                {
                    entities = set1.GetDenseEntityPtr();
                    minCount = count1;
                }
                else if (count2 <= count3)
                {
                    entities = set2.GetDenseEntityPtr();
                    minCount = count2;
                }
                else
                {
                    entities = set3.GetDenseEntityPtr();
                    minCount = count3;
                }

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;
                int guard3 = set3.StructuralVersion;

                for (int i = 0; i < minCount; i++)
                {
                    int entityIndex = entities[i];

                    int idx1 = set1.GetDenseIndex(entityIndex);
                    int idx2 = set2.GetDenseIndex(entityIndex);
                    int idx3 = set3.GetDenseIndex(entityIndex);

                    if (idx1 < 0 || idx2 < 0 || idx3 < 0)
                        continue;

                    action(entityIndex, ref data1[idx1], ref data2[idx2], ref data3[idx3]);

                    QueryGuard.Check(guard1, set1.StructuralVersion);
                    QueryGuard.Check(guard2, set2.StructuralVersion);
                    QueryGuard.Check(guard3, set3.StructuralVersion);
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
