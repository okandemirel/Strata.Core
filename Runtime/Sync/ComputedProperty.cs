using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Strada.Core.Sync
{
    public sealed class ComputedProperty<T> : IReadOnlyReactiveProperty<T>, IUntypedReactiveProperty, IDisposable
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> s_subscribeMethodCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> s_invalidateMethodCache = new();
        private static readonly MethodInfo s_invalidateIgnoreParamMethod = typeof(ComputedProperty<T>)
            .GetMethod("InvalidateIgnoreParam", BindingFlags.NonPublic | BindingFlags.Instance);

        // Built once per closed generic so the notify guard can name the offending property
        // without formatting anything on the hot path.
        private static readonly string s_diagnosticName = "ComputedProperty<" + typeof(T).Name + ">";

        private readonly Func<T> _computation;
        // Copy-on-write, as in ReactiveProperty<T>: notification reads an array that can no
        // longer change under it. Indexing a live List while invoking arbitrary handlers
        // skipped the next handler whenever one unsubscribed itself from inside the callback.
        private Action<T>[] _handlers = Array.Empty<Action<T>>();
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
            if (dependency == null)
                throw new ArgumentNullException(nameof(dependency),
                    "FromMany was given a null dependency; every element of the dependency array must be a reactive property.");

            // Static path: no reflection at all. Every reactive property in the framework
            // implements IUntypedReactiveProperty precisely so this call stays AOT-safe.
            if (dependency is IUntypedReactiveProperty untyped)
            {
                _subscriptions.Add(untyped.SubscribeUntyped(Invalidate));
                return;
            }

            var type = dependency.GetType();
            var interfaces = type.GetInterfaces();

            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyReactiveProperty<>))
                {
                    var depType = iface.GetGenericArguments()[0];

                    // Reflective fallback for third-party IReadOnlyReactiveProperty<> types that
                    // do not implement IUntypedReactiveProperty. MakeGenericMethod on a value-type
                    // argument needs a closed instantiation IL2CPP cannot see, so this path can
                    // throw ExecutionEngineException on AOT players — see AotHints below.
                    var subscribeMethod = s_subscribeMethodCache.GetOrAdd(iface, i => i.GetMethod("Subscribe"));

                    var invalidateMethod = s_invalidateMethodCache.GetOrAdd(depType,
                        dt => s_invalidateIgnoreParamMethod.MakeGenericMethod(dt));

                    var handlerType = typeof(Action<>).MakeGenericType(depType);
                    var handler = Delegate.CreateDelegate(handlerType, this, invalidateMethod);

                    var token = (IDisposable)subscribeMethod.Invoke(dependency, new object[] { handler });
                    _subscriptions.Add(token);
                    return;
                }
            }

            // Falling off the end used to return silently, producing a computed property that
            // compiled fine and then never updated for the rest of its life.
            throw new ArgumentException(
                $"FromMany dependency of type {type.Name} does not implement IReadOnlyReactiveProperty<>; it cannot be watched.",
                nameof(dependency));
        }

        // Reached only through Delegate.CreateDelegate, so Unity's managed-code stripping
        // (Medium by default on IL2CPP) would otherwise remove it and leave the cached
        // MethodInfo null.
        [UnityEngine.Scripting.Preserve]
        private void InvalidateIgnoreParam<TIgnored>(TIgnored _) => Invalidate();

        /// <summary>
        /// Never called. It exists so IL2CPP generates the closed
        /// <c>InvalidateIgnoreParam&lt;T&gt;</c> instantiations for the common value types the
        /// reflective fallback in <see cref="WatchUntypedDependency"/> needs; AOT cannot create
        /// them on demand.
        /// </summary>
        [UnityEngine.Scripting.Preserve]
        private static void AotHints(ComputedProperty<T> instance)
        {
            instance.InvalidateIgnoreParam<int>(default);
            instance.InvalidateIgnoreParam<long>(default);
            instance.InvalidateIgnoreParam<float>(default);
            instance.InvalidateIgnoreParam<double>(default);
            instance.InvalidateIgnoreParam<bool>(default);
            instance.InvalidateIgnoreParam<byte>(default);
            instance.InvalidateIgnoreParam<UnityEngine.Vector2>(default);
            instance.InvalidateIgnoreParam<UnityEngine.Vector3>(default);
            instance.InvalidateIgnoreParam<UnityEngine.Vector4>(default);
            instance.InvalidateIgnoreParam<UnityEngine.Quaternion>(default);
            instance.InvalidateIgnoreParam<UnityEngine.Color>(default);
            instance.InvalidateIgnoreParam<object>(default);
        }

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

            // Nobody is listening, so leave the value dirty and let the next read of Value pay
            // for the recomputation. Reading Value back here defeated the lazy cache entirely:
            // a chain of N computed properties ran N full computations per dependency change
            // even when the result was never observed.
            var snapshot = _handlers;
            if (snapshot.Length == 0) return;

            var newValue = Value;
            if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return;

            // Handlers may write back into the graph, so the same cycle hazard as
            // ReactiveProperty.Notify applies here — and, as there, the guard is carried only
            // in Editor and Development builds so a shipped player does not pay a
            // thread-static read and an inlining-blocking try/finally per notification.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!ReactiveNotifyGuard.TryEnter())
            {
                ReactiveNotifyGuard.ReportOverflow(s_diagnosticName);
                return;
            }
            try
            {
#endif
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i](newValue);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            }
            finally
            {
                ReactiveNotifyGuard.Exit();
            }
#endif
        }

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

        /// <inheritdoc />
        public IDisposable SubscribeUntyped(Action onChanged)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            return Subscribe(_ => onChanged());
        }

        private void Unsubscribe(Action<T> handler)
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var sub in _subscriptions)
                sub.Dispose();

            _subscriptions.Clear();
            _handlers = Array.Empty<Action<T>>();
        }

    }
}
