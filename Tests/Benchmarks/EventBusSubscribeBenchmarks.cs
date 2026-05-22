using System;
using System.Diagnostics;
using NUnit.Framework;
using Strada.Core.Communication;

namespace Strada.Core.Tests.Benchmarks
{
    [TestFixture]
    [Category("Benchmark")]
    public class EventBusSubscribeBenchmarks
    {
        private EventBus _bus;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
        }

        [TearDown]
        public void TearDown()
        {
            _bus?.Dispose();
        }

        [Test]
        public void EventBus_Subscribe_Unsubscribe_Stress()
        {
            const int iterations = 1000;
            Action<TestEvent>[] handlers = new Action<TestEvent>[iterations];
            var tokens = new Strada.Core.SubscriptionToken[iterations];

            for (int i = 0; i < iterations; i++)
            {
                handlers[i] = e => { };
            }

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                tokens[i] = _bus.Subscribe(handlers[i]);
            }
            for (int i = 0; i < 100; i++)
            {
                tokens[i].Dispose();
            }

            // Benchmark subscribe
            var swSubscribe = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                tokens[i] = _bus.Subscribe(handlers[i]);
            }
            swSubscribe.Stop();

            // Benchmark unsubscribe
            var swUnsubscribe = Stopwatch.StartNew();
            for (int i = iterations - 1; i >= 0; i--)
            {
                tokens[i].Dispose();
            }
            swUnsubscribe.Stop();

            double avgSubscribeNs = swSubscribe.Elapsed.TotalMilliseconds * 1000000 / iterations;
            double avgUnsubscribeNs = swUnsubscribe.Elapsed.TotalMilliseconds * 1000000 / iterations;

            UnityEngine.Debug.Log($"[EventBus] Subscribe/Unsubscribe Stress ({iterations} handlers):");
            UnityEngine.Debug.Log($"  Subscribe - Total: {swSubscribe.ElapsedMilliseconds}ms, Avg: {avgSubscribeNs:F0}ns");
            UnityEngine.Debug.Log($"  Unsubscribe - Total: {swUnsubscribe.ElapsedMilliseconds}ms, Avg: {avgUnsubscribeNs:F0}ns");

            Assert.AreEqual(0, _bus.GetSubscriberCount<TestEvent>());
        }

        [Test]
        public void EventBus_Publish_With_Many_Subscribers()
        {
            const int subscriberCount = 50;
            const int publishIterations = 1000;
            int callCount = 0;

            for (int i = 0; i < subscriberCount; i++)
            {
                _bus.Subscribe<TestEvent>(e => callCount++);
            }

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                _bus.Publish(new TestEvent { Value = i });
            }
            callCount = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < publishIterations; i++)
            {
                _bus.Publish(new TestEvent { Value = i });
            }
            sw.Stop();

            double avgNs = sw.Elapsed.TotalMilliseconds * 1000000 / publishIterations;

            UnityEngine.Debug.Log($"[EventBus] Publish to {subscriberCount} subscribers ({publishIterations} publishes):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNs:F0}ns per publish");
            UnityEngine.Debug.Log($"  Total handler calls: {callCount}");

            Assert.AreEqual(publishIterations * subscriberCount, callCount);
        }

        private struct TestEvent
        {
            public int Value;
        }
    }
}
