using System;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Storage;

namespace Strada.Core.ECS.Query
{
    public readonly struct QueryBuilder
    {
        private readonly EntityManager _manager;

        internal QueryBuilder(EntityManager manager) => _manager = manager;

        internal EntityManager Manager => _manager;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1> Select<T1>() where T1 : unmanaged, IComponent
        {
            return new EntityQuery<T1>(_manager.Store.GetOrCreateStorage<T1>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2> Select<T1, T2>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
        {
            return new EntityQuery<T1, T2>(
                _manager.Store.GetOrCreateStorage<T1>(),
                _manager.Store.GetOrCreateStorage<T2>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2, T3> Select<T1, T2, T3>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
        {
            return new EntityQuery<T1, T2, T3>(
                _manager.Store.GetOrCreateStorage<T1>(),
                _manager.Store.GetOrCreateStorage<T2>(),
                _manager.Store.GetOrCreateStorage<T3>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2, T3, T4> Select<T1, T2, T3, T4>()
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent where T4 : unmanaged, IComponent
        {
            return new EntityQuery<T1, T2, T3, T4>(
                _manager.Store.GetOrCreateStorage<T1>(), _manager.Store.GetOrCreateStorage<T2>(),
                _manager.Store.GetOrCreateStorage<T3>(), _manager.Store.GetOrCreateStorage<T4>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2, T3, T4, T5> Select<T1, T2, T3, T4, T5>()
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent
        {
            return new EntityQuery<T1, T2, T3, T4, T5>(
                _manager.Store.GetOrCreateStorage<T1>(), _manager.Store.GetOrCreateStorage<T2>(),
                _manager.Store.GetOrCreateStorage<T3>(), _manager.Store.GetOrCreateStorage<T4>(),
                _manager.Store.GetOrCreateStorage<T5>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2, T3, T4, T5, T6> Select<T1, T2, T3, T4, T5, T6>()
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
        {
            return new EntityQuery<T1, T2, T3, T4, T5, T6>(
                _manager.Store.GetOrCreateStorage<T1>(), _manager.Store.GetOrCreateStorage<T2>(),
                _manager.Store.GetOrCreateStorage<T3>(), _manager.Store.GetOrCreateStorage<T4>(),
                _manager.Store.GetOrCreateStorage<T5>(), _manager.Store.GetOrCreateStorage<T6>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2, T3, T4, T5, T6, T7> Select<T1, T2, T3, T4, T5, T6, T7>()
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
            where T7 : unmanaged, IComponent
        {
            return new EntityQuery<T1, T2, T3, T4, T5, T6, T7>(
                _manager.Store.GetOrCreateStorage<T1>(), _manager.Store.GetOrCreateStorage<T2>(),
                _manager.Store.GetOrCreateStorage<T3>(), _manager.Store.GetOrCreateStorage<T4>(),
                _manager.Store.GetOrCreateStorage<T5>(), _manager.Store.GetOrCreateStorage<T6>(),
                _manager.Store.GetOrCreateStorage<T7>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2, T3, T4, T5, T6, T7, T8> Select<T1, T2, T3, T4, T5, T6, T7, T8>()
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
            where T7 : unmanaged, IComponent where T8 : unmanaged, IComponent
        {
            return new EntityQuery<T1, T2, T3, T4, T5, T6, T7, T8>(
                _manager.Store.GetOrCreateStorage<T1>(), _manager.Store.GetOrCreateStorage<T2>(),
                _manager.Store.GetOrCreateStorage<T3>(), _manager.Store.GetOrCreateStorage<T4>(),
                _manager.Store.GetOrCreateStorage<T5>(), _manager.Store.GetOrCreateStorage<T6>(),
                _manager.Store.GetOrCreateStorage<T7>(), _manager.Store.GetOrCreateStorage<T8>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1> Filter<T1>() where T1 : unmanaged, IComponent
        {
            var storage = _manager.Store.GetOrCreateStorage<T1>();
            return new FilteredQueryBuilder<T1>(_manager, storage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2> Filter<T1, T2>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
        {
            var s1 = _manager.Store.GetOrCreateStorage<T1>();
            var s2 = _manager.Store.GetOrCreateStorage<T2>();
            return new FilteredQueryBuilder<T1, T2>(_manager, s1, s2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2, T3> Filter<T1, T2, T3>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
        {
            var s1 = _manager.Store.GetOrCreateStorage<T1>();
            var s2 = _manager.Store.GetOrCreateStorage<T2>();
            var s3 = _manager.Store.GetOrCreateStorage<T3>();
            return new FilteredQueryBuilder<T1, T2, T3>(_manager, s1, s2, s3);
        }

        /// <summary>
        /// Alias for Select. Adds a component type to the query.
        /// </summary>
        /// <remarks>
        /// DEPRECATED: Use Select&lt;T&gt;() instead for consistency.
        /// This method will be removed in v3.0.
        /// </remarks>
        [Obsolete("Use Select<T>() instead. This method will be removed in v3.0.")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1> With<T1>() where T1 : unmanaged, IComponent => Select<T1>();

        /// <summary>
        /// Alias for Select. Adds component types to the query.
        /// </summary>
        /// <remarks>
        /// DEPRECATED: Use Select&lt;T1, T2&gt;() instead for consistency.
        /// This method will be removed in v3.0.
        /// </remarks>
        [Obsolete("Use Select<T1, T2>() instead. This method will be removed in v3.0.")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2> With<T1, T2>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            => Select<T1, T2>();

        /// <summary>
        /// Alias for Select. Adds component types to the query.
        /// </summary>
        /// <remarks>
        /// DEPRECATED: Use Select&lt;T1, T2, T3&gt;() instead for consistency.
        /// This method will be removed in v3.0.
        /// </remarks>
        [Obsolete("Use Select<T1, T2, T3>() instead. This method will be removed in v3.0.")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery<T1, T2, T3> With<T1, T2, T3>()
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            => Select<T1, T2, T3>();
    }

    public static class EntityManagerQueryExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QueryBuilder Query(this EntityManager manager) => new QueryBuilder(manager);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1>(this EntityManager em, QueryDelegate<T1> action)
            where T1 : unmanaged, IComponent
            => em.Query().Select<T1>().ForEach(action);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1, T2>(this EntityManager em, QueryDelegate<T1, T2> action)
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            => em.Query().Select<T1, T2>().ForEach(action);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1, T2, T3>(this EntityManager em, QueryDelegate<T1, T2, T3> action)
            where T1 : unmanaged, IComponent
            where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent
            => em.Query().Select<T1, T2, T3>().ForEach(action);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1, T2, T3, T4>(this EntityManager em, QueryDelegate<T1, T2, T3, T4> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent
            where T3 : unmanaged, IComponent where T4 : unmanaged, IComponent
            => em.Query().Select<T1, T2, T3, T4>().ForEach(action);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1, T2, T3, T4, T5>(this EntityManager em, QueryDelegate<T1, T2, T3, T4, T5> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent
            => em.Query().Select<T1, T2, T3, T4, T5>().ForEach(action);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1, T2, T3, T4, T5, T6>(this EntityManager em, QueryDelegate<T1, T2, T3, T4, T5, T6> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
            => em.Query().Select<T1, T2, T3, T4, T5, T6>().ForEach(action);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1, T2, T3, T4, T5, T6, T7>(this EntityManager em, QueryDelegate<T1, T2, T3, T4, T5, T6, T7> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
            where T7 : unmanaged, IComponent
            => em.Query().Select<T1, T2, T3, T4, T5, T6, T7>().ForEach(action);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T1, T2, T3, T4, T5, T6, T7, T8>(this EntityManager em, QueryDelegate<T1, T2, T3, T4, T5, T6, T7, T8> action)
            where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
            where T4 : unmanaged, IComponent where T5 : unmanaged, IComponent where T6 : unmanaged, IComponent
            where T7 : unmanaged, IComponent where T8 : unmanaged, IComponent
            => em.Query().Select<T1, T2, T3, T4, T5, T6, T7, T8>().ForEach(action);
    }
}
