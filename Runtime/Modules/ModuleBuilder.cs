using System;
using Strada.Core.DI;

namespace Strada.Core.Modules
{
    /// <summary>
    /// Implementation of IModuleBuilder that wraps the DI ContainerBuilder.
    /// Provides a fluent, VContainer-like API for module configuration.
    /// </summary>
    public sealed class ModuleBuilder : IModuleBuilder
    {
        // ContainerBuilder exposes Register<T>(Lifetime) and Register<TInterface,TImplementation>(Lifetime).
        // Both have the identical parameter signature (Lifetime), so the arity-agnostic
        // GetMethod(name, Type[]) overload matches both and throws AmbiguousMatchException.
        // The generic-arity-aware overload is the only one that can tell them apart.
        private static readonly System.Reflection.MethodInfo SelfRegisterMethod =
            typeof(ContainerBuilder).GetMethod(nameof(ContainerBuilder.Register), 1, new[] { typeof(Lifetime) })
            ?? throw new InvalidOperationException(
                "ContainerBuilder.Register<T>(Lifetime) not found — Strada.Core is built against an incompatible ContainerBuilder.");

        private static readonly System.Reflection.MethodInfo PairRegisterMethod =
            typeof(ContainerBuilder).GetMethod(nameof(ContainerBuilder.Register), 2, new[] { typeof(Lifetime) })
            ?? throw new InvalidOperationException(
                "ContainerBuilder.Register<TInterface,TImplementation>(Lifetime) not found — Strada.Core is built against an incompatible ContainerBuilder.");

        private readonly ContainerBuilder _containerBuilder;

        /// <summary>
        /// Creates a new ModuleBuilder wrapping the given ContainerBuilder.
        /// </summary>
        /// <param name="containerBuilder">The underlying container builder.</param>
        public ModuleBuilder(ContainerBuilder containerBuilder)
        {
            _containerBuilder = containerBuilder ?? throw new ArgumentNullException(nameof(containerBuilder));
        }

        /// <inheritdoc/>
        public IModuleBuilder Register<TInterface, TImplementation>(Lifetime lifetime = Lifetime.Singleton)
            where TInterface : class
            where TImplementation : class, TInterface
        {
            _containerBuilder.Register<TInterface, TImplementation>(lifetime);
            return this;
        }

        /// <inheritdoc/>
        public IModuleBuilder Register<T>(Lifetime lifetime = Lifetime.Singleton) where T : class
        {
            _containerBuilder.Register<T>(lifetime);
            return this;
        }

        /// <inheritdoc/>
        public IModuleBuilder Register(Type interfaceType, Type implementationType, Lifetime lifetime = Lifetime.Singleton)
        {
            if (interfaceType == null || implementationType == null)
            {
                throw new ArgumentNullException(interfaceType == null ? nameof(interfaceType) : nameof(implementationType));
            }

            if (interfaceType == implementationType)
            {
                var genericMethod = SelfRegisterMethod.MakeGenericMethod(implementationType);
                genericMethod.Invoke(_containerBuilder, new object[] { lifetime });
            }
            else
            {
                var genericMethod = PairRegisterMethod.MakeGenericMethod(interfaceType, implementationType);
                genericMethod.Invoke(_containerBuilder, new object[] { lifetime });
            }

            return this;
        }

        /// <inheritdoc/>
        public IModuleBuilder RegisterInstance<T>(T instance) where T : class
        {
            _containerBuilder.RegisterInstance(instance);
            return this;
        }

        /// <inheritdoc/>
        public IModuleBuilder RegisterFactory<T>(Func<IServiceLocator, T> factory, Lifetime lifetime = Lifetime.Singleton)
            where T : class
        {
            // The locator only wraps the container, which never changes, so build it once per
            // registration instead of allocating a new one on every single resolve.
            IServiceLocator cached = null;
            _containerBuilder.RegisterFactory<T>(container =>
                factory(cached ??= new ServiceLocator(container)), lifetime);
            return this;
        }

        /// <inheritdoc/>
        public IModuleBuilder RegisterModel<TInterface, TImplementation>()
            where TInterface : class
            where TImplementation : class, TInterface
        {
            return Register<TInterface, TImplementation>(Lifetime.Singleton);
        }

        /// <inheritdoc/>
        public IModuleBuilder RegisterController<T>() where T : class
        {
            return Register<T>(Lifetime.Singleton);
        }

        /// <inheritdoc/>
        public IModuleBuilder RegisterService<TInterface, TImplementation>()
            where TInterface : class
            where TImplementation : class, TInterface
        {
            return Register<TInterface, TImplementation>(Lifetime.Singleton);
        }

        /// <inheritdoc/>
        public IModuleBuilder RegisterFactory<TInterface, TImplementation>()
            where TInterface : class
            where TImplementation : class, TInterface
        {
            return Register<TInterface, TImplementation>(Lifetime.Singleton);
        }
    }
}
