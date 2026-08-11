using NUnit.Framework;
using Strada.Core.DI;
using Unity.PerformanceTesting;

namespace Strada.Core.Tests.Tests.Runtime.DI
{
    [TestFixture]
    public class ContainerPerformanceTests
    {
        public interface IServiceA { }
        public interface IServiceB { }
        public interface IServiceC { }
        public interface IServiceD { }

        public class ServiceA : IServiceA { }
        public class ServiceB : IServiceB
        {
            public ServiceB(IServiceA a) { }
        }
        public class ServiceC : IServiceC
        {
            public ServiceC(IServiceA a, IServiceB b) { }
        }
        public class ServiceD : IServiceD
        {
            public ServiceD(IServiceA a, IServiceB b, IServiceC c) { }
        }

        // Ten tag types that combine into a hundred distinct closed Svc<,> types.
        // ContainerBuilder keys _registrations by typeof(TInterface) — a plain Dictionary
        // indexer write — so registering one type a hundred times left exactly ONE entry behind.
        // Build() then compiled a single factory and the result was published as the cost of a
        // hundred registrations.
        public sealed class Tag0 { }
        public sealed class Tag1 { }
        public sealed class Tag2 { }
        public sealed class Tag3 { }
        public sealed class Tag4 { }
        public sealed class Tag5 { }
        public sealed class Tag6 { }
        public sealed class Tag7 { }
        public sealed class Tag8 { }
        public sealed class Tag9 { }

        /// <summary>
        /// One distinct service type per (TA, TB) pair. The constructor dependency is what makes
        /// Build() do the work a real container does per registration: pick a constructor, look
        /// the parameter up in the registration map and compile an expression tree for it.
        /// </summary>
        public sealed class Svc<TA, TB>
        {
            public Svc(IServiceA a) { }
        }

        /// <summary>Registers ten distinct <c>Svc&lt;TA, ...&gt;</c> types.</summary>
        private static void RegisterRow<TA>(ContainerBuilder builder)
        {
            builder.Register<Svc<TA, Tag0>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag1>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag2>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag3>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag4>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag5>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag6>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag7>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag8>>(Lifetime.Singleton);
            builder.Register<Svc<TA, Tag9>>(Lifetime.Singleton);
        }

        [Test, Performance]
        public void Benchmark_SingleResolution_Transient_1000()
        {
            var builder = new ContainerBuilder();
            builder.Register<IServiceA, ServiceA>(Lifetime.Transient);
            var container = builder.Build();

            Measure.Method(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var service = container.Resolve<IServiceA>();
                }
            })
            .WarmupCount(10)
            .MeasurementCount(100)
            .IterationsPerMeasurement(5)
            .GC()
            .Run();
        }

        [Test, Performance]
        public void Benchmark_SingleResolution_Singleton_1000()
        {
            var builder = new ContainerBuilder();
            builder.Register<IServiceA, ServiceA>(Lifetime.Singleton);
            var container = builder.Build();

            Measure.Method(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var service = container.Resolve<IServiceA>();
                }
            })
            .WarmupCount(10)
            .MeasurementCount(100)
            .IterationsPerMeasurement(5)
            .GC()
            .Run();
        }

        [Test, Performance]
        public void Benchmark_ComplexDependencyGraph_10000()
        {
            var builder = new ContainerBuilder();
            builder.Register<IServiceA, ServiceA>(Lifetime.Singleton);
            builder.Register<IServiceB, ServiceB>(Lifetime.Singleton);
            builder.Register<IServiceC, ServiceC>(Lifetime.Singleton);
            builder.Register<IServiceD, ServiceD>(Lifetime.Transient);
            var container = builder.Build();

            Measure.Method(() =>
            {
                for (int i = 0; i < 10000; i++)
                {
                    var service = container.Resolve<IServiceD>();
                }
            })
            .WarmupCount(10)
            .MeasurementCount(50)
            .IterationsPerMeasurement(3)
            .GC()
            .Run();
        }

        [Test, Performance]
        public void Benchmark_MemoryAllocation_Transient_1000()
        {
            var builder = new ContainerBuilder();
            builder.Register<IServiceA, ServiceA>(Lifetime.Transient);
            var container = builder.Build();

            Measure.Method(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var service = container.Resolve<IServiceA>();
                }
            })
            .WarmupCount(5)
            .MeasurementCount(20)
            .IterationsPerMeasurement(1)
            .GC()
            .Run();
        }

        [Test, Performance]
        public void Benchmark_MemoryAllocation_Singleton_1000()
        {
            var builder = new ContainerBuilder();
            builder.Register<IServiceA, ServiceA>(Lifetime.Singleton);
            var container = builder.Build();

            Measure.Method(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var service = container.Resolve<IServiceA>();
                }
            })
            .WarmupCount(5)
            .MeasurementCount(20)
            .IterationsPerMeasurement(1)
            .GC()
            .Run();
        }

        [Test, Performance]
        public void Benchmark_ContainerBuild_100Registrations()
        {
            Measure.Method(() =>
            {
                var builder = new ContainerBuilder();
                builder.Register<IServiceA, ServiceA>(Lifetime.Singleton);

                // 10 rows x 10 columns = 100 distinct service types, so Build() performs 100
                // constructor analyses and 100 Expression.Lambda(...).Compile() calls.
                RegisterRow<Tag0>(builder);
                RegisterRow<Tag1>(builder);
                RegisterRow<Tag2>(builder);
                RegisterRow<Tag3>(builder);
                RegisterRow<Tag4>(builder);
                RegisterRow<Tag5>(builder);
                RegisterRow<Tag6>(builder);
                RegisterRow<Tag7>(builder);
                RegisterRow<Tag8>(builder);
                RegisterRow<Tag9>(builder);

                var container = builder.Build();
            })
            .WarmupCount(5)
            .MeasurementCount(20)
            .IterationsPerMeasurement(1)
            .GC()
            .Run();

            // The same registrations again, outside the measured region: this pins that the
            // hundred calls really leave a hundred resolvable entries behind rather than
            // overwriting one.
            var verifyBuilder = new ContainerBuilder();
            verifyBuilder.Register<IServiceA, ServiceA>(Lifetime.Singleton);
            RegisterRow<Tag0>(verifyBuilder);
            RegisterRow<Tag1>(verifyBuilder);
            RegisterRow<Tag2>(verifyBuilder);
            RegisterRow<Tag3>(verifyBuilder);
            RegisterRow<Tag4>(verifyBuilder);
            RegisterRow<Tag5>(verifyBuilder);
            RegisterRow<Tag6>(verifyBuilder);
            RegisterRow<Tag7>(verifyBuilder);
            RegisterRow<Tag8>(verifyBuilder);
            RegisterRow<Tag9>(verifyBuilder);

            using var verifyContainer = verifyBuilder.Build();
            Assert.NotNull(verifyContainer.Resolve<Svc<Tag0, Tag0>>());
            Assert.NotNull(verifyContainer.Resolve<Svc<Tag9, Tag9>>());
        }

        [Test, Performance]
        public void Benchmark_ScopeCreation_1000()
        {
            var builder = new ContainerBuilder();
            builder.Register<IServiceA, ServiceA>(Lifetime.Scoped);
            var container = builder.Build();

            Measure.Method(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    using var scope = container.CreateScope();
                    var service = scope.Resolve<IServiceA>();
                }
            })
            .WarmupCount(10)
            .MeasurementCount(50)
            .IterationsPerMeasurement(3)
            .GC()
            .Run();
        }
    }
}
