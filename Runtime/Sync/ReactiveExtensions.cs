using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Strada.Core.Sync
{
    public static class ReactiveExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MappedProperty<TSource, TResult> Select<TSource, TResult>(
            this IReadOnlyReactiveProperty<TSource> source,
            Func<TSource, TResult> selector)
        {
            return new MappedProperty<TSource, TResult>(source, selector);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FilteredProperty<T> Where<T>(
            this IReadOnlyReactiveProperty<T> source,
            Func<T, bool> predicate)
        {
            return new FilteredProperty<T>(source, predicate);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CombinedProperty<T1, T2, TResult> CombineLatest<T1, T2, TResult>(
            this IReadOnlyReactiveProperty<T1> source1,
            IReadOnlyReactiveProperty<T2> source2,
            Func<T1, T2, TResult> combiner)
        {
            return new CombinedProperty<T1, T2, TResult>(source1, source2, combiner);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CombinedProperty<T1, T2, T3, TResult> CombineLatest<T1, T2, T3, TResult>(
            this IReadOnlyReactiveProperty<T1> source1,
            IReadOnlyReactiveProperty<T2> source2,
            IReadOnlyReactiveProperty<T3> source3,
            Func<T1, T2, T3, TResult> combiner)
        {
            return new CombinedProperty<T1, T2, T3, TResult>(source1, source2, source3, combiner);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ThrottledProperty<T> Throttle<T>(
            this IReadOnlyReactiveProperty<T> source,
            float intervalSeconds)
        {
            return new ThrottledProperty<T>(source, intervalSeconds);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DistinctProperty<T> DistinctUntilChanged<T>(
            this IReadOnlyReactiveProperty<T> source)
        {
            return new DistinctProperty<T>(source);
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
    }

    public sealed class MappedProperty<TSource, TResult> : IReadOnlyReactiveProperty<TResult>, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<TSource> _source;
        private readonly Func<TSource, TResult> _selector;
        private readonly List<Action<TResult>> _handlers = new(4);
        private Strada.Core.SubscriptionToken _sourceToken;
        private TResult _cachedValue;
        private bool _disposed;

        public TResult Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _cachedValue;
        }

        public MappedProperty(IReadOnlyReactiveProperty<TSource> source, Func<TSource, TResult> selector)
        {
            _source = source;
            _selector = selector;
            _cachedValue = _selector(_source.Value);
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(TSource value)
        {
            _cachedValue = _selector(value);
            for (int i = 0; i < _handlers.Count; i++)
                _handlers[i](_cachedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<TResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() => _handlers.Remove(handler));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
            _handlers.Clear();
        }
    }

    public sealed class FilteredProperty<T> : IReadOnlyReactiveProperty<T>, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T> _source;
        private readonly Func<T, bool> _predicate;
        private readonly List<Action<T>> _handlers = new(4);
        private Strada.Core.SubscriptionToken _sourceToken;
        private T _lastValidValue;
        private bool _disposed;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _lastValidValue;
        }

        public FilteredProperty(IReadOnlyReactiveProperty<T> source, Func<T, bool> predicate)
        {
            _source = source;
            _predicate = predicate;
            if (_predicate(_source.Value))
                _lastValidValue = _source.Value;
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value)
        {
            if (!_predicate(value)) return;
            _lastValidValue = value;
            for (int i = 0; i < _handlers.Count; i++)
                _handlers[i](value);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() => _handlers.Remove(handler));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
            _handlers.Clear();
        }
    }

    public sealed class CombinedProperty<T1, T2, TResult> : IReadOnlyReactiveProperty<TResult>, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T1> _source1;
        private readonly IReadOnlyReactiveProperty<T2> _source2;
        private readonly Func<T1, T2, TResult> _combiner;
        private readonly List<Action<TResult>> _handlers = new(4);
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
            Func<T1, T2, TResult> combiner)
        {
            _source1 = source1;
            _source2 = source2;
            _combiner = combiner;
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
            for (int i = 0; i < _handlers.Count; i++)
                _handlers[i](_cachedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<TResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() => _handlers.Remove(handler));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source1Token?.Dispose();
            _source2Token?.Dispose();
            _handlers.Clear();
        }
    }

    public sealed class CombinedProperty<T1, T2, T3, TResult> : IReadOnlyReactiveProperty<TResult>, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T1> _source1;
        private readonly IReadOnlyReactiveProperty<T2> _source2;
        private readonly IReadOnlyReactiveProperty<T3> _source3;
        private readonly Func<T1, T2, T3, TResult> _combiner;
        private readonly List<Action<TResult>> _handlers = new(4);
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
            Func<T1, T2, T3, TResult> combiner)
        {
            _source1 = source1;
            _source2 = source2;
            _source3 = source3;
            _combiner = combiner;
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
            for (int i = 0; i < _handlers.Count; i++)
                _handlers[i](_cachedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<TResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() => _handlers.Remove(handler));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source1Token?.Dispose();
            _source2Token?.Dispose();
            _source3Token?.Dispose();
            _handlers.Clear();
        }
    }

    public sealed class ThrottledProperty<T> : IReadOnlyReactiveProperty<T>, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T> _source;
        private readonly float _interval;
        private readonly List<Action<T>> _handlers = new(4);
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

        public ThrottledProperty(IReadOnlyReactiveProperty<T> source, float intervalSeconds)
        {
            _source = source;
            _interval = intervalSeconds;
            _lastEmittedValue = _source.Value;
            _lastEmitTime = UnityEngine.Time.realtimeSinceStartup;
            _sourceToken = _source.Subscribe(OnSourceChanged);
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
                for (int i = 0; i < _handlers.Count; i++)
                    _handlers[i](value);
            }
            else
            {
                _pendingValue = value;
                _hasPending = true;
            }
        }

        public void Flush()
        {
            if (!_hasPending) return;
            _lastEmittedValue = _pendingValue;
            _lastEmitTime = UnityEngine.Time.realtimeSinceStartup;
            _hasPending = false;
            for (int i = 0; i < _handlers.Count; i++)
                _handlers[i](_lastEmittedValue);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() => _handlers.Remove(handler));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
            _handlers.Clear();
        }
    }

    public sealed class DistinctProperty<T> : IReadOnlyReactiveProperty<T>, IDisposable
    {
        private readonly IReadOnlyReactiveProperty<T> _source;
        private readonly EqualityComparer<T> _comparer = EqualityComparer<T>.Default;
        private readonly List<Action<T>> _handlers = new(4);
        private Strada.Core.SubscriptionToken _sourceToken;
        private T _lastValue;
        private bool _disposed;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _lastValue;
        }

        public DistinctProperty(IReadOnlyReactiveProperty<T> source)
        {
            _source = source;
            _lastValue = _source.Value;
            _sourceToken = _source.Subscribe(OnSourceChanged);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value)
        {
            if (_comparer.Equals(_lastValue, value)) return;
            _lastValue = value;
            for (int i = 0; i < _handlers.Count; i++)
                _handlers[i](value);
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() => _handlers.Remove(handler));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sourceToken?.Dispose();
            _handlers.Clear();
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
