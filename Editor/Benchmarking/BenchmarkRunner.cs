using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Strada.Core.Communication;
using Strada.Core.DI;
using Strada.Core.ECS;
using Strada.Core.ECS.Query;
using Strada.Core.ECS.World;

namespace Strada.Core.Editor.Benchmarking
{
    /// <summary>
    /// Executes predefined performance benchmarks for Strada framework components.
    /// Captures timing and memory metrics for DI resolution, ECS queries, and message dispatch.
    /// </summary>
    public class BenchmarkRunner
    {
        private readonly List<BenchmarkDefinition> _benchmarks = new List<BenchmarkDefinition>();
        private readonly Dictionary<string, BenchmarkThreshold> _thresholds = new Dictionary<string, BenchmarkThreshold>();

        public IReadOnlyList<BenchmarkDefinition> Benchmarks => _benchmarks;
        public event Action<BenchmarkResult> OnBenchmarkCompleted;
        public event Action<string> OnBenchmarkStarted;

        public BenchmarkRunner()
        {
            RegisterDefaultBenchmarks();
            RegisterDefaultThresholds();
        }

        private void RegisterDefaultBenchmarks()
        {
            _benchmarks.Add(new BenchmarkDefinition
            {
                Name = "DI_TransientResolve",
                Category = "DI Container",
                Description = "Measures transient service resolution performance",
                DefaultIterations = 10000,
                Execute = RunDITransientBenchmark
            });

            _benchmarks.Add(new BenchmarkDefinition
            {
                Name = "DI_SingletonResolve",
                Category = "DI Container",
                Description = "Measures singleton service resolution performance",
                DefaultIterations = 10000,
                Execute = RunDISingletonBenchmark
            });

            _benchmarks.Add(new BenchmarkDefinition
            {
                Name = "ECS_EntityCreation",
                Category = "ECS",
                Description = "Measures entity creation performance",
                DefaultIterations = 10000,
                Execute = RunECSEntityCreationBenchmark
            });

            _benchmarks.Add(new BenchmarkDefinition
            {
                Name = "ECS_ComponentAdd",
                Category = "ECS",
                Description = "Measures component addition performance",
                DefaultIterations = 10000,
                Execute = RunECSComponentAddBenchmark
            });

            _benchmarks.Add(new BenchmarkDefinition
            {
                Name = "ECS_ComponentQuery",
                Category = "ECS",
                Description = "Measures component query performance",
                DefaultIterations = 1000,
                Execute = RunECSQueryBenchmark
            });

            _benchmarks.Add(new BenchmarkDefinition
            {
                Name = "Bus_EventPublish",
                Category = "Message Bus",
                Description = "Measures event publishing performance",
                DefaultIterations = 10000,
                Execute = RunBusEventBenchmark
            });

            _benchmarks.Add(new BenchmarkDefinition
            {
                Name = "Bus_CommandDispatch",
                Category = "Message Bus",
                Description = "Measures command dispatch performance",
                DefaultIterations = 10000,
                Execute = RunBusCommandBenchmark
            });
        }

        private void RegisterDefaultThresholds()
        {
            _thresholds["DI_TransientResolve"] = new BenchmarkThreshold
            {
                BenchmarkName = "DI_TransientResolve",
                MinOpsPerSecond = 100000,
                MaxAverageTimeMs = 0.01
            };

            _thresholds["DI_SingletonResolve"] = new BenchmarkThreshold
            {
                BenchmarkName = "DI_SingletonResolve",
                MinOpsPerSecond = 500000,
                MaxAverageTimeMs = 0.002
            };

            _thresholds["ECS_EntityCreation"] = new BenchmarkThreshold
            {
                BenchmarkName = "ECS_EntityCreation",
                MinOpsPerSecond = 50000,
                MaxAverageTimeMs = 0.02
            };

            _thresholds["ECS_ComponentQuery"] = new BenchmarkThreshold
            {
                BenchmarkName = "ECS_ComponentQuery",
                MinOpsPerSecond = 10000,
                MaxAverageTimeMs = 0.1
            };

            _thresholds["Bus_EventPublish"] = new BenchmarkThreshold
            {
                BenchmarkName = "Bus_EventPublish",
                MinOpsPerSecond = 100000,
                MaxAverageTimeMs = 0.01
            };
        }

        public BenchmarkThreshold GetThreshold(string benchmarkName)
        {
            return _thresholds.TryGetValue(benchmarkName, out var threshold) ? threshold : null;
        }

        public void SetThreshold(string benchmarkName, BenchmarkThreshold threshold)
        {
            _thresholds[benchmarkName] = threshold;
        }

        /// <summary>
        /// Runs a single benchmark by name.
        /// </summary>
        public BenchmarkResult RunBenchmark(string name, int? iterations = null)
        {
            var benchmark = _benchmarks.FirstOrDefault(b => b.Name == name);
            if (benchmark == null)
            {
                return new BenchmarkResult
                {
                    Name = name,
                    Timestamp = DateTime.Now,
                    Passed = false,
                    ErrorMessage = $"Benchmark '{name}' not found"
                };
            }

            return RunBenchmark(benchmark, iterations ?? benchmark.DefaultIterations);
        }

        /// <summary>
        /// Runs a benchmark definition.
        /// </summary>
        public BenchmarkResult RunBenchmark(BenchmarkDefinition benchmark, int iterations)
        {
            OnBenchmarkStarted?.Invoke(benchmark.Name);

            try
            {
                var result = benchmark.Execute(iterations);

                if (_thresholds.TryGetValue(benchmark.Name, out var threshold))
                {
                    if (!threshold.CheckPassed(result))
                    {
                        result.Passed = false;
                        result.ErrorMessage = threshold.GetFailureReason(result);
                    }
                }

                OnBenchmarkCompleted?.Invoke(result);
                return result;
            }
            catch (Exception ex)
            {
                var result = new BenchmarkResult
                {
                    Name = benchmark.Name,
                    Category = benchmark.Category,
                    Timestamp = DateTime.Now,
                    Iterations = iterations,
                    Passed = false,
                    ErrorMessage = ex.Message
                };
                OnBenchmarkCompleted?.Invoke(result);
                return result;
            }
        }

        /// <summary>
        /// Runs all benchmarks.
        /// </summary>
        public List<BenchmarkResult> RunAllBenchmarks()
        {
            var results = new List<BenchmarkResult>();
            foreach (var benchmark in _benchmarks)
            {
                results.Add(RunBenchmark(benchmark, benchmark.DefaultIterations));
            }
            return results;
        }

        /// <summary>
        /// Runs benchmarks in a specific category.
        /// </summary>
        public List<BenchmarkResult> RunCategory(string category)
        {
            var results = new List<BenchmarkResult>();
            foreach (var benchmark in _benchmarks.Where(b => b.Category == category))
            {
                results.Add(RunBenchmark(benchmark, benchmark.DefaultIterations));
            }
            return results;
        }

        /// <summary>
        /// Gets all unique categories.
        /// </summary>
        public IEnumerable<string> GetCategories()
        {
            return _benchmarks.Select(b => b.Category).Distinct();
        }

        private BenchmarkResult RunDITransientBenchmark(int iterations)
        {
            return RunDIBenchmark("DI_TransientResolve", Lifetime.Transient, iterations);
        }

        private BenchmarkResult RunDISingletonBenchmark(int iterations)
        {
            return RunDIBenchmark("DI_SingletonResolve", Lifetime.Singleton, iterations);
        }

        /// <summary>
        /// Untimed iterations run before measurement starts, per Documentation~/Benchmarks.md.
        /// </summary>
        private const int WarmupIterations = 1000;

        /// <summary>
        /// Iterations per Stopwatch start/stop pair.
        /// </summary>
        private const int BatchSize = 64;

        private static int WarmupCountFor(int iterations)
        {
            return Math.Min(WarmupIterations, Math.Max(iterations, 0));
        }

        /// <summary>
        /// Warms up, settles the heap, then times <paramref name="operation"/> in batches and
        /// returns per-iteration timings in milliseconds.
        /// </summary>
        /// <param name="iterations">Number of measured iterations.</param>
        /// <param name="operation">
        /// The operation under test. It receives a monotonically increasing index that spans the
        /// warmup phase first (0..warmup-1) and then the measured phase, so operations that
        /// consume a distinct input per call can size their inputs with
        /// <see cref="WarmupCountFor"/> and index them directly.
        /// </param>
        /// <param name="memoryDelta">Bytes allocated across the measured loop only.</param>
        /// <remarks>
        /// Three things this fixes over timing each iteration in isolation with no warmup.
        /// First, the very first call pays JIT of the operation and, for the container, the
        /// one-off Expression.Compile of the resolution path - a millisecond-scale outlier that
        /// BenchmarkResult.Calculate folds into a plain untrimmed mean whose pass threshold is
        /// measured in microseconds. Second, Stopwatch start/stop plus the TimeSpan
        /// normalisation of <c>sw.Elapsed</c> cost tens of nanoseconds, the same order as the
        /// operations being measured, so the cost is amortised over a batch and divided back
        /// out. Third, the allocation baseline is taken here, after all caller setup, so a
        /// container or a pre-populated world is no longer counted as the operation's cost.
        /// </remarks>
        private static double[] Measure(int iterations, Action<int> operation, out long memoryDelta)
        {
            var timings = new double[Math.Max(iterations, 0)];
            if (iterations <= 0)
            {
                memoryDelta = 0;
                return timings;
            }

            var warmup = WarmupCountFor(iterations);
            for (int i = 0; i < warmup; i++)
            {
                operation(i);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long memoryBefore = GC.GetTotalMemory(true);

            var sw = new Stopwatch();
            int done = 0;
            while (done < iterations)
            {
                var batch = Math.Min(BatchSize, iterations - done);

                sw.Restart();
                for (int i = 0; i < batch; i++)
                {
                    operation(warmup + done + i);
                }
                sw.Stop();

                var perIteration = sw.Elapsed.TotalMilliseconds / batch;
                for (int i = 0; i < batch; i++)
                {
                    timings[done + i] = perIteration;
                }

                done += batch;
            }

            memoryDelta = GC.GetTotalMemory(false) - memoryBefore;
            return timings;
        }

        private BenchmarkResult RunDIBenchmark(string name, Lifetime lifetime, int iterations)
        {
            var container = new ContainerBuilder()
                .Register<ITestService, TestServiceImpl>(lifetime)
                .Build();

            var timings = Measure(iterations, _ => container.Resolve<ITestService>(), out var memoryDelta);

            container.Dispose();

            return BenchmarkResult.Calculate(
                name,
                "DI Container",
                timings,
                memoryDelta,
                iterations);
        }

        private BenchmarkResult RunECSEntityCreationBenchmark(int iterations)
        {
            var world = new ECSBuilder().Build();

            var timings = Measure(iterations, _ => world.CreateEntity(), out var memoryDelta);

            world.Dispose();

            return BenchmarkResult.Calculate(
                "ECS_EntityCreation",
                "ECS",
                timings,
                memoryDelta,
                iterations);
        }

        private BenchmarkResult RunECSComponentAddBenchmark(int iterations)
        {
            var world = new ECSBuilder().Build();

            // One entity per call including the warmup phase, so every measured call is a first
            // add on a fresh entity rather than an overwrite of one the warmup already touched.
            var entities = new Entity[WarmupCountFor(iterations) + Math.Max(iterations, 0)];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = world.CreateEntity();
            }

            var timings = Measure(
                iterations,
                i => world.AddComponent(entities[i], new TestComponent { Value = i }),
                out var memoryDelta);

            world.Dispose();

            return BenchmarkResult.Calculate(
                "ECS_ComponentAdd",
                "ECS",
                timings,
                memoryDelta,
                iterations);
        }

        private BenchmarkResult RunECSQueryBenchmark(int iterations)
        {
            var world = new ECSBuilder().Build();

            for (int i = 0; i < 1000; i++)
            {
                var entity = world.CreateEntity();
                world.AddComponent(entity, new TestComponent { Value = i });
            }

            // The counter and the delegate are hoisted out of the measured loop on purpose.
            // Declaring `count` inside the loop makes the lambda capture it, which costs a
            // display-class plus a delegate allocation per iteration - inside the timing window,
            // and counted against the query's allocation figure.
            int count = 0;
            QueryDelegate<TestComponent> countAction = (int entityIndex, ref TestComponent c) => count++;

            var timings = Measure(
                iterations,
                _ =>
                {
                    count = 0;
                    world.EntityManager.ForEach(countAction);
                },
                out var memoryDelta);

            world.Dispose();

            return BenchmarkResult.Calculate(
                "ECS_ComponentQuery",
                "ECS",
                timings,
                memoryDelta,
                iterations);
        }

        private BenchmarkResult RunBusEventBenchmark(int iterations)
        {
            var bus = new EventBus();
            int receivedCount = 0;
            bus.Subscribe<TestEvent>(e => receivedCount++);

            var testEvent = new TestEvent { Data = "test" };

            var timings = Measure(iterations, _ => bus.Publish(testEvent), out var memoryDelta);

            bus.Dispose();

            return BenchmarkResult.Calculate(
                "Bus_EventPublish",
                "Message Bus",
                timings,
                memoryDelta,
                iterations);
        }

        private BenchmarkResult RunBusCommandBenchmark(int iterations)
        {
            var bus = new EventBus();
            bus.RegisterSignalHandler<TestSignal>(cmd => { /* no-op handler */ });

            var testCommand = new TestSignal { Id = 1 };

            var timings = Measure(iterations, _ => bus.Send(testCommand), out var memoryDelta);

            bus.Dispose();

            return BenchmarkResult.Calculate(
                "Bus_CommandDispatch",
                "Message Bus",
                timings,
                memoryDelta,
                iterations);
        }

        private interface ITestService { }
        private class TestServiceImpl : ITestService { }

        private struct TestComponent : IComponent
        {
            public int Value;
        }

        private struct TestEvent
        {
            public string Data;
        }

        private struct TestSignal
        {
            public int Id;
        }
    }
}
