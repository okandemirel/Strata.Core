using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Strada.Core.Communication;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [TestFixture]
    [Category("Performance")]
    public class AsyncPerformanceTests
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
        public async Task Benchmark_100k_AsyncSignalDispatches_Synchronous()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;
            int counter = 0;

            // Handler that completes synchronously (ValueTask optimization)
            _bus.RegisterAsyncSignalHandler<AsyncSignal>((s, ct) =>
            {
                counter += s.Value;
                return default;
            });

            for (int i = 0; i < Warmup; i++)
            {
                await _bus.SendAsync(new AsyncSignal { Value = 1 });
            }
            counter = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                await _bus.SendAsync(new AsyncSignal { Value = 1 });
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[AsyncEventBus] SendAsync - Sync Handler ({Iterations} dispatches):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per dispatch");
            UnityEngine.Debug.Log($"  Throughput: {Iterations / sw.Elapsed.TotalSeconds:F0} dispatches/sec");

            Assert.AreEqual(Iterations, counter);
            Assert.Less(sw.ElapsedMilliseconds, 50, "Async signal dispatch (sync handler) too slow");
        }

        [Test]
        public async Task Benchmark_10k_AsyncQueryDispatches_Synchronous()
        {
            const int Iterations = 10000;
            const int Warmup = 100;

            // Handler that completes synchronously (ValueTask optimization)
            _bus.RegisterAsyncQueryHandler<AsyncQuery, int>((q, ct) =>
                new ValueTask<int>(q.Input * 2));

            for (int i = 0; i < Warmup; i++)
            {
                await _bus.QueryAsync<AsyncQuery, int>(new AsyncQuery { Input = i });
            }

            var sw = Stopwatch.StartNew();
            int sum = 0;
            for (int i = 0; i < Iterations; i++)
            {
                sum += await _bus.QueryAsync<AsyncQuery, int>(new AsyncQuery { Input = 1 });
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[AsyncEventBus] QueryAsync - Sync Handler ({Iterations} queries):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per query");
            UnityEngine.Debug.Log($"  Throughput: {Iterations / sw.Elapsed.TotalSeconds:F0} queries/sec");

            Assert.AreEqual(Iterations * 2, sum);
            Assert.Less(sw.ElapsedMilliseconds, 100, "Async query dispatch too slow");
        }

        [Test]
        public async Task Benchmark_SignalSequence_AsyncExecution()
        {
            const int Iterations = 1000;
            int counter = 0;

            _bus.RegisterAsyncSignalHandler<AsyncSignal>((s, ct) =>
            {
                counter++;
                return default;
            });

            var sequence = new SignalSequence(_bus)
                .Then(new AsyncSignal { Value = 1 })
                .Then(new AsyncSignal { Value = 2 })
                .Then(new AsyncSignal { Value = 3 })
                .Then(new AsyncSignal { Value = 4 })
                .Then(new AsyncSignal { Value = 5 });

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                await sequence.ExecuteAsync();
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[SignalSequence] ExecuteAsync 5-Signal ({Iterations} executions):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per execution");

            Assert.AreEqual(Iterations * 5, counter);
            Assert.Less(sw.ElapsedMilliseconds, 200, "Async sequence too slow");

            sequence.Dispose();
        }

        /// <summary>
        /// Batched, not parallel. The handler returns <c>default</c> — an already-completed
        /// ValueTask — so <see cref="EventBus.SendAsync{TSignal}"/> runs it inline on the calling
        /// thread and the ValueTask is finished before the loop advances. By the time the await
        /// loop below runs, all ten have long since completed: no continuation is scheduled, no
        /// thread pool work item is queued, and no async state machine is allocated. What this
        /// measures is the cost of the batching shape around a synchronous handler, which is
        /// worth measuring — it just is not a parallelism figure, and calling it one made the
        /// number mean something it does not.
        /// </summary>
        [Test]
        public async Task Benchmark_BatchedAsyncSignals_SyncHandler()
        {
            const int Iterations = 1000;
            const int BatchSize = 10;
            int counter = 0;

            _bus.RegisterAsyncSignalHandler<AsyncSignal>((s, ct) =>
            {
                Interlocked.Increment(ref counter);
                return default;
            });

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var tasks = new ValueTask[BatchSize];
                for (int j = 0; j < BatchSize; j++)
                {
                    tasks[j] = _bus.SendAsync(new AsyncSignal { Value = 1 });
                }
                for (int j = 0; j < BatchSize; j++)
                {
                    await tasks[j];
                }
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[AsyncEventBus] Batched SendAsync, sync handler ({Iterations} batches of {BatchSize}):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per batch");
            UnityEngine.Debug.Log($"  Total dispatches: {counter}");
            UnityEngine.Debug.Log("  (All dispatches complete synchronously on this thread - this is not a parallelism measurement.)");

            Assert.AreEqual(Iterations * BatchSize, counter);
            Assert.Less(sw.ElapsedMilliseconds, 200, "Batched async too slow");
        }

        [Test]
        public async Task Benchmark_MixedSyncAsync_Operations()
        {
            const int Iterations = 10000;
            int syncCounter = 0;
            int asyncCounter = 0;

            _bus.RegisterSignalHandler<SyncSignal>(s => syncCounter++);
            _bus.RegisterAsyncSignalHandler<AsyncSignal>((s, ct) =>
            {
                asyncCounter++;
                return default;
            });

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                _bus.Send(new SyncSignal { Value = i });
                await _bus.SendAsync(new AsyncSignal { Value = i });
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[EventBus] Mixed Sync+Async ({Iterations} pairs):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per pair");

            Assert.AreEqual(Iterations, syncCounter);
            Assert.AreEqual(Iterations, asyncCounter);
            Assert.Less(sw.ElapsedMilliseconds, 100, "Mixed operations too slow");
        }

        private struct AsyncSignal
        {
            public int Value;
        }

        private struct SyncSignal
        {
            public int Value;
        }

        private struct AsyncQuery : IAsyncQuery<int>
        {
            public int Input;
        }
    }
}
