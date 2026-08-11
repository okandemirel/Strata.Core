using System.Diagnostics;
using NUnit.Framework;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Jobs;
using Strada.Core.ECS.Query;
using Unity.Burst;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [BurstCompile]
    public struct MoveJob : IJobComponent<Position, Velocity>
    {
        public float DeltaTime;

        [BurstCompile]
        public void Execute(int entity, ref Position t, ref Velocity v)
        {
            t.X += v.X * DeltaTime;
            t.Y += v.Y * DeltaTime;
            t.Z += v.Z * DeltaTime;
        }
    }

    [BurstCompile]
    public struct DamageJob : IJobComponent<Health>
    {
        public int Damage;

        [BurstCompile]
        public void Execute(int entity, ref Health h)
        {
            h.Current -= Damage;
            if (h.Current < 0) h.Current = 0;
        }
    }

    [BurstCompile]
    public struct ComplexJob : IJobComponent<Position, Velocity, Health>
    {
        public float DeltaTime;

        [BurstCompile]
        public void Execute(int entity, ref Position t, ref Velocity v, ref Health h)
        {
            t.X += v.X * DeltaTime;
            t.Y += v.Y * DeltaTime;
            t.Z += v.Z * DeltaTime;

            if (h.Current > 0)
                h.Current -= 1;
        }
    }

    [TestFixture]
    [Category("Performance")]
    public class ParallelJobPerformanceTests
    {
        private EntityManager _entityManager;

        [SetUp]
        public void Setup()
        {
            _entityManager = new EntityManager();
        }

        [TearDown]
        public void TearDown()
        {
            _entityManager?.Dispose();
        }

        [Test]
        public void Benchmark_ParallelJob_100k_TwoComponents()
        {
            const int Count = 100000;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i, Y = i, Z = i });
                _entityManager.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
            }

            var job = new MoveJob { DeltaTime = 0.016f };

            var warmup = _entityManager.ScheduleParallel<MoveJob, Position, Velocity>(job);
            warmup.Complete();

            var sw = Stopwatch.StartNew();
            for (int frame = 0; frame < 10; frame++)
            {
                var handle = _entityManager.ScheduleParallel<MoveJob, Position, Velocity>(job);
                handle.Complete();
            }
            sw.Stop();

            UnityEngine.Debug.Log($"[STRADA PARALLEL] 10 frames of {Count} entities (2 components):");
            UnityEngine.Debug.Log($"  Total: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Per frame: {sw.ElapsedMilliseconds / 10.0:F2}ms");

            Assert.Less(sw.ElapsedMilliseconds, 100, "Parallel job too slow");
        }

        [Test]
        public void Benchmark_ParallelJob_100k_ThreeComponents()
        {
            const int Count = 100000;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i, Y = i, Z = i });
                _entityManager.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
                _entityManager.AddComponent(entity, new Health { Current = 100, Max = 100 });
            }

            var job = new ComplexJob { DeltaTime = 0.016f };

            var warmup = _entityManager.ScheduleParallel<ComplexJob, Position, Velocity, Health>(job);
            warmup.Complete();

            var sw = Stopwatch.StartNew();
            for (int frame = 0; frame < 10; frame++)
            {
                var handle = _entityManager.ScheduleParallel<ComplexJob, Position, Velocity, Health>(job);
                handle.Complete();
            }
            sw.Stop();

            UnityEngine.Debug.Log($"[STRADA PARALLEL] 10 frames of {Count} entities (3 components):");
            UnityEngine.Debug.Log($"  Total: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Per frame: {sw.ElapsedMilliseconds / 10.0:F2}ms");

            Assert.Less(sw.ElapsedMilliseconds, 150, "Complex parallel job too slow");
        }

        [Test]
        public void Benchmark_ParallelVsSequential_100k()
        {
            const int Count = 100000;
            const int Frames = 10;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i, Y = i, Z = i });
                _entityManager.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
            }

            var job = new MoveJob { DeltaTime = 0.016f };

            // Warm up all three legs before any Stopwatch starts. The managed leg used to run
            // cold while the job leg was warmed explicitly, so first-call JIT of the closed
            // generic ForEach<Position, Velocity> and of its delegate was charged to the
            // comparison's denominator only — bias entirely in the direction of a bigger speedup.
            _entityManager.ForEach<Position, Velocity>((int e, ref Position t, ref Velocity v) =>
            {
                t.X += v.X * 0.016f;
                t.Y += v.Y * 0.016f;
                t.Z += v.Z * 0.016f;
            });
            _entityManager.ScheduleParallel<MoveJob, Position, Velocity>(job, Count).Complete();
            _entityManager.ScheduleParallel<MoveJob, Position, Velocity>(job).Complete();

            var swManaged = Stopwatch.StartNew();
            for (int frame = 0; frame < Frames; frame++)
            {
                _entityManager.ForEach<Position, Velocity>((int e, ref Position t, ref Velocity v) =>
                {
                    t.X += v.X * 0.016f;
                    t.Y += v.Y * 0.016f;
                    t.Z += v.Z * 0.016f;
                });
            }
            swManaged.Stop();

            // A batch size equal to the entity count gives IJobParallelFor exactly one batch, so
            // this leg is the same Burst-compiled job on a single worker. Without it the reported
            // ratio folds Burst codegen and the elimination of one managed delegate call per
            // entity into a number labelled "parallel speedup".
            var swSingleThreaded = Stopwatch.StartNew();
            for (int frame = 0; frame < Frames; frame++)
            {
                _entityManager.ScheduleParallel<MoveJob, Position, Velocity>(job, Count).Complete();
            }
            swSingleThreaded.Stop();

            var swParallel = Stopwatch.StartNew();
            for (int frame = 0; frame < Frames; frame++)
            {
                _entityManager.RunParallel<MoveJob, Position, Velocity>(job);
            }
            swParallel.Stop();

            // TotalMilliseconds, not ElapsedMilliseconds: the latter is a whole-millisecond
            // integer, so a sub-millisecond parallel run made the denominator 0 and the ratio
            // float.PositiveInfinity — which sails past Assert.Greater and gets published.
            double managedMs = swManaged.Elapsed.TotalMilliseconds;
            double singleThreadedMs = swSingleThreaded.Elapsed.TotalMilliseconds;
            double parallelMs = swParallel.Elapsed.TotalMilliseconds;

            Assert.Greater(singleThreadedMs, 0.0, "Single-threaded leg produced no measurable time");
            Assert.Greater(parallelMs, 0.0, "Parallel leg produced no measurable time");

            double burstSpeedup = managedMs / singleThreadedMs;
            double parallelSpeedup = singleThreadedMs / parallelMs;
            double totalSpeedup = managedMs / parallelMs;

            UnityEngine.Debug.Log($"[STRADA BENCHMARK] Managed vs Burst vs Parallel ({Count} entities, {Frames} frames):");
            UnityEngine.Debug.Log($"  Managed ForEach:      {managedMs:F2}ms");
            UnityEngine.Debug.Log($"  Burst, 1 worker:      {singleThreadedMs:F2}ms");
            UnityEngine.Debug.Log($"  Burst, all workers:   {parallelMs:F2}ms");
            UnityEngine.Debug.Log($"  Burst vs managed:     {burstSpeedup:F2}x");
            UnityEngine.Debug.Log($"  Parallel vs 1 worker: {parallelSpeedup:F2}x  <- the actual parallelism figure");
            UnityEngine.Debug.Log($"  Combined:             {totalSpeedup:F2}x");

            Assert.Greater(totalSpeedup, 1.0, "Parallel Burst should beat the managed delegate loop");
        }

        [Test]
        public void Benchmark_SingleComponent_100k()
        {
            const int Count = 100000;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Health { Current = 100, Max = 100 });
            }

            var job = new DamageJob { Damage = 1 };

            var warmup = _entityManager.ScheduleParallel<DamageJob, Health>(job);
            warmup.Complete();

            var sw = Stopwatch.StartNew();
            for (int frame = 0; frame < 100; frame++)
            {
                _entityManager.RunParallel<DamageJob, Health>(job);
            }
            sw.Stop();

            UnityEngine.Debug.Log($"[STRADA PARALLEL] 100 frames of {Count} entities (1 component):");
            UnityEngine.Debug.Log($"  Total: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Per frame: {sw.ElapsedMilliseconds / 100.0:F2}ms");

            Assert.Less(sw.ElapsedMilliseconds, 200, "Single component parallel job too slow");
        }
    }
}
