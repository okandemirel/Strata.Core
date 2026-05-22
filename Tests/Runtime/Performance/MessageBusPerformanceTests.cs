using System;
using System.Diagnostics;
using NUnit.Framework;
using Strada.Core.Communication;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [TestFixture]
    [Category("Performance")]
    public class MessageBusPerformanceTests
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
        public void Benchmark_100k_CommandDispatches()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;
            int counter = 0;

            _bus.RegisterSignalHandler<BenchmarkSignal>(cmd => counter += cmd.Value);

            for (int i = 0; i < Warmup; i++)
            {
                _bus.Send(new BenchmarkSignal { Value = 1 });
            }
            counter = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                _bus.Send(new BenchmarkSignal { Value = 1 });
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Command Dispatch ({Iterations} dispatches):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per dispatch");
            UnityEngine.Debug.Log($"  Throughput: {Iterations / sw.Elapsed.TotalSeconds:F0} dispatches/sec");

            Assert.AreEqual(Iterations, counter);
            Assert.Less(sw.ElapsedMilliseconds, 20, "Command dispatch too slow (Target: <20ms for 100k)");
        }

        [Test]
        public void Benchmark_100k_QueryDispatches()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;

            _bus.RegisterQueryHandler<BenchmarkQuery, int>(q => q.Input * 2);

            for (int i = 0; i < Warmup; i++)
            {
                _bus.Query<BenchmarkQuery, int>(new BenchmarkQuery { Input = i });
            }

            var sw = Stopwatch.StartNew();
            int sum = 0;
            for (int i = 0; i < Iterations; i++)
            {
                sum += _bus.Query<BenchmarkQuery, int>(new BenchmarkQuery { Input = 1 });
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Query Dispatch ({Iterations} queries):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per query");
            UnityEngine.Debug.Log($"  Throughput: {Iterations / sw.Elapsed.TotalSeconds:F0} queries/sec");

            Assert.AreEqual(Iterations * 2, sum);
            Assert.Less(sw.ElapsedMilliseconds, 20, "Query dispatch too slow (Target: <20ms for 100k)");
        }

        [Test]
        public void Benchmark_100k_EventPublishes_SingleSubscriber()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;
            int counter = 0;

            _bus.Subscribe<BenchmarkEvent>(evt => counter += evt.Value);

            for (int i = 0; i < Warmup; i++)
            {
                _bus.Publish(new BenchmarkEvent { Value = 1 });
            }
            counter = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                _bus.Publish(new BenchmarkEvent { Value = 1 });
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Event Publish - Single Subscriber ({Iterations} publishes):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per publish");
            UnityEngine.Debug.Log($"  Throughput: {Iterations / sw.Elapsed.TotalSeconds:F0} publishes/sec");

            Assert.AreEqual(Iterations, counter);
            Assert.Less(sw.ElapsedMilliseconds, 20, "Event publish too slow (Target: <20ms for 100k)");
        }

        [Test]
        public void Benchmark_100k_EventPublishes_10Subscribers()
        {
            const int Iterations = 100000;
            const int Subscribers = 10;
            const int Warmup = 1000;
            int counter = 0;

            for (int s = 0; s < Subscribers; s++)
            {
                _bus.Subscribe<BenchmarkEvent>(evt => counter += evt.Value);
            }

            for (int i = 0; i < Warmup; i++)
            {
                _bus.Publish(new BenchmarkEvent { Value = 1 });
            }
            counter = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                _bus.Publish(new BenchmarkEvent { Value = 1 });
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Event Publish - {Subscribers} Subscribers ({Iterations} publishes):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per publish (dispatching to {Subscribers})");
            UnityEngine.Debug.Log($"  Throughput: {Iterations / sw.Elapsed.TotalSeconds:F0} publishes/sec");

            Assert.AreEqual(Iterations * Subscribers, counter);
            Assert.Less(sw.ElapsedMilliseconds, 50, "Event publish with 10 subscribers too slow (Target: <50ms for 100k)");
        }

        [Test]
        public void Benchmark_MixedOperations()
        {
            const int Iterations = 10000;
            int cmdCount = 0;
            int querySum = 0;
            int evtCount = 0;

            _bus.RegisterSignalHandler<BenchmarkSignal>(c => cmdCount++);
            _bus.RegisterQueryHandler<BenchmarkQuery, int>(q => q.Input * 2);
            _bus.Subscribe<BenchmarkEvent>(e => evtCount++);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                _bus.Send(new BenchmarkSignal { Value = i });
                querySum += _bus.Query<BenchmarkQuery, int>(new BenchmarkQuery { Input = 1 });
                _bus.Publish(new BenchmarkEvent { Value = i });
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Mixed Operations ({Iterations} iterations, 3 ops each):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per iteration (cmd+query+event)");
            UnityEngine.Debug.Log($"  Commands: {cmdCount}, Queries: {querySum / 2}, Events: {evtCount}");

            Assert.AreEqual(Iterations, cmdCount);
            Assert.AreEqual(Iterations * 2, querySum);
            Assert.AreEqual(Iterations, evtCount);
            Assert.Less(sw.ElapsedMilliseconds, 50, "Mixed operations too slow (Target: <50ms for 10k)");
        }

        [Test]
        public void Benchmark_100k_SignalDispatch_ByRef()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;
            int counter = 0;

            _bus.RegisterSignalHandler<BenchmarkSignal>(s => counter += s.Value);

            for (int i = 0; i < Warmup; i++)
            {
                var signal = new BenchmarkSignal { Value = 1 };
                _bus.Send(ref signal);
            }
            counter = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var signal = new BenchmarkSignal { Value = 1 };
                _bus.Send(ref signal);
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Signal Dispatch By-Ref ({Iterations} dispatches):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per dispatch");
            UnityEngine.Debug.Log($"  Throughput: {Iterations / sw.Elapsed.TotalSeconds:F0} dispatches/sec");

            Assert.AreEqual(Iterations, counter);
            Assert.Less(sw.ElapsedMilliseconds, 20, "By-ref signal dispatch too slow");
        }

        [Test]
        public void Benchmark_100k_LargeStructSignal_ByRef()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;
            int counter = 0;

            _bus.RegisterSignalHandler<LargeSignal>(s => counter++);

            for (int i = 0; i < Warmup; i++)
            {
                var signal = new LargeSignal { A = 1, B = 2, C = 3, D = 4, E = 5, F = 6, G = 7, H = 8 };
                _bus.Send(ref signal);
            }
            counter = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var signal = new LargeSignal { A = 1, B = 2, C = 3, D = 4, E = 5, F = 6, G = 7, H = 8 };
                _bus.Send(ref signal);
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Large Struct Signal (64 bytes) By-Ref ({Iterations} dispatches):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per dispatch");

            Assert.AreEqual(Iterations, counter);
            Assert.Less(sw.ElapsedMilliseconds, 30, "Large struct signal too slow");
        }

        [Test]
        public void Benchmark_MultiType_SignalLookup()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;
            int counter1 = 0, counter2 = 0, counter3 = 0, counter4 = 0;

            _bus.RegisterSignalHandler<SignalType1>(s => counter1++);
            _bus.RegisterSignalHandler<SignalType2>(s => counter2++);
            _bus.RegisterSignalHandler<SignalType3>(s => counter3++);
            _bus.RegisterSignalHandler<SignalType4>(s => counter4++);

            for (int i = 0; i < Warmup; i++)
            {
                _bus.Send(new SignalType1());
                _bus.Send(new SignalType2());
                _bus.Send(new SignalType3());
                _bus.Send(new SignalType4());
            }
            counter1 = counter2 = counter3 = counter4 = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                _bus.Send(new SignalType1());
                _bus.Send(new SignalType2());
                _bus.Send(new SignalType3());
                _bus.Send(new SignalType4());
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / (Iterations * 4);

            UnityEngine.Debug.Log($"[MessageBus] Multi-Type Signal Lookup ({Iterations * 4} dispatches, 4 types):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per dispatch");
            UnityEngine.Debug.Log($"  Throughput: {Iterations * 4 / sw.Elapsed.TotalSeconds:F0} dispatches/sec");

            Assert.AreEqual(Iterations, counter1);
            Assert.AreEqual(Iterations, counter2);
            Assert.AreEqual(Iterations, counter3);
            Assert.AreEqual(Iterations, counter4);
            Assert.Less(sw.ElapsedMilliseconds, 80, "Multi-type lookup too slow");
        }

        [Test]
        public void Benchmark_100Subscribers_EventPublish()
        {
            const int Iterations = 10000;
            const int Subscribers = 100;
            int counter = 0;

            for (int s = 0; s < Subscribers; s++)
            {
                _bus.Subscribe<BenchmarkEvent>(evt => counter++);
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                _bus.Publish(new BenchmarkEvent { Value = 1 });
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[MessageBus] Event Publish to {Subscribers} Subscribers ({Iterations} publishes):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per publish");
            UnityEngine.Debug.Log($"  Total callbacks: {counter}");

            Assert.AreEqual(Iterations * Subscribers, counter);
            Assert.Less(sw.ElapsedMilliseconds, 200, "100 subscriber broadcast too slow");
        }

        [Test]
        public void Benchmark_SignalSequence_5Signals()
        {
            const int Iterations = 10000;
            const int Warmup = 100;
            int counter = 0;

            _bus.RegisterSignalHandler<BenchmarkSignal>(s => counter++);

            var sequence = new SignalSequence(_bus)
                .Then(new BenchmarkSignal { Value = 1 })
                .Then(new BenchmarkSignal { Value = 2 })
                .Then(new BenchmarkSignal { Value = 3 })
                .Then(new BenchmarkSignal { Value = 4 })
                .Then(new BenchmarkSignal { Value = 5 });

            for (int i = 0; i < Warmup; i++)
            {
                sequence.Execute();
            }
            counter = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                sequence.Execute();
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[SignalSequence] Execute 5-Signal Sequence ({Iterations} executions):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per execution");
            UnityEngine.Debug.Log($"  Signal callbacks: {counter}");

            Assert.AreEqual(Iterations * 5, counter);
            Assert.Less(sw.ElapsedMilliseconds, 50, "Sequence execution too slow");

            sequence.Dispose();
        }

        [Test]
        public void Benchmark_SignalSequence_NestedSequences()
        {
            const int Iterations = 10000;
            int counter = 0;

            _bus.RegisterSignalHandler<BenchmarkSignal>(s => counter++);

            var inner1 = new SignalSequence(_bus)
                .Then(new BenchmarkSignal { Value = 2 })
                .Then(new BenchmarkSignal { Value = 3 });

            var inner2 = new SignalSequence(_bus)
                .Then(new BenchmarkSignal { Value = 5 })
                .Then(new BenchmarkSignal { Value = 6 });

            var outer = new SignalSequence(_bus)
                .Then(new BenchmarkSignal { Value = 1 })
                .Include(inner1)
                .Then(new BenchmarkSignal { Value = 4 })
                .Include(inner2)
                .Then(new BenchmarkSignal { Value = 7 });

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                outer.Execute();
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[SignalSequence] Nested Sequences (7 signals, 2 nested) ({Iterations} executions):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per execution");

            Assert.AreEqual(Iterations * 7, counter);
            Assert.Less(sw.ElapsedMilliseconds, 100, "Nested sequence too slow");

            outer.Dispose();
            inner1.Dispose();
            inner2.Dispose();
        }

        [Test]
        public void Benchmark_SignalSequence_WithActions()
        {
            const int Iterations = 10000;
            int signalCounter = 0;
            int actionCounter = 0;

            _bus.RegisterSignalHandler<BenchmarkSignal>(s => signalCounter++);

            var sequence = new SignalSequence(_bus)
                .Then(() => actionCounter++)
                .Then(new BenchmarkSignal { Value = 1 })
                .Then(() => actionCounter++)
                .Then(new BenchmarkSignal { Value = 2 })
                .Then(() => actionCounter++);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                sequence.Execute();
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[SignalSequence] Mixed Signals+Actions ({Iterations} executions):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per execution");

            Assert.AreEqual(Iterations * 2, signalCounter);
            Assert.AreEqual(Iterations * 3, actionCounter);
            Assert.Less(sw.ElapsedMilliseconds, 50, "Mixed sequence too slow");

            sequence.Dispose();
        }

        [Test]
        public void Benchmark_SignalSequenceRegistry()
        {
            const int Iterations = 10000;
            int counter = 0;

            _bus.RegisterSignalHandler<BenchmarkSignal>(s => counter++);

            var registry = new SignalSequenceRegistry(_bus);
            registry.Create("spawn", seq => seq
                .Then(new BenchmarkSignal { Value = 1 })
                .Then(new BenchmarkSignal { Value = 2 })
                .Then(new BenchmarkSignal { Value = 3 }));

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                registry.Execute("spawn");
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[SignalSequenceRegistry] Named Sequence Execute ({Iterations} executions):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}us per execution");

            Assert.AreEqual(Iterations * 3, counter);
            Assert.Less(sw.ElapsedMilliseconds, 50, "Registry execution too slow");

            registry.Dispose();
        }

        [Test]
        public void Benchmark_DirectMethodCall_Baseline()
        {
            const int Iterations = 100000;
            int counter = 0;

            Action<int> handler = v => counter += v;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                handler(1);
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[Baseline] Direct Delegate Call ({Iterations} calls):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per call");

            Assert.AreEqual(Iterations, counter);
        }

        private struct BenchmarkSignal
        {
            public int Value;
        }

        private struct BenchmarkQuery : IQuery<int>
        {
            public int Input;
        }

        private struct BenchmarkEvent
        {
            public int Value;
        }

        // 64-byte struct for large signal benchmark
        private struct LargeSignal
        {
            public long A, B, C, D, E, F, G, H;
        }

        private struct SignalType1 { public int V; }
        private struct SignalType2 { public int V; }
        private struct SignalType3 { public int V; }
        private struct SignalType4 { public int V; }
    }
}
