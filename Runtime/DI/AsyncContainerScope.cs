using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Strada.Core.DI
{
    public sealed class AsyncContainerScope : IContainerScope, IAsyncDisposable
    {
        private static readonly object Initialized = new object();

        private readonly IContainerScope _innerScope;
        private readonly Func<Type, CancellationToken, ValueTask<object>>[] _asyncFactories;
        private readonly object[] _asyncInstances;
        private readonly int[] _typeIdToAsyncIndex;
        private readonly int _maxAsyncTypeId;

        // Deliberately never disposed. An in-flight ResolveAsync can be holding it when the scope is
        // torn down, and Release() on a disposed SemaphoreSlim throws out of that method's finally
        // block, masking whatever the caller was doing. AvailableWaitHandle is never touched, so the
        // semaphore owns no unmanaged resource and the GC can reclaim it.
        private readonly SemaphoreSlim _initLock = new(1, 1);

        // Instances whose InitializeAsync has already completed for this scope. Scoped and Singleton
        // resolves hand back the same cached object every time, so without this every ResolveAsync
        // would re-run initialization. Weak keys: a transient resolved once must not be retained.
        private readonly ConditionalWeakTable<object, object> _initialized = new ConditionalWeakTable<object, object>();

        // 0 = live, 1 = disposed. An int so Dispose/DisposeAsync can claim it with Interlocked and
        // only one of them ever runs the teardown.
        private int _disposed;

        internal AsyncContainerScope(
            IContainerScope innerScope,
            Func<Type, CancellationToken, ValueTask<object>>[] asyncFactories = null,
            int[] typeIdToAsyncIndex = null,
            int maxAsyncTypeId = -1,
            IReadOnlyList<object> preWarmedInstances = null)
        {
            _innerScope = innerScope;
            _asyncFactories = asyncFactories ?? Array.Empty<Func<Type, CancellationToken, ValueTask<object>>>();
            _asyncInstances = new object[_asyncFactories.Length];
            _typeIdToAsyncIndex = typeIdToAsyncIndex ?? Array.Empty<int>();
            _maxAsyncTypeId = maxAsyncTypeId;

            if (preWarmedInstances != null)
            {
                for (int i = 0; i < preWarmedInstances.Count; i++)
                {
                    // PreWarm can name the same scoped type twice, which yields the same instance;
                    // ConditionalWeakTable.Add throws on a duplicate key.
                    var instance = preWarmedInstances[i];
                    if (!_initialized.TryGetValue(instance, out _))
                        _initialized.Add(instance, Initialized);
                }
            }
        }

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public IContainer Parent => _innerScope.Parent;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Resolve<T>() where T : class => _innerScope.Resolve<T>();

        public object Resolve(Type type) => _innerScope.Resolve(type);

        public bool TryResolve<T>(out T instance) where T : class => _innerScope.TryResolve(out instance);

        public bool IsRegistered<T>() where T : class => _innerScope.IsRegistered<T>();

        public bool IsRegistered(Type type) => _innerScope.IsRegistered(type);

        public IContainerScope CreateScope() => _innerScope.CreateScope();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async ValueTask<T> ResolveAsync<T>(CancellationToken cancellation = default) where T : class
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(AsyncContainerScope));

            int typeId = TypeRegistry.GetId<T>();

            if (typeId <= _maxAsyncTypeId && _typeIdToAsyncIndex.Length > typeId)
            {
                int asyncIndex = _typeIdToAsyncIndex[typeId];
                if (asyncIndex >= 0)
                    return (T)await ResolveFromAsyncFactoryAsync(asyncIndex, typeof(T), cancellation).ConfigureAwait(false);
            }

            var instance = _innerScope.Resolve<T>();
            await EnsureInitializedAsync(instance, cancellation).ConfigureAwait(false);
            return instance;
        }

        public async ValueTask<object> ResolveAsync(Type type, CancellationToken cancellation = default)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(AsyncContainerScope));

            int typeId = TypeRegistry.GetId(type);

            if (typeId <= _maxAsyncTypeId && _typeIdToAsyncIndex.Length > typeId)
            {
                int asyncIndex = _typeIdToAsyncIndex[typeId];
                if (asyncIndex >= 0)
                    return await ResolveFromAsyncFactoryAsync(asyncIndex, type, cancellation).ConfigureAwait(false);
            }

            var instance = _innerScope.Resolve(type);
            await EnsureInitializedAsync(instance, cancellation).ConfigureAwait(false);
            return instance;
        }

        /// <summary>
        /// Resolves an async-factory registration, caching the result for the scope's lifetime.
        /// </summary>
        /// <remarks>
        /// Without the cache an async factory behaves as an untracked transient: every ResolveAsync
        /// builds another instance and nothing ever disposes any of them, even though the factory
        /// was registered on a scope builder.
        /// </remarks>
        private async ValueTask<object> ResolveFromAsyncFactoryAsync(int asyncIndex, Type type, CancellationToken cancellation)
        {
            var cached = Volatile.Read(ref _asyncInstances[asyncIndex]);
            if (cached != null)
                return cached;

            await _initLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cached = _asyncInstances[asyncIndex];
                if (cached == null)
                {
                    cached = await _asyncFactories[asyncIndex](type, cancellation).ConfigureAwait(false);
                    Volatile.Write(ref _asyncInstances[asyncIndex], cached);
                }
            }
            finally
            {
                _initLock.Release();
            }

            return cached;
        }

        private async ValueTask EnsureInitializedAsync(object instance, CancellationToken cancellation)
        {
            if (!(instance is IAsyncInitializable asyncInit))
                return;

            await _initLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (_initialized.TryGetValue(instance, out _))
                    return;

                await asyncInit.InitializeAsync(cancellation).ConfigureAwait(false);

                // Recorded only after success, so a failed initialization can be retried.
                _initialized.Add(instance, Initialized);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            DisposeAsyncFactoryInstances();
            _innerScope.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            for (int i = 0; i < _asyncInstances.Length; i++)
            {
                var instance = Interlocked.Exchange(ref _asyncInstances[i], null);
                if (instance is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    (instance as IDisposable)?.Dispose();
            }

            if (_innerScope is IAsyncDisposable innerAsync)
                await innerAsync.DisposeAsync().ConfigureAwait(false);
            else
                _innerScope.Dispose();
        }

        private void DisposeAsyncFactoryInstances()
        {
            for (int i = 0; i < _asyncInstances.Length; i++)
                (Interlocked.Exchange(ref _asyncInstances[i], null) as IDisposable)?.Dispose();
        }
    }
}
