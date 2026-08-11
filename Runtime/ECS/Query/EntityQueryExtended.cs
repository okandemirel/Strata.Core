using System;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Storage;

namespace Strada.Core.ECS.Query
{
    public delegate void QueryDelegate<T1, T2, T3, T4>(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;

    public delegate void QueryDelegate<T1, T2, T3, T4, T5>(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged;

    public delegate void QueryDelegate<T1, T2, T3, T4, T5, T6>(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged;

    public delegate void QueryDelegate<T1, T2, T3, T4, T5, T6, T7>(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged;

    public delegate void QueryDelegate<T1, T2, T3, T4, T5, T6, T7, T8>(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, ref T8 c8)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged;

    public readonly struct EntityQuery<T1, T2, T3, T4> : IDisposable
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private readonly ComponentStorage<T3> _storage3;
        private readonly ComponentStorage<T4> _storage4;

        internal EntityQuery(ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3, ComponentStorage<T4> s4)
        {
            _storage1 = s1; _storage2 = s2; _storage3 = s3; _storage4 = s4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2, T3, T4> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();
            ref var set4 = ref _storage4.GetSparseSet();

            int c1 = set1.Count, c2 = set2.Count, c3 = set3.Count, c4 = set4.Count;

            unsafe
            {
                int minCount; int* entities;
                if (c1 <= c2 && c1 <= c3 && c1 <= c4) { entities = set1.GetDenseEntityPtr(); minCount = c1; }
                else if (c2 <= c3 && c2 <= c4) { entities = set2.GetDenseEntityPtr(); minCount = c2; }
                else if (c3 <= c4) { entities = set3.GetDenseEntityPtr(); minCount = c3; }
                else { entities = set4.GetDenseEntityPtr(); minCount = c4; }

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                T4* data4 = set4.GetDataPtr();
                int g1 = set1.StructuralVersion;
                int g2 = set2.StructuralVersion;
                int g3 = set3.StructuralVersion;
                int g4 = set4.StructuralVersion;

                for (int i = 0; i < minCount; i++)
                {
                    int e = entities[i];
                    int i1 = set1.GetDenseIndex(e), i2 = set2.GetDenseIndex(e), i3 = set3.GetDenseIndex(e), i4 = set4.GetDenseIndex(e);
                    if (i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0) continue;
                    action(e, ref data1[i1], ref data2[i2], ref data3[i3], ref data4[i4]);
                    QueryGuard.Check(g1, set1.StructuralVersion);
                    QueryGuard.Check(g2, set2.StructuralVersion);
                    QueryGuard.Check(g3, set3.StructuralVersion);
                    QueryGuard.Check(g4, set4.StructuralVersion);
                }
            }
        }

        public void Dispose() { }
    }

    public readonly struct EntityQuery<T1, T2, T3, T4, T5> : IDisposable
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private readonly ComponentStorage<T3> _storage3;
        private readonly ComponentStorage<T4> _storage4;
        private readonly ComponentStorage<T5> _storage5;

        internal EntityQuery(ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3, ComponentStorage<T4> s4, ComponentStorage<T5> s5)
        {
            _storage1 = s1; _storage2 = s2; _storage3 = s3; _storage4 = s4; _storage5 = s5;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2, T3, T4, T5> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();
            ref var set4 = ref _storage4.GetSparseSet();
            ref var set5 = ref _storage5.GetSparseSet();

            int c1 = set1.Count, c2 = set2.Count, c3 = set3.Count, c4 = set4.Count, c5 = set5.Count;

            unsafe
            {
                int min = c1, idx = 0;
                if (c2 < min) { min = c2; idx = 1; }
                if (c3 < min) { min = c3; idx = 2; }
                if (c4 < min) { min = c4; idx = 3; }
                if (c5 < min) { min = c5; idx = 4; }

                int* entities = idx switch { 0 => set1.GetDenseEntityPtr(), 1 => set2.GetDenseEntityPtr(), 2 => set3.GetDenseEntityPtr(), 3 => set4.GetDenseEntityPtr(), _ => set5.GetDenseEntityPtr() };

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                T4* data4 = set4.GetDataPtr();
                T5* data5 = set5.GetDataPtr();
                int g1 = set1.StructuralVersion;
                int g2 = set2.StructuralVersion;
                int g3 = set3.StructuralVersion;
                int g4 = set4.StructuralVersion;
                int g5 = set5.StructuralVersion;

                for (int i = 0; i < min; i++)
                {
                    int e = entities[i];
                    int i1 = set1.GetDenseIndex(e), i2 = set2.GetDenseIndex(e), i3 = set3.GetDenseIndex(e), i4 = set4.GetDenseIndex(e), i5 = set5.GetDenseIndex(e);
                    if (i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0) continue;
                    action(e, ref data1[i1], ref data2[i2], ref data3[i3], ref data4[i4], ref data5[i5]);
                    QueryGuard.Check(g1, set1.StructuralVersion);
                    QueryGuard.Check(g2, set2.StructuralVersion);
                    QueryGuard.Check(g3, set3.StructuralVersion);
                    QueryGuard.Check(g4, set4.StructuralVersion);
                    QueryGuard.Check(g5, set5.StructuralVersion);
                }
            }
        }

        public void Dispose() { }
    }

    public readonly struct EntityQuery<T1, T2, T3, T4, T5, T6> : IDisposable
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private readonly ComponentStorage<T3> _storage3;
        private readonly ComponentStorage<T4> _storage4;
        private readonly ComponentStorage<T5> _storage5;
        private readonly ComponentStorage<T6> _storage6;

        internal EntityQuery(ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3,
            ComponentStorage<T4> s4, ComponentStorage<T5> s5, ComponentStorage<T6> s6)
        {
            _storage1 = s1; _storage2 = s2; _storage3 = s3; _storage4 = s4; _storage5 = s5; _storage6 = s6;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2, T3, T4, T5, T6> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();
            ref var set4 = ref _storage4.GetSparseSet();
            ref var set5 = ref _storage5.GetSparseSet();
            ref var set6 = ref _storage6.GetSparseSet();

            int c1 = set1.Count, c2 = set2.Count, c3 = set3.Count, c4 = set4.Count, c5 = set5.Count, c6 = set6.Count;

            unsafe
            {
                int min = c1, idx = 0;
                if (c2 < min) { min = c2; idx = 1; }
                if (c3 < min) { min = c3; idx = 2; }
                if (c4 < min) { min = c4; idx = 3; }
                if (c5 < min) { min = c5; idx = 4; }
                if (c6 < min) { min = c6; idx = 5; }

                int* entities = idx switch { 0 => set1.GetDenseEntityPtr(), 1 => set2.GetDenseEntityPtr(), 2 => set3.GetDenseEntityPtr(), 3 => set4.GetDenseEntityPtr(), 4 => set5.GetDenseEntityPtr(), _ => set6.GetDenseEntityPtr() };

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                T4* data4 = set4.GetDataPtr();
                T5* data5 = set5.GetDataPtr();
                T6* data6 = set6.GetDataPtr();
                int g1 = set1.StructuralVersion;
                int g2 = set2.StructuralVersion;
                int g3 = set3.StructuralVersion;
                int g4 = set4.StructuralVersion;
                int g5 = set5.StructuralVersion;
                int g6 = set6.StructuralVersion;

                for (int i = 0; i < min; i++)
                {
                    int e = entities[i];
                    int i1 = set1.GetDenseIndex(e), i2 = set2.GetDenseIndex(e), i3 = set3.GetDenseIndex(e), i4 = set4.GetDenseIndex(e), i5 = set5.GetDenseIndex(e), i6 = set6.GetDenseIndex(e);
                    if (i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0 || i6 < 0) continue;
                    action(e, ref data1[i1], ref data2[i2], ref data3[i3], ref data4[i4], ref data5[i5], ref data6[i6]);
                    QueryGuard.Check(g1, set1.StructuralVersion);
                    QueryGuard.Check(g2, set2.StructuralVersion);
                    QueryGuard.Check(g3, set3.StructuralVersion);
                    QueryGuard.Check(g4, set4.StructuralVersion);
                    QueryGuard.Check(g5, set5.StructuralVersion);
                    QueryGuard.Check(g6, set6.StructuralVersion);
                }
            }
        }

        public void Dispose() { }
    }

    public readonly struct EntityQuery<T1, T2, T3, T4, T5, T6, T7> : IDisposable
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
        where T7 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private readonly ComponentStorage<T3> _storage3;
        private readonly ComponentStorage<T4> _storage4;
        private readonly ComponentStorage<T5> _storage5;
        private readonly ComponentStorage<T6> _storage6;
        private readonly ComponentStorage<T7> _storage7;

        internal EntityQuery(ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3,
            ComponentStorage<T4> s4, ComponentStorage<T5> s5, ComponentStorage<T6> s6, ComponentStorage<T7> s7)
        {
            _storage1 = s1; _storage2 = s2; _storage3 = s3; _storage4 = s4; _storage5 = s5; _storage6 = s6; _storage7 = s7;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2, T3, T4, T5, T6, T7> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();
            ref var set4 = ref _storage4.GetSparseSet();
            ref var set5 = ref _storage5.GetSparseSet();
            ref var set6 = ref _storage6.GetSparseSet();
            ref var set7 = ref _storage7.GetSparseSet();

            int c1 = set1.Count, c2 = set2.Count, c3 = set3.Count, c4 = set4.Count, c5 = set5.Count, c6 = set6.Count, c7 = set7.Count;

            unsafe
            {
                int min = c1, idx = 0;
                if (c2 < min) { min = c2; idx = 1; }
                if (c3 < min) { min = c3; idx = 2; }
                if (c4 < min) { min = c4; idx = 3; }
                if (c5 < min) { min = c5; idx = 4; }
                if (c6 < min) { min = c6; idx = 5; }
                if (c7 < min) { min = c7; idx = 6; }

                int* entities = idx switch { 0 => set1.GetDenseEntityPtr(), 1 => set2.GetDenseEntityPtr(), 2 => set3.GetDenseEntityPtr(), 3 => set4.GetDenseEntityPtr(), 4 => set5.GetDenseEntityPtr(), 5 => set6.GetDenseEntityPtr(), _ => set7.GetDenseEntityPtr() };

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                T4* data4 = set4.GetDataPtr();
                T5* data5 = set5.GetDataPtr();
                T6* data6 = set6.GetDataPtr();
                T7* data7 = set7.GetDataPtr();
                int g1 = set1.StructuralVersion;
                int g2 = set2.StructuralVersion;
                int g3 = set3.StructuralVersion;
                int g4 = set4.StructuralVersion;
                int g5 = set5.StructuralVersion;
                int g6 = set6.StructuralVersion;
                int g7 = set7.StructuralVersion;

                for (int i = 0; i < min; i++)
                {
                    int e = entities[i];
                    int i1 = set1.GetDenseIndex(e), i2 = set2.GetDenseIndex(e), i3 = set3.GetDenseIndex(e), i4 = set4.GetDenseIndex(e), i5 = set5.GetDenseIndex(e), i6 = set6.GetDenseIndex(e), i7 = set7.GetDenseIndex(e);
                    if (i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0 || i6 < 0 || i7 < 0) continue;
                    action(e, ref data1[i1], ref data2[i2], ref data3[i3], ref data4[i4], ref data5[i5], ref data6[i6], ref data7[i7]);
                    QueryGuard.Check(g1, set1.StructuralVersion);
                    QueryGuard.Check(g2, set2.StructuralVersion);
                    QueryGuard.Check(g3, set3.StructuralVersion);
                    QueryGuard.Check(g4, set4.StructuralVersion);
                    QueryGuard.Check(g5, set5.StructuralVersion);
                    QueryGuard.Check(g6, set6.StructuralVersion);
                    QueryGuard.Check(g7, set7.StructuralVersion);
                }
            }
        }

        public void Dispose() { }
    }

    public readonly struct EntityQuery<T1, T2, T3, T4, T5, T6, T7, T8> : IDisposable
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
        where T7 : unmanaged, IComponent where T8 : unmanaged, IComponent
    {
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private readonly ComponentStorage<T3> _storage3;
        private readonly ComponentStorage<T4> _storage4;
        private readonly ComponentStorage<T5> _storage5;
        private readonly ComponentStorage<T6> _storage6;
        private readonly ComponentStorage<T7> _storage7;
        private readonly ComponentStorage<T8> _storage8;

        internal EntityQuery(ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3,
            ComponentStorage<T4> s4, ComponentStorage<T5> s5, ComponentStorage<T6> s6, ComponentStorage<T7> s7, ComponentStorage<T8> s8)
        {
            _storage1 = s1; _storage2 = s2; _storage3 = s3; _storage4 = s4; _storage5 = s5; _storage6 = s6; _storage7 = s7; _storage8 = s8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2, T3, T4, T5, T6, T7, T8> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();
            ref var set4 = ref _storage4.GetSparseSet();
            ref var set5 = ref _storage5.GetSparseSet();
            ref var set6 = ref _storage6.GetSparseSet();
            ref var set7 = ref _storage7.GetSparseSet();
            ref var set8 = ref _storage8.GetSparseSet();

            int c1 = set1.Count, c2 = set2.Count, c3 = set3.Count, c4 = set4.Count, c5 = set5.Count, c6 = set6.Count, c7 = set7.Count, c8 = set8.Count;

            unsafe
            {
                int min = c1, idx = 0;
                if (c2 < min) { min = c2; idx = 1; }
                if (c3 < min) { min = c3; idx = 2; }
                if (c4 < min) { min = c4; idx = 3; }
                if (c5 < min) { min = c5; idx = 4; }
                if (c6 < min) { min = c6; idx = 5; }
                if (c7 < min) { min = c7; idx = 6; }
                if (c8 < min) { min = c8; idx = 7; }

                int* entities = idx switch { 0 => set1.GetDenseEntityPtr(), 1 => set2.GetDenseEntityPtr(), 2 => set3.GetDenseEntityPtr(), 3 => set4.GetDenseEntityPtr(), 4 => set5.GetDenseEntityPtr(), 5 => set6.GetDenseEntityPtr(), 6 => set7.GetDenseEntityPtr(), _ => set8.GetDenseEntityPtr() };

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                T4* data4 = set4.GetDataPtr();
                T5* data5 = set5.GetDataPtr();
                T6* data6 = set6.GetDataPtr();
                T7* data7 = set7.GetDataPtr();
                T8* data8 = set8.GetDataPtr();
                int g1 = set1.StructuralVersion;
                int g2 = set2.StructuralVersion;
                int g3 = set3.StructuralVersion;
                int g4 = set4.StructuralVersion;
                int g5 = set5.StructuralVersion;
                int g6 = set6.StructuralVersion;
                int g7 = set7.StructuralVersion;
                int g8 = set8.StructuralVersion;

                for (int i = 0; i < min; i++)
                {
                    int e = entities[i];
                    int i1 = set1.GetDenseIndex(e), i2 = set2.GetDenseIndex(e), i3 = set3.GetDenseIndex(e), i4 = set4.GetDenseIndex(e);
                    int i5 = set5.GetDenseIndex(e), i6 = set6.GetDenseIndex(e), i7 = set7.GetDenseIndex(e), i8 = set8.GetDenseIndex(e);
                    if (i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0 || i6 < 0 || i7 < 0 || i8 < 0) continue;
                    action(e, ref data1[i1], ref data2[i2], ref data3[i3], ref data4[i4],
                           ref data5[i5], ref data6[i6], ref data7[i7], ref data8[i8]);
                    QueryGuard.Check(g1, set1.StructuralVersion);
                    QueryGuard.Check(g2, set2.StructuralVersion);
                    QueryGuard.Check(g3, set3.StructuralVersion);
                    QueryGuard.Check(g4, set4.StructuralVersion);
                    QueryGuard.Check(g5, set5.StructuralVersion);
                    QueryGuard.Check(g6, set6.StructuralVersion);
                    QueryGuard.Check(g7, set7.StructuralVersion);
                    QueryGuard.Check(g8, set8.StructuralVersion);
                }
            }
        }

        public void Dispose() { }
    }
}
