using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Strada.Core.Sync
{
    public interface IReadOnlyReactiveProperty<T>
    {
        T Value { get; }
        Strada.Core.SubscriptionToken Subscribe(Action<T> handler);
    }

    /// <remarks>
    /// FRAMEWORK DESIGN: ReactiveProperty is intentionally not thread-safe. Strada
    /// targets Unity's main-thread UI/game-state model; locks on every Value setter would
    /// add overhead on a path that runs hundreds of times per frame. Cross-thread updates
    /// must be marshalled to the main thread by the caller (eg. via
    /// <see cref="UnityEngine.PlayerLoop"/> or a job-completion callback) before assigning.
    /// </remarks>
    public sealed class ReactiveProperty<T> : IReadOnlyReactiveProperty<T>, IDisposable
    {
        private T _value;
        // Copy-on-write: mutation rebuilds the array, notification just reads it. This is the
        // same shape EventBus.EventChannel<T> already uses. The previous List + per-notify
        // ToArray() allocated a fresh Action<T>[] on EVERY value change.
        private Action<T>[] _handlers = Array.Empty<Action<T>>();
        // static: EqualityComparer<T>.Default is per-closed-generic anyway, so an instance
        // field cost 8 bytes on every ReactiveProperty for nothing.
        private static readonly EqualityComparer<T> _comparer = EqualityComparer<T>.Default;
        private bool _disposed;

        public ReactiveProperty() => _value = default;

        public ReactiveProperty(T initialValue) => _value = initialValue;

        public int SubscriberCount => _handlers.Length;
        public bool HasSubscribers => _handlers.Length > 0;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (_comparer.Equals(_value, value))
                    return;

                _value = value;
                Notify();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetWithoutNotify(T value)
        {
            _value = value;
        }

        /// <summary>
        /// Subscribes <paramref name="handler"/> to value changes and returns a
        /// <see cref="Strada.Core.SubscriptionToken"/> that, when disposed, removes
        /// exactly this handler. Callers that ignore the return value retain the
        /// previous behaviour and must call <see cref="Unsubscribe(Action{T})"/>
        /// manually.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var current = _handlers;
            var grown = new Action<T>[current.Length + 1];
            Array.Copy(current, grown, current.Length);
            grown[current.Length] = handler;
            _handlers = grown;
            return new Strada.Core.SubscriptionToken(() => Unsubscribe(handler));
        }

        /// <summary>
        /// Subscribes <paramref name="handler"/>, invokes it once with the current value,
        /// and returns a token whose <see cref="Strada.Core.SubscriptionToken.Dispose"/>
        /// removes the handler.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Strada.Core.SubscriptionToken SubscribeAndInvoke(Action<T> handler)
        {
            var token = Subscribe(handler);
            handler(_value);
            return token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Unsubscribe(Action<T> handler)
        {
            var current = _handlers;
            for (int i = current.Length - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(current[i], handler)) continue;

                if (current.Length == 1)
                {
                    _handlers = Array.Empty<Action<T>>();
                    return;
                }

                var shrunk = new Action<T>[current.Length - 1];
                Array.Copy(current, 0, shrunk, 0, i);
                Array.Copy(current, i + 1, shrunk, i, current.Length - i - 1);
                _handlers = shrunk;
                return;
            }
        }

        /// <summary>
        /// Notifies all subscribers of the current value.
        /// </summary>
        /// <remarks>
        /// Uses a snapshot approach to safely handle cases where handlers modify the subscriber list.
        /// If a handler calls Subscribe/Unsubscribe during notification, the changes will take effect
        /// on the next notification cycle.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Notify()
        {
            // Zero allocation: the array is immutable once published, so reading the field
            // IS the snapshot. A handler that subscribes/unsubscribes during notification
            // swaps in a new array and takes effect on the next cycle, exactly as before.
            var snapshot = _handlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_value);
        }

        public void Clear()
        {
            _handlers = Array.Empty<Action<T>>();
        }


        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }

        public static implicit operator T(ReactiveProperty<T> property) => property.Value;
    }

    public sealed class ReactiveCollection<T> : IDisposable
    {
        private readonly List<T> _items = new();
        // Copy-on-write, as in ReactiveProperty<T> above: notification must not allocate.
        private Action<T>[] _addHandlers = Array.Empty<Action<T>>();
        private Action<T>[] _removeHandlers = Array.Empty<Action<T>>();
        private Action[] _clearHandlers = Array.Empty<Action>();
        private bool _disposed;

        public int Count => _items.Count;
        public T this[int index] => _items[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            _items.Add(item);
            NotifyAdd(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(T item)
        {
            if (_items.Remove(item))
            {
                NotifyRemove(item);
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
            var item = _items[index];
            _items.RemoveAt(index);
            NotifyRemove(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _items.Clear();
            NotifyClear();
        }

        public void OnAdd(Action<T> handler) => _addHandlers = AppendHandler(_addHandlers, handler);
        public void OnRemove(Action<T> handler) => _removeHandlers = AppendHandler(_removeHandlers, handler);
        public void OnClear(Action handler) => _clearHandlers = AppendHandler(_clearHandlers, handler);

        public void OffAdd(Action<T> handler) => _addHandlers = RemoveHandler(_addHandlers, handler);
        public void OffRemove(Action<T> handler) => _removeHandlers = RemoveHandler(_removeHandlers, handler);
        public void OffClear(Action handler) => _clearHandlers = RemoveHandler(_clearHandlers, handler);

        private void NotifyAdd(T item)
        {
            var snapshot = _addHandlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](item);
        }

        private void NotifyRemove(T item)
        {
            var snapshot = _removeHandlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](item);
        }

        private void NotifyClear()
        {
            var snapshot = _clearHandlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i]();
        }

        private static THandler[] AppendHandler<THandler>(THandler[] current, THandler handler)
            where THandler : class
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var grown = new THandler[current.Length + 1];
            Array.Copy(current, grown, current.Length);
            grown[current.Length] = handler;
            return grown;
        }

        private static THandler[] RemoveHandler<THandler>(THandler[] current, THandler handler)
            where THandler : class
        {
            for (int i = current.Length - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(current[i], handler)) continue;
                if (current.Length == 1) return Array.Empty<THandler>();
                var shrunk = new THandler[current.Length - 1];
                Array.Copy(current, 0, shrunk, 0, i);
                Array.Copy(current, i + 1, shrunk, i, current.Length - i - 1);
                return shrunk;
            }
            return current;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _items.Clear();
            _addHandlers = Array.Empty<Action<T>>();
            _removeHandlers = Array.Empty<Action<T>>();
            _clearHandlers = Array.Empty<Action>();
        }
    }
}
