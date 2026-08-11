using NUnit.Framework;
using Strada.Core.ECS;
using Strada.Core.ECS.Reactive;
using Unity.PerformanceTesting;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [TestFixture]
    [Category("Performance")]
    public sealed class ReactiveSystemPerformanceTests
    {
        private struct TestComponent : IComponent
        {
            public int Value;
        }

        [Test, Performance]
        public void Benchmark_ReactiveAdd_10k()
        {
            var storage = new ReactiveComponentStorage<TestComponent>(16384, 16384);
            var addCount = 0;

            storage.SubscribeOnAdd((entity, component) => addCount++);

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    storage.Add(i, new TestComponent { Value = i });
                }
            })
            .WarmupCount(1)
            .MeasurementCount(5)
            .SetUp(() =>
            {
                storage.Clear();
                addCount = 0;
            })
            // Without a GC sample group the report is wall-clock only, and the notification
            // path's per-write callback-list snapshot never shows up anywhere.
            .GC()
            .Run();

            // The harness owns how many warmup and measurement passes ran, so assert on a pass
            // this test controls. Previously nothing here could fail: a storage that dropped
            // every OnAdd notification would still report a green benchmark.
            storage.Clear();
            addCount = 0;
            for (var i = 0; i < 10000; i++)
                storage.Add(i, new TestComponent { Value = i });

            Assert.AreEqual(10000, addCount, "Every Add should raise exactly one OnAdd");
            Assert.AreEqual(10000, storage.Count);

            storage.Dispose();
        }

        [Test, Performance]
        public void Benchmark_ReactiveChange_10k()
        {
            var storage = new ReactiveComponentStorage<TestComponent>(16384, 16384);
            var changeCount = 0;

            for (var i = 0; i < 10000; i++)
            {
                storage.Add(i, new TestComponent { Value = i });
            }

            storage.SubscribeOnChange((entity, old, newVal) => changeCount++);

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    storage.Set(i, new TestComponent { Value = i + 1 });
                }
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .GC()
            .Run();

            changeCount = 0;
            for (var i = 0; i < 10000; i++)
                storage.Set(i, new TestComponent { Value = i + 2 });

            Assert.AreEqual(10000, changeCount, "Every Set on an existing entity should raise exactly one OnChange");

            storage.Dispose();
        }

        [Test, Performance]
        public void Benchmark_MultipleSubscribers_10k()
        {
            var storage = new ReactiveComponentStorage<TestComponent>(16384, 16384);
            var counts = new int[5];

            storage.SubscribeOnAdd((e, c) => counts[0]++);
            storage.SubscribeOnAdd((e, c) => counts[1]++);
            storage.SubscribeOnAdd((e, c) => counts[2]++);
            storage.SubscribeOnAdd((e, c) => counts[3]++);
            storage.SubscribeOnAdd((e, c) => counts[4]++);

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    storage.Add(i, new TestComponent { Value = i });
                }
            })
            .WarmupCount(1)
            .MeasurementCount(5)
            .SetUp(() =>
            {
                storage.Clear();
                for (var i = 0; i < 5; i++) counts[i] = 0;
            })
            .GC()
            .Run();

            storage.Clear();
            for (var i = 0; i < 5; i++) counts[i] = 0;
            for (var i = 0; i < 10000; i++)
                storage.Add(i, new TestComponent { Value = i });

            for (var s = 0; s < 5; s++)
                Assert.AreEqual(10000, counts[s], $"Subscriber {s} should have been notified for every Add");

            storage.Dispose();
        }

        [Test, Performance]
        public void Benchmark_NonReactive_Baseline_10k()
        {
            var storage = new Strada.Core.ECS.Storage.ComponentStorage<TestComponent>(16384, 16384);

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    storage.Add(i, new TestComponent { Value = i });
                }
            })
            .WarmupCount(1)
            .MeasurementCount(5)
            .SetUp(() => storage.Clear())
            // This is the baseline the three reactive benchmarks above are meant to be read
            // against, so it needs the same GC sample group for the comparison to mean anything.
            .GC()
            .Run();

            storage.Clear();
            for (var i = 0; i < 10000; i++)
                storage.Add(i, new TestComponent { Value = i });

            Assert.AreEqual(10000, storage.Count);

            storage.Dispose();
        }
    }
}
