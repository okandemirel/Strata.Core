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
        private readonly List<IDisposable> _disposables = new(4);
        private bool _initialized;
        private bool _disposed;

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
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            OnDispose();
            // Release any tokens captured by the RegisterSignalHandler / RegisterQueryHandler
            // wrappers below so the EventBus slots do not retain references to this disposed
            // system. LIFO disposal matches Patterns/Base.
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();
            _disposed = true;
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
            var token = EventBus?.RegisterSignalHandler(handler);
            if (token != null) _disposables.Add(token);
        }
    }

    public abstract class SystemBase<T1> : SystemBase
        where T1 : unmanaged, IComponent
    {
        private EntityQuery<T1> _cachedQuery;
        private bool _queryInitialized;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1) => OnUpdateEntity(entity, ref c1, deltaTime));
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, float deltaTime);
    }

    public abstract class SystemBase<T1, T2> : SystemBase
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2> _cachedQuery;
        private bool _queryInitialized;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1, ref T2 c2) => OnUpdateEntity(entity, ref c1, ref c2, deltaTime));
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

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1, ref T2 c2, ref T3 c3) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, deltaTime));
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent where T4 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4> _cachedQuery;
        private bool _queryInitialized;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, deltaTime));
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4, T5> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4, T5> _cachedQuery;
        private bool _queryInitialized;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, deltaTime));
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, float deltaTime);
    }

    public abstract class SystemBase<T1, T2, T3, T4, T5, T6> : SystemBase
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
    {
        private EntityQuery<T1, T2, T3, T4, T5, T6> _cachedQuery;
        private bool _queryInitialized;

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5, T6>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, deltaTime));
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

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5, T6, T7>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7, deltaTime));
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

        protected sealed override void OnUpdate(float deltaTime)
        {
            if (!_queryInitialized)
            {
                _cachedQuery = EntityManager.Query().Select<T1, T2, T3, T4, T5, T6, T7, T8>();
                _queryInitialized = true;
            }
            _cachedQuery.ForEach((int entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, ref T8 c8) =>
                OnUpdateEntity(entity, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7, ref c8, deltaTime));
        }

        protected abstract void OnUpdateEntity(int entityIndex, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, ref T8 c8, float deltaTime);
    }
}
