using System;
using System.Collections.Generic;
using NUnit.Framework;
using Strada.Core.DI;

namespace Strada.Core.Tests.Tests.Runtime.DI
{
    [TestFixture]
    public class ContainerDisposalTests
    {
        private class Tracker
        {
            public List<string> DisposedOrder { get; } = new List<string>();
        }

        private class ServiceA : IDisposable
        {
            private readonly Tracker _tracker;
            public ServiceA(Tracker tracker) => _tracker = tracker;
            public void Dispose() => _tracker.DisposedOrder.Add("ServiceA");
        }

        private class ServiceB : IDisposable
        {
            private readonly Tracker _tracker;
            private readonly ServiceA _serviceA; // Depends on A
            public ServiceB(Tracker tracker, ServiceA serviceA) 
            {
                _tracker = tracker;
                _serviceA = serviceA;
            }
            public void Dispose() => _tracker.DisposedOrder.Add("ServiceB");
        }

        [Test]
        public void Dispose_RespectsDependencyOrder()
        {
            var builder = new ContainerBuilder();
            var tracker = new Tracker();
            
            builder.RegisterInstance(tracker);
            builder.Register<ServiceA>(Lifetime.Singleton);
            builder.Register<ServiceB>(Lifetime.Singleton);
            
            var container = builder.Build();
            var b = container.Resolve<ServiceB>(); 
            
            container.Dispose();
            
            Assert.AreEqual("ServiceB", tracker.DisposedOrder[0], "Dependents should be disposed before dependencies");
            Assert.AreEqual("ServiceA", tracker.DisposedOrder[1]);
        }
        private sealed class CountingDisposable : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        /// <summary>
        /// A RegisterInstance'd IDisposable was pushed onto the disposal stack twice: once at
        /// build time and again by the singleton wrapper on first resolve. Dispose() then ran
        /// twice, which corrupts any non-idempotent Dispose (native handles, pooled objects).
        /// </summary>
        [Test]
        public void Dispose_RegisteredInstance_IsDisposedExactlyOnce()
        {
            var instance = new CountingDisposable();

            var builder = new ContainerBuilder();
            builder.RegisterInstance(instance);
            var container = builder.Build();

            container.Resolve<CountingDisposable>();   // installs it into the singleton slot
            container.Dispose();

            Assert.AreEqual(1, instance.DisposeCount, "Registered instance must be disposed exactly once.");
        }

        /// <summary>
        /// TryResolve was the only resolve entry point with no disposed check, so it rebuilt
        /// singletons on a dead container and pushed them onto a stack that can never drain.
        /// </summary>
        [Test]
        public void TryResolve_AfterDispose_ReturnsFalseAndDoesNotConstruct()
        {
            var builder = new ContainerBuilder();
            builder.Register<CountingDisposable>(Lifetime.Singleton);
            var container = builder.Build();
            container.Dispose();

            Assert.IsFalse(container.TryResolve<CountingDisposable>(out var resolved),
                "TryResolve must fail on a disposed container.");
            Assert.IsNull(resolved);
        }
    }
}
