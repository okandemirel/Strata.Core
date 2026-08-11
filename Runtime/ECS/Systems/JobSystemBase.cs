using System.Runtime.CompilerServices;
using Strada.Core.ECS.Jobs;
using Strada.Core.ECS.Storage;
using Unity.Collections;
using Unity.Jobs;

namespace Strada.Core.ECS.Systems
{
    public abstract class JobSystemBase : SystemBase
    {
        private JobHandle _lastJobHandle;
        private EntityCommandBuffer _commandBuffer;
        private bool _commandBufferCreated;

        protected JobHandle Dependency
        {
            get => _lastJobHandle;
            set => _lastJobHandle = value;
        }

        /// <summary>
        /// The system's deferred command buffer.
        /// </summary>
        /// <remarks>
        /// Returned BY REFERENCE. EntityCommandBuffer is a mutable struct: returning it by
        /// value handed every caller a copy, so `CommandBuffer.CreateEntity()` incremented the
        /// copy's counter and always returned 0, and the recorded commands were discarded with
        /// the copy — every deferred command then threw at playback.
        /// </remarks>
        protected ref EntityCommandBuffer CommandBuffer
        {
            get
            {
                if (!_commandBufferCreated)
                {
                    // Persistent, not TempJob: this buffer lives for the system's lifetime, and
                    // TempJob allocations older than 4 frames trigger a leak warning every frame.
                    _commandBuffer = new EntityCommandBuffer(Allocator.Persistent);
                    _commandBufferCreated = true;
                }
                return ref _commandBuffer;
            }
        }

        protected override void OnInitialize() => OnCreate();

        protected override void OnDispose()
        {
            // In-flight jobs hold raw pointers into the component storages that World.Dispose
            // is about to free. Completing here closes that use-after-free window.
            _lastJobHandle.Complete();
            _lastJobHandle = default;

            if (_commandBufferCreated)
            {
                _commandBuffer.Dispose();
                _commandBufferCreated = false;
            }

            OnDestroy();
        }

        protected virtual void OnCreate() { }
        protected virtual void OnDestroy() { }

        protected sealed override void OnUpdate(float deltaTime)
        {
            _lastJobHandle.Complete();

            if (_commandBufferCreated)
            {
                // Clear in a finally: Playback throws on a bad deferred index, a stream
                // overflow, a component size mismatch, or — most easily — a SetComponent whose
                // target lost that component between recording and playback. Skipping the Clear
                // would leave the whole stream buffered, so every subsequent frame would replay
                // the failing command again and append that frame's recording on top of it.
                try
                {
                    _commandBuffer.Playback(EntityManager);
                }
                finally
                {
                    _commandBuffer.Clear();
                }
            }

            _lastJobHandle = OnSchedule(deltaTime, _lastJobHandle);
        }

        protected abstract JobHandle OnSchedule(float deltaTime, JobHandle dependency);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected JobHandle ScheduleParallel<TJob, T1>(
            TJob job,
            int batchSize = EntityJobs.DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1>
            where T1 : unmanaged, IComponent
        {
            return EntityManager.ScheduleParallel<TJob, T1>(job, batchSize, dependency);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected JobHandle ScheduleParallel<TJob, T1, T2>(
            TJob job,
            int batchSize = EntityJobs.DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1, T2>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
        {
            return EntityManager.ScheduleParallel<TJob, T1, T2>(job, batchSize, dependency);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected JobHandle ScheduleParallel<TJob, T1, T2, T3>(
            TJob job,
            int batchSize = EntityJobs.DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1, T2, T3>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
        {
            return EntityManager.ScheduleParallel<TJob, T1, T2, T3>(job, batchSize, dependency);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected JobHandle ScheduleParallel<TJob, T1, T2, T3, T4>(
            TJob job,
            int batchSize = EntityJobs.DefaultBatchSize,
            JobHandle dependency = default)
            where TJob : struct, IJobComponent<T1, T2, T3, T4>
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent
        {
            return EntityManager.ScheduleParallel<TJob, T1, T2, T3, T4>(job, batchSize, dependency);
        }

        public void CompleteAllJobs()
        {
            _lastJobHandle.Complete();
            _lastJobHandle = default;
        }

        public void FlushCommandBuffer()
        {
            if (_commandBufferCreated)
            {
                // See OnUpdate: a Playback that throws must still leave the stream empty.
                try
                {
                    _commandBuffer.Playback(EntityManager);
                }
                finally
                {
                    _commandBuffer.Clear();
                }
            }
        }
    }
}
