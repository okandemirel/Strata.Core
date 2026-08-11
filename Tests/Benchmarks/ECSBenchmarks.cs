using System;
using NUnit.Framework;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.World;
using Unity.PerformanceTesting;

namespace Strada.Core.Tests.Benchmarks
{
    public class ECSBenchmarks
    {
        private World _world;

        [SetUp]
        public void Setup()
        {
            _world = new ECSBuilder().Build();
        }

        [TearDown]
        public void Teardown()
        {
            _world?.Dispose();
        }

        [Test, Performance]
        public void CreateEntity_Benchmark()
        {
            Measure.Method(() =>
            {
                _world.EntityManager.CreateEntity();
            })
            .WarmupCount(10)
            .MeasurementCount(100)
            .IterationsPerMeasurement(1000)
            .Run();

            Assert.Greater(_world.EntityManager.EntityCount, 0, "Measured calls must have created entities");
        }

        [Test, Performance]
        public void AddComponent_Benchmark()
        {
            const int Count = 500;
            var entities = new Entity[Count];

            // SparseSet.Add short-circuits into a two-array overwrite when the entity already
            // holds the component. Adding to one entity for every measured call therefore hit
            // the real insert path exactly once, during warmup, and reported the overwrite cost
            // as "AddComponent". A fresh batch per iteration keeps every call on the insert
            // path: capacity check, dense append, sparse write, count bump.
            Measure.Method(() =>
            {
                for (int i = 0; i < Count; i++)
                    _world.EntityManager.AddComponent(entities[i], new TestComponent { Value = i });
            })
            .SetUp(() =>
            {
                // Recycle rather than accumulate, so the sparse set settles at one batch worth
                // of capacity instead of growing into a realloc inside the timed region.
                // DestroyEntity is a no-op for the default entities of the first iteration.
                for (int i = 0; i < Count; i++)
                {
                    _world.EntityManager.DestroyEntity(entities[i]);
                    entities[i] = _world.EntityManager.CreateEntity();
                }
            })
            .WarmupCount(5)
            .MeasurementCount(50)
            .IterationsPerMeasurement(1)
            .Run();

            for (int i = 0; i < Count; i++)
                Assert.IsTrue(_world.EntityManager.HasComponent<TestComponent>(entities[i]),
                    "Every measured call must have performed a real insert");
        }

        [Test, Performance]
        public void Iteration_10k_Entities_Benchmark()
        {
            int count = 10000;
            for (int i = 0; i < count; i++)
            {
                var e = _world.EntityManager.CreateEntity();
                _world.EntityManager.AddComponent(e, new TestComponent { Value = i });
            }

            int visited = 0;
            long checksum = 0;

            Measure.Method(() =>
            {
                visited = 0;
                checksum = 0;
                foreach (var index in _world.EntityManager.GetAllEntities())
                {
                    var entity = _world.EntityManager.GetEntity(index);
                    if (_world.EntityManager.HasComponent<TestComponent>(entity))
                    {
                        var cmp = _world.EntityManager.GetComponent<TestComponent>(entity);
                        visited++;
                        checksum += cmp.Value;
                    }
                }
            })
            .WarmupCount(5)
            .MeasurementCount(20)
            .Run();

            // Accumulating and asserting roots the fetched component: a discarded
            // GetComponent result is a dead load the optimiser is free to delete, which is how
            // an iteration benchmark ends up reporting a cost far below the real one.
            Assert.AreEqual(count, visited);
            Assert.AreEqual((long)count * (count - 1) / 2, checksum);
        }

        private struct TestComponent : IComponent
        {
            public int Value;
        }
    }
}
