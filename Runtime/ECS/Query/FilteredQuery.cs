using System;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Storage;

namespace Strada.Core.ECS.Query
{
    /// <summary>
    /// A small, fixed set of filter storages held inline, by value.
    /// </summary>
    /// <remarks>
    /// The query builders are structs, so every chained call copies them. While the filters
    /// lived in a <c>List&lt;IComponentStorage&gt;</c>, all those copies shared one list
    /// instance: two chains branched off a common builder silently contaminated each other's
    /// filters, and a builder reused from a field accumulated one extra filter per call. The
    /// list was also garbage — one List plus backing array per Also-chain and per None-chain,
    /// dead the moment ForEach returned, on a path that is rebuilt every frame. Inline slots
    /// copy with the struct, so a copy is genuinely independent and nothing is allocated.
    ///
    /// The members are declared <c>readonly</c> so that passing this around by <c>in</c> or
    /// reading it off a builder does not make the compiler insert a defensive copy of all
    /// five fields on every entity.
    /// </remarks>
    internal struct FilterSet
    {
        internal const int Capacity = 4;

        private IComponentStorage _s0;
        private IComponentStorage _s1;
        private IComponentStorage _s2;
        private IComponentStorage _s3;
        private int _count;

        public readonly int Count => _count;

        public void Add(IComponentStorage storage)
        {
            switch (_count)
            {
                case 0: _s0 = storage; break;
                case 1: _s1 = storage; break;
                case 2: _s2 = storage; break;
                case 3: _s3 = storage; break;
                default:
                    throw new InvalidOperationException(
                        $"A filtered query supports at most {Capacity} Also filters and " +
                        $"{Capacity} None filters.");
            }
            _count++;
        }

        /// <summary>
        /// True when <paramref name="entity"/> is present in every storage in this set.
        /// </summary>
        /// <remarks>
        /// Unrolled rather than looped: this runs in the innermost query loop, and the
        /// previous foreach paid for a List enumerator plus its version check per entity per
        /// filter on top of the storage lookup itself.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool ContainsAll(int entity)
        {
            if (_count == 0) return true;
            if (!_s0.Contains(entity)) return false;
            if (_count == 1) return true;
            if (!_s1.Contains(entity)) return false;
            if (_count == 2) return true;
            if (!_s2.Contains(entity)) return false;
            if (_count == 3) return true;
            return _s3.Contains(entity);
        }

        /// <summary>
        /// True when <paramref name="entity"/> is present in none of the storages in this set.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool ContainsNone(int entity)
        {
            if (_count == 0) return true;
            if (_s0.Contains(entity)) return false;
            if (_count == 1) return true;
            if (_s1.Contains(entity)) return false;
            if (_count == 2) return true;
            if (_s2.Contains(entity)) return false;
            if (_count == 3) return true;
            return !_s3.Contains(entity);
        }
    }

    internal static class QueryFilterHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool PassesFilters(int entity, in FilterSet withFilters, in FilterSet withoutFilters)
        {
            return withFilters.ContainsAll(entity) && withoutFilters.ContainsNone(entity);
        }
    }

    public struct FilteredQueryBuilder<T1> where T1 : unmanaged, IComponent
    {
        private readonly EntityManager _manager;
        private readonly ComponentStorage<T1> _storage;
        private FilterSet _withFilters;
        private FilterSet _withoutFilters;

        internal FilteredQueryBuilder(EntityManager manager, ComponentStorage<T1> storage)
        {
            _manager = manager;
            _storage = storage;
            _withFilters = default;
            _withoutFilters = default;
        }

        /// <summary>
        /// Narrows the query to entities that also have <typeparamref name="TFilter"/>.
        /// </summary>
        /// <remarks>
        /// Returns a new builder instead of mutating this one: a builder held in a field would
        /// otherwise gain a filter every time it was reused, and two chains branched off it
        /// would see each other's filters.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1> Also<TFilter>() where TFilter : unmanaged, IComponent
        {
            var next = this;
            next._withFilters.Add(_manager.Store.GetOrCreateStorage<TFilter>());
            return next;
        }

        /// <summary>
        /// Narrows the query to entities that do not have <typeparamref name="TExclude"/>.
        /// Returns a new builder rather than mutating this one; see Also.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1> None<TExclude>() where TExclude : unmanaged, IComponent
        {
            var next = this;
            next._withoutFilters.Add(_manager.Store.GetOrCreateStorage<TExclude>());
            return next;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1> action)
        {
            ref var set = ref _storage.GetSparseSet();
            int count = set.Count;

            // Copied out of the builder so the loop reads them from registers or the local
            // frame: the delegate call is opaque to the optimiser, which would otherwise have
            // to reload the filter storages through `this` on every entity.
            var withFilters = _withFilters;
            var withoutFilters = _withoutFilters;

            unsafe
            {
                int* entities = set.GetDenseEntityPtr();
                T1* data = set.GetDataPtr();
                int guard = set.StructuralVersion;

                for (int i = 0; i < count; i++)
                {
                    // The live count shrinks the loop if the callback removes a component:
                    // Remove swap-removes, so iterating to the snapshotted count would revisit
                    // the vacated slot and discard whatever the callback wrote through the ref.
                    if (i >= set.Count) break;

                    int entity = entities[i];
                    if (!QueryFilterHelper.PassesFilters(entity, in withFilters, in withoutFilters))
                        continue;

                    action(entity, ref data[i]);
                    QueryGuard.Check(guard, set.StructuralVersion);
                }
            }
        }
    }

    public struct FilteredQueryBuilder<T1, T2>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        private readonly EntityManager _manager;
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private FilterSet _withFilters;
        private FilterSet _withoutFilters;

        internal FilteredQueryBuilder(EntityManager manager, ComponentStorage<T1> s1, ComponentStorage<T2> s2)
        {
            _manager = manager;
            _storage1 = s1;
            _storage2 = s2;
            _withFilters = default;
            _withoutFilters = default;
        }

        /// <summary>
        /// Narrows the query to entities that also have <typeparamref name="TFilter"/>.
        /// Returns a new builder rather than mutating this one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2> Also<TFilter>() where TFilter : unmanaged, IComponent
        {
            var next = this;
            next._withFilters.Add(_manager.Store.GetOrCreateStorage<TFilter>());
            return next;
        }

        /// <summary>
        /// Narrows the query to entities that do not have <typeparamref name="TExclude"/>.
        /// Returns a new builder rather than mutating this one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2> None<TExclude>() where TExclude : unmanaged, IComponent
        {
            var next = this;
            next._withoutFilters.Add(_manager.Store.GetOrCreateStorage<TExclude>());
            return next;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            bool useFirst = set1.Count <= set2.Count;

            var withFilters = _withFilters;
            var withoutFilters = _withoutFilters;

            unsafe
            {
                int* entities = useFirst ? set1.GetDenseEntityPtr() : set2.GetDenseEntityPtr();
                int count = useFirst ? set1.Count : set2.Count;

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;

                for (int i = 0; i < count; i++)
                {
                    // See FilteredQueryBuilder<T1>.ForEach: the driving set's live count keeps
                    // a removal made from inside the callback from being visited twice.
                    if (i >= (useFirst ? set1.Count : set2.Count)) break;

                    int entity = entities[i];
                    int idx1 = set1.GetDenseIndex(entity);
                    int idx2 = set2.GetDenseIndex(entity);

                    if (idx1 < 0 || idx2 < 0)
                        continue;

                    if (!QueryFilterHelper.PassesFilters(entity, in withFilters, in withoutFilters))
                        continue;

                    action(entity, ref data1[idx1], ref data2[idx2]);
                    QueryGuard.Check(guard1, set1.StructuralVersion);
                    QueryGuard.Check(guard2, set2.StructuralVersion);
                }
            }
        }
    }

    public struct FilteredQueryBuilder<T1, T2, T3>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        private readonly EntityManager _manager;
        private readonly ComponentStorage<T1> _storage1;
        private readonly ComponentStorage<T2> _storage2;
        private readonly ComponentStorage<T3> _storage3;
        private FilterSet _withFilters;
        private FilterSet _withoutFilters;

        internal FilteredQueryBuilder(EntityManager manager, ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3)
        {
            _manager = manager;
            _storage1 = s1;
            _storage2 = s2;
            _storage3 = s3;
            _withFilters = default;
            _withoutFilters = default;
        }

        /// <summary>
        /// Narrows the query to entities that also have <typeparamref name="TFilter"/>.
        /// Returns a new builder rather than mutating this one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2, T3> Also<TFilter>() where TFilter : unmanaged, IComponent
        {
            var next = this;
            next._withFilters.Add(_manager.Store.GetOrCreateStorage<TFilter>());
            return next;
        }

        /// <summary>
        /// Narrows the query to entities that do not have <typeparamref name="TExclude"/>.
        /// Returns a new builder rather than mutating this one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2, T3> None<TExclude>() where TExclude : unmanaged, IComponent
        {
            var next = this;
            next._withoutFilters.Add(_manager.Store.GetOrCreateStorage<TExclude>());
            return next;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2, T3> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            ref var set3 = ref _storage3.GetSparseSet();

            int count1 = set1.Count;
            int count2 = set2.Count;
            int count3 = set3.Count;

            var withFilters = _withFilters;
            var withoutFilters = _withoutFilters;

            unsafe
            {
                int minCount;
                int* entities;
                int driving;

                if (count1 <= count2 && count1 <= count3)
                {
                    entities = set1.GetDenseEntityPtr();
                    minCount = count1;
                    driving = 1;
                }
                else if (count2 <= count3)
                {
                    entities = set2.GetDenseEntityPtr();
                    minCount = count2;
                    driving = 2;
                }
                else
                {
                    entities = set3.GetDenseEntityPtr();
                    minCount = count3;
                    driving = 3;
                }

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;
                int guard3 = set3.StructuralVersion;

                for (int i = 0; i < minCount; i++)
                {
                    // See FilteredQueryBuilder<T1>.ForEach: the driving set's live count keeps
                    // a removal made from inside the callback from being visited twice.
                    int liveCount = driving == 1 ? set1.Count : driving == 2 ? set2.Count : set3.Count;
                    if (i >= liveCount) break;

                    int entity = entities[i];
                    int idx1 = set1.GetDenseIndex(entity);
                    int idx2 = set2.GetDenseIndex(entity);
                    int idx3 = set3.GetDenseIndex(entity);

                    if (idx1 < 0 || idx2 < 0 || idx3 < 0)
                        continue;

                    if (!QueryFilterHelper.PassesFilters(entity, in withFilters, in withoutFilters))
                        continue;

                    action(entity, ref data1[idx1], ref data2[idx2], ref data3[idx3]);
                    QueryGuard.Check(guard1, set1.StructuralVersion);
                    QueryGuard.Check(guard2, set2.StructuralVersion);
                    QueryGuard.Check(guard3, set3.StructuralVersion);
                }
            }
        }
    }
}
