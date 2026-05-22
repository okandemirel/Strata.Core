using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Strada.Core.Communication;

namespace Strada.Core.Tests.Communication
{
    [TestFixture]
    [Category("ThreadSafety")]
    public class EventBusThreadSafetyTests
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
        public void Subscribe_ConcurrentSubscriptions_DoesNotThrow()
        {
            const int threadCount = 4;
            const int subscriptionsPerThread = 25;
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < subscriptionsPerThread; i++)
                    {
                        _bus.Subscribe<TestEvent>(e => { });
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.AreEqual(threadCount * subscriptionsPerThread, _bus.GetSubscriberCount<TestEvent>());
        }

        [Test]
        public void SubscribeAndPublish_ConcurrentAccess_DoesNotThrow()
        {
            const int threadCount = 4;
            var tasks = new Task[threadCount * 2];

            // Pre-subscribe some handlers
            for (int i = 0; i < 5; i++)
            {
                _bus.Subscribe<TestEvent>(e => { });
            }

            // Concurrent subscribe tasks
            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        _bus.Subscribe<TestEvent>(e => { });
                    }
                });
            }

            // Concurrent publish tasks
            for (int t = 0; t < threadCount; t++)
            {
                int index = threadCount + t;
                tasks[index] = Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        _bus.Publish(new TestEvent { Value = i });
                    }
                });
            }

            Assert.DoesNotThrow(() => Task.WaitAll(tasks));
        }

        [Test]
        public void Send_ConcurrentSends_AllHandlersExecute()
        {
            var counter = 0;
            _bus.RegisterSignalHandler<TestSignal>(s => Interlocked.Increment(ref counter));

            const int threadCount = 4;
            const int sendsPerThread = 25;
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < sendsPerThread; i++)
                    {
                        _bus.Send(new TestSignal { Value = i });
                    }
                });
            }

            Task.WaitAll(tasks);

            Assert.AreEqual(threadCount * sendsPerThread, counter);
        }

        private struct TestEvent
        {
            public int Value;
        }

        private struct TestSignal
        {
            public int Value;
        }
    }
}
