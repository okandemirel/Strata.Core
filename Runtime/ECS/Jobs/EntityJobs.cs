using System.Runtime.CompilerServices;
using Strada.Core.ECS.Storage;
using Unity.Jobs;

namespace Strada.Core.ECS.Jobs
{
    /// <summary>
    /// Schedules <see cref="ComponentJobParallel{TJob, T1}"/> and its higher-arity siblings over
    /// component storages.
    /// </summary>
    /// <remarks>
    /// <para><b>AOT / IL2CPP: every concrete job must be registered.</b> The jobs scheduled here
    /// are generic, and their only construction site is inside these generic methods, which are in
    /// turn reached only through further generic methods
    /// (<c>EntityManagerJobExtensions.ScheduleParallel</c>, <c>JobSystemBase.ScheduleParallel</c>,
    /// <c>BurstSystem.OnSchedule</c>). Burst's AOT compiler discovers generic job instantiations by
    /// static analysis of concrete <c>new Job&lt;A, B&gt;()</c> expressions; an instantiation that
    /// exists only through a generic method's type parameters is invisible to it and is silently
    /// left uncompiled — the job still runs on a player build, just as managed IL2CPP code at a
    /// fraction of the speed the Editor measurement suggests.</para>
    /// <para>Declare each concrete combination once, anywhere in the assembly that defines the job:
    /// <code>
    /// [assembly: Unity.Jobs.RegisterGenericJobType(
    ///     typeof(Strada.Core.ECS.Jobs.ComponentJobParallel&lt;MoveJob, Position, Velocity&gt;))]
    /// </code>
    /// </para>
    /// </remarks>
    public static class EntityJobs
    {
        public const int DefaultBatchSize = 64;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static JobHandle Schedule<TJob, T1>(
            TJob job,
            ComponentStorage<T1> storage,
            int batchSize = DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1>
            where T1 : unmanaged, IComponent
        {
            ref var set = ref storage.GetSparseSet();
            if (set.Count == 0) return dependency;

            unsafe
            {
                var handle = new ComponentJobParallel<TJob, T1>
                {
                    UserJob = job,
                    EntityIds = set.GetDenseEntityPtr(),
                    Components1 = set.GetDataPtr(),
                    SparseIndex1 = set.GetSparsePtr(),
                    MaxSparse1 = set.SparseCapacity
                }.Schedule(set.Count, batchSize, dependency);
                // Structural changes to these storages must now wait for this job.
                storage.AddDependency(handle);
                return handle;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static JobHandle Schedule<TJob, T1, T2>(
            TJob job,
            ComponentStorage<T1> storage1,
            ComponentStorage<T2> storage2,
            int batchSize = DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1, T2>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
        {
            ref var set1 = ref storage1.GetSparseSet();
            ref var set2 = ref storage2.GetSparseSet();
            if (set1.Count == 0 || set2.Count == 0) return dependency;

            bool iterateFirst = set1.Count <= set2.Count;

            unsafe
            {
                var handle = new ComponentJobParallel<TJob, T1, T2>
                {
                    UserJob = job,
                    EntityIds = iterateFirst ? set1.GetDenseEntityPtr() : set2.GetDenseEntityPtr(),
                    Components1 = set1.GetDataPtr(),
                    Components2 = set2.GetDataPtr(),
                    SparseIndex1 = set1.GetSparsePtr(),
                    SparseIndex2 = set2.GetSparsePtr(),
                    MaxSparse1 = set1.SparseCapacity,
                    MaxSparse2 = set2.SparseCapacity
                }.Schedule(iterateFirst ? set1.Count : set2.Count, batchSize, dependency);
                // Structural changes to these storages must now wait for this job.
                storage1.AddDependency(handle);
                storage2.AddDependency(handle);
                return handle;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static JobHandle Schedule<TJob, T1, T2, T3>(
            TJob job,
            ComponentStorage<T1> storage1,
            ComponentStorage<T2> storage2,
            ComponentStorage<T3> storage3,
            int batchSize = DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1, T2, T3>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
        {
            ref var set1 = ref storage1.GetSparseSet();
            ref var set2 = ref storage2.GetSparseSet();
            ref var set3 = ref storage3.GetSparseSet();
            if (set1.Count == 0 || set2.Count == 0 || set3.Count == 0) return dependency;

            int minCount = set1.Count;
            unsafe
            {
                int* entityIds = set1.GetDenseEntityPtr();
                if (set2.Count < minCount) { minCount = set2.Count; entityIds = set2.GetDenseEntityPtr(); }
                if (set3.Count < minCount) { minCount = set3.Count; entityIds = set3.GetDenseEntityPtr(); }

                var handle = new ComponentJobParallel<TJob, T1, T2, T3>
                {
                    UserJob = job,
                    EntityIds = entityIds,
                    Components1 = set1.GetDataPtr(),
                    Components2 = set2.GetDataPtr(),
                    Components3 = set3.GetDataPtr(),
                    SparseIndex1 = set1.GetSparsePtr(),
                    SparseIndex2 = set2.GetSparsePtr(),
                    SparseIndex3 = set3.GetSparsePtr(),
                    MaxSparse1 = set1.SparseCapacity,
                    MaxSparse2 = set2.SparseCapacity,
                    MaxSparse3 = set3.SparseCapacity
                }.Schedule(minCount, batchSize, dependency);
                // Structural changes to these storages must now wait for this job.
                storage1.AddDependency(handle);
                storage2.AddDependency(handle);
                storage3.AddDependency(handle);
                return handle;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static JobHandle Schedule<TJob, T1, T2, T3, T4>(
            TJob job,
            ComponentStorage<T1> storage1,
            ComponentStorage<T2> storage2,
            ComponentStorage<T3> storage3,
            ComponentStorage<T4> storage4,
            int batchSize = DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1, T2, T3, T4>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent
        {
            ref var set1 = ref storage1.GetSparseSet();
            ref var set2 = ref storage2.GetSparseSet();
            ref var set3 = ref storage3.GetSparseSet();
            ref var set4 = ref storage4.GetSparseSet();
            if (set1.Count == 0 || set2.Count == 0 || set3.Count == 0 || set4.Count == 0) return dependency;

            int minCount = set1.Count;
            unsafe
            {
                int* entityIds = set1.GetDenseEntityPtr();
                if (set2.Count < minCount) { minCount = set2.Count; entityIds = set2.GetDenseEntityPtr(); }
                if (set3.Count < minCount) { minCount = set3.Count; entityIds = set3.GetDenseEntityPtr(); }
                if (set4.Count < minCount) { minCount = set4.Count; entityIds = set4.GetDenseEntityPtr(); }

                var handle = new ComponentJobParallel<TJob, T1, T2, T3, T4>
                {
                    UserJob = job,
                    EntityIds = entityIds,
                    Components1 = set1.GetDataPtr(),
                    Components2 = set2.GetDataPtr(),
                    Components3 = set3.GetDataPtr(),
                    Components4 = set4.GetDataPtr(),
                    SparseIndex1 = set1.GetSparsePtr(),
                    SparseIndex2 = set2.GetSparsePtr(),
                    SparseIndex3 = set3.GetSparsePtr(),
                    SparseIndex4 = set4.GetSparsePtr(),
                    MaxSparse1 = set1.SparseCapacity,
                    MaxSparse2 = set2.SparseCapacity,
                    MaxSparse3 = set3.SparseCapacity,
                    MaxSparse4 = set4.SparseCapacity
                }.Schedule(minCount, batchSize, dependency);
                // Structural changes to these storages must now wait for this job.
                storage1.AddDependency(handle);
                storage2.AddDependency(handle);
                storage3.AddDependency(handle);
                storage4.AddDependency(handle);
                return handle;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Run<TJob, T1>(TJob job, ComponentStorage<T1> storage)
            where TJob : struct, IJobComponent<T1>
            where T1 : unmanaged, IComponent
            => Schedule(job, storage).Complete();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Run<TJob, T1, T2>(TJob job, ComponentStorage<T1> s1, ComponentStorage<T2> s2)
            where TJob : struct, IJobComponent<T1, T2>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            => Schedule(job, s1, s2).Complete();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Run<TJob, T1, T2, T3>(TJob job, ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3)
            where TJob : struct, IJobComponent<T1, T2, T3>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            => Schedule(job, s1, s2, s3).Complete();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Run<TJob, T1, T2, T3, T4>(TJob job, ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3, ComponentStorage<T4> s4)
            where TJob : struct, IJobComponent<T1, T2, T3, T4>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent
            => Schedule(job, s1, s2, s3, s4).Complete();
    }
}
