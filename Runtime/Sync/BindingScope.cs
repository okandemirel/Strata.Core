using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Strada.Core.Sync
{
    public sealed class BindingScope : IDisposable
    {
        private readonly List<IDisposable> _disposables = new(8);
        private readonly List<SubscriptionHandle> _subscriptions = new(8);
        private bool _disposed;

        public T Track<T>(T disposable) where T : IDisposable
        {
            _disposables.Add(disposable);
            return disposable;
        }

        /// <summary>
        /// Adds a disposable (typically a <see cref="Strada.Core.SubscriptionToken"/>) to
        /// the scope so it is disposed in LIFO order when the scope itself is disposed.
        /// Returns the same instance so calls can be chained: <c>scope.Add(bus.Subscribe(...))</c>.
        /// </summary>
        public IDisposable Add(IDisposable disposable)
        {
            if (disposable == null) throw new ArgumentNullException(nameof(disposable));
            if (_disposed) { disposable.Dispose(); return disposable; }
            _disposables.Add(disposable);
            return disposable;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Subscribe<T>(IReadOnlyReactiveProperty<T> property, Action<T> handler)
        {
            property.Subscribe(handler);
            _subscriptions.Add(new SubscriptionHandle<T>(property, handler));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SubscribeAndInvoke<T>(ReactiveProperty<T> property, Action<T> handler)
        {
            property.SubscribeAndInvoke(handler);
            _subscriptions.Add(new SubscriptionHandle<T>(property, handler));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MappedProperty<TSource, TResult> Select<TSource, TResult>(
            IReadOnlyReactiveProperty<TSource> source,
            Func<TSource, TResult> selector)
        {
            var mapped = source.Select(selector);
            _disposables.Add(mapped);
            return mapped;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredProperty<T> Where<T>(
            IReadOnlyReactiveProperty<T> source,
            Func<T, bool> predicate)
        {
            var filtered = source.Where(predicate);
            _disposables.Add(filtered);
            return filtered;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CombinedProperty<T1, T2, TResult> CombineLatest<T1, T2, TResult>(
            IReadOnlyReactiveProperty<T1> source1,
            IReadOnlyReactiveProperty<T2> source2,
            Func<T1, T2, TResult> combiner)
        {
            var combined = source1.CombineLatest(source2, combiner);
            _disposables.Add(combined);
            return combined;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ComputedProperty<T> Computed<T1, T>(
            IReadOnlyReactiveProperty<T1> dep1,
            Func<T1, T> computation)
        {
            var computed = ComputedProperty<T>.From(dep1, computation);
            _disposables.Add(computed);
            return computed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ComputedProperty<T> Computed<T1, T2, T>(
            IReadOnlyReactiveProperty<T1> dep1,
            IReadOnlyReactiveProperty<T2> dep2,
            Func<T1, T2, T> computation)
        {
            var computed = ComputedProperty<T>.From(dep1, dep2, computation);
            _disposables.Add(computed);
            return computed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TwoWayBinding<T> BindTwoWay<T>(
            ReactiveProperty<T> source,
            ReactiveProperty<T> target)
        {
            var binding = new TwoWayBinding<T>(source, target);
            _disposables.Add(binding);
            return binding;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TwoWayBinding<TSource, TTarget> BindTwoWay<TSource, TTarget>(
            ReactiveProperty<TSource> source,
            ReactiveProperty<TTarget> target,
            Func<TSource, TTarget> toTarget,
            Func<TTarget, TSource> toSource)
        {
            var binding = new TwoWayBinding<TSource, TTarget>(source, target, toTarget, toSource);
            _disposables.Add(binding);
            return binding;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var sub in _subscriptions)
                sub.Unsubscribe();

            foreach (var disposable in _disposables)
                disposable.Dispose();

            _subscriptions.Clear();
            _disposables.Clear();
        }

        private abstract class SubscriptionHandle
        {
            public abstract void Unsubscribe();
        }

        private sealed class SubscriptionHandle<T> : SubscriptionHandle
        {
            private readonly IReadOnlyReactiveProperty<T> _property;
            private readonly Action<T> _handler;

            public SubscriptionHandle(IReadOnlyReactiveProperty<T> property, Action<T> handler)
            {
                _property = property;
                _handler = handler;
            }

            public override void Unsubscribe() => _property.Unsubscribe(_handler);
        }
    }

    public sealed class TwoWayBinding<T> : IDisposable
    {
        private readonly ReactiveProperty<T> _source;
        private readonly ReactiveProperty<T> _target;
        private readonly Action<T> _sourceHandler;
        private readonly Action<T> _targetHandler;
        private bool _updating;
        private bool _disposed;

        public TwoWayBinding(ReactiveProperty<T> source, ReactiveProperty<T> target)
        {
            _source = source;
            _target = target;
            _sourceHandler = OnSourceChanged;
            _targetHandler = OnTargetChanged;

            _target.SetWithoutNotify(_source.Value);
            _source.Subscribe(_sourceHandler);
            _target.Subscribe(_targetHandler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value)
        {
            if (_updating) return;
            _updating = true;
            try { _target.Value = value; }
            finally { _updating = false; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnTargetChanged(T value)
        {
            if (_updating) return;
            _updating = true;
            try { _source.Value = value; }
            finally { _updating = false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Unsubscribe(_sourceHandler);
            _target.Unsubscribe(_targetHandler);
        }
    }

    public sealed class TwoWayBinding<TSource, TTarget> : IDisposable
    {
        private readonly ReactiveProperty<TSource> _source;
        private readonly ReactiveProperty<TTarget> _target;
        private readonly Func<TSource, TTarget> _toTarget;
        private readonly Func<TTarget, TSource> _toSource;
        private readonly Action<TSource> _sourceHandler;
        private readonly Action<TTarget> _targetHandler;
        private bool _updating;
        private bool _disposed;

        public TwoWayBinding(
            ReactiveProperty<TSource> source,
            ReactiveProperty<TTarget> target,
            Func<TSource, TTarget> toTarget,
            Func<TTarget, TSource> toSource)
        {
            _source = source;
            _target = target;
            _toTarget = toTarget;
            _toSource = toSource;
            _sourceHandler = OnSourceChanged;
            _targetHandler = OnTargetChanged;

            _target.SetWithoutNotify(_toTarget(_source.Value));
            _source.Subscribe(_sourceHandler);
            _target.Subscribe(_targetHandler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(TSource value)
        {
            if (_updating) return;
            _updating = true;
            try { _target.Value = _toTarget(value); }
            finally { _updating = false; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnTargetChanged(TTarget value)
        {
            if (_updating) return;
            _updating = true;
            try { _source.Value = _toSource(value); }
            finally { _updating = false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Unsubscribe(_sourceHandler);
            _target.Unsubscribe(_targetHandler);
        }
    }

    public sealed class ValidatedBinding<T> : IDisposable
    {
        private readonly ReactiveProperty<T> _source;
        private readonly ReactiveProperty<T> _target;
        private readonly Func<T, bool> _validator;
        private readonly Action<T> _onInvalid;
        private readonly Action<T> _sourceHandler;
        private readonly Action<T> _targetHandler;
        private bool _updating;
        private bool _disposed;

        public ValidatedBinding(
            ReactiveProperty<T> source,
            ReactiveProperty<T> target,
            Func<T, bool> validator,
            Action<T> onInvalid = null)
        {
            _source = source;
            _target = target;
            _validator = validator;
            _onInvalid = onInvalid;
            _sourceHandler = OnSourceChanged;
            _targetHandler = OnTargetChanged;

            if (_validator(_source.Value))
                _target.SetWithoutNotify(_source.Value);

            _source.Subscribe(_sourceHandler);
            _target.Subscribe(_targetHandler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnSourceChanged(T value)
        {
            if (_updating) return;
            if (!_validator(value))
            {
                _onInvalid?.Invoke(value);
                return;
            }
            _updating = true;
            _target.Value = value;
            _updating = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnTargetChanged(T value)
        {
            if (_updating) return;
            if (!_validator(value))
            {
                _onInvalid?.Invoke(value);
                return;
            }
            _updating = true;
            _source.Value = value;
            _updating = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Unsubscribe(_sourceHandler);
            _target.Unsubscribe(_targetHandler);
        }
    }
}
