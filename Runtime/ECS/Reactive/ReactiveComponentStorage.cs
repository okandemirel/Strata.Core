using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Storage;
using UnityEngine;

namespace Strada.Core.ECS.Reactive
{
    internal interface IReactiveStorage
    {
        bool Remove(int entityIndex);
    }

    public sealed class ReactiveComponentStorage<T> : IDisposable, IReactiveStorage where T : unmanaged, IComponent
    {
        private const int MaxNotifyDepth = 8;

        private readonly ComponentStorage<T> _storage;
        private readonly List<Action<int, T>> _onAddCallbacks = new(4);
        private readonly List<Action<int, T>> _onRemoveCallbacks = new(4);
        private readonly List<Action<int, T, T>> _onChangeCallbacks = new(4);
        private int _notifyDepth;

        public ComponentStorage<T> Storage => _storage;
        public int Count => _storage.Count;

        public ReactiveComponentStorage(int sparseCapacity = 1024, int denseCapacity = 256)
        {
            _storage = new ComponentStorage<T>(sparseCapacity, denseCapacity);
        }

        /// <summary>
        /// Subscribes to component additions and returns a token that removes exactly this
        /// subscription.
        /// </summary>
        /// <remarks>
        /// The <c>Unsubscribe*</c> overloads compare delegates by <c>Target</c> reference, so a
        /// lambda that captures anything can only be removed by handing back the very same
        /// delegate instance — which callers writing <c>SubscribeOnAdd((e, c) =&gt; ...)</c> do not
        /// have. The token closes over the list and the delegate, so it works for any callback,
        /// and can be enrolled in a BindingScope or a SystemBase's disposables.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubscriptionToken SubscribeOnAdd(Action<int, T> callback) => Subscribe(_onAddCallbacks, callback);

        /// <inheritdoc cref="SubscribeOnAdd"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubscriptionToken SubscribeOnRemove(Action<int, T> callback) => Subscribe(_onRemoveCallbacks, callback);

        /// <inheritdoc cref="SubscribeOnAdd"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubscriptionToken SubscribeOnChange(Action<int, T, T> callback) => Subscribe(_onChangeCallbacks, callback);

        private static SubscriptionToken Subscribe<TCallback>(List<TCallback> callbacks, TCallback callback)
            where TCallback : Delegate
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            callbacks.Add(callback);
            return new SubscriptionToken(() => callbacks.Remove(callback));
        }

        /// <summary>
        /// Removes a subscription by delegate identity. Prefer disposing the
        /// <see cref="SubscriptionToken"/> returned by <see cref="SubscribeOnAdd"/>: this overload
        /// only works when the caller still holds the exact delegate instance it subscribed with.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsubscribeOnAdd(Action<int, T> callback) => _onAddCallbacks.Remove(callback);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsubscribeOnRemove(Action<int, T> callback) => _onRemoveCallbacks.Remove(callback);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsubscribeOnChange(Action<int, T, T> callback) => _onChangeCallbacks.Remove(callback);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int entityIndex, T component)
        {
            // SparseSet.Add silently overwrites when the entity already has the component, so an
            // Add on an existing entity is a value mutation. It used to commit that write with no
            // notification at all — OnAdd correctly suppressed, OnChange never reached — leaving
            // subscribers holding a stale value. Every write now raises exactly one event.
            if (_storage.Contains(entityIndex))
            {
                var previous = _storage.Get(entityIndex);
                _storage.Set(entityIndex, component);
                NotifyChange(entityIndex, previous, component);
                return;
            }

            _storage.Add(entityIndex, component);
            NotifyAdd(entityIndex, component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(int entityIndex)
        {
            if (!_storage.Contains(entityIndex))
                return false;

            // Mutate first, then notify — the order Add and Set already use. Notifying first left
            // the component still present during the callback, so Contains reported true inside
            // an OnRemove handler and a handler that removed again re-entered, producing up to
            // MaxNotifyDepth duplicate notifications plus an error log. The returned bool was
            // also whatever the second removal decided.
            var component = _storage.Get(entityIndex);
            if (!_storage.Remove(entityIndex))
                return false;

            NotifyRemove(entityIndex, component);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int entityIndex, T component)
        {
            if (!_storage.Contains(entityIndex))
            {
                Add(entityIndex, component);
                return;
            }

            var oldValue = _storage.Get(entityIndex);
            _storage.Set(entityIndex, component);
            NotifyChange(entityIndex, oldValue, component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get(int entityIndex) => _storage.Get(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(int entityIndex, out T component) => _storage.TryGet(entityIndex, out component);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int entityIndex) => _storage.Contains(entityIndex);

        private void NotifyAdd(int entityIndex, T component)
        {
            if (_notifyDepth >= MaxNotifyDepth)
            {
                Debug.LogError($"[ReactiveComponentStorage<{typeof(T).Name}>] Max notify depth ({MaxNotifyDepth}) exceeded in OnAdd. Aborting to prevent stack overflow.");
                return;
            }

            _notifyDepth++;
            try
            {
                var snapshot = _onAddCallbacks.ToArray();
                foreach (var callback in snapshot)
                {
                    try { callback(entityIndex, component); }
                    catch (Exception ex) { Debug.LogError($"[ReactiveComponentStorage<{typeof(T).Name}>] Exception in OnAdd callback: {ex}"); }
                }
            }
            finally { _notifyDepth--; }
        }

        private void NotifyRemove(int entityIndex, T component)
        {
            if (_notifyDepth >= MaxNotifyDepth)
            {
                Debug.LogError($"[ReactiveComponentStorage<{typeof(T).Name}>] Max notify depth ({MaxNotifyDepth}) exceeded in OnRemove. Aborting to prevent stack overflow.");
                return;
            }

            _notifyDepth++;
            try
            {
                var snapshot = _onRemoveCallbacks.ToArray();
                foreach (var callback in snapshot)
                {
                    try { callback(entityIndex, component); }
                    catch (Exception ex) { Debug.LogError($"[ReactiveComponentStorage<{typeof(T).Name}>] Exception in OnRemove callback: {ex}"); }
                }
            }
            finally { _notifyDepth--; }
        }

        private void NotifyChange(int entityIndex, T oldValue, T newValue)
        {
            if (_notifyDepth >= MaxNotifyDepth)
            {
                Debug.LogError($"[ReactiveComponentStorage<{typeof(T).Name}>] Max notify depth ({MaxNotifyDepth}) exceeded in OnChange. Aborting to prevent stack overflow.");
                return;
            }

            _notifyDepth++;
            try
            {
                var snapshot = _onChangeCallbacks.ToArray();
                foreach (var callback in snapshot)
                {
                    try { callback(entityIndex, oldValue, newValue); }
                    catch (Exception ex) { Debug.LogError($"[ReactiveComponentStorage<{typeof(T).Name}>] Exception in OnChange callback: {ex}"); }
                }
            }
            finally { _notifyDepth--; }
        }

        /// <summary>
        /// Removes every component, raising OnRemove for each one.
        /// </summary>
        /// <remarks>
        /// Clear used to forward straight to the sparse set, invalidating every subscriber's view
        /// of the world without telling any of them. The values are read out before the storage is
        /// wiped so the callbacks see the post-state — Contains is already false inside the
        /// handler, matching Remove.
        /// </remarks>
        public void Clear()
        {
            if (_onRemoveCallbacks.Count == 0)
            {
                _storage.Clear();
                return;
            }

            var indices = new List<int>(_storage.Count);
            _storage.GetEntityIndices(indices);

            int count = indices.Count;
            var values = new T[count];
            for (int i = 0; i < count; i++)
                values[i] = _storage.Get(indices[i]);

            _storage.Clear();

            for (int i = 0; i < count; i++)
                NotifyRemove(indices[i], values[i]);
        }

        /// <summary>
        /// Releases the storage and drops every subscription.
        /// </summary>
        /// <remarks>
        /// Deliberately silent, unlike <see cref="Clear"/>: the callbacks are dropped before the
        /// native memory is released, because a handler invoked during teardown would be handed a
        /// storage it can no longer read.
        /// </remarks>
        public void Dispose()
        {
            _onAddCallbacks.Clear();
            _onRemoveCallbacks.Clear();
            _onChangeCallbacks.Clear();
            _storage.Dispose();
        }
    }
}
