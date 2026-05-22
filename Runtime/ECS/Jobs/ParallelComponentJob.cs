using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Strada.Core.ECS.Jobs
{
    // FRAMEWORK DESIGN: The struct families below carry [NativeDisableUnsafePtrRestriction]
    // on every pointer field. This is a Unity Burst requirement — parallel jobs must hold
    // raw pointers to component storage so Burst can vectorise the inner loop. The
    // attribute disables the Job Safety System's pointer-aliasing check, but Burst's own
    // bounds and aliasing analysis still runs at compile time. Removing the attribute
    // would prevent the jobs from compiling under Burst.
    [BurstCompile]
    public unsafe struct ComponentJobParallel<TJob, T1> : IJobParallelFor
        where TJob : struct, IJobComponent<T1>
        where T1 : unmanaged, IComponent
    {
        public TJob UserJob;
        [NativeDisableUnsafePtrRestriction] public int* EntityIds;
        [NativeDisableUnsafePtrRestriction] public T1* Components1;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex1;
        public int MaxSparse1;

        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(int i)
        {
            int entity = EntityIds[i];
            int idx = entity < MaxSparse1 ? SparseIndex1[entity] : -1;
            if (idx < 0) return;
            UserJob.Execute(entity, ref Components1[idx]);
        }
    }

    [BurstCompile]
    public unsafe struct ComponentJobParallel<TJob, T1, T2> : IJobParallelFor
        where TJob : struct, IJobComponent<T1, T2>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        public TJob UserJob;
        [NativeDisableUnsafePtrRestriction] public int* EntityIds;
        [NativeDisableUnsafePtrRestriction] public T1* Components1;
        [NativeDisableUnsafePtrRestriction] public T2* Components2;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex1;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex2;
        public int MaxSparse1;
        public int MaxSparse2;

        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(int i)
        {
            int entity = EntityIds[i];
            int idx1 = entity < MaxSparse1 ? SparseIndex1[entity] : -1;
            int idx2 = entity < MaxSparse2 ? SparseIndex2[entity] : -1;
            if (idx1 < 0 || idx2 < 0) return;
            UserJob.Execute(entity, ref Components1[idx1], ref Components2[idx2]);
        }
    }

    [BurstCompile]
    public unsafe struct ComponentJobParallel<TJob, T1, T2, T3> : IJobParallelFor
        where TJob : struct, IJobComponent<T1, T2, T3>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        public TJob UserJob;
        [NativeDisableUnsafePtrRestriction] public int* EntityIds;
        [NativeDisableUnsafePtrRestriction] public T1* Components1;
        [NativeDisableUnsafePtrRestriction] public T2* Components2;
        [NativeDisableUnsafePtrRestriction] public T3* Components3;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex1;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex2;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex3;
        public int MaxSparse1;
        public int MaxSparse2;
        public int MaxSparse3;

        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(int i)
        {
            int entity = EntityIds[i];
            int idx1 = entity < MaxSparse1 ? SparseIndex1[entity] : -1;
            int idx2 = entity < MaxSparse2 ? SparseIndex2[entity] : -1;
            int idx3 = entity < MaxSparse3 ? SparseIndex3[entity] : -1;
            if (idx1 < 0 || idx2 < 0 || idx3 < 0) return;
            UserJob.Execute(entity, ref Components1[idx1], ref Components2[idx2], ref Components3[idx3]);
        }
    }

    [BurstCompile]
    public unsafe struct ComponentJobParallel<TJob, T1, T2, T3, T4> : IJobParallelFor
        where TJob : struct, IJobComponent<T1, T2, T3, T4>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        public TJob UserJob;
        [NativeDisableUnsafePtrRestriction] public int* EntityIds;
        [NativeDisableUnsafePtrRestriction] public T1* Components1;
        [NativeDisableUnsafePtrRestriction] public T2* Components2;
        [NativeDisableUnsafePtrRestriction] public T3* Components3;
        [NativeDisableUnsafePtrRestriction] public T4* Components4;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex1;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex2;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex3;
        [NativeDisableUnsafePtrRestriction] public int* SparseIndex4;
        public int MaxSparse1;
        public int MaxSparse2;
        public int MaxSparse3;
        public int MaxSparse4;

        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(int i)
        {
            int entity = EntityIds[i];
            int idx1 = entity < MaxSparse1 ? SparseIndex1[entity] : -1;
            int idx2 = entity < MaxSparse2 ? SparseIndex2[entity] : -1;
            int idx3 = entity < MaxSparse3 ? SparseIndex3[entity] : -1;
            int idx4 = entity < MaxSparse4 ? SparseIndex4[entity] : -1;
            if (idx1 < 0 || idx2 < 0 || idx3 < 0 || idx4 < 0) return;
            UserJob.Execute(entity, ref Components1[idx1], ref Components2[idx2], ref Components3[idx3], ref Components4[idx4]);
        }
    }
}
