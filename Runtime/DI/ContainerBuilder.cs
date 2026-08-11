using System;
using System.Collections.Generic;
using System.Reflection;

namespace Strada.Core.DI
{
    public sealed class ContainerBuilder : IContainerBuilder
    {
        private readonly Dictionary<Type, Registration> _registrations = new();

        /// <summary>
        /// Registers a service with an interface and implementation type.
        /// </summary>
        /// <typeparam name="TInterface">The interface type.</typeparam>
        /// <typeparam name="TImplementation">The implementation type.</typeparam>
        /// <param name="lifetime">The lifetime of the service (Singleton or Transient).</param>
        public ContainerBuilder Register<TInterface, TImplementation>(Lifetime lifetime = Lifetime.Singleton)
            where TInterface : class
            where TImplementation : class, TInterface
        {
            ValidateType(typeof(TImplementation));
            _registrations[typeof(TInterface)] = Registration.FromType(
                typeof(TInterface), typeof(TImplementation), lifetime);
            return this;
        }

        /// <summary>
        /// Registers a concrete type as itself.
        /// </summary>
        /// <typeparam name="T">The concrete type.</typeparam>
        /// <param name="lifetime">The lifetime of the service.</param>
        public ContainerBuilder Register<T>(Lifetime lifetime = Lifetime.Singleton) where T : class
        {
            ValidateType(typeof(T));
            _registrations[typeof(T)] = Registration.FromType(typeof(T), typeof(T), lifetime);
            return this;
        }

        /// <summary>
        /// Registers a service using a factory delegate.
        /// </summary>
        /// <typeparam name="T">The service type.</typeparam>
        /// <param name="factory">The factory function.</param>
        /// <param name="lifetime">The lifetime of the service.</param>
        public ContainerBuilder RegisterFactory<T>(Func<IContainer, T> factory, Lifetime lifetime = Lifetime.Transient)
            where T : class
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            _registrations[typeof(T)] = Registration.FromFactory(typeof(T), container => factory(container), lifetime);
            return this;
        }

        /// <summary>
        /// Registers an existing instance as a singleton.
        /// </summary>
        /// <typeparam name="T">The service type.</typeparam>
        /// <param name="instance">The instance to register.</param>
        public ContainerBuilder RegisterInstance<T>(T instance) where T : class
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            _registrations[typeof(T)] = Registration.FromInstance(typeof(T), instance);
            return this;
        }

        /// <summary>
        /// Builds the container and validates dependencies.
        /// </summary>
        /// <returns>The built IContainer.</returns>
        /// <exception cref="InvalidOperationException">Thrown if circular dependencies are detected.</exception>
        public IContainer Build()
        {
            DetectCircularDependencies();
            DetectCaptiveDependencies();
            var container = new Container(_registrations, autoRegisterSelf: true);
            return container;
        }

        /// <summary>
        /// Rejects Singleton -> Scoped constructor edges before they become a confusing runtime failure.
        /// </summary>
        /// <remarks>
        /// A singleton is built by the root container, which has no scope, so the scoped dependency
        /// resolves through the root's throwing factory and the caller sees "Cannot resolve scoped
        /// type from root container. Use CreateScope() first." on a type they resolved from a scope.
        /// The graph can never resolve, so failing at Build() with the real reason is strictly better.
        /// </remarks>
        private void DetectCaptiveDependencies()
        {
            foreach (var kvp in _registrations)
            {
                var registration = kvp.Value;

                if (registration.Lifetime != Lifetime.Singleton ||
                    registration.Factory != null ||
                    registration.Instance != null)
                    continue;

                var constructor = Container.GetBestConstructor(registration.ImplementationType);

                foreach (var param in constructor.GetParameters())
                {
                    if (_registrations.TryGetValue(param.ParameterType, out var depReg) &&
                        depReg.Lifetime == Lifetime.Scoped)
                    {
                        throw new InvalidOperationException(
                            $"Captive dependency: Singleton '{kvp.Key.Name}' cannot depend on Scoped " +
                            $"'{param.ParameterType.Name}'. Register the dependent as Scoped, or the dependency as Singleton.");
                    }
                }
            }
        }

        private static void ValidateType(Type type)
        {
            if (type.IsAbstract || type.IsInterface)
                throw new ArgumentException($"Cannot register abstract type or interface '{type.Name}'");
        }

        private void DetectCircularDependencies()
        {
            var visiting = new HashSet<Type>();
            var visited = new HashSet<Type>();

            foreach (var kvp in _registrations)
            {
                var registration = kvp.Value;

                if (registration.Factory != null || registration.Instance != null)
                    continue;

                if (!visited.Contains(kvp.Key))
                {
                    DetectCycle(kvp.Key, registration.ImplementationType, visiting, visited);
                }
            }
        }

        private void DetectCycle(Type serviceType, Type implType, HashSet<Type> visiting, HashSet<Type> visited)
        {
            if (visited.Contains(serviceType))
                return;

            if (visiting.Contains(serviceType))
                throw new InvalidOperationException($"Circular dependency detected involving type '{serviceType.Name}'");

            visiting.Add(serviceType);

            var constructor = Container.GetBestConstructor(implType);
            var parameters = constructor.GetParameters();

            foreach (var param in parameters)
            {
                var paramType = param.ParameterType;

                if (_registrations.TryGetValue(paramType, out var depReg) &&
                    depReg.Factory == null &&
                    depReg.Instance == null)
                {
                    DetectCycle(paramType, depReg.ImplementationType, visiting, visited);
                }
            }

            visiting.Remove(serviceType);
            visited.Add(serviceType);
        }

        IContainerBuilder IContainerBuilder.Register<TInterface, TImplementation>(Lifetime lifetime)
        {
            return Register<TInterface, TImplementation>(lifetime);
        }

        IContainerBuilder IContainerBuilder.Register<T>(Lifetime lifetime)
        {
            return Register<T>(lifetime);
        }

        IContainerBuilder IContainerBuilder.RegisterFactory<T>(Func<IContainer, T> factory, Lifetime lifetime)
        {
            return RegisterFactory(factory, lifetime);
        }

        IContainerBuilder IContainerBuilder.RegisterInstance<T>(T instance)
        {
            return RegisterInstance(instance);
        }
    }
}
