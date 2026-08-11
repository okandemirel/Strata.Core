using System;
using System.Diagnostics;
using NUnit.Framework;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Query;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    public struct Position : IComponent
    {
        public float X, Y, Z;
    }

    public struct Rotation : IComponent
    {
        public float X, Y, Z, W;
    }

    public struct Scale : IComponent
    {
        public float X, Y, Z;
    }

    public struct Velocity : IComponent
    {
        public float X, Y, Z;
    }

    public struct Health : IComponent
    {
        public int Current;
        public int Max;
    }

    public struct Damage : IComponent
    {
        public int Amount;
    }

    public struct Tag : IComponent
    {
        public int Id;
    }

    [TestFixture]
    [Category("Performance")]
    public class ECSPerformanceTests
    {
        private EntityManager _entityManager;
        private const int WarmupIterations = 100;

        // Sampling for the repeatable measurements below. A single Stopwatch reading is one draw
        // from a distribution whose tail is set by GC pauses, OS scheduling and whatever else the
        // Editor is doing that millisecond; every published ECS number used to come from n=1.
        private const int SampleCount = 11;
        private const int DiscardedSamples = 3;

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

        /// <summary>
        /// Runs <paramref name="body"/> repeatedly and returns the median elapsed milliseconds,
        /// discarding the first few passes.
        /// </summary>
        /// <remarks>
        /// The discarded passes absorb first-call JIT of the closed generic and its delegate plus
        /// cold caches; collecting immediately before each timed pass keeps a collection triggered
        /// by an earlier benchmark from landing inside this one's window. Only measurements whose
        /// body is idempotent can use this — creation and destruction benchmarks consume the
        /// state they measure and warm up explicitly instead.
        /// </remarks>
        private static double MedianMs(Action body, int samples = SampleCount, int discard = DiscardedSamples)
        {
            var timings = new double[samples];

            for (int i = 0; i < samples + discard; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();

                var sw = Stopwatch.StartNew();
                body();
                sw.Stop();

                if (i >= discard)
                    timings[i - discard] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(timings);
            return samples % 2 == 1
                ? timings[samples / 2]
                : (timings[samples / 2 - 1] + timings[samples / 2]) * 0.5;
        }

        [Test]
        public void Benchmark_EntityCreation_Simple_100k()
        {
            const int Count = 100_000;

            for (int i = 0; i < WarmupIterations; i++)
            {
                var e = _entityManager.CreateEntity();
                _entityManager.DestroyEntity(e);
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++)
            {
                _entityManager.CreateEntity();
            }
            sw.Stop();

            double usPerOp = (sw.Elapsed.TotalMilliseconds * 1000) / Count;
            double nsPerOp = usPerOp * 1000;

            UnityEngine.Debug.Log($"=== STRADA ECS: Entity Creation ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-entity: {nsPerOp:F1}ns ({usPerOp:F3}μs)");

            Assert.AreEqual(Count, _entityManager.EntityCount);
            Assert.Less(usPerOp, 1.0, "Entity creation should be under 1μs");
        }

        [Test]
        public void Benchmark_EntityCreation_WithComponent_100k()
        {
            const int Count = 100_000;

            // Without this the first-call JIT of the closed generic AddComponent<Position> and
            // its storage lookup land inside the timed window.
            for (int i = 0; i < WarmupIterations; i++)
            {
                var w = _entityManager.CreateEntity();
                _entityManager.AddComponent(w, new Position { X = i });
                _entityManager.DestroyEntity(w);
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i, Y = i, Z = i });
            }
            sw.Stop();

            double usPerOp = (sw.Elapsed.TotalMilliseconds * 1000) / Count;

            UnityEngine.Debug.Log($"=== STRADA ECS: Entity + 1 Component ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-entity: {usPerOp:F3}μs");

            Assert.Less(usPerOp, 2.0, "Entity + component should be under 2μs");
        }

        [Test]
        public void Benchmark_EntityCreation_With3Components_100k()
        {
            const int Count = 100_000;

            for (int i = 0; i < WarmupIterations; i++)
            {
                var w = _entityManager.CreateEntity();
                _entityManager.AddComponent(w, new Position { X = i });
                _entityManager.AddComponent(w, new Velocity { X = 1 });
                _entityManager.AddComponent(w, new Health { Current = 100, Max = 100 });
                _entityManager.DestroyEntity(w);
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i, Y = i, Z = i });
                _entityManager.AddComponent(entity, new Velocity { X = 1, Y = 0, Z = 0 });
                _entityManager.AddComponent(entity, new Health { Current = 100, Max = 100 });
            }
            sw.Stop();

            double usPerOp = (sw.Elapsed.TotalMilliseconds * 1000) / Count;

            UnityEngine.Debug.Log($"=== STRADA ECS: Entity + 3 Components ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-entity: {usPerOp:F3}μs");

            Assert.Less(usPerOp, 5.0, "Entity + 3 components should be under 5μs");
        }

        [Test]
        public void Benchmark_Query_SingleComponent_100k()
        {
            const int Count = 100_000;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i, Y = i, Z = i });
            }

            int warmupCount = 0;
            _entityManager.ForEach<Position>((int idx, ref Position p) => warmupCount++);

            int iterCount = 0;
            float sum = 0;

            double ms = MedianMs(() =>
            {
                iterCount = 0;
                sum = 0;
                _entityManager.ForEach<Position>((int idx, ref Position p) =>
                {
                    iterCount++;
                    sum += p.X + p.Y + p.Z;
                });
            });

            double usPerEntity = (ms * 1000) / Count;
            double nsPerEntity = usPerEntity * 1000;

            UnityEngine.Debug.Log($"=== STRADA ECS: Single Component Query ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Median: {ms:F2}ms over {SampleCount} samples");
            UnityEngine.Debug.Log($"  Per-entity: {nsPerEntity:F1}ns ({usPerEntity:F4}μs)");

            Assert.AreEqual(Count, iterCount);
            // `sum` is otherwise a dead store and the loads feeding it are removable, which is
            // how a query benchmark ends up reporting a cost well under the real one.
            Assert.AreNotEqual(0f, sum, "The query result must be observed");
            // README publishes 6.6ns/entity. The margin covers the spread between the machine
            // that produced that figure and slower CI hardware; a checked-in per-machine
            // baseline would be the right way to close it further.
            Assert.Less(usPerEntity, 0.04, "Single component query should be under 0.04μs per entity (README: 6.6ns)");
        }

        [Test]
        public void Benchmark_Query_TwoComponents_100k()
        {
            const int Count = 100_000;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i });
                _entityManager.AddComponent(entity, new Velocity { X = 1 });
            }

            int iterCount = 0;

            double ms = MedianMs(() =>
            {
                iterCount = 0;
                _entityManager.ForEach<Position, Velocity>((int idx, ref Position p, ref Velocity v) =>
                {
                    iterCount++;
                    p.X += v.X;
                    p.Y += v.Y;
                    p.Z += v.Z;
                });
            });

            double usPerEntity = (ms * 1000) / Count;
            double nsPerEntity = usPerEntity * 1000;

            UnityEngine.Debug.Log($"=== STRADA ECS: Two Component Query ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Median: {ms:F2}ms over {SampleCount} samples");
            UnityEngine.Debug.Log($"  Per-entity: {nsPerEntity:F1}ns ({usPerEntity:F4}μs)");

            Assert.AreEqual(Count, iterCount);
            Assert.Less(usPerEntity, 0.06, "Two component query should be under 0.06μs per entity");
        }

        [Test]
        public void Benchmark_Query_ThreeComponents_100k()
        {
            const int Count = 100_000;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i });
                _entityManager.AddComponent(entity, new Velocity { X = 1 });
                _entityManager.AddComponent(entity, new Health { Current = 100, Max = 100 });
            }

            int iterCount = 0;

            double ms = MedianMs(() =>
            {
                iterCount = 0;
                _entityManager.ForEach<Position, Velocity, Health>((int idx, ref Position p, ref Velocity v, ref Health h) =>
                {
                    iterCount++;
                    p.X += v.X;
                    h.Current -= 1;
                });
            });

            double usPerEntity = (ms * 1000) / Count;
            double nsPerEntity = usPerEntity * 1000;

            UnityEngine.Debug.Log($"=== STRADA ECS: Three Component Query ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Median: {ms:F2}ms over {SampleCount} samples");
            UnityEngine.Debug.Log($"  Per-entity: {nsPerEntity:F1}ns ({usPerEntity:F4}μs)");

            Assert.AreEqual(Count, iterCount);
            Assert.Less(usPerEntity, 0.08, "Three component query should be under 0.08μs per entity");
        }

        [Test]
        public void Benchmark_SimulationLoop_10Frames_100k()
        {
            const int EntityCount = 100_000;
            const int FrameCount = 10;

            for (int i = 0; i < EntityCount; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = 0, Y = 0, Z = 0 });
                _entityManager.AddComponent(entity, new Velocity { X = 1, Y = 0.5f, Z = 0.1f });
            }

            _entityManager.ForEach<Position, Velocity>((int idx, ref Position p, ref Velocity v) =>
            {
                p.X += v.X;
                p.Y += v.Y;
                p.Z += v.Z;
            });

            double ms = MedianMs(() =>
            {
                for (int frame = 0; frame < FrameCount; frame++)
                {
                    _entityManager.ForEach<Position, Velocity>((int idx, ref Position p, ref Velocity v) =>
                    {
                        p.X += v.X;
                        p.Y += v.Y;
                        p.Z += v.Z;
                    });
                }
            });

            double msPerFrame = ms / FrameCount;
            double usPerEntity = (ms * 1000) / (FrameCount * EntityCount);

            UnityEngine.Debug.Log($"=== STRADA ECS: Simulation Loop ({EntityCount:N0} entities, {FrameCount} frames) ===");
            UnityEngine.Debug.Log($"  Median: {ms:F2}ms over {SampleCount} samples");
            UnityEngine.Debug.Log($"  Per-frame: {msPerFrame:F2}ms");
            UnityEngine.Debug.Log($"  Per-entity-frame: {usPerEntity * 1000:F1}ns");

            // README publishes 1.62ms/frame; 10ms tolerated a 6x regression without complaint.
            Assert.Less(msPerFrame, 5, "Per frame should be under 5ms for 100k entities (README: 1.62ms)");
        }

        [Test]
        public void Benchmark_EntityDestruction_100k()
        {
            const int Count = 100_000;
            var entities = new Entity[Count];

            // Warm up destruction on a throwaway batch, so the JIT of DestroyEntity and the
            // storage walk it performs are paid before the Stopwatch starts.
            for (int i = 0; i < WarmupIterations; i++)
            {
                var w = _entityManager.CreateEntity();
                _entityManager.AddComponent(w, new Position());
                _entityManager.AddComponent(w, new Velocity());
                _entityManager.DestroyEntity(w);
            }

            for (int i = 0; i < Count; i++)
            {
                entities[i] = _entityManager.CreateEntity();
                _entityManager.AddComponent(entities[i], new Position());
                _entityManager.AddComponent(entities[i], new Velocity());
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++)
            {
                _entityManager.DestroyEntity(entities[i]);
            }
            sw.Stop();

            double usPerOp = (sw.Elapsed.TotalMilliseconds * 1000) / Count;

            UnityEngine.Debug.Log($"=== STRADA ECS: Entity Destruction ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-entity: {usPerOp:F3}μs");

            Assert.AreEqual(0, _entityManager.EntityCount);
            Assert.Less(usPerOp, 2.0, "Entity destruction should be under 2μs");
        }

        [Test]
        public void Benchmark_EntityRecycling_3Cycles_100k()
        {
            const int Count = 100_000;

            UnityEngine.Debug.Log($"=== STRADA ECS: Entity Recycling ({Count:N0} entities, 3 cycles) ===");

            for (int cycle = 0; cycle < 3; cycle++)
            {
                var entities = new Entity[Count];

                var createSw = Stopwatch.StartNew();
                for (int i = 0; i < Count; i++)
                {
                    entities[i] = _entityManager.CreateEntity();
                    _entityManager.AddComponent(entities[i], new Position());
                }
                createSw.Stop();

                var destroySw = Stopwatch.StartNew();
                for (int i = 0; i < Count; i++)
                {
                    _entityManager.DestroyEntity(entities[i]);
                }
                destroySw.Stop();

                UnityEngine.Debug.Log($"  Cycle {cycle + 1}: Create {createSw.ElapsedMilliseconds}ms, Destroy {destroySw.ElapsedMilliseconds}ms");
            }

            Assert.AreEqual(0, _entityManager.EntityCount);
        }

        [Test]
        public void Benchmark_ComponentAddRemove_100k()
        {
            const int Count = 100_000;

            var entities = new Entity[Count];
            for (int i = 0; i < Count; i++)
            {
                entities[i] = _entityManager.CreateEntity();
            }

            // Both timed windows below start cold otherwise: the first AddComponent<Position>
            // and the first RemoveComponent<Position> each pay generic JIT and a storage
            // lookup inside the measurement.
            for (int i = 0; i < WarmupIterations; i++)
            {
                var w = _entityManager.CreateEntity();
                _entityManager.AddComponent(w, new Position { X = i });
                _entityManager.RemoveComponent<Position>(w);
                _entityManager.DestroyEntity(w);
            }

            var addSw = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++)
            {
                _entityManager.AddComponent(entities[i], new Position { X = i });
            }
            addSw.Stop();

            var removeSw = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++)
            {
                _entityManager.RemoveComponent<Position>(entities[i]);
            }
            removeSw.Stop();

            double addUs = (addSw.Elapsed.TotalMilliseconds * 1000) / Count;
            double removeUs = (removeSw.Elapsed.TotalMilliseconds * 1000) / Count;

            UnityEngine.Debug.Log($"=== STRADA ECS: Component Add/Remove ({Count:N0} operations) ===");
            UnityEngine.Debug.Log($"  Add: {addSw.ElapsedMilliseconds}ms ({addUs:F3}μs/op)");
            UnityEngine.Debug.Log($"  Remove: {removeSw.ElapsedMilliseconds}ms ({removeUs:F3}μs/op)");

            Assert.Less(addUs, 1.0, "Component add should be under 1μs");
            Assert.Less(removeUs, 1.0, "Component remove should be under 1μs");
        }

        [Test]
        public void Benchmark_HasComponent_100k()
        {
            const int Count = 100_000;
            var entities = new Entity[Count];

            for (int i = 0; i < Count; i++)
            {
                entities[i] = _entityManager.CreateEntity();
                if (i % 2 == 0)
                    _entityManager.AddComponent(entities[i], new Position());
            }

            int hasCount = 0;

            double ms = MedianMs(() =>
            {
                hasCount = 0;
                for (int i = 0; i < Count; i++)
                {
                    if (_entityManager.HasComponent<Position>(entities[i]))
                        hasCount++;
                }
            });

            double nsPerOp = ms * 1000 * 1000 / Count;

            UnityEngine.Debug.Log($"=== STRADA ECS: HasComponent Check ({Count:N0} checks) ===");
            UnityEngine.Debug.Log($"  Median: {ms:F2}ms over {SampleCount} samples");
            UnityEngine.Debug.Log($"  Per-check: {nsPerOp:F1}ns");

            Assert.AreEqual(Count / 2, hasCount);
            Assert.Less(nsPerOp, 100, "HasComponent should be under 100ns");
        }

        [Test]
        public void Benchmark_GetComponent_100k()
        {
            const int Count = 100_000;
            var entities = new Entity[Count];

            for (int i = 0; i < Count; i++)
            {
                entities[i] = _entityManager.CreateEntity();
                _entityManager.AddComponent(entities[i], new Position { X = i });
            }

            float sum = 0;

            double ms = MedianMs(() =>
            {
                sum = 0;
                for (int i = 0; i < Count; i++)
                {
                    var pos = _entityManager.GetComponent<Position>(entities[i]);
                    sum += pos.X;
                }
            });

            double nsPerOp = ms * 1000 * 1000 / Count;

            UnityEngine.Debug.Log($"=== STRADA ECS: GetComponent ({Count:N0} gets) ===");
            UnityEngine.Debug.Log($"  Median: {ms:F2}ms over {SampleCount} samples");
            UnityEngine.Debug.Log($"  Per-get: {nsPerOp:F1}ns");

            // The accumulated value is the only thing rooting the fetched components; without
            // it the loop is a sequence of dead loads.
            Assert.AreNotEqual(0f, sum, "The fetched components must be observed");
            Assert.Less(nsPerOp, 100, "GetComponent should be under 100ns");
        }

        [Test]
        public void Benchmark_SetComponent_100k()
        {
            const int Count = 100_000;
            var entities = new Entity[Count];

            for (int i = 0; i < Count; i++)
            {
                entities[i] = _entityManager.CreateEntity();
                _entityManager.AddComponent(entities[i], new Position { X = 0 });
            }

            double ms = MedianMs(() =>
            {
                for (int i = 0; i < Count; i++)
                {
                    _entityManager.SetComponent(entities[i], new Position { X = i, Y = i, Z = i });
                }
            });

            double nsPerOp = ms * 1000 * 1000 / Count;

            UnityEngine.Debug.Log($"=== STRADA ECS: SetComponent ({Count:N0} sets) ===");
            UnityEngine.Debug.Log($"  Median: {ms:F2}ms over {SampleCount} samples");
            UnityEngine.Debug.Log($"  Per-set: {nsPerOp:F1}ns");

            Assert.Less(nsPerOp, 100, "SetComponent should be under 100ns");
        }

        [Test]
        public void Benchmark_MemoryUsage_100k()
        {
            const int Count = 100_000;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long managedBefore = GC.GetTotalMemory(true);
            long nativeBefore = _entityManager.Store.AllocatedBytes;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i, Y = i, Z = i });
                _entityManager.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
            }

            // Component data lives in NativeArrays, which the managed GC cannot see:
            // GC.GetTotalMemory reports NONE of the component storage. Measuring the
            // published per-entity memory figure with GC statistics alone therefore measures
            // managed overhead and misses the thing being claimed. Native bytes are read from
            // the storages' own allocated capacity.
            long managedBytes = GC.GetTotalMemory(true) - managedBefore;
            long nativeBytes = _entityManager.Store.AllocatedBytes - nativeBefore;
            long usedBytes = managedBytes + nativeBytes;
            double bytesPerEntity = usedBytes / (double)Count;

            UnityEngine.Debug.Log($"  Managed: {managedBytes / 1024.0:F1} KB | Native: {nativeBytes / 1024.0:F1} KB");

            double theoreticalMin = 28;

            UnityEngine.Debug.Log($"=== STRADA ECS: Memory Usage ({Count:N0} entities, 2 components each) ===");
            UnityEngine.Debug.Log($"  Total: {usedBytes / 1024.0:F2} KB ({usedBytes / 1024.0 / 1024.0:F2} MB)");
            UnityEngine.Debug.Log($"  Per-entity: {bytesPerEntity:F1} bytes");
            UnityEngine.Debug.Log($"  Theoretical min: {theoreticalMin} bytes");
            UnityEngine.Debug.Log($"  Overhead: {(bytesPerEntity / theoreticalMin - 1) * 100:F1}%");

            Assert.Less(bytesPerEntity, 128, "Memory per entity should be under 128 bytes");
        }

        [Test]
        public void Benchmark_MixedEntityTypes_100k()
        {
            const int Count = 100_000;

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = i });

                if (i % 2 == 0)
                    _entityManager.AddComponent(entity, new Velocity { X = 1 });
                if (i % 3 == 0)
                    _entityManager.AddComponent(entity, new Health { Current = 100 });
                if (i % 5 == 0)
                    _entityManager.AddComponent(entity, new Tag { Id = i });
            }

            int posVelCount = 0;
            int posHealthCount = 0;

            double ms = MedianMs(() =>
            {
                posVelCount = 0;
                posHealthCount = 0;

                _entityManager.ForEach<Position, Velocity>((int idx, ref Position p, ref Velocity v) =>
                {
                    posVelCount++;
                    p.X += v.X;
                });

                _entityManager.ForEach<Position, Health>((int idx, ref Position p, ref Health h) =>
                {
                    posHealthCount++;
                    h.Current -= 1;
                });
            });

            UnityEngine.Debug.Log($"=== STRADA ECS: Mixed Entity Types ({Count:N0} entities) ===");
            UnityEngine.Debug.Log($"  Position+Velocity matches: {posVelCount:N0}");
            UnityEngine.Debug.Log($"  Position+Health matches: {posHealthCount:N0}");
            UnityEngine.Debug.Log($"  Median query time: {ms:F2}ms over {SampleCount} samples");

            Assert.AreEqual(Count / 2, posVelCount);
            Assert.AreEqual(Count / 3 + 1, posHealthCount);
        }

        [Test]
        public void Benchmark_Comparison_ManualVsECS()
        {
            const int Count = 100_000;
            const int Frames = 10;

            var manualPositions = new Position[Count];
            var manualVelocities = new Velocity[Count];
            for (int i = 0; i < Count; i++)
            {
                manualPositions[i] = new Position { X = 0, Y = 0, Z = 0 };
                manualVelocities[i] = new Velocity { X = 1, Y = 0.5f, Z = 0.1f };
            }

            for (int i = 0; i < Count; i++)
            {
                var entity = _entityManager.CreateEntity();
                _entityManager.AddComponent(entity, new Position { X = 0, Y = 0, Z = 0 });
                _entityManager.AddComponent(entity, new Velocity { X = 1, Y = 0.5f, Z = 0.1f });
            }

            for (int i = 0; i < Count; i++)
            {
                manualPositions[i].X += manualVelocities[i].X;
            }
            _entityManager.ForEach<Position, Velocity>((int idx, ref Position p, ref Velocity v) => p.X += v.X);

            double manualMs = MedianMs(() =>
            {
                for (int f = 0; f < Frames; f++)
                {
                    for (int i = 0; i < Count; i++)
                    {
                        manualPositions[i].X += manualVelocities[i].X;
                        manualPositions[i].Y += manualVelocities[i].Y;
                        manualPositions[i].Z += manualVelocities[i].Z;
                    }
                }
            });

            double ecsMs = MedianMs(() =>
            {
                for (int f = 0; f < Frames; f++)
                {
                    _entityManager.ForEach<Position, Velocity>((int idx, ref Position p, ref Velocity v) =>
                    {
                        p.X += v.X;
                        p.Y += v.Y;
                        p.Z += v.Z;
                    });
                }
            });

            double overhead = ecsMs / manualMs;

            UnityEngine.Debug.Log($"=== STRADA ECS vs Manual Arrays ({Count:N0} entities, {Frames} frames) ===");
            UnityEngine.Debug.Log($"  Manual arrays: {manualMs:F2}ms (median of {SampleCount})");
            UnityEngine.Debug.Log($"  ECS ForEach:   {ecsMs:F2}ms (median of {SampleCount})");
            UnityEngine.Debug.Log($"  ECS Overhead:  {overhead:F2}x");

            // Both legs run on the same machine in the same run, so this ratio is the one
            // assertion here that does not need slack for unknown hardware. README publishes
            // 1.56x; the previous bound of 10x tolerated a 6x regression.
            Assert.Less(overhead, 3.0, "ECS overhead should be less than 3x manual arrays (README: 1.56x)");
        }
    }
}
