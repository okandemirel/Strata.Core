using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Strada.Core.Sync
{
    public sealed class ComputedProperty<T> : IReadOnlyReactiveProperty<T>, IDisposable
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> s_subscribeMethodCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> s_invalidateMethodCache = new();
        private static readonly MethodInfo s_invalidateIgnoreParamMethod = typeof(ComputedProperty<T>)
            .GetMethod("InvalidateIgnoreParam", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly Func<T> _computation;
        private readonly List<Action<T>> _handlers = new(4);
        private readonly List<IDisposable> _subscriptions = new(4);
        private T _cachedValue;
        private bool _isDirty = true;
        private bool _disposed;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_isDirty)
                {
                    _cachedValue = _computation();
                    _isDirty = false;
                }
                return _cachedValue;
            }
        }

        private ComputedProperty(Func<T> computation)
        {
            _computation = computation;
            _cachedValue = _computation();
            _isDirty = false;
        }

        public static ComputedProperty<T> From<T1>(
            IReadOnlyReactiveProperty<T1> dep1,
            Func<T1, T> computation)
        {
            var computed = new ComputedProperty<T>(() => computation(dep1.Value));
            computed.WatchDependency(dep1);
            return computed;
        }

        public static ComputedProperty<T> From<T1, T2>(
            IReadOnlyReactiveProperty<T1> dep1,
            IReadOnlyReactiveProperty<T2> dep2,
            Func<T1, T2, T> computation)
        {
            var computed = new ComputedProperty<T>(() => computation(dep1.Value, dep2.Value));
            computed.WatchDependency(dep1);
            computed.WatchDependency(dep2);
            return computed;
        }

        public static ComputedProperty<T> From<T1, T2, T3>(
            IReadOnlyReactiveProperty<T1> dep1,
            IReadOnlyReactiveProperty<T2> dep2,
            IReadOnlyReactiveProperty<T3> dep3,
            Func<T1, T2, T3, T> computation)
        {
            var computed = new ComputedProperty<T>(() => computation(dep1.Value, dep2.Value, dep3.Value));
            computed.WatchDependency(dep1);
            computed.WatchDependency(dep2);
            computed.WatchDependency(dep3);
            return computed;
        }

        public static ComputedProperty<T> From<T1, T2, T3, T4>(
            IReadOnlyReactiveProperty<T1> dep1,
            IReadOnlyReactiveProperty<T2> dep2,
            IReadOnlyReactiveProperty<T3> dep3,
            IReadOnlyReactiveProperty<T4> dep4,
            Func<T1, T2, T3, T4, T> computation)
        {
            var computed = new ComputedProperty<T>(() => computation(dep1.Value, dep2.Value, dep3.Value, dep4.Value));
            computed.WatchDependency(dep1);
            computed.WatchDependency(dep2);
            computed.WatchDependency(dep3);
            computed.WatchDependency(dep4);
            return computed;
        }

        public static ComputedProperty<T> From<T1, T2, T3, T4, T5>(
            IReadOnlyReactiveProperty<T1> dep1,
            IReadOnlyReactiveProperty<T2> dep2,
            IReadOnlyReactiveProperty<T3> dep3,
            IReadOnlyReactiveProperty<T4> dep4,
            IReadOnlyReactiveProperty<T5> dep5,
            Func<T1, T2, T3, T4, T5, T> computation)
        {
            var computed = new ComputedProperty<T>(() => computation(dep1.Value, dep2.Value, dep3.Value, dep4.Value, dep5.Value));
            computed.WatchDependency(dep1);
            computed.WatchDependency(dep2);
            computed.WatchDependency(dep3);
            computed.WatchDependency(dep4);
            computed.WatchDependency(dep5);
            return computed;
        }

        public static ComputedProperty<T> From<T1, T2, T3, T4, T5, T6>(
            IReadOnlyReactiveProperty<T1> dep1,
            IReadOnlyReactiveProperty<T2> dep2,
            IReadOnlyReactiveProperty<T3> dep3,
            IReadOnlyReactiveProperty<T4> dep4,
            IReadOnlyReactiveProperty<T5> dep5,
            IReadOnlyReactiveProperty<T6> dep6,
            Func<T1, T2, T3, T4, T5, T6, T> computation)
        {
            var computed = new ComputedProperty<T>(() => computation(dep1.Value, dep2.Value, dep3.Value, dep4.Value, dep5.Value, dep6.Value));
            computed.WatchDependency(dep1);
            computed.WatchDependency(dep2);
            computed.WatchDependency(dep3);
            computed.WatchDependency(dep4);
            computed.WatchDependency(dep5);
            computed.WatchDependency(dep6);
            return computed;
        }

        /// <summary>
        /// Creates a computed property from a computation function and multiple dependencies.
        /// Use this when you need more than 6 dependencies.
        /// </summary>
        /// <param name="computation">The computation function that calculates the property value.</param>
        /// <param name="dependencies">The reactive properties this computation depends on (untyped).</param>
        /// <returns>A new computed property.</returns>
        public static ComputedProperty<T> FromMany(Func<T> computation, params object[] dependencies)
        {
            var computed = new ComputedProperty<T>(computation);
            foreach (var dep in dependencies)
            {
                computed.WatchUntypedDependency(dep);
            }
            return computed;
        }

        private void WatchUntypedDependency(object dependency)
        {
            var type = dependency.GetType();
            var interfaces = type.GetInterfaces();

            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyReactiveProperty<>))
                {
                    var depType = iface.GetGenericArguments()[0];

                    // Resolve the interface Subscribe(Action<TDep>) method which returns SubscriptionToken.
                    var subscribeMethod = s_subscribeMethodCache.GetOrAdd(iface, i => i.GetMethod("Subscribe"));

                    // Get or cache the generic InvalidateIgnoreParam method
                    var invalidateMethod = s_invalidateMethodCache.GetOrAdd(depType,
                        dt => s_invalidateIgnoreParamMethod.MakeGenericMethod(dt));

                    // Create handler delegate
                    var handlerType = typeof(Action<>).MakeGenericType(depType);
                    var handler = Delegate.CreateDelegate(handlerType, this, invalidateMethod);

                    var token = (IDisposable)subscribeMethod.Invoke(dependency, new object[] { handler });
                    _subscriptions.Add(token);
                    return;
                }
            }
        }

        private void InvalidateIgnoreParam<TIgnored>(TIgnored _) => Invalidate();

        private void WatchDependency<TDep>(IReadOnlyReactiveProperty<TDep> dependency)
        {
            Action<TDep> handler = _ => Invalidate();
            // Subscribe returns a SubscriptionToken (IDisposable) directly from the
            // interface — zero extra wrapper objects on the concrete-type fast path.
            _subscriptions.Add(dependency.Subscribe(handler));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Invalidate()
        {
            var oldValue = _cachedValue;
            _isDirty = true;
            var newValue = Value;

            if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                for (int i = 0; i < _handlers.Count; i++)
                    _handlers[i](newValue);
            }
        }

        public Strada.Core.SubscriptionToken Subscribe(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            return new Strada.Core.SubscriptionToken(() =>
            {
                for (int i = _handlers.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_handlers[i], handler))
                    {
                        _handlers.RemoveAt(i);
                        break;
                    }
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var sub in _subscriptions)
                sub.Dispose();

            _subscriptions.Clear();
            _handlers.Clear();
        }

    }
}
