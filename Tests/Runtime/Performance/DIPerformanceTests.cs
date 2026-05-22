using System;
using System.Diagnostics;
using NUnit.Framework;
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

            Assert.Less(usPerOp, 1.0, "Simple transient should resolve under 1μs");
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

            Assert.Less(usPerOp, 0.5, "Singleton lookup should be under 0.5μs (500ns)");
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

        [Test]
        public void Benchmark_ContainerBuild_100Types()
        {
            const int TypeCount = 100;

            var sw = Stopwatch.StartNew();
            var builder = new ContainerBuilder();

            for (int i = 0; i < TypeCount; i++)
            {
                builder.RegisterFactory<SimpleService>(_ => new SimpleService());
            }

            var container = builder.Build();
            sw.Stop();
            container.Dispose();

            UnityEngine.Debug.Log($"=== STRADA DI: Container Build ({TypeCount} registrations) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-registration: {sw.Elapsed.TotalMilliseconds * 1000 / TypeCount:F2}μs");

            Assert.Less(sw.ElapsedMilliseconds, 50, "100 registrations should build under 50ms");
        }

        [Test]
        public void Benchmark_ContainerBuild_1000Types()
        {
            const int TypeCount = 1000;

            var sw = Stopwatch.StartNew();
            var builder = new ContainerBuilder();

            for (int i = 0; i < TypeCount; i++)
            {
                builder.RegisterFactory<SimpleService>(_ => new SimpleService());
            }

            var container = builder.Build();
            sw.Stop();
            container.Dispose();

            UnityEngine.Debug.Log($"=== STRADA DI: Container Build ({TypeCount} registrations) ===");
            UnityEngine.Debug.Log($"  Total: {sw.Elapsed.TotalMilliseconds:F2}ms");
            UnityEngine.Debug.Log($"  Per-registration: {sw.Elapsed.TotalMilliseconds * 1000 / TypeCount:F2}μs");

            Assert.Less(sw.ElapsedMilliseconds, 200, "1000 registrations should build under 200ms");
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

            Assert.Less(usPerOp, 0.5, "Scoped lookup should be under 0.5μs");
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

            for (int i = 0; i < WarmupIterations; i++)
                container.Resolve<ServiceA>();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long memBefore = GC.GetTotalMemory(true);

            for (int i = 0; i < SmallIterations; i++)
                container.Resolve<ServiceA>();

            long memAfter = GC.GetTotalMemory(true);
            long allocated = memAfter - memBefore;
            double bytesPerOp = (double)allocated / SmallIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: GC Allocation ({SmallIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total allocated: {allocated / 1024.0:F2}KB");
            UnityEngine.Debug.Log($"  Per-op: {bytesPerOp:F1} bytes");
            UnityEngine.Debug.Log($"  (Expected: ~96 bytes for 4 objects)");

            Assert.Less(bytesPerOp, 200, "Should allocate less than 200 bytes per resolution");
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

            container.Resolve<ServiceA>();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long memBefore = GC.GetTotalMemory(true);

            for (int i = 0; i < LargeIterations; i++)
                container.Resolve<ServiceA>();

            long memAfter = GC.GetTotalMemory(true);
            long allocated = memAfter - memBefore;
            double bytesPerOp = (double)allocated / LargeIterations;

            UnityEngine.Debug.Log($"=== STRADA DI: GC Allocation Singleton ({LargeIterations:N0} resolutions) ===");
            UnityEngine.Debug.Log($"  Total allocated: {allocated / 1024.0:F2}KB");
            UnityEngine.Debug.Log($"  Per-op: {bytesPerOp:F2} bytes");
            UnityEngine.Debug.Log($"  (Expected: ~0 bytes - cached lookup)");

            Assert.Less(bytesPerOp, 1, "Singleton should allocate near-zero per resolution");
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

            Assert.Less(overhead, 20, "DI overhead should be less than 20x");
        }
    }
}
