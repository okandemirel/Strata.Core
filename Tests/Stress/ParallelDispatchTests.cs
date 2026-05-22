using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Strada.Core.Communication;
using UnityEngine;

namespace Strada.Core.Tests.Stress
{
    public class ParallelDispatchTests
    {
        private EventBus _bus;

        [SetUp]
        public void Setup()
        {
            _bus = new EventBus();
        }

        [TearDown]
        public void Teardown()
        {
            _bus?.Dispose();
        }

        [Test]
        public void Parallel_Publish_And_Subscribe_ShouldNotCrash()
        {
            int threadCount = 20;
            int eventsPerThread = 1000;
            int receivedCount = 0;

            _bus.Subscribe<TestEvent>(evt => Interlocked.Increment(ref receivedCount));

            StressTestRunner.Run("Parallel Publish/Subscribe", () =>
            {
                var tasks = new Task[threadCount];
                for (int i = 0; i < threadCount; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        for (int j = 0; j < eventsPerThread; j++)
                        {
                            _bus.Publish(new TestEvent { Value = j });

                            if (j % 100 == 0)
                            {
                                Action<TestEvent> tempHandler = e => { };
                                var token = _bus.Subscribe(tempHandler);
                                token.Dispose();
                            }
                        }
                    });
                }

                Task.WaitAll(tasks);
            });

            Assert.AreEqual(threadCount * eventsPerThread, receivedCount);
        }

        private struct TestEvent
        {
            public int Value;
        }
    }
}
