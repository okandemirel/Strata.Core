using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Strada.Core.Pooling
{
    public sealed class PoolRegistry : IDisposable
    {
        private readonly Dictionary<Type, object> _pools = new(32);
        private bool _disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ObjectPool<T> GetOrCreate<T>(Func<T> factory, int initialSize = 0, int maxSize = int.MaxValue) where T : class
        {
            var type = typeof(T);

            if (_pools.TryGetValue(type, out var existing))
                return (ObjectPool<T>)existing;

            var pool = new ObjectPool<T>(factory, initialSize, maxSize);
            _pools[type] = pool;
            return pool;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ObjectPool<T> Get<T>() where T : class
        {
            return _pools.TryGetValue(typeof(T), out var pool) ? (ObjectPool<T>)pool : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet<T>(out ObjectPool<T> pool) where T : class
        {
            if (_pools.TryGetValue(typeof(T), out var obj))
            {
                pool = (ObjectPool<T>)obj;
                return true;
            }
            pool = null;
            return false;
        }

        public void Register<T>(ObjectPool<T> pool) where T : class
        {
            _pools[typeof(T)] = pool;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Spawn<T>() where T : class
        {
            var pool = Get<T>();
            if (pool == null)
            {
                LogMissingPool(typeof(T), "Spawn");
                return null;
            }

            return pool.Spawn();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Despawn<T>(T instance) where T : class
        {
            if (instance == null) return;

            var pool = Get<T>();
            if (pool != null)
            {
                pool.Despawn(instance);
                return;
            }

            DespawnByRuntimeType(instance);
        }

        /// <summary>
        /// Slow path for a despawn whose static type argument did not name a registered pool.
        /// </summary>
        /// <remarks>
        /// Get&lt;T&gt; keys on the compile-time type argument, which is inferred from the
        /// variable at the call site rather than from the object, so returning a pooled object
        /// through a base-typed or interface-typed variable resolved the wrong key. Both misses
        /// used to be swallowed by `?.`, silently turning a zero-allocation pooled path into a
        /// full allocation per spawn with no diagnostic anywhere.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DespawnByRuntimeType(object instance)
        {
            var runtimeType = instance.GetType();

            if (_pools.TryGetValue(runtimeType, out var pool) && pool is IObjectPool nonGeneric
                && nonGeneric.DespawnObject(instance))
                return;

            LogMissingPool(runtimeType, "Despawn");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LogMissingPool(Type requestedType, string operation)
        {
            UnityEngine.Debug.LogError(
                $"PoolRegistry.{operation}: no pool is registered for '{requestedType}'. " +
                "Register the pool with the same concrete type you spawn and despawn through.");
        }

        public void Clear()
        {
            foreach (var pool in _pools.Values)
            {
                if (pool is IDisposable d)
                    d.Dispose();
            }
            _pools.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}
