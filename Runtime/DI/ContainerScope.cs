using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Strada.Core.DI
{
    public sealed class ContainerScope : IContainerScope, IIndexResolver
    {
        // Written into a scoped slot by Dispose(). A resolve that started before _disposed was set
        // will then lose its publishing CAS instead of storing a live instance into a slot the
        // disposer has already walked past — which used to leak that instance for good.
        private static readonly object DisposedSlot = new object();

        private readonly Container _parent;
        private readonly Func<IIndexResolver, object>[] _factories;
        private readonly Func<IIndexResolver, object>[] _scopedFactories;
        private readonly Lifetime[] _lifetimes;
        private readonly int[] _typeIdToIndex;
        private readonly int _maxTypeId;
        private readonly object[] _parentSingletons;
        private readonly object[] _scopedInstances;
        private volatile bool _disposed;
        private readonly object _disposeLock = new object();

        // [TrackTransientDisposal] transients created through this scope. Allocated on first use —
        // the attribute is opt-in and most scopes never see one.
        private List<IDisposable> _trackedTransients;

        // The handle the owning container holds us by, so Dispose() can drop it.
        private WeakReference<ContainerScope> _ownerHandle;

        public IContainer Parent => _parent;

        internal ContainerScope(
            Container parent,
            Func<IIndexResolver, object>[] factories,
            Func<IIndexResolver, object>[] scopedFactories,
            Lifetime[] lifetimes,
            int[] typeIdToIndex,
            int maxTypeId,
            object[] parentSingletons)
        {
            _parent = parent;
            _factories = factories;
            _scopedFactories = scopedFactories;
            _lifetimes = lifetimes;
            _typeIdToIndex = typeIdToIndex;
            _maxTypeId = maxTypeId;
            _parentSingletons = parentSingletons;
            _scopedInstances = new object[factories.Length];
        }

        internal void AttachOwnerHandle(WeakReference<ContainerScope> handle) => _ownerHandle = handle;

        /// <summary>
        /// Takes ownership of a <c>[TrackTransientDisposal]</c> transient created through this scope.
        /// </summary>
        internal void TrackDisposable(IDisposable disposable)
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    disposable.Dispose();
                    throw new ObjectDisposedException(nameof(ContainerScope));
                }

                (_trackedTransients ??= new List<IDisposable>(4)).Add(disposable);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Resolve<T>() where T : class
        {
            int typeId = TypeRegistry.GetId<T>();
            return (T)ResolveById(typeId, typeof(T));
        }

        public object Resolve(Type type)
        {
            int typeId = TypeRegistry.GetId(type);
            return ResolveById(typeId, type);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object ResolveById(int typeId, Type requestedType)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ContainerScope));

            if (typeId > _maxTypeId)
                throw new InvalidOperationException($"Type '{requestedType.FullName}' is not registered in the container");

            int index = _typeIdToIndex[typeId];
            if (index < 0)
                throw new InvalidOperationException($"Type '{requestedType.FullName}' is not registered in the container");

            return ResolveByIndex(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object IIndexResolver.ResolveByIndex(int index) => ResolveByIndex(index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal object ResolveByIndex(int index)
        {
            // Compiled factories reach this directly through IIndexResolver, bypassing the checks
            // in ResolveById, so both guards belong here too.
            if (_disposed)
                throw new ObjectDisposedException(nameof(ContainerScope));
            if ((uint)index >= (uint)_lifetimes.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            var lifetime = _lifetimes[index];

            if (lifetime == Lifetime.Singleton)
            {
                var existing = Volatile.Read(ref _parentSingletons[index]);
                if (existing != null)
                    return existing;

                return _parent.ResolveByIndex(index);
            }

            if (lifetime == Lifetime.Scoped)
            {
                var existing = Volatile.Read(ref _scopedInstances[index]);
                if (existing != null)
                {
                    if (ReferenceEquals(existing, DisposedSlot))
                        throw new ObjectDisposedException(nameof(ContainerScope));
                    return existing;
                }

                var instance = _scopedFactories[index](this);

                var prev = Interlocked.CompareExchange(ref _scopedInstances[index], instance, null);
                if (prev != null)
                {
                    (instance as IDisposable)?.Dispose();
                    if (ReferenceEquals(prev, DisposedSlot))
                        throw new ObjectDisposedException(nameof(ContainerScope));
                    return prev;
                }

                // Dispose() may have started between the _disposed check above and the CAS. If it
                // has, claim the slot back so this instance is disposed exactly once — either here,
                // or by the disposer if it already swapped the sentinel in.
                if (_disposed)
                {
                    var claimed = Interlocked.CompareExchange(ref _scopedInstances[index], DisposedSlot, instance);
                    if (ReferenceEquals(claimed, instance))
                        (instance as IDisposable)?.Dispose();
                    throw new ObjectDisposedException(nameof(ContainerScope));
                }

                return instance;
            }

            return _factories[index](this);
        }

        public bool TryResolve<T>(out T instance) where T : class
        {
            if (_disposed)
            {
                instance = null;
                return false;
            }

            int typeId = TypeRegistry.GetId<T>();

            if (typeId > _maxTypeId || _typeIdToIndex[typeId] < 0)
            {
                instance = null;
                return false;
            }

            instance = (T)ResolveByIndex(_typeIdToIndex[typeId]);
            return true;
        }

        public IContainerScope CreateScope()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ContainerScope));

            // Child scopes are rooted at the same container, so route through it: that is what
            // registers the new scope for teardown when the container is disposed.
            return _parent.CreateScope();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsRegistered<T>() where T : class
        {
            int typeId = TypeRegistry.GetId<T>();
            return typeId <= _maxTypeId && _typeIdToIndex[typeId] >= 0;
        }

        public bool IsRegistered(Type type)
        {
            int typeId = TypeRegistry.GetId(type);
            return typeId <= _maxTypeId && _typeIdToIndex[typeId] >= 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_disposeLock)
            {
                if (_disposed)
                    return;

                _disposed = true;

                for (int i = 0; i < _scopedInstances.Length; i++)
                {
                    // Swap in the sentinel rather than null: a concurrent resolve that already
                    // passed the _disposed check must fail its CAS on a slot we have walked past.
                    var instance = Interlocked.Exchange(ref _scopedInstances[i], DisposedSlot);
                    (instance as IDisposable)?.Dispose();
                }

                if (_trackedTransients != null)
                {
                    for (int i = _trackedTransients.Count - 1; i >= 0; i--)
                        _trackedTransients[i].Dispose();
                    _trackedTransients.Clear();
                }
            }

            _parent.UnregisterScope(_ownerHandle);
            _ownerHandle = null;
        }
    }
}
