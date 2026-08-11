using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Strada.Core.DI
{
    public sealed class AsyncScopeBuilder
    {
        private readonly IContainer _container;
        private readonly List<(Type type, Func<IContainer, CancellationToken, ValueTask<object>> factory)> _asyncFactories = new(8);
        private readonly List<Type> _preWarmTypes = new(8);

        public AsyncScopeBuilder(IContainer container)
        {
            _container = container;
        }

        public AsyncScopeBuilder RegisterAsync<T>(Func<IContainer, CancellationToken, ValueTask<T>> factory) where T : class
        {
            _asyncFactories.Add((typeof(T), async (c, ct) => await factory(c, ct).ConfigureAwait(false)));
            return this;
        }

        public AsyncScopeBuilder RegisterAsync<T>(IAsyncFactory<T> factory) where T : class
        {
            _asyncFactories.Add((typeof(T), async (c, ct) => await factory.CreateAsync(c, ct).ConfigureAwait(false)));
            return this;
        }

        public AsyncScopeBuilder PreWarm<T>() where T : class
        {
            _preWarmTypes.Add(typeof(T));
            return this;
        }

        public AsyncScopeBuilder PreWarm(Type type)
        {
            _preWarmTypes.Add(type);
            return this;
        }

        public async ValueTask<AsyncContainerScope> BuildAsync(CancellationToken cancellation = default)
        {
            var innerScope = _container.CreateScope();

            try
            {
                List<object> preWarmed = null;

                foreach (var type in _preWarmTypes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var instance = innerScope.Resolve(type);

                    if (instance is IAsyncInitializable asyncInit)
                    {
                        await asyncInit.InitializeAsync(cancellation).ConfigureAwait(false);
                        // Recorded so the scope's first ResolveAsync of a pre-warmed type does not
                        // run InitializeAsync a second time on the same cached instance.
                        (preWarmed ??= new List<object>(_preWarmTypes.Count)).Add(instance);
                    }
                }

                if (_asyncFactories.Count == 0)
                    return new AsyncContainerScope(innerScope, preWarmedInstances: preWarmed);

                int maxTypeId = 0;
                foreach (var (type, _) in _asyncFactories)
                {
                    int id = TypeRegistry.GetId(type);
                    if (id > maxTypeId) maxTypeId = id;
                }

                var typeIdToAsyncIndex = new int[maxTypeId + 1];
                Array.Fill(typeIdToAsyncIndex, -1);

                var factories = new Func<Type, CancellationToken, ValueTask<object>>[_asyncFactories.Count];
                for (int i = 0; i < _asyncFactories.Count; i++)
                {
                    var (type, factory) = _asyncFactories[i];
                    int typeId = TypeRegistry.GetId(type);
                    typeIdToAsyncIndex[typeId] = i;
                    // The scope, not the root container: a factory registered on a *scope* builder
                    // must be able to resolve that scope's Scoped services.
                    factories[i] = (t, ct) => factory(innerScope, ct);
                }

                return new AsyncContainerScope(innerScope, factories, typeIdToAsyncIndex, maxTypeId, preWarmed);
            }
            catch
            {
                // Nothing else holds a reference to innerScope on the failure path, so every scoped
                // disposable already built by an earlier PreWarm iteration would leak.
                innerScope.Dispose();
                throw;
            }
        }
    }

    public static class AsyncContainerExtensions
    {
        public static AsyncScopeBuilder CreateAsyncScopeBuilder(this IContainer container)
        {
            return new AsyncScopeBuilder(container);
        }

        public static ValueTask<AsyncContainerScope> CreateScopeAsync(
            this IContainer container,
            CancellationToken cancellation = default)
        {
            return new AsyncScopeBuilder(container).BuildAsync(cancellation);
        }

        public static async ValueTask<AsyncContainerScope> CreateScopeWithPreWarmAsync<T1>(
            this IContainer container,
            CancellationToken cancellation = default)
            where T1 : class
        {
            return await new AsyncScopeBuilder(container)
                .PreWarm<T1>()
                .BuildAsync(cancellation)
                .ConfigureAwait(false);
        }

        public static async ValueTask<AsyncContainerScope> CreateScopeWithPreWarmAsync<T1, T2>(
            this IContainer container,
            CancellationToken cancellation = default)
            where T1 : class
            where T2 : class
        {
            return await new AsyncScopeBuilder(container)
                .PreWarm<T1>()
                .PreWarm<T2>()
                .BuildAsync(cancellation)
                .ConfigureAwait(false);
        }

        public static async ValueTask<AsyncContainerScope> CreateScopeWithPreWarmAsync<T1, T2, T3>(
            this IContainer container,
            CancellationToken cancellation = default)
            where T1 : class
            where T2 : class
            where T3 : class
        {
            return await new AsyncScopeBuilder(container)
                .PreWarm<T1>()
                .PreWarm<T2>()
                .PreWarm<T3>()
                .BuildAsync(cancellation)
                .ConfigureAwait(false);
        }
    }
}
