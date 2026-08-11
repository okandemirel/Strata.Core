using System;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;   // brings in the ConstraintExpression extension
using UnityConstraints = UnityEngine.TestTools.Constraints;
using Strada.Core.DI;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    public class SimpleService
    {
        public int Value = 42;
    }

    public class ServiceD { public int Value = 4; }
    public class ServiceC { public ServiceD D; public ServiceC(ServiceD d) => D = d; }
    public class ServiceB { public ServiceC C; public ServiceB(ServiceC c) => C = c; }
    public class ServiceA { public ServiceB B; public ServiceA(ServiceB b) => B = b; }

    public class DepOne { }
    public class DepTwo { }
    public class DepThree { }
    public class DepFour { }
    public class DepFive { }
    public class WideService
    {
        public DepOne One;
        public DepTwo Two;
        public DepThree Three;
        public DepFour Four;
        public DepFive Five;

        public WideService(DepOne one, DepTwo two, DepThree three, DepFour four, DepFive five)
        {
            One = one; Two = two; Three = three; Four = four; Five = five;
        }
    }

    // Ten tag types that combine into a thousand distinct closed BuildSvc<,,> types.
    // ContainerBuilder writes _registrations[typeof(T)] through a Dictionary indexer, so the
    // container-build benchmarks that registered ONE type N times ended up with a single entry:
    // Build() ran zero constructor analyses and zero Expression.Compile calls, and that was
    // published as the cost of building a 100- and a 1000-type container.
    public sealed class BuildTag0 { }
    public sealed class BuildTag1 { }
    public sealed class BuildTag2 { }
    public sealed class BuildTag3 { }
    public sealed class BuildTag4 { }
    public sealed class BuildTag5 { }
    public sealed class BuildTag6 { }
    public sealed class BuildTag7 { }
    public sealed class BuildTag8 { }
    public sealed class BuildTag9 { }

    /// <summary>
    /// One distinct service type per (TA, TB, TC) triple. The constructor dependency is what
    /// forces Build() through the path a real container takes per registration: constructor
    /// selection, a registration-map lookup for the parameter, and an expression tree compiled
    /// into a factory delegate.
    /// </summary>
    public sealed class BuildSvc<TA, TB, TC>
    {
        public BuildSvc(SimpleService dependency) { }
    }

    public interface IRepository { }
    public class Repository : IRepository { }
    public interface IDITestService { }
    public class DITestService : IDITestService
    {
        public IRepository Repo;
        public DITestService(IRepository repo) => Repo = repo;
    }

    [TestFixture]
    [Category("Performance")]
    public class DIPerformanceTests
    {
        private const int WarmupIterations = 100;
        private const int SmallIterations = 10_000;
        private const int LargeIterations = 100_000;

        [SetUp]
        public void Setup()
        {
            ClearAllDirectFactories();
        }

        [TearDown]
        public void TearDown()
        {
            ClearAllDirectFactories();
        }

        private void ClearAllDirectFactories()
        {
            DirectFactory<SimpleService>.Clear();
            DirectFactory<ServiceA>.Clear();
            DirectFactory<ServiceB>.Clear();
            DirectFactory<ServiceC>.Clear();
            DirectFactory<ServiceD>.Clear();
            DirectFactory<WideService>.Clear();
            DirectFactory<DepOne>.Clear();
            DirectFactory<DepTwo>.Clear();
            DirectFactory<DepThree>.Clear();
            DirectFactory<DepFour>.Clear();
            DirectFactory<DepFive>.Clear();
            DirectFactory<IRepository>.Clear();
            DirectFactory<Repository>.Clear();
            DirectFactory<IDITestService>.Clear();
            DirectFactory<DITestService>.Clear();
        }

        [Test]
        public void Benchmark_Simple_Transient_10k()
        {
            var builder = new ContainerBuilder();
            builder.Register<SimpleService>(Lifetime.Transient);
            using var container = builder.Build();

            for (int i = 0; i < WarmupIterations; i++)
                container.Resolve<SimpleService>();

            var instance = container.Resolve<SimpleService>();
            Assert.NotNull(instance);
            Assert.AreEqual(42, instance.Value);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
                container.Resolve<SimpleService>();
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: Simple Transient ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F3}μs");

            // README publishes 0.11μs for this resolve. A bound of 1.0μs tolerated a 9x
            // regression without complaint; the margin here still covers slower CI hardware.
            Assert.Less(usPerOp, 0.5, "Simple transient should resolve under 0.5μs (README: 0.11μs)");
        }

        [Test]
        public void Benchmark_DeepChain_Transient_10k()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Transient);
            builder.Register<ServiceC>(Lifetime.Transient);
            builder.Register<ServiceB>(Lifetime.Transient);
            builder.Register<ServiceA>(Lifetime.Transient);
            using var container = builder.Build();

            for (int i = 0; i < WarmupIterations; i++)
                container.Resolve<ServiceA>();

            var instance = container.Resolve<ServiceA>();
            Assert.NotNull(instance);
            Assert.NotNull(instance.B);
            Assert.NotNull(instance.B.C);
            Assert.NotNull(instance.B.C.D);
            Assert.AreEqual(4, instance.B.C.D.Value);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
                container.Resolve<ServiceA>();
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: 4-Level Deep Chain ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F3}μs");
            UnityEngine.Debug.Log($"  (Creates 4 objects per resolution)");

            Assert.Less(usPerOp, 5.0, "4-level deep transient should resolve under 5μs");
        }

        [Test]
        public void Benchmark_WideService_Transient_10k()
        {
            var builder = new ContainerBuilder();
            builder.Register<DepOne>(Lifetime.Transient);
            builder.Register<DepTwo>(Lifetime.Transient);
            builder.Register<DepThree>(Lifetime.Transient);
            builder.Register<DepFour>(Lifetime.Transient);
            builder.Register<DepFive>(Lifetime.Transient);
            builder.Register<WideService>(Lifetime.Transient);
            using var container = builder.Build();

            for (int i = 0; i < WarmupIterations; i++)
                container.Resolve<WideService>();

            var instance = container.Resolve<WideService>();
            Assert.NotNull(instance);
            Assert.NotNull(instance.One);
            Assert.NotNull(instance.Two);
            Assert.NotNull(instance.Three);
            Assert.NotNull(instance.Four);
            Assert.NotNull(instance.Five);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
                container.Resolve<WideService>();
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: Wide Service 5 Deps ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F3}μs");
            UnityEngine.Debug.Log($"  (Creates 6 objects per resolution)");

            Assert.Less(usPerOp, 5.0, "Wide service should resolve under 5μs");
        }

        [Test]
        public void Benchmark_Singleton_100k()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Singleton);
            builder.Register<ServiceC>(Lifetime.Singleton);
            builder.Register<ServiceB>(Lifetime.Singleton);
            builder.Register<ServiceA>(Lifetime.Singleton);
            using var container = builder.Build();

            for (int i = 0; i < WarmupIterations; i++)
                container.Resolve<ServiceA>();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < LargeIterations; i++)
                container.Resolve<ServiceA>();
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / LargeIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: Singleton Lookup ({LargeIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F4}μs ({usPerOp * 1000:F2}ns)");

            // README publishes 61ns. 500ns tolerated an 8x regression.
            Assert.Less(usPerOp, 0.25, "Singleton lookup should be under 0.25μs (250ns) (README: 61ns)");
        }

        [Test]
        public void Benchmark_Interface_Registration_10k()
        {
            var builder = new ContainerBuilder();
            builder.Register<IRepository, Repository>(Lifetime.Transient);
            builder.Register<IDITestService, DITestService>(Lifetime.Transient);
            using var container = builder.Build();

            for (int i = 0; i < WarmupIterations; i++)
                container.Resolve<IDITestService>();

            var instance = container.Resolve<IDITestService>();
            Assert.NotNull(instance);
            Assert.IsInstanceOf<DITestService>(instance);
            Assert.NotNull(((DITestService)instance).Repo);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
                container.Resolve<IDITestService>();
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: Interface Registration ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F3}μs");

            Assert.Less(usPerOp, 3.0, "Interface resolution should be under 3μs");
        }

        /// <summary>Registers ten distinct <c>BuildSvc&lt;TA, TB, ...&gt;</c> types.</summary>
        private static void RegisterCell<TA, TB>(ContainerBuilder builder)
        {
            builder.Register<BuildSvc<TA, TB, BuildTag0>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag1>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag2>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag3>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag4>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag5>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag6>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag7>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag8>>(Lifetime.Transient);
            builder.Register<BuildSvc<TA, TB, BuildTag9>>(Lifetime.Transient);
        }

        /// <summary>Registers a hundred distinct <c>BuildSvc&lt;TA, ...&gt;</c> types.</summary>
        private static void RegisterRow<TA>(ContainerBuilder builder)
        {
            RegisterCell<TA, BuildTag0>(builder);
            RegisterCell<TA, BuildTag1>(builder);
            RegisterCell<TA, BuildTag2>(builder);
            RegisterCell<TA, BuildTag3>(builder);
            RegisterCell<TA, BuildTag4>(builder);
            RegisterCell<TA, BuildTag5>(builder);
            RegisterCell<TA, BuildTag6>(builder);
            RegisterCell<TA, BuildTag7>(builder);
            RegisterCell<TA, BuildTag8>(builder);
            RegisterCell<TA, BuildTag9>(builder);
        }

        /// <summary>Registers a thousand distinct <c>BuildSvc&lt;...&gt;</c> types.</summary>
        private static void RegisterAll(ContainerBuilder builder)
        {
            RegisterRow<BuildTag0>(builder);
            RegisterRow<BuildTag1>(builder);
            RegisterRow<BuildTag2>(builder);
            RegisterRow<BuildTag3>(builder);
            RegisterRow<BuildTag4>(builder);
            RegisterRow<BuildTag5>(builder);
            RegisterRow<BuildTag6>(builder);
            RegisterRow<BuildTag7>(builder);
            RegisterRow<BuildTag8>(builder);
            RegisterRow<BuildTag9>(builder);
        }

        [Test]
        public void Benchmark_ContainerBuild_100Types()
        {
            const int TypeCount = 100;

            var sw = Stopwatch.StartNew();
            var builder = new ContainerBuilder();
            builder.Register<SimpleService>(Lifetime.Singleton);
            RegisterRow<BuildTag0>(builder);

            var container = builder.Build();
            sw.Stop();

            UnityEngine.Debug.Log($"=== STRADA DI: Container Build ({TypeCount} registrations) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-registration: {sw.Elapsed.TotalMilliseconds * 1000 / TypeCount:F2}μs");

            // Resolving proves the compiled factories are real, and keeps the whole build from
            // being dead code.
            Assert.NotNull(container.Resolve<BuildSvc<BuildTag0, BuildTag0, BuildTag0>>());
            Assert.NotNull(container.Resolve<BuildSvc<BuildTag0, BuildTag9, BuildTag9>>());
            container.Dispose();

            // Deliberately a smoke-level bound, not a regression gate: the previous 50ms guarded
            // a build that compiled nothing at all, so there is no measured baseline for the real
            // work yet. Tighten it once a per-machine baseline is recorded.
            Assert.Less(sw.Elapsed.TotalMilliseconds, 250, "100 distinct registrations should build under 250ms");
        }

        [Test]
        public void Benchmark_ContainerBuild_1000Types()
        {
            const int TypeCount = 1000;

            var sw = Stopwatch.StartNew();
            var builder = new ContainerBuilder();
            builder.Register<SimpleService>(Lifetime.Singleton);
            RegisterAll(builder);

            var container = builder.Build();
            sw.Stop();

            UnityEngine.Debug.Log($"=== STRADA DI: Container Build ({TypeCount} registrations) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-registration: {sw.Elapsed.TotalMilliseconds * 1000 / TypeCount:F2}μs");

            Assert.NotNull(container.Resolve<BuildSvc<BuildTag0, BuildTag0, BuildTag0>>());
            Assert.NotNull(container.Resolve<BuildSvc<BuildTag9, BuildTag9, BuildTag9>>());
            container.Dispose();

            // Ten times the work of the 100-type build, plus the one-time cost of the expression
            // compiler itself. Same caveat as above: a smoke bound, pending a real baseline.
            Assert.Less(sw.Elapsed.TotalMilliseconds, 2000, "1000 distinct registrations should build under 2s");
        }

        [Test]
        public void Benchmark_ScopedResolution_10k()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Scoped);
            builder.Register<ServiceC>(Lifetime.Scoped);
            builder.Register<ServiceB>(Lifetime.Scoped);
            builder.Register<ServiceA>(Lifetime.Scoped);
            using var container = builder.Build();
            using var scope = container.CreateScope();

            for (int i = 0; i < WarmupIterations; i++)
                scope.Resolve<ServiceA>();

            var first = scope.Resolve<ServiceA>();
            var second = scope.Resolve<ServiceA>();
            Assert.AreSame(first, second, "Scoped should return same instance");

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
                scope.Resolve<ServiceA>();
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: Scoped Resolution ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F4}μs ({usPerOp * 1000:F2}ns)");

            // README publishes 21ns. 500ns tolerated a 24x regression — the loosest threshold
            // in the DI suite guarding the tightest published number.
            Assert.Less(usPerOp, 0.2, "Scoped lookup should be under 0.2μs (200ns) (README: 21ns)");
        }

        [Test]
        public void Benchmark_ScopeCreation_1k()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Scoped);
            builder.Register<ServiceC>(Lifetime.Scoped);
            builder.Register<ServiceB>(Lifetime.Scoped);
            builder.Register<ServiceA>(Lifetime.Scoped);
            using var container = builder.Build();

            const int Iterations = 1000;

            for (int i = 0; i < 10; i++)
            {
                using var scope = container.CreateScope();
                scope.Resolve<ServiceA>();
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                using var scope = container.CreateScope();
                var _ = scope.Resolve<ServiceA>();
            }
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"=== STRADA DI: Scope Creation + Resolve ({Iterations:N0} cycles) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-cycle: {usPerOp:F2}μs");

            Assert.Less(usPerOp, 10, "Scope creation + resolve should be under 10μs");
        }

        [Test]
        public void Benchmark_MixedLifetimes_10k()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Singleton);
            builder.Register<ServiceC>(Lifetime.Transient);
            builder.Register<ServiceB>(Lifetime.Transient);
            builder.Register<ServiceA>(Lifetime.Transient);
            using var container = builder.Build();

            for (int i = 0; i < WarmupIterations; i++)
                container.Resolve<ServiceA>();

            var first = container.Resolve<ServiceA>();
            var second = container.Resolve<ServiceA>();
            Assert.AreNotSame(first, second, "Top should be transient");
            Assert.AreSame(first.B.C.D, second.B.C.D, "Bottom should be singleton");

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
                container.Resolve<ServiceA>();
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: Mixed Lifetimes ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F3}μs");
            UnityEngine.Debug.Log($"  (1 singleton lookup + 3 transient creates)");

            Assert.Less(usPerOp, 3.0, "Mixed lifetime should resolve under 3μs");
        }

        [Test]
        public void Benchmark_GCAllocation_Transient()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Transient);
            builder.Register<ServiceC>(Lifetime.Transient);
            builder.Register<ServiceB>(Lifetime.Transient);
            builder.Register<ServiceA>(Lifetime.Transient);
            using var container = builder.Build();

            // A transient resolve constructs a new object every call, so it MUST allocate.
            // Asserting that here keeps the measurement honest: if this ever reports
            // no-allocation, the instrument is broken rather than the code being fast.
            BenchmarkSink.Prime(() => BenchmarkSink.Consume(container.Resolve<ServiceA>()), WarmupIterations);

            Assert.That(() => BenchmarkSink.Consume(container.Resolve<ServiceA>()),
                UnityConstraints.Is.AllocatingGCMemory(),
                "A transient resolve constructs a new instance, so it must allocate. " +
                "If this fails, the allocation measurement is not working.");

            UnityEngine.Debug.Log("=== STRADA DI: Transient resolve allocates (as expected) ===");
        }

        [Test]
        public void Benchmark_GCAllocation_Singleton()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Singleton);
            builder.Register<ServiceC>(Lifetime.Singleton);
            builder.Register<ServiceB>(Lifetime.Singleton);
            builder.Register<ServiceA>(Lifetime.Singleton);
            using var container = builder.Build();

            BenchmarkSink.Prime(() => BenchmarkSink.Consume(container.Resolve<ServiceA>()));

            // This is the README's "GC Allocation (Singleton resolve): 0 bytes" claim, now
            // asserted with the only mechanism on this runtime that actually observes an
            // allocation.
            Assert.That(() => BenchmarkSink.Consume(container.Resolve<ServiceA>()),
                UnityConstraints.Is.Not.AllocatingGCMemory(),
                "README publishes 0 bytes for a singleton resolve.");

            UnityEngine.Debug.Log("=== STRADA DI: Singleton resolve is allocation-free (verified) ===");
        }

        [Test]
        public void Benchmark_AotPath_DirectFactory_Transient_10k()
        {
            // Every other number in this file is produced by Container.CompileFactory, i.e.
            // Expression.Lambda(...).Compile(). IL2CPP has no Reflection.Emit, so that path
            // degrades to an interpreted delegate there and these figures do not describe a
            // shipped player build. The AOT-safe route is the source-generated
            // DirectFactory<T> hook, which BuildFactories prefers whenever one is registered —
            // but nothing measured that route, and nothing asserted the container takes it.
            int constructed = 0;
            DirectFactory<SimpleService>.Register(_ =>
            {
                constructed++;
                return new SimpleService();
            });

            var builder = new ContainerBuilder();
            builder.Register<SimpleService>(Lifetime.Transient);
            using var container = builder.Build();

            var instance = container.Resolve<SimpleService>();
            Assert.NotNull(instance);
            Assert.AreEqual(42, instance.Value);
            Assert.AreEqual(1, constructed,
                "Container must prefer the source-generated DirectFactory over Expression.Compile");

            for (int i = 0; i < WarmupIterations; i++)
                BenchmarkSink.Consume(container.Resolve<SimpleService>());

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
                BenchmarkSink.Consume(container.Resolve<SimpleService>());
            sw.Stop();

            double usPerOp = sw.Elapsed.TotalMilliseconds * 1000 / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: AOT path, DirectFactory ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-op: {usPerOp:F3}μs");
            UnityEngine.Debug.Log($"  (This is the route an IL2CPP build takes; the other DI numbers here are Editor / Mono JIT.)");

            Assert.AreEqual(1 + WarmupIterations + SmallIterations, constructed,
                "Every resolve must go through the direct factory");
            Assert.Less(usPerOp, 1.0, "Direct factory transient should resolve under 1μs");
        }

        [Test]
        public void Benchmark_Comparison_ManualVsDI()
        {
            var builder = new ContainerBuilder();
            builder.Register<ServiceD>(Lifetime.Transient);
            builder.Register<ServiceC>(Lifetime.Transient);
            builder.Register<ServiceB>(Lifetime.Transient);
            builder.Register<ServiceA>(Lifetime.Transient);
            using var container = builder.Build();

            for (int i = 0; i < WarmupIterations; i++)
            {
                container.Resolve<ServiceA>();
                new ServiceA(new ServiceB(new ServiceC(new ServiceD())));
            }

            var swManual = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
            {
                var instance = new ServiceA(new ServiceB(new ServiceC(new ServiceD())));
            }
            swManual.Stop();

            var swDI = Stopwatch.StartNew();
            for (int i = 0; i < SmallIterations; i++)
            {
                var instance = container.Resolve<ServiceA>();
            }
            swDI.Stop();

            double manualUs = swManual.Elapsed.TotalMilliseconds * 1000 / SmallIterations;
            double diUs = swDI.Elapsed.TotalMilliseconds * 1000 / SmallIterations;
            double overhead = diUs / manualUs;

            UnityEngine.Debug.Log($"=== STRADA DI vs Manual Construction ({SmallIterations:N0} iterations) ===");
            UnityEngine.Debug.Log($"  Manual new(): {manualUs:F4}μs/op");
            UnityEngine.Debug.Log($"  DI Resolve(): {diUs:F4}μs/op");
            UnityEngine.Debug.Log($"  DI Overhead: {overhead:F2}x slower than manual");
            UnityEngine.Debug.Log($"  (Typical DI overhead is 2-10x)");

            // Both legs run on the same machine in the same run, so this ratio needs the least
            // slack of anything here. README publishes 1.56x; 20x tolerated a 12x regression.
            Assert.Less(overhead, 6.0, "DI overhead should be less than 6x manual construction (README: 1.56x)");
        }
    }
}
