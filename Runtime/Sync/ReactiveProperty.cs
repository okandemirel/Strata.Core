using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Strada.Core.Sync
{
    public interface IReadOnlyReactiveProperty<T>
    {
        T Value { get; }
        void Subscribe(Action<T> handler);

        /// <summary>
        /// Removes the given handler. Prefer disposing the
        /// <see cref="Strada.Core.SubscriptionToken"/> returned by
        /// <see cref="ReactiveProperty{T}.Subscribe(Action{T})"/> instead — disposal is
        /// idempotent and aggregates cleanly into a <see cref="BindingScope"/>.
        /// </summary>
        [Obsolete("Dispose the SubscriptionToken returned by Subscribe instead. " +
                  "Unsubscribe-by-reference will be removed in the next major release.",
                  error: false)]
        void Unsubscribe(Action<T> handler);
    }

    /// <summary>
    /// Helper that returns a <see cref="Strada.Core.SubscriptionToken"/> from any
    /// <see cref="IReadOnlyReactiveProperty{T}"/>, even when the static type is the
    /// interface (whose explicit <c>Subscribe</c> implementation returns <c>void</c>).
    /// </summary>
    public static class ReactivePropertySubscriptionExtensions
    {
        public static Strada.Core.SubscriptionToken SubscribeToken<T>(
            this IReadOnlyReactiveProperty<T> property,
            Action<T> handler)
        {
            if (property == null) throw new ArgumentNullException(nameof(property));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            // Concrete ReactiveProperty<T>'s class-level Subscribe already returns a token —
            // prefer it when we can see the concrete type.
            if (property is ReactiveProperty<T> concrete)
                return concrete.Subscribe(handler);

            // Interface fallback. Re-route through the explicit interface impl (void) and
            // synthesise a token that calls Unsubscribe on dispose. The call to the
            // [Obsolete] Unsubscribe is intentional — this helper is the migration path.
#pragma warning disable CS0618
            property.Subscribe(handler);
            return new Strada.Core.SubscriptionToken(() => property.Unsubscribe(handler));
#pragma warning restore CS0618
        }
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
        private readonly List<Action<T>> _handlers = new(4);
        private readonly EqualityComparer<T> _comparer = EqualityComparer<T>.Default;
        private bool _disposed;

        public ReactiveProperty() => _value = default;

        public ReactiveProperty(T initialValue) => _value = initialValue;

        public int SubscriberCount => _handlers.Count;
        public bool HasSubscribers => _handlers.Count > 0;

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
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() => Unsubscribe(handler));
        }

        // Explicit interface implementation preserves the IReadOnlyReactiveProperty<T>.Subscribe
        // void contract for callers that go through the interface.
        void IReadOnlyReactiveProperty<T>.Subscribe(Action<T> handler) => Subscribe(handler);

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
            for (int i = _handlers.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_handlers[i], handler))
                {
                    _handlers.RemoveAt(i);
                    break;
                }
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
            var snapshot = _handlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_value);
        }

        public void Clear()
        {
            _handlers.Clear();
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
        private readonly List<Action<T>> _addHandlers = new(4);
        private readonly List<Action<T>> _removeHandlers = new(4);
        private readonly List<Action> _clearHandlers = new(2);
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

        public void OnAdd(Action<T> handler) => _addHandlers.Add(handler);
        public void OnRemove(Action<T> handler) => _removeHandlers.Add(handler);
        public void OnClear(Action handler) => _clearHandlers.Add(handler);

        public void OffAdd(Action<T> handler) => _addHandlers.Remove(handler);
        public void OffRemove(Action<T> handler) => _removeHandlers.Remove(handler);
        public void OffClear(Action handler) => _clearHandlers.Remove(handler);

        private void NotifyAdd(T item)
        {
            var snapshot = _addHandlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](item);
        }

        private void NotifyRemove(T item)
        {
            var snapshot = _removeHandlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](item);
        }

        private void NotifyClear()
        {
            var snapshot = _clearHandlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i]();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _items.Clear();
            _addHandlers.Clear();
            _removeHandlers.Clear();
            _clearHandlers.Clear();
        }
    }
}
