using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Core;

namespace Strada.Core.ECS.Reactive
{
    public sealed class ReactiveEntityManager : IDisposable
    {
        private readonly EntityManager _entityManager;
        private readonly bool _ownsEntityManager;
        private readonly Dictionary<Type, object> _reactiveStorages = new(16);

        public EntityManager Entities => _entityManager;

        public ReactiveEntityManager()
        {
            _entityManager = new EntityManager();
            _ownsEntityManager = true;
        }

        /// <summary>
        /// Wraps an EntityManager owned by the caller. Disposing this instance releases the
        /// reactive storages only — the injected EntityManager is left alone.
        /// </summary>
        /// <remarks>
        /// Dispose used to tear down the EntityManager unconditionally, which for an injected one
        /// freed the persistent NativeArrays and the whole ComponentStore out from under whoever
        /// actually owned it.
        /// </remarks>
        public ReactiveEntityManager(EntityManager entityManager)
        {
            _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
            _ownsEntityManager = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReactiveComponentStorage<T> GetReactiveStorage<T>() where T : unmanaged, IComponent
        {
            var type = typeof(T);
            if (_reactiveStorages.TryGetValue(type, out var storage))
                return (ReactiveComponentStorage<T>)storage;

            var newStorage = new ReactiveComponentStorage<T>();
            _reactiveStorages[type] = newStorage;
            return newStorage;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Entity CreateEntity() => _entityManager.CreateEntity();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DestroyEntity(Entity entity)
        {
            // The cleanup loop keys on entity.Index alone, but EntityManager recycles indices, so
            // a stale handle carries a live index with a dead version. Without this guard the two
            // halves of the method disagreed about which entity they were operating on: the loop
            // stripped the reactive components off whichever entity currently holds the index,
            // while EntityManager.DestroyEntity correctly did nothing.
            if (!_entityManager.Exists(entity))
                return;

            foreach (var storage in _reactiveStorages.Values)
            {
                if (storage is IReactiveStorage reactive)
                    reactive.Remove(entity.Index);
            }
            _entityManager.DestroyEntity(entity);
        }

        // Each of these used to take entity.Index and ignore entity.Version, so a stale handle
        // read or wrote the reactive components of whichever entity had since been given that
        // recycled index. The guards mirror the EntityManager counterparts exactly: Add and Set
        // are no-ops for a dead handle, Remove reports that it removed nothing, Get throws.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddReactiveComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
        {
            if (!_entityManager.Exists(entity))
                return;

            var storage = GetReactiveStorage<T>();
            storage.Add(entity.Index, component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetReactiveComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
        {
            if (!_entityManager.Exists(entity))
                return;

            var storage = GetReactiveStorage<T>();
            storage.Set(entity.Index, component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool RemoveReactiveComponent<T>(Entity entity) where T : unmanaged, IComponent
        {
            if (!_entityManager.Exists(entity))
                return false;

            var storage = GetReactiveStorage<T>();
            return storage.Remove(entity.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetReactiveComponent<T>(Entity entity) where T : unmanaged, IComponent
        {
            if (!_entityManager.Exists(entity))
                throw new InvalidOperationException(
                    $"Entity {entity.Index}:{entity.Version} does not exist or version mismatch");

            var storage = GetReactiveStorage<T>();
            return storage.Get(entity.Index);
        }

        /// <summary>
        /// Subscribes to additions of <typeparamref name="T"/> and returns a token that removes
        /// exactly this subscription.
        /// </summary>
        /// <remarks>
        /// These three used to return void, and the class exposed no unsubscribe at all: the only
        /// way out was to reach through <see cref="GetReactiveStorage{T}"/> holding the original
        /// delegate instance. A subscriber that outlived nothing therefore kept its whole object
        /// graph alive and kept receiving writes after its target was destroyed. Enrol the token
        /// in a BindingScope or a system's disposables.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubscriptionToken OnAdd<T>(Action<int, T> callback) where T : unmanaged, IComponent
        {
            return GetReactiveStorage<T>().SubscribeOnAdd(callback);
        }

        /// <inheritdoc cref="OnAdd{T}"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubscriptionToken OnRemove<T>(Action<int, T> callback) where T : unmanaged, IComponent
        {
            return GetReactiveStorage<T>().SubscribeOnRemove(callback);
        }

        /// <inheritdoc cref="OnAdd{T}"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubscriptionToken OnChange<T>(Action<int, T, T> callback) where T : unmanaged, IComponent
        {
            return GetReactiveStorage<T>().SubscribeOnChange(callback);
        }

        public void Dispose()
        {
            foreach (var storage in _reactiveStorages.Values)
            {
                if (storage is IDisposable d)
                    d.Dispose();
            }
            _reactiveStorages.Clear();

            if (_ownsEntityManager)
                _entityManager.Dispose();
        }
    }
}
