using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Strada.Core.Sync
{
    /// <summary>
    /// Marks a reactive property that was produced by an operator rather than owned by the
    /// caller. Operators created through the extension methods below dispose sources carrying
    /// this marker, because a chained intermediate — the <c>Select</c> in
    /// <c>src.Select(f).Where(p)</c> — is unreachable to the caller yet stays subscribed to the
    /// root property forever if nobody disposes it.
    /// </summary>
    internal interface IReactiveOperator : IDisposable
    {
    }

    public static class ReactiveExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MappedProperty<TSource, TResult> Select<TSource, TResult>(
            this IReadOnlyReactiveProperty<TSource> source,
            Func<TSource, TResult> selector)
        {
            return new MappedProperty<TSource, TResult>(source, selector, ownsSource: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FilteredProperty<T> Where<T>(
            this IReadOnlyReactiveProperty<T> source,
            Func<T, bool> predicate)
        {
            return new FilteredProperty<T>(source, predicate, ownsSource: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CombinedProperty<T1, T2, TResult> CombineLatest<T1, T2, TResult>(
            this IReadOnlyReactiveProperty<T1> source1,
            IReadOnlyReactiveProperty<T2> source2,
            Func<T1, T2, TResult> combiner)
        {
            return new CombinedProperty<T1, T2, TResult>(source1, source2, combiner, ownsSources: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CombinedProperty<T1, T2, T3, TResult> CombineLatest<T1, T2, T3, TResult>(
            this IReadOnlyReactiveProperty<T1> source1,
            IReadOnlyReactiveProperty<T2> source2,
            IReadOnlyReactiveProperty<T3> source3,
            Func<T1, T2, T3, TResult> combiner)
        {
            return new CombinedProperty<T1, T2, T3, TResult>(source1, source2, source3, combiner, ownsSources: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ThrottledProperty<T> Throttle<T>(
            this IReadOnlyReactiveProperty<T> source,
            float intervalSeconds)
        {
            return new ThrottledProperty<T>(source, intervalSeconds, ownsSource: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DistinctProperty<T> DistinctUntilChanged<T>(
            this IReadOnlyReactiveProperty<T> source)
        {
            return new DistinctProperty<T>(source, ownsSource: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable BindTo<T>(
            this IReadOnlyReactiveProperty<T> source,
            ReactiveProperty<T> target)
        {
            return new PropertyBinding<T>(source, target);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable BindTo<TSource, TTarget>(
            this IReadOnlyReactiveProperty<TSource> source,
            ReactiveProperty<TTarget> target,
            Func<TSource, TTarget> converter)
        {
            return new ConvertedBinding<TSource, TTarget>(source, target, converter);
        }

        /// <summary>
        /// Disposes <paramref name="source"/> only if it is an operator this API created. A
        /// source the caller owns — a plain ReactiveProperty, or an operator the caller built
        /// with <c>new</c> — is left alone.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DisposeIfOperator(object source)
        {
            if (source is IReactiveOperator op)
                op.Dispose();
        }
    }

    public sealed class MappedProperty<TSource, TResult>
        : IReadOnlyReactiveProperty<TResult>, IUntypedReactiveProperty, IReactiveOperator, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<TSource> _source;
        private readonly Func<TSource, TResult> _selector;
        private readonly bool _ownsSource;
        private Action<TResult>[] _handlers = Array.Empty<Action<TResult>>();
        private Strada.Core.SubscriptionToken _sourceToken;
        private TResult _cachedValue;
        private bool _disposed;

        public TResult Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _cachedValue;
        }

        public MappedProperty(
            IReadOnlyReactiveProperty<TSource> source,
            Func<TSource, TResult> selector,
            bool ownsSource = false)
        {
            _source = source;
            _selector = selector;
            _ownsSource = ownsSource;
            _cachedValue = _selector(_source.Value);
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(TSource value)
        {
            _cachedValue = _selector(value);
            // Iterate the published array, not a live list: a handler that disposes its own
            // token during the callback would otherwise shift the list and skip its successor.
            var snapshot = _handlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_cachedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<TResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers = ReactiveHandlers.Append(_handlers, handler);
            return new Strada.Core.SubscriptionToken(
                () => _handlers = ReactiveHandlers.Remove(_handlers, handler));
        }

        /// <inheritdoc />
        public IDisposable SubscribeUntyped(Action onChanged)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            return Subscribe(_ => onChanged());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
            _handlers = Array.Empty<Action<TResult>>();
            if (_ownsSource) ReactiveExtensions.DisposeIfOperator(_source);
        }
    }

    public sealed class FilteredProperty<T>
        : IReadOnlyReactiveProperty<T>, IUntypedReactiveProperty, IReactiveOperator, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T> _source;
        private readonly Func<T, bool> _predicate;
        private readonly bool _ownsSource;
        private Action<T>[] _handlers = Array.Empty<Action<T>>();
        private Strada.Core.SubscriptionToken _sourceToken;
        private T _lastValidValue;
        private bool _disposed;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _lastValidValue;
        }

        public FilteredProperty(
            IReadOnlyReactiveProperty<T> source,
            Func<T, bool> predicate,
            bool ownsSource = false)
        {
            _source = source;
            _predicate = predicate;
            _ownsSource = ownsSource;
            if (_predicate(_source.Value))
                _lastValidValue = _source.Value;
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value)
        {
            if (!_predicate(value)) return;
            _lastValidValue = value;
            var snapshot = _handlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](value);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers = ReactiveHandlers.Append(_handlers, handler);
            return new Strada.Core.SubscriptionToken(
                () => _handlers = ReactiveHandlers.Remove(_handlers, handler));
        }

        /// <inheritdoc />
        public IDisposable SubscribeUntyped(Action onChanged)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            return Subscribe(_ => onChanged());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
            _handlers = Array.Empty<Action<T>>();
            if (_ownsSource) ReactiveExtensions.DisposeIfOperator(_source);
        }
    }

    public sealed class CombinedProperty<T1, T2, TResult>
        : IReadOnlyReactiveProperty<TResult>, IUntypedReactiveProperty, IReactiveOperator, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T1> _source1;
        private readonly IReadOnlyReactiveProperty<T2> _source2;
        private readonly Func<T1, T2, TResult> _combiner;
        private readonly bool _ownsSources;
        private Action<TResult>[] _handlers = Array.Empty<Action<TResult>>();
        private Strada.Core.SubscriptionToken _source1Token;
        private Strada.Core.SubscriptionToken _source2Token;
        private TResult _cachedValue;
        private bool _disposed;

        public TResult Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _cachedValue;
        }

        public CombinedProperty(
            IReadOnlyReactiveProperty<T1> source1,
            IReadOnlyReactiveProperty<T2> source2,
            Func<T1, T2, TResult> combiner,
            bool ownsSources = false)
        {
            _source1 = source1;
            _source2 = source2;
            _combiner = combiner;
            _ownsSources = ownsSources;
            _cachedValue = _combiner(_source1.Value, _source2.Value);
            _source1Token = _source1.Subscribe(OnSource1Changed);
            _source2Token = _source2.Subscribe(OnSource2Changed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSource1Changed(T1 _) => UpdateValue();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSource2Changed(T2 _) => UpdateValue();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateValue()
        {
            _cachedValue = _combiner(_source1.Value, _source2.Value);
            var snapshot = _handlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_cachedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<TResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers = ReactiveHandlers.Append(_handlers, handler);
            return new Strada.Core.SubscriptionToken(
                () => _handlers = ReactiveHandlers.Remove(_handlers, handler));
        }

        /// <inheritdoc />
        public IDisposable SubscribeUntyped(Action onChanged)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            return Subscribe(_ => onChanged());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source1Token?.Dispose();
            _source2Token?.Dispose();
            _handlers = Array.Empty<Action<TResult>>();
            if (_ownsSources)
            {
                ReactiveExtensions.DisposeIfOperator(_source1);
                ReactiveExtensions.DisposeIfOperator(_source2);
            }
        }
    }

    public sealed class CombinedProperty<T1, T2, T3, TResult>
        : IReadOnlyReactiveProperty<TResult>, IUntypedReactiveProperty, IReactiveOperator, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T1> _source1;
        private readonly IReadOnlyReactiveProperty<T2> _source2;
        private readonly IReadOnlyReactiveProperty<T3> _source3;
        private readonly Func<T1, T2, T3, TResult> _combiner;
        private readonly bool _ownsSources;
        private Action<TResult>[] _handlers = Array.Empty<Action<TResult>>();
        private Strada.Core.SubscriptionToken _source1Token;
        private Strada.Core.SubscriptionToken _source2Token;
        private Strada.Core.SubscriptionToken _source3Token;
        private TResult _cachedValue;
        private bool _disposed;

        public TResult Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _cachedValue;
        }

        public CombinedProperty(
            IReadOnlyReactiveProperty<T1> source1,
            IReadOnlyReactiveProperty<T2> source2,
            IReadOnlyReactiveProperty<T3> source3,
            Func<T1, T2, T3, TResult> combiner,
            bool ownsSources = false)
        {
            _source1 = source1;
            _source2 = source2;
            _source3 = source3;
            _combiner = combiner;
            _ownsSources = ownsSources;
            _cachedValue = _combiner(_source1.Value, _source2.Value, _source3.Value);
            _source1Token = _source1.Subscribe(OnSource1Changed);
            _source2Token = _source2.Subscribe(OnSource2Changed);
            _source3Token = _source3.Subscribe(OnSource3Changed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSource1Changed(T1 _) => UpdateValue();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSource2Changed(T2 _) => UpdateValue();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSource3Changed(T3 _) => UpdateValue();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateValue()
        {
            _cachedValue = _combiner(_source1.Value, _source2.Value, _source3.Value);
            var snapshot = _handlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_cachedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<TResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers = ReactiveHandlers.Append(_handlers, handler);
            return new Strada.Core.SubscriptionToken(
                () => _handlers = ReactiveHandlers.Remove(_handlers, handler));
        }

        /// <inheritdoc />
        public IDisposable SubscribeUntyped(Action onChanged)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            return Subscribe(_ => onChanged());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source1Token?.Dispose();
            _source2Token?.Dispose();
            _source3Token?.Dispose();
            _handlers = Array.Empty<Action<TResult>>();
            if (_ownsSources)
            {
                ReactiveExtensions.DisposeIfOperator(_source1);
                ReactiveExtensions.DisposeIfOperator(_source2);
                ReactiveExtensions.DisposeIfOperator(_source3);
            }
        }
    }

    public sealed class ThrottledProperty<T>
        : IReadOnlyReactiveProperty<T>, IUntypedReactiveProperty, IReactiveOperator, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T> _source;
        private readonly float _interval;
        private readonly bool _ownsSource;
        // Drains the trailing value once the window closes. Without a pump, a change that
        // arrives inside the window and is followed by silence is stashed in _pendingValue and
        // never emitted — Value keeps reporting the pre-change value forever.
        private readonly Action<float> _drain;
        private Action<T>[] _handlers = Array.Empty<Action<T>>();
        private Strada.Core.SubscriptionToken _sourceToken;
        private T _pendingValue;
        private T _lastEmittedValue;
        private float _lastEmitTime;
        private bool _hasPending;
        private bool _disposed;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _lastEmittedValue;
        }

        public ThrottledProperty(
            IReadOnlyReactiveProperty<T> source,
            float intervalSeconds,
            bool ownsSource = false)
        {
            _source = source;
            _interval = intervalSeconds;
            _ownsSource = ownsSource;
            _lastEmittedValue = _source.Value;
            _lastEmitTime = UnityEngine.Time.realtimeSinceStartup;
            _sourceToken = _source.Subscribe(OnSourceChanged);

            _drain = DrainPending;
            Strada.Core.Core.PlayerLoop.RegisterUpdate(_drain);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastEmitTime >= _interval)
            {
                _lastEmittedValue = value;
                _lastEmitTime = now;
                _hasPending = false;
                var snapshot = _handlers;
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i](value);
            }
            else
            {
                _pendingValue = value;
                _hasPending = true;
            }
        }

        private void DrainPending(float deltaTime)
        {
            if (_disposed || !_hasPending) return;
            if (UnityEngine.Time.realtimeSinceStartup - _lastEmitTime < _interval) return;
            Flush();
        }

        public void Flush()
        {
            if (!_hasPending) return;
            _lastEmittedValue = _pendingValue;
            _lastEmitTime = UnityEngine.Time.realtimeSinceStartup;
            _hasPending = false;
            var snapshot = _handlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](_lastEmittedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers = ReactiveHandlers.Append(_handlers, handler);
            return new Strada.Core.SubscriptionToken(
                () => _handlers = ReactiveHandlers.Remove(_handlers, handler));
        }

        /// <inheritdoc />
        public IDisposable SubscribeUntyped(Action onChanged)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            return Subscribe(_ => onChanged());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Strada.Core.Core.PlayerLoop.UnregisterUpdate(_drain);
            _sourceToken?.Dispose();
            _handlers = Array.Empty<Action<T>>();
            if (_ownsSource) ReactiveExtensions.DisposeIfOperator(_source);
        }
    }

    public sealed class DistinctProperty<T>
        : IReadOnlyReactiveProperty<T>, IUntypedReactiveProperty, IReactiveOperator, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T> _source;
        private readonly EqualityComparer<T> _comparer = EqualityComparer<T>.Default;
        private readonly bool _ownsSource;
        private Action<T>[] _handlers = Array.Empty<Action<T>>();
        private Strada.Core.SubscriptionToken _sourceToken;
        private T _lastValue;
        private bool _disposed;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _lastValue;
        }

        public DistinctProperty(IReadOnlyReactiveProperty<T> source, bool ownsSource = false)
        {
            _source = source;
            _ownsSource = ownsSource;
            _lastValue = _source.Value;
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value)
        {
            if (_comparer.Equals(_lastValue, value)) return;
            _lastValue = value;
            var snapshot = _handlers;
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](value);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers = ReactiveHandlers.Append(_handlers, handler);
            return new Strada.Core.SubscriptionToken(
                () => _handlers = ReactiveHandlers.Remove(_handlers, handler));
        }

        /// <inheritdoc />
        public IDisposable SubscribeUntyped(Action onChanged)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            return Subscribe(_ => onChanged());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
            _handlers = Array.Empty<Action<T>>();
            if (_ownsSource) ReactiveExtensions.DisposeIfOperator(_source);
        }
    }

    public sealed class PropertyBinding<T> : IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T> _source;
        private readonly ReactiveProperty<T> _target;
        private Strada.Core.SubscriptionToken _sourceToken;
        private bool _disposed;

        public PropertyBinding(IReadOnlyReactiveProperty<T> source, ReactiveProperty<T> target)
        {
            _source = source;
            _target = target;
            _target.SetWithoutNotify(_source.Value);
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value) => _target.Value = value;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
        }
    }

    public sealed class ConvertedBinding<TSource, TTarget> : IDisposable
    {
        private readonly IReadOnlyReactiveProperty<TSource> _source;
        private readonly ReactiveProperty<TTarget> _target;
        private readonly Func<TSource, TTarget> _converter;
        private Strada.Core.SubscriptionToken _sourceToken;
        private bool _disposed;

        public ConvertedBinding(
            IReadOnlyReactiveProperty<TSource> source,
            ReactiveProperty<TTarget> target,
            Func<TSource, TTarget> converter)
        {
            _source = source;
            _target = target;
            _converter = converter;
            _target.SetWithoutNotify(_converter(_source.Value));
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(TSource value) => _target.Value = _converter(value);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
        }
    }
}
