using System;
using System.Diagnostics;
using NUnit.Framework;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Query;
using Unity.Collections;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [TestFixture]
    [Category("Performance")]
    public class RealisticSimulationTests
    {
        private EntityManager _manager;
        private const int EntityCount = 100_000;

        private struct Position : IComponent { public float X, Y, Z; }
        private struct Velocity : IComponent { public float X, Y, Z; }
        private struct Health : IComponent { public int Value; }

        [SetUp]
        public void Setup()
        {
            _manager = new EntityManager(EntityCount);
            // Create entities with mixed components to simulate fragmentation
            for (int i = 0; i < EntityCount; i++)
            {
                var e = _manager.CreateEntity();
                _manager.AddComponent(e, new Position { X = i });
                if (i % 2 == 0) _manager.AddComponent(e, new Velocity { X = 1 });
                if (i % 3 == 0) _manager.AddComponent(e, new Health { Value = 100 });
            }
        }

        [TearDown]
        public void TearDown() => _manager?.Dispose();

        [Test]
        public void Simulation_MixedReadWrite()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            _manager.ForEach((int e, ref Position p, ref Velocity v) =>
            {
                v.Y -= 9.81f * 0.016f; // dt
            });

            _manager.ForEach((int e, ref Position p, ref Velocity v) =>
            {
                p.X += v.X * 0.016f;
                p.Y += v.Y * 0.016f;
            });

            stopwatch.Stop();
            UnityEngine.Debug.Log($"Mixed Read/Write ({EntityCount} entities): {stopwatch.Elapsed.TotalMilliseconds} ms");
            
            Assert.Less(stopwatch.Elapsed.TotalMilliseconds, 50.0);
        }

        [Test]
        public void Simulation_CacheThrashing()
        {
            // Persistent, not Temp: this is a 400 KB buffer held across a 100k-iteration
            // synchronous loop, well past what Temp's small-block bump allocator is meant for.
            // try/finally rather than a trailing Dispose(): a throw inside the loop below — a
            // stale entity reaching GetComponentRef, say — used to skip the Dispose entirely,
            // leaking the allocation and arming Unity's leak detector for the rest of the run.
            // (`using var` cannot be used here: the local would be readonly and the buffer is
            // filled after declaration.)
            var randomIndices = new NativeArray<int>(EntityCount, Allocator.Persistent);
            try
            {
            var rand = new Random(12345);
            for (int i = 0; i < EntityCount; i++)
                randomIndices[i] = rand.Next(1, EntityCount);

            int touched = 0;
            float checksum = 0;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            for (int i = 0; i < EntityCount; i++)
            {
                int index = randomIndices[i];
                var entity = _manager.GetEntity(index);
                if (_manager.Exists(entity))
                {
                    if (_manager.HasComponent<Position>(entity))
                    {
                        ref var p = ref _manager.GetComponentRef<Position>(entity);
                        p.X += 1;
                        touched++;
                        checksum += p.X;
                    }
                }
            }

            stopwatch.Stop();

            UnityEngine.Debug.Log($"Random Access ({EntityCount} ops): {stopwatch.Elapsed.TotalMilliseconds} ms");

            // Every index in [1, EntityCount) belongs to a live entity that Setup gave a
            // Position, so every probe must hit. Without this the loop's writes are unobserved
            // and a build that skipped them entirely would still report a passing benchmark.
            Assert.AreEqual(EntityCount, touched, "Every random probe should hit a live entity with a Position");
            Assert.Greater(checksum, 0f);
            Assert.Less(stopwatch.Elapsed.TotalMilliseconds, 500.0);
            }
            finally
            {
                randomIndices.Dispose();
            }
        }
    }
}
