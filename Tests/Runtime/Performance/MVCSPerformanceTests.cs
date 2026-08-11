using System;
using System.Diagnostics;
using NUnit.Framework;
using Strada.Core.Sync;
using Strada.Core.Communication;
using Strada.Core.DI;
using Strada.Core.DI.Attributes;
using Strada.Core.Modules;
using Strada.Core.Patterns;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [TestFixture]
    [Category("Performance")]
    public class MVCSPerformanceTests
    {
        private IContainer _container;

        [SetUp]
        public void SetUp()
        {
            var builder = new ContainerBuilder();
            builder.Register<EventBus>(Lifetime.Singleton);
            // Backing registrations for the [Inject] members on BenchmarkService and
            // BenchmarkController. Without them InjectionProcessor has nothing to resolve and
            // the injection benchmarks below reduce to a dictionary lookup over empty lists.
            builder.Register<BenchmarkDependencyA>(Lifetime.Singleton);
            builder.Register<BenchmarkDependencyB>(Lifetime.Singleton);
            builder.Register<BenchmarkDependencyC>(Lifetime.Singleton);
            _container = builder.Build();
        }

        [TearDown]
        public void TearDown()
        {
            _container?.Dispose();
        }

        [Test]
        public void Benchmark_InjectionProcessor_10k_Injections()
        {
            const int Iterations = 10000;
            const int Warmup = 100;

            for (int i = 0; i < Warmup; i++)
            {
                var service = new BenchmarkService();
                InjectionProcessor.Inject(service, _container);
            }

            bool injected = true;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var service = new BenchmarkService();
                InjectionProcessor.Inject(service, _container);
                injected &= service.DependenciesInjected;
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] InjectionProcessor ({Iterations} injections):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}μs per injection");

            Assert.IsTrue(injected, "Every injection must fill all three [Inject] fields");
            // Raised from 100ms: BenchmarkService now carries three [Inject] fields, so each
            // iteration pays three container resolves plus three reflective field writes rather
            // than iterating three empty lists.
            Assert.Less(sw.ElapsedMilliseconds, 300, "Injection too slow (Target: <300ms for 10k x 3 fields)");
        }

        [Test]
        public void Benchmark_ReactiveProperty_100k_Updates()
        {
            const int Iterations = 100000;
            const int Warmup = 1000;

            var property = new ReactiveProperty<int>(0);
            int notifyCount = 0;
            property.Subscribe(_ => notifyCount++);

            for (int i = 0; i < Warmup; i++)
                property.Value = i;

            notifyCount = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                property.Value = i;
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] ReactiveProperty Updates ({Iterations} updates):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Notifications: {notifyCount}");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per update");

            Assert.Less(sw.ElapsedMilliseconds, 50, "ReactiveProperty updates too slow");
        }

        [Test]
        public void Benchmark_ReactiveProperty_MultipleSubscribers()
        {
            const int Subscribers = 10;
            const int Iterations = 10000;

            var property = new ReactiveProperty<int>(0);
            int[] counts = new int[Subscribers];

            for (int s = 0; s < Subscribers; s++)
            {
                int idx = s;
                property.Subscribe(_ => counts[idx]++);
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                property.Value = i;
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] ReactiveProperty with {Subscribers} subscribers ({Iterations} updates):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}μs per update (dispatching to {Subscribers} subscribers)");

            Assert.Less(sw.ElapsedMilliseconds, 100, "ReactiveProperty multi-subscriber too slow");
        }

        [Test]
        public void Benchmark_ContainerScope_10k_Resolutions()
        {
            const int Iterations = 10000;
            const int Warmup = 100;

            var builder = new ContainerBuilder();
            builder.Register<LocalService>(Lifetime.Singleton);
            var scopedContainer = builder.Build();

            for (int i = 0; i < Warmup; i++)
                scopedContainer.Resolve<LocalService>();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var svc = scopedContainer.Resolve<LocalService>();
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] Container Resolution ({Iterations} resolutions):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per resolution");

            scopedContainer.Dispose();

            Assert.Less(sw.ElapsedMilliseconds, 10, "Container resolution too slow");
        }

        [Test]
        public void Benchmark_ContainerScope_NestedScopes()
        {
            const int Depth = 10;
            const int Iterations = 10000;

            var builder = new ContainerBuilder();
            builder.Register<LocalService>(Lifetime.Scoped);
            var rootContainer = builder.Build();

            IContainerScope current = rootContainer.CreateScope();
            for (int d = 0; d < Depth - 1; d++)
            {
                current = current.CreateScope();
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var svc = current.Resolve<LocalService>();
            }
            sw.Stop();

            double avgNanoseconds = sw.Elapsed.TotalMilliseconds * 1000000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] Nested Scope ({Depth} levels, {Iterations} resolutions):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgNanoseconds:F0}ns per resolution");

            ((IDisposable)current).Dispose();
            rootContainer.Dispose();

            Assert.Less(sw.ElapsedMilliseconds, 20, "Nested scope resolution too slow");
        }

        [Test]
        public void Benchmark_Controller_Lifecycle()
        {
            const int Iterations = 1000;
            bool injected = true;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var controller = new BenchmarkController();
                InjectionProcessor.Inject(controller, _container);
                injected &= controller.DependenciesInjected;
                controller.Initialize();
                controller.Dispose();
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] Controller Full Lifecycle ({Iterations} cycles):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}μs per cycle (create+inject+init+dispose)");

            Assert.IsTrue(injected, "The inject step of the lifecycle must actually inject something");
            Assert.Less(sw.ElapsedMilliseconds, 500, "Controller lifecycle too slow");
        }

        [Test]
        public void Benchmark_Model_PropertyCreation()
        {
            const int Properties = 100;
            const int Iterations = 1000;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var model = new MultiPropertyModel(Properties);
                model.Initialize();
                model.Dispose();
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] Model with {Properties} Properties ({Iterations} cycles):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}μs per cycle");

            Assert.Less(sw.ElapsedMilliseconds, 1000, "Model property creation too slow");
        }

        [Test]
        public void Benchmark_ReactiveCollection_Operations()
        {
            const int Items = 10000;
            const int Warmup = 100;

            var collection = new ReactiveCollection<int>();
            int addCount = 0;
            int removeCount = 0;

            collection.OnAdd(_ => addCount++);
            collection.OnRemove(_ => removeCount++);

            for (int i = 0; i < Warmup; i++)
            {
                collection.Add(i);
                collection.Remove(i);
            }
            collection.Clear();
            addCount = 0;
            removeCount = 0;

            var swAdd = Stopwatch.StartNew();
            for (int i = 0; i < Items; i++)
            {
                collection.Add(i);
            }
            swAdd.Stop();

            var swRemove = Stopwatch.StartNew();
            for (int i = Items - 1; i >= 0; i--)
            {
                collection.Remove(i);
            }
            swRemove.Stop();

            double avgAddNs = swAdd.Elapsed.TotalMilliseconds * 1000000 / Items;
            double avgRemoveNs = swRemove.Elapsed.TotalMilliseconds * 1000000 / Items;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] ReactiveCollection ({Items} items):");
            UnityEngine.Debug.Log($"  Add: {swAdd.ElapsedMilliseconds}ms total, {avgAddNs:F0}ns avg");
            UnityEngine.Debug.Log($"  Remove: {swRemove.ElapsedMilliseconds}ms total, {avgRemoveNs:F0}ns avg");
            UnityEngine.Debug.Log($"  Add notifications: {addCount}, Remove notifications: {removeCount}");

            Assert.Less(swAdd.ElapsedMilliseconds, 50, "Collection add too slow");
            Assert.Less(swRemove.ElapsedMilliseconds, 100, "Collection remove too slow");
        }

        [Test]
        public void Benchmark_ModuleInstaller_Lifecycle()
        {
            const int Iterations = 1000;
            var builder = new ContainerBuilder();
            bool wired = true;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var installer = new BenchmarkModuleInstaller();
                installer.Install(builder);
                installer.Initialize(_container);
                wired &= installer.Bus != null && installer.DependencyA != null && installer.DependencyB != null;
                installer.Shutdown();
            }
            sw.Stop();

            double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / Iterations;

            UnityEngine.Debug.Log($"[MVCS BENCHMARK] Module Full Lifecycle ({Iterations} cycles):");
            UnityEngine.Debug.Log($"  Total Time: {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"  Avg: {avgMicroseconds:F2}μs per full lifecycle");

            Assert.IsTrue(wired, "Initialize must resolve the services Install registered");
            Assert.Less(sw.ElapsedMilliseconds, 200, "Module lifecycle too slow");
        }

        private class LocalService { }

        private class BenchmarkDependencyA { }
        private class BenchmarkDependencyB { }
        private class BenchmarkDependencyC { }

        // Carries real [Inject] members. With none, InjectionProcessor.Inject is a
        // ConcurrentDictionary lookup followed by three foreach loops over empty arrays, which
        // is what "10k injections" used to measure.
        private class BenchmarkService : Service
        {
            [Inject] private BenchmarkDependencyA _a = null;
            [Inject] private BenchmarkDependencyB _b = null;
            [Inject] private BenchmarkDependencyC _c = null;

            public bool DependenciesInjected => _a != null && _b != null && _c != null;

            protected override void OnInitialize() { }
        }

        private class BenchmarkController : Controller
        {
            [Inject] private EventBus _bus = null;
            [Inject] private BenchmarkDependencyA _a = null;

            public bool DependenciesInjected => _bus != null && _a != null;

            protected override void OnInitialize() { }
        }

        private class MultiPropertyModel : Model
        {
            private readonly int _propertyCount;
            private ReactiveProperty<int>[] _properties;

            public MultiPropertyModel(int propertyCount)
            {
                _propertyCount = propertyCount;
            }

            protected override void OnInitialize()
            {
                _properties = new ReactiveProperty<int>[_propertyCount];
                for (int i = 0; i < _propertyCount; i++)
                {
                    _properties[i] = CreateProperty(i);
                }
            }
        }

        // Three empty bodies made "Module Full Lifecycle" a measurement of 1,000 small
        // allocations and 3,000 no-op calls. An installer's actual cost is registration during
        // Install and resolution during Initialize, so it does both here.
        private class BenchmarkModuleInstaller : IModuleInstaller
        {
            public EventBus Bus;
            public BenchmarkDependencyA DependencyA;
            public BenchmarkDependencyB DependencyB;

            public void Install(IContainerBuilder builder)
            {
                builder.Register<BenchmarkDependencyA>(Lifetime.Singleton);
                builder.Register<BenchmarkDependencyB>(Lifetime.Singleton);
                builder.Register<BenchmarkDependencyC>(Lifetime.Singleton);
            }

            public void Initialize(IContainer container)
            {
                Bus = container.Resolve<EventBus>();
                DependencyA = container.Resolve<BenchmarkDependencyA>();
                DependencyB = container.Resolve<BenchmarkDependencyB>();
            }

            public void Shutdown()
            {
                Bus = null;
                DependencyA = null;
                DependencyB = null;
            }
        }
    }
}
