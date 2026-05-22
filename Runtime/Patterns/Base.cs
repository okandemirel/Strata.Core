using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.Communication;
using Strada.Core.DI;
using Strada.Core.DI.Attributes;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.World;
using Strada.Core.Patterns.Interfaces;

namespace Strada.Core.Patterns
{
    /// <summary>
    /// Base class for all pattern components (Controllers, Services, etc).
    /// Provides DI integration, messaging, and lifecycle management.
    /// </summary>
    public abstract class Base : IInitializable, IDisposable
    {
        private readonly List<IDisposable> _disposables = new(4);
        private bool _initialized;
        private bool _disposed;

        protected IContainer Container { get; private set; }
        protected World World { get; private set; }
        protected EntityManager EntityManager { get; private set; }
        protected EventBus EventBus { get; private set; }
        protected bool IsInitialized => _initialized;
        protected bool IsDisposed => _disposed;

        [Inject]
        public void Construct(IContainer container)
        {
            if (Container != null) return;
            Container = container;
            container.TryResolve(out World world);
            World = world;
            EntityManager = world?.EntityManager;
            container.TryResolve(out EventBus bus);
            EventBus = bus;
        }

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            OnInitialize();
        }

        protected virtual void OnInitialize() { }

        protected virtual void OnDispose() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected T Resolve<T>() where T : class => Container?.Resolve<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryResolve<T>(out T instance) where T : class
        {
            if (Container == null)
            {
                instance = null;
                return false;
            }
            return Container.TryResolve(out instance);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void Subscribe<T>(Action<T> handler) where T : struct
        {
            // Token-based lifecycle: EventBus.Subscribe returns a SubscriptionToken whose
            // Dispose removes only this handler. Storing the token in _disposables means
            // base Dispose() unsubscribes everything in LIFO order automatically.
            var token = EventBus?.Subscribe(handler);
            if (token != null) _disposables.Add(token);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void Publish<T>(T message) where T : struct
        {
            EventBus?.Publish(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void Send<T>(T signal) where T : struct
        {
            EventBus?.Send(signal);
        }

        /// <summary>
        /// Register a signal handler. Signals have exactly one handler and are used for direct actions.
        /// Use this instead of Subscribe for messages that represent requests/actions.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void RegisterSignalHandler<T>(Action<T> handler) where T : struct
        {
            // Store the token so base Dispose clears the slot if this instance still owns it.
            var token = EventBus?.RegisterSignalHandler(handler);
            if (token != null) _disposables.Add(token);
        }

        /// <summary>
        /// Register a query handler. Queries return data and have exactly one handler.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            var token = EventBus?.RegisterQueryHandler(handler);
            if (token != null) _disposables.Add(token);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected TResult Query<TQuery, TResult>(TQuery query) where TQuery : struct, IQuery<TResult>
        {
            return EventBus != null ? EventBus.Query<TQuery, TResult>(query) : default;
        }

        protected void AddDisposable(IDisposable disposable)
        {
            _disposables.Add(disposable);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected Entity CreateEntity() => EntityManager?.CreateEntity() ?? default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void DestroyEntity(Entity entity) => EntityManager?.DestroyEntity(entity);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            OnDispose();

            // Dispose in LIFO order so later-acquired resources release before earlier ones.
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();

            GC.SuppressFinalize(this);
        }
    }
}
