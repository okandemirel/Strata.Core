using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.Communication;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Query;
using Strada.Core.ECS.Storage;
using Strada.Core.Sync;
using UnityEngine;

namespace Strada.Core.ECS.Systems
{
    public abstract class SystemBase : ISystem
    {
        /// <summary>
        /// Number of consecutive failed frames logged in full before repeats are collapsed.
        /// </summary>
        private const int MaxLoggedFailures = 3;

        private readonly List<IDisposable> _disposables = new(4);
        private bool _initialized;
        private bool _disposed;
        private int _consecutiveFailures;

        protected EntityManager EntityManager { get; private set; }
        protected EventBus EventBus { get; private set; }
        protected EntityHandleRegistry HandleRegistry { get; private set; }

        public void Inject(EntityManager entityManager, EventBus bus = null, EntityHandleRegistry handleRegistry = null)
        {
            EntityManager = entityManager;
            EventBus = bus;
            HandleRegistry = handleRegistry;
        }

        public void Initialize()
        {
            if (_initialized) return;
            OnInitialize();
            _initialized = true;
        }

        public void Update(float deltaTime)
        {
            if (!_initialized || _disposed) return;
            try
            {
                OnUpdate(deltaTime);
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                // A system that fails deterministically fails every frame, and Debug.LogException
                // formats the whole stack trace, appends to the console ring buffer and repaints
                // the Editor console each time — 60 times a second, indefinitely. Repeats are
                // collapsed after the first few; the counter resets on the first clean frame.
                _consecutiveFailures++;
                if (_consecutiveFailures <= MaxLoggedFailures)
                {
                    Debug.LogException(ex);
                    if (_consecutiveFailures == MaxLoggedFailures)
                        Debug.LogError(
                            $"[Strada] {GetType().Name} has thrown {MaxLoggedFailures} frames in a row; " +
                            "suppressing further exception logs from this system until it updates cleanly.");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            // Flagged first, and the cleanup runs in a finally: OnDispose is user code, and when
            // it threw the tokens below were never released — so the EventBus slot kept a strong
            // reference to this system — _disposed stayed false so Update kept running it every
            // frame, and a second Dispose re-entered OnDispose on a half-torn-down object.
            _disposed = true;
            try
            {
                OnDispose();
            }
            finally
            {
                // Release any tokens captured by the RegisterSignalHandler / RegisterQueryHandler /
                // Subscribe wrappers below so the EventBus slots do not retain references to this
                // disposed system. LIFO disposal matches Patterns/Base.
                for (int i = _disposables.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _disposables[i].Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
                _disposables.Clear();
            }
        }

        protected virtual void OnInitialize() { }
        protected abstract void OnUpdate(float deltaTime);
        protected virtual void OnDispose() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected QueryBuilder Query() => EntityManager.Query();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1>(QueryDelegate<T1> action) where T1 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1, T2>(QueryDelegate<T1, T2> action)
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1, T2, T3>(QueryDelegate<T1, T2, T3> action)
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1, T2, T3, T4>(QueryDelegate<T1, T2, T3, T4> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent where T4 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1, T2, T3, T4, T5>(QueryDelegate<T1, T2, T3, T4, T5> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1, T2, T3, T4, T5, T6>(QueryDelegate<T1, T2, T3, T4, T5, T6> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1, T2, T3, T4, T5, T6, T7>(QueryDelegate<T1, T2, T3, T4, T5, T6, T7> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
            where T7 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ForEach<T1, T2, T3, T4, T5, T6, T7, T8>(QueryDelegate<T1, T2, T3, T4, T5, T6, T7, T8> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
            where T7 : unmanaged, IComponent where T8 : unmanaged, IComponent
        {
            EntityManager.ForEach(action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected Entity CreateEntity() => EntityManager.CreateEntity();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void DestroyEntity(Entity entity) => EntityManager.DestroyEntity(entity);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void Publish<T>(T evt) where T : struct
        {
            EventBus?.Publish(evt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void Send<T>(T signal) where T : struct
        {
            EventBus?.Send(signal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void RegisterSignalHandler<T>(Action<T> handler) where T : struct
        {
            // Capture the token so Dispose can clear the slot if this system still owns it.
            AddDisposable(EventBus?.RegisterSignalHandler(handler));
        }

        /// <summary>
        /// Subscribes to an event and ties the subscription's lifetime to this system.
        /// </summary>
        /// <remarks>
        /// The counterpart to <see cref="Publish{T}"/>. A subclass that called
        /// <c>EventBus.Subscribe</c> directly got back a token with nowhere to put it, and the bus
        /// then kept delivering events to the system long after it was disposed.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void Subscribe<T>(Action<T> handler) where T : struct
        {
            AddDisposable(EventBus?.Subscribe(handler));
        }

        /// <summary>
        /// Registers a query handler and ties its lifetime to this system, so the EventBus slot
        /// is released on Dispose instead of holding this system alive.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            AddDisposable(EventBus?.RegisterQueryHandler(handler));
        }

        /// <summary>
        /// Enrols a disposable in this system's teardown; it is disposed LIFO by
        /// <see cref="Dispose"/>. Null is ignored, so results of <c>EventBus?.Xxx()</c> can be
        /// passed straight through.
        /// </summary>
        protected void AddDisposable(IDisposable disposable)
        {
            if (disposable != null) _disposables.Add(disposable);
        }
    }

    public abstract class SystemBase<T1> : SystemBase
        where T1 : unmanaged, IComponent
    {
        private EntityQuery<T1> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1>();
                _queryInitialized = true;
            }

            // deltaTime is passed through a field so the lambda captures only `this`.
            // Capturing the parameter forced Roslyn to build a fresh display class and a
            // fresh QueryDelegate on every call — two allocations per system per frame,
            // and QueryDelegate is a managed delegate with no struct-callback overload.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1) =>
                OnUpdateEntity(entity, ref c1, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, float deltaTime);
    }

    public abstract class SystemBase<T1, T2> : SystemBase
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1, T2> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2>();
                _queryInitialized = true;
            }

            // See SystemBase<T1>: deltaTime goes through a field so the delegate can be cached.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1, ref T2 c2) =>
                OnUpdateEntity(entity, ref c1, ref c2, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3> : SystemBase
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1, T2, T3> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3>();
                _queryInitialized = true;
            }

            // See SystemBase<T1>: deltaTime goes through a field so the delegate can be cached.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1, ref T2 c2, ref T3 c3) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent where T4 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1, T2, T3, T4> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4>();
                _queryInitialized = true;
            }

            // See SystemBase<T1>: deltaTime goes through a field so the delegate can be cached.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4, T5> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4, T5> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1, T2, T3, T4, T5> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5>();
                _queryInitialized = true;
            }

            // See SystemBase<T1>: deltaTime goes through a field so the delegate can be cached.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4, T5, T6> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4, T5, T6> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1, T2, T3, T4, T5, T6> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5, T6>();
                _queryInitialized = true;
            }

            // See SystemBase<T1>: deltaTime goes through a field so the delegate can be cached.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4, T5, T6, T7> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
        where T7 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4, T5, T6, T7> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1, T2, T3, T4, T5, T6, T7> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5, T6, T7>();
                _queryInitialized = true;
            }

            // See SystemBase<T1>: deltaTime goes through a field so the delegate can be cached.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4, T5, T6, T7, T8> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
        where T7 : unmanaged, IComponent where T8 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4, T5, T6, T7, T8> _cachedQuery;
        private bool _queryInitialized;
        private QueryDelegate<T1, T2, T3, T4, T5, T6, T7, T8> _cachedCallback;
        private float _deltaTime;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5, T6, T7, T8>();
                _queryInitialized = true;
            }

            // See SystemBase<T1>: deltaTime goes through a field so the delegate can be cached.
            _deltaTime = deltaTime;
            _cachedCallback ??= (int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, ref T8 c8) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7, ref c8, _deltaTime);
            _cachedQuery.ForEach(_cachedCallback);
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, ref T8 c8, float deltaTime);
    }
}
