using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Strada.Core.Logging;

namespace Strada.Core.DI
{
    /// <summary>
    /// High-performance dependency injection container with support for Singleton, Transient, and Scoped lifetimes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Container instances should be created using <see cref="ContainerBuilder"/> and typically live for the
    /// duration of the application. The container is thread-safe for resolution operations.
    /// </para>
    /// <para>
    /// Singleton resolution uses lock-free patterns with Interlocked.CompareExchange for optimal performance.
    /// Scoped services require creating a scope via <see cref="CreateScope"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new ContainerBuilder();
    /// builder.Register&lt;IMyService, MyService&gt;(Lifetime.Singleton);
    /// using var container = builder.Build();
    /// var service = container.Resolve&lt;IMyService&gt;();
    /// </code>
    /// </example>
    public sealed class Container : IContainer, IIndexResolver
    {
        private readonly Stack<IDisposable> _disposalStack = new Stack<IDisposable>();
        private readonly object _lock = new object();
        private readonly Func<IIndexResolver, object>[] _factories;
        private readonly Func<IIndexResolver, object>[] _scopedFactories;
        private readonly object[] _singletons;
        private readonly Lifetime[] _lifetimes;
        private readonly int[] _typeIdToIndex;
        private readonly int _maxTypeId;
        private readonly Type[] _registeredTypes;
        private int _registeredCount;
        private volatile bool _disposed;

        // Scopes handed out by CreateScope. Held weakly so a scope the caller forgot to dispose
        // does not keep its object graph alive for the container's lifetime; entries are removed
        // eagerly when the scope disposes itself, so the list stays bounded by live scopes.
        private readonly List<WeakReference<ContainerScope>> _scopes = new List<WeakReference<ContainerScope>>();

        // Factory delegates currently executing on this thread. A factory delegate is opaque to
        // ContainerBuilder's build-time cycle detection, so a cycle between two factory
        // registrations would otherwise recurse until the stack is exhausted — and
        // StackOverflowException cannot be caught, it kills the process with no Unity log entry.
        [ThreadStatic] private static List<object> t_activeFactories;

        internal Container(Dictionary<Type, Registration> registrations, bool autoRegisterSelf = false)
        {
            if (autoRegisterSelf && !registrations.ContainsKey(typeof(IContainer)))
            {
                registrations = new Dictionary<Type, Registration>(registrations);
                registrations[typeof(IContainer)] = Registration.FromInstance(typeof(IContainer), this);
            }

            var count = registrations.Count;
            _registeredTypes = new Type[count];
            _factories = new Func<IIndexResolver, object>[count];
            _scopedFactories = new Func<IIndexResolver, object>[count];
            _singletons = new object[count];
            _lifetimes = new Lifetime[count];

            var typeIdMap = BuildTypeIdMap(registrations, out _maxTypeId);
            _typeIdToIndex = BuildIndexArray(_maxTypeId, typeIdMap);
            BuildFactories(registrations, typeIdMap);
        }

        /// <summary>
        /// Resolves an instance of the specified service type.
        /// </summary>
        /// <typeparam name="T">The service type to resolve.</typeparam>
        /// <returns>An instance of the requested service type.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the type is not registered.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the container has been disposed.</exception>
        /// <remarks>
        /// This method is optimized for high-frequency calls. Singleton resolution uses lock-free patterns.
        /// For Scoped services, use <see cref="CreateScope"/> to create a scope first.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Resolve<T>() where T : class
        {
            if (_disposed) ThrowDisposed();
            var typeId = TypeId<T>.Id;
            if (typeId <= _maxTypeId)
            {
                var index = _typeIdToIndex[typeId];
                if (index >= 0)
                    return (T)_factories[index](this);
            }
            ThrowNotRegistered<T>();
            return default;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotRegistered<T>() =>
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowDisposed() =>
            throw new ObjectDisposedException(nameof(Container));

        public object Resolve(Type type)
        {
            if (_disposed) ThrowDisposed();
            return ResolveByType(type);
        }

        /// <summary>
        /// Attempts to resolve an instance of the specified service type without throwing an exception.
        /// </summary>
        /// <typeparam name="T">The service type to resolve.</typeparam>
        /// <param name="instance">When this method returns, contains the resolved instance if successful; otherwise, null.</param>
        /// <returns>true if the service was successfully resolved; otherwise, false.</returns>
        public bool TryResolve<T>(out T instance) where T : class
        {
            if (_disposed)
            {
                instance = null;
                return false;
            }

            var typeId = TypeId<T>.Id;
            if (typeId <= _maxTypeId)
            {
                var index = _typeIdToIndex[typeId];
                if (index >= 0)
                {
                    // A Scoped registration resolves to a throwing factory on the root container.
                    // The try-pattern contract is to report failure, not to propagate that.
                    if (_lifetimes[index] == Lifetime.Scoped)
                    {
                        instance = null;
                        return false;
                    }

                    // No lock: the singleton wrapper installed by BuildFactories publishes with
                    // Interlocked.CompareExchange, so it is already safe. Taking _lock here would
                    // additionally hold it across arbitrary user constructor code.
                    instance = (T)_factories[index](this);
                    return true;
                }
            }
            instance = null;
            return false;
        }

        /// <summary>
        /// Creates a new scope for resolving scoped services.
        /// </summary>
        /// <returns>A new container scope that should be disposed when no longer needed.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the container has been disposed.</exception>
        /// <remarks>
        /// Scoped services will return the same instance within a single scope but different instances
        /// across different scopes. The scope must be disposed to properly clean up scoped instances.
        /// </remarks>
        public IContainerScope CreateScope()
        {
            if (_disposed) ThrowDisposed();
            var scope = new ContainerScope(this, _factories, _scopedFactories, _lifetimes, _typeIdToIndex, _maxTypeId, _singletons);
            var handle = new WeakReference<ContainerScope>(scope);
            scope.AttachOwnerHandle(handle);
            lock (_lock) _scopes.Add(handle);
            return scope;
        }

        // Called by ContainerScope.Dispose so a long-lived container does not accumulate one dead
        // WeakReference per scope it ever created.
        internal void UnregisterScope(WeakReference<ContainerScope> handle)
        {
            if (handle == null) return;
            lock (_lock) _scopes.Remove(handle);
        }

        private void DisposeOutstandingScopes()
        {
            WeakReference<ContainerScope>[] handles;
            lock (_lock)
            {
                if (_scopes.Count == 0) return;
                handles = _scopes.ToArray();
                _scopes.Clear();
            }

            // Scoped instances are released before the singletons they depend on, and outside
            // _lock because ContainerScope.Dispose runs arbitrary user Dispose() code.
            for (int i = 0; i < handles.Length; i++)
            {
                if (!handles[i].TryGetTarget(out var scope)) continue;
                try
                {
                    scope.Dispose();
                }
                catch (Exception e)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    UnityEngine.Debug.LogError($"Error disposing scope: {e}");
#else
                    UnityEngine.Debug.LogError($"Error disposing scope: {e.Message}");
#endif
                }
            }
        }

        /// <summary>
        /// Checks whether a service type is registered in the container.
        /// </summary>
        /// <typeparam name="T">The service type to check.</typeparam>
        /// <returns>true if the type is registered; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsRegistered<T>() where T : class
        {
            var typeId = TypeId<T>.Id;
            return typeId <= _maxTypeId && _typeIdToIndex[typeId] >= 0;
        }

        /// <summary>
        /// Checks whether a service type is registered in the container using a Type parameter.
        /// </summary>
        /// <param name="type">The service type to check.</param>
        /// <returns>true if the type is registered; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsRegistered(Type type)
        {
            var typeId = TypeRegistry.GetId(type);
            return typeId <= _maxTypeId && _typeIdToIndex[typeId] >= 0;
        }

        public void Dispose()
        {
            if (_disposed) return;

            IDisposable[] pending;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                // Drain the stack into a local snapshot (still LIFO) and run the Dispose calls
                // outside _lock. User Dispose() implementations are arbitrary code; running them
                // under the container-wide lock lets any service that resolves — or that waits on
                // a thread which resolves — deadlock the whole container.
                pending = new IDisposable[_disposalStack.Count];
                for (int i = 0; i < pending.Length; i++)
                    pending[i] = _disposalStack.Pop();
            }

            // Scoped instances go first: they may hold references to the singletons below.
            DisposeOutstandingScopes();

            for (int i = 0; i < pending.Length; i++)
            {
                try
                {
                    pending[i].Dispose();
                }
                // FRAMEWORK DESIGN: broad catch is required here. Container disposal
                // must continue draining the stack even if one service's Dispose throws —
                // otherwise a single bad service leaks every disposable below it on the stack.
                catch (Exception e)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    UnityEngine.Debug.LogError($"Error disposing service: {e}");
                    StradaLog.LogError($"Error disposing service: {e}", LogModule.DI);
#else
                    // Both sinks must carry the same redaction level: StradaLog buffers entries
                    // and forwards them to subscribers (crash reporters, in-game consoles), so
                    // interpolating the full exception here would undo the redaction above.
                    UnityEngine.Debug.LogError($"Error disposing service: {e.Message}");
                    StradaLog.LogError($"Error disposing service: {e.Message}", LogModule.DI);
#endif
                }
            }

            for (int i = 0; i < _singletons.Length; i++)
                _singletons[i] = null;
        }

        /// <summary>
        /// Clears the source-generated <see cref="DirectFactory{T}"/> delegate for every type this
        /// container registered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="DirectFactory{T}"/> is process-global static state that the generated
        /// initializer populates exactly once per app run, so this is deliberately NOT part of
        /// <see cref="Dispose"/>: clearing it there would permanently disable source-generated
        /// factories for every container built afterwards. Call this only from a test teardown or
        /// editor reset that also re-runs the generated initializer.
        /// </para>
        /// </remarks>
        public void ClearDirectFactories()
        {
            for (int i = 0; i < _registeredCount; i++)
                ClearFactory(_registeredTypes[i]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal object ResolveByIndex(int index)
        {
            // Reached from compiled factories and from ContainerScope whenever a parent singleton
            // slot is null. Dispose() nulls every slot, so without this check a scope that outlives
            // its container silently re-creates singletons onto an already-drained disposal stack.
            if (_disposed) ThrowDisposed();
            return _factories[index](this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object IIndexResolver.ResolveByIndex(int index) => ResolveByIndex(index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object ResolveByType(Type type)
        {
            var typeId = TypeRegistry.GetId(type);
            if (typeId <= _maxTypeId)
            {
                var index = _typeIdToIndex[typeId];
                if (index >= 0)
                {
                    // Deliberately lock-free, matching Resolve<T>(). The singleton wrapper
                    // publishes with Interlocked.CompareExchange, so the old container-wide lock
                    // bought nothing except serialising every Resolve(Type) call and holding the
                    // lock across arbitrary user constructor code.
                    return _factories[index](this);
                }
            }
            throw new InvalidOperationException($"Type '{type.Name}' is not registered");
        }

        private Dictionary<int, int> BuildTypeIdMap(Dictionary<Type, Registration> registrations, out int maxId)
        {
            var map = new Dictionary<int, int>(registrations.Count);
            maxId = 0;
            int index = 0;

            foreach (var kvp in registrations)
            {
                int typeId = TypeRegistry.GetId(kvp.Key);
                map[typeId] = index;
                _registeredTypes[index] = kvp.Key;
                index++;
                if (typeId > maxId) maxId = typeId;
            }
            _registeredCount = index;
            return map;
        }

        private static int[] BuildIndexArray(int maxId, Dictionary<int, int> typeIdMap)
        {
            var arr = new int[maxId + 1];
            for (int i = 0; i <= maxId; i++) arr[i] = -1;
            foreach (var kvp in typeIdMap) arr[kvp.Key] = kvp.Value;
            return arr;
        }

        private void BuildFactories(Dictionary<Type, Registration> registrations, Dictionary<int, int> typeIdMap)
        {
            foreach (var kvp in registrations)
            {
                var reg = kvp.Value;
                int index = typeIdMap[TypeRegistry.GetId(kvp.Key)];

                _lifetimes[index] = reg.Lifetime;

                Func<IIndexResolver, object> rawFactory;

                // True when the container itself constructs the instance, and therefore owns
                // its disposal. For a RegisterInstance'd object the caller already handed us a
                // live instance; it is pushed onto the disposal stack once, here.
                bool containerOwnsInstance = reg.Instance == null;

                if (reg.Instance != null)
                {
                    if (reg.Instance is IDisposable d)
                    {
                        lock (_lock) _disposalStack.Push(d);
                    }
                    rawFactory = _ => reg.Instance;
                }
                else if (reg.Factory != null)
                {
                    var userFactory = reg.Factory;
                    var serviceType = kvp.Key;
                    rawFactory = resolver =>
                    {
                        // Re-entrancy guard: see t_activeFactories. The delegate instance is unique
                        // per registration, so it also distinguishes two containers that register
                        // the same service type.
                        var active = t_activeFactories;
                        if (active == null)
                        {
                            active = new List<object>(4);
                            t_activeFactories = active;
                        }

                        for (int i = 0; i < active.Count; i++)
                        {
                            if (ReferenceEquals(active[i], userFactory))
                                ThrowFactoryCycle(serviceType);
                        }

                        active.Add(userFactory);
                        try
                        {
                            // Hand the factory the resolver that is actually resolving it, so a
                            // factory invoked from a scope can reach that scope's Scoped services.
                            return userFactory(resolver as IContainer ?? this);
                        }
                        finally
                        {
                            active.RemoveAt(active.Count - 1);
                        }
                    };
                }
                else
                {
                    var directFactory = TryGetDirectFactory(kvp.Key);
                    rawFactory = directFactory ?? CompileFactory(reg.ImplementationType, registrations, typeIdMap);
                }

                if (reg.Lifetime == Lifetime.Singleton)
                {
                    _factories[index] = _ =>
                    {
                        var instance = Volatile.Read(ref _singletons[index]);
                        if (instance != null) return instance;

                        instance = rawFactory(this);

                        var prev = Interlocked.CompareExchange(ref _singletons[index], instance, null);
                        if (prev != null)
                        {
                            if (instance is IDisposable d) d.Dispose();
                            return prev;
                        }

                        // Skipped for RegisterInstance: that object is already on the stack
                        // from build time, and pushing it again disposed it twice.
                        if (containerOwnsInstance && instance is IDisposable disposable)
                        {
                            lock (_lock) _disposalStack.Push(disposable);
                        }

                        return instance;
                    };
                }
                else if (reg.Lifetime == Lifetime.Scoped)
                {
                    _factories[index] = _ => throw new InvalidOperationException("Cannot resolve scoped type from root container. Use CreateScope() first.");
                    _scopedFactories[index] = rawFactory;
                }
                else
                {
                    // Transient: opt-in disposal tracking via [TrackTransientDisposal] attribute
                    var typeForAttrCheck = reg.ImplementationType ?? reg.ServiceType;
                    var trackDisposal = typeForAttrCheck != null
                        && typeForAttrCheck.IsDefined(typeof(Strada.Core.DI.Attributes.TrackTransientDisposalAttribute), inherit: true);

                    if (trackDisposal)
                    {
                        _factories[index] = resolver =>
                        {
                            var instance = rawFactory(resolver ?? this);
                            if (instance is IDisposable d)
                            {
                                // Bill the instance to whoever resolved it. Routing every tracked
                                // transient to the container's stack retains it for the container's
                                // lifetime even when a short-lived scope created it, which is the
                                // unbounded growth this opt-in attribute exists to bound.
                                if (resolver is ContainerScope scope)
                                {
                                    scope.TrackDisposable(d);
                                }
                                else
                                {
                                    lock (_lock)
                                    {
                                        if (_disposed) { d.Dispose(); ThrowDisposed(); }
                                        _disposalStack.Push(d);
                                    }
                                }
                            }
                            return instance;
                        };
                    }
                    else
                    {
                        _factories[index] = rawFactory;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFactoryCycle(Type serviceType) =>
            throw new InvalidOperationException(
                $"Circular dependency detected: the factory registered for '{serviceType.Name}' re-entered itself " +
                "while resolving its own dependencies. Factory delegates are opaque to build-time cycle detection.");

        private Func<IIndexResolver, object> TryGetDirectFactory(Type serviceType)
        {
            var method = typeof(Container).GetMethod(nameof(CreateDirectFactoryWrapper), BindingFlags.NonPublic | BindingFlags.Static);
            // nameof() compiles to a string literal, so this method has no static callers and
            // UnityLinker will strip it unless the [Preserve] below survives. Fail with a message
            // that names the cause instead of a bare NullReferenceException.
            if (method == null)
                throw new InvalidOperationException(
                    "Container.CreateDirectFactoryWrapper was stripped by managed code stripping. " +
                    "Add a link.xml entry preserving Strada.Core.DI.Container, or lower the Managed Stripping Level.");

            var genericMethod = method.MakeGenericMethod(serviceType);
            return (Func<IIndexResolver, object>)genericMethod.Invoke(null, new object[] { this });
        }

        [UnityEngine.Scripting.Preserve]
        private static Func<IIndexResolver, object> CreateDirectFactoryWrapper<T>(Container container) where T : class
        {
            var factory = DirectFactory<T>.Get();
            if (factory == null) return null;

            return (resolver) => factory(resolver is IContainer c ? c : container);
        }

        private static Func<IIndexResolver, object> CompileFactory(Type implType, Dictionary<Type, Registration> regs, Dictionary<int, int> typeIdMap)
        {
            var ctor = GetBestConstructor(implType);
            var parameters = ctor.GetParameters();
            var resolverParam = Expression.Parameter(typeof(IIndexResolver), "resolver");

            if (parameters.Length == 0)
                return Expression.Lambda<Func<IIndexResolver, object>>(Expression.New(ctor), resolverParam).Compile();

            var args = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var pType = parameters[i].ParameterType;
                if (!regs.TryGetValue(pType, out var depReg))
                    throw new InvalidOperationException($"Dependency '{pType.Name}' not registered for '{implType.Name}'");
                args[i] = BuildDependencyExpr(pType, depReg, typeIdMap, resolverParam);
            }
            return Expression.Lambda<Func<IIndexResolver, object>>(Expression.New(ctor, args), resolverParam).Compile();
        }

        internal static ConstructorInfo GetBestConstructor(Type type)
        {
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (ctors.Length == 0)
                throw new InvalidOperationException($"No public constructor for {type.Name}");

            if (ctors.Length == 1) return ctors[0];

            ConstructorInfo best = ctors[0];
            int bestCount = best.GetParameters().Length;

            for (int i = 1; i < ctors.Length; i++)
            {
                int count = ctors[i].GetParameters().Length;
                if (count > bestCount)
                {
                    best = ctors[i];
                    bestCount = count;
                }
            }
            return best;
        }

        private static readonly MethodInfo ResolveByIndexMethod =
            typeof(IIndexResolver).GetMethod(nameof(IIndexResolver.ResolveByIndex));

        private static Expression BuildDependencyExpr(Type serviceType, Registration reg, Dictionary<int, int> typeIdMap, ParameterExpression resolverParam)
        {
            if (reg.Instance != null)
                return Expression.Constant(reg.Instance, serviceType);

            int index = typeIdMap[TypeRegistry.GetId(serviceType)];
            return Expression.Convert(
                Expression.Call(resolverParam, ResolveByIndexMethod, Expression.Constant(index)),
                serviceType);
        }

        private static void ClearFactory(Type type)
        {
            var clear = typeof(DirectFactory<>).MakeGenericType(type).GetMethod(nameof(DirectFactory<object>.Clear));
            // Reached only through nameof(), so managed code stripping can remove it.
            if (clear == null)
                throw new InvalidOperationException(
                    $"DirectFactory<{type.Name}>.Clear was stripped by managed code stripping. " +
                    "Add a link.xml entry preserving Strada.Core.DI.DirectFactory`1.");

            clear.Invoke(null, null);
        }

        private static class TypeId<T>
        {
            public static readonly int Id = TypeRegistry.GetId<T>();
        }
    }
}
