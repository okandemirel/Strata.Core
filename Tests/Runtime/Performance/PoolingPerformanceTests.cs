using NUnit.Framework;
using Strada.Core.Pooling;
using Unity.PerformanceTesting;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [TestFixture]
    [Category("Performance")]
    public sealed class PoolingPerformanceTests
    {
        private sealed class HeavyPoolable : IPoolable
        {
            public const int SpawnMarker = 1;

            public int[] Data = new int[1000];

            public void OnSpawn()
            {
                // Deliberately NOT zero. The CLR already zero-initialises this 4 KB array as
                // part of allocating it, so a spawn state of all-zeros made OnSpawn a redundant
                // 4 KB memset that only the direct-allocation leg paid — allocate + zero-init +
                // clear again versus the pool's single OnSpawn. That biased the published
                // pooling speedup by one full array clear per iteration. With a non-zero marker
                // both legs must genuinely run the loop.
                for (var i = 0; i < Data.Length; i++)
                    Data[i] = SpawnMarker;
            }

            public void OnDespawn() { }
        }

        [Test, Performance]
        public void Benchmark_10k_PoolSpawnDespawn()
        {
            var pool = new ObjectPool<HeavyPoolable>(() => new HeavyPoolable(), 100);
            pool.Prewarm(100);
            int createdBeforeRun = pool.TotalCreated;

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    var obj = pool.Spawn();
                    pool.Despawn(obj);
                }
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();

            // The pooled leg is only a fair comparison against direct allocation if it really
            // recycles. A pool that quietly constructed a fresh object per Spawn would post
            // direct-allocation timings and nothing here would notice.
            Assert.AreEqual(createdBeforeRun, pool.TotalCreated,
                "Spawn/Despawn cycles must not construct new instances");

            var reused = pool.Spawn();
            pool.Despawn(reused);
            Assert.AreSame(reused, pool.Spawn(), "Spawn should hand back the despawned instance");
            Assert.AreEqual(HeavyPoolable.SpawnMarker, reused.Data[0], "Spawn should run OnSpawn");
        }

        [Test, Performance]
        public void Benchmark_10k_DirectAllocation()
        {
            HeavyPoolable last = null;

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    var obj = new HeavyPoolable();
                    obj.OnSpawn();
                    last = obj;
                }
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();

            // Roots the allocation so the loop cannot be eliminated as dead, and pins that this
            // leg does the same spawn work the pooled leg does.
            Assert.NotNull(last);
            Assert.AreEqual(HeavyPoolable.SpawnMarker, last.Data[0]);
        }

        [Test, Performance]
        public void Benchmark_PoolRegistry_SpawnByType()
        {
            var registry = new PoolRegistry();
            registry.GetOrCreate(() => new HeavyPoolable(), 100);
            registry.Get<HeavyPoolable>().Prewarm(50);
            int createdBeforeRun = registry.Get<HeavyPoolable>().TotalCreated;

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    var obj = registry.Spawn<HeavyPoolable>();
                    registry.Despawn(obj);
                }
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();

            Assert.AreEqual(createdBeforeRun, registry.Get<HeavyPoolable>().TotalCreated,
                "Registry spawn/despawn must recycle rather than construct");

            registry.Dispose();
        }
    }
}
