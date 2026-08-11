using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Storage;

namespace Strada.Core.ECS.Query
{
    internal static class QueryFilterHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool PassesFilters(int entity, List<IComponentStorage> withFilters, List<IComponentStorage> withoutFilters)
        {
            if (withFilters != null)
            {
                foreach (var storage in withFilters)
                {
                    if (!storage.Contains(entity))
                        return false;
                }
            }

            if (withoutFilters != null)
            {
                foreach (var storage in withoutFilters)
                {
                    if (storage.Contains(entity))
                        return false;
                }
            }

            return true;
        }
    }

    public struct FilteredQueryBuilder<T1> where T1 : unmanaged, IComponent
    {
        private readonly EntityManager _manager;
        private readonly ComponentStorage<T1> _storage;
        private List<IComponentStorage> _withFilters;
        private List<IComponentStorage> _withoutFilters;

        internal FilteredQueryBuilder(EntityManager manager, ComponentStorage<T1> storage)
        {
            _manager = manager;
            _storage = storage;
            _withFilters = null;
            _withoutFilters = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1> Also<TFilter>() where TFilter : unmanaged, IComponent
        {
            _withFilters ??= new List<IComponentStorage>(4);
            _withFilters.Add(_manager.Store.GetOrCreateStorage<TFilter>());
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1> None<TExclude>() where TExclude : unmanaged, IComponent
        {
            _withoutFilters ??= new List<IComponentStorage>(4);
            _withoutFilters.Add(_manager.Store.GetOrCreateStorage<TExclude>());
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1> action)
        {
            ref var set = ref _storage.GetSparseSet();
            int count = set.Count;

            unsafe
            {
                int* entities = set.GetDenseEntityPtr();
                T1* data = set.GetDataPtr();
                int guard = set.StructuralVersion;

                for (int i = 0; i < count; i++)
                {
                    int entity = entities[i];
                    if (!QueryFilterHelper.PassesFilters(entity, _withFilters, _withoutFilters))
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
        private List<IComponentStorage> _withFilters;
        private List<IComponentStorage> _withoutFilters;

        internal FilteredQueryBuilder(EntityManager manager, ComponentStorage<T1> s1, ComponentStorage<T2> s2)
        {
            _manager = manager;
            _storage1 = s1;
            _storage2 = s2;
            _withFilters = null;
            _withoutFilters = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2> Also<TFilter>() where TFilter : unmanaged, IComponent
        {
            _withFilters ??= new List<IComponentStorage>(4);
            _withFilters.Add(_manager.Store.GetOrCreateStorage<TFilter>());
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2> None<TExclude>() where TExclude : unmanaged, IComponent
        {
            _withoutFilters ??= new List<IComponentStorage>(4);
            _withoutFilters.Add(_manager.Store.GetOrCreateStorage<TExclude>());
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForEach(QueryDelegate<T1, T2> action)
        {
            ref var set1 = ref _storage1.GetSparseSet();
            ref var set2 = ref _storage2.GetSparseSet();
            bool useFirst = set1.Count <= set2.Count;

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
                    int entity = entities[i];
                    int idx1 = set1.GetDenseIndex(entity);
                    int idx2 = set2.GetDenseIndex(entity);

                    if (idx1 < 0 || idx2 < 0)
                        continue;

                    if (!QueryFilterHelper.PassesFilters(entity, _withFilters, _withoutFilters))
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
        private List<IComponentStorage> _withFilters;
        private List<IComponentStorage> _withoutFilters;

        internal FilteredQueryBuilder(EntityManager manager, ComponentStorage<T1> s1, ComponentStorage<T2> s2, ComponentStorage<T3> s3)
        {
            _manager = manager;
            _storage1 = s1;
            _storage2 = s2;
            _storage3 = s3;
            _withFilters = null;
            _withoutFilters = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2, T3> Also<TFilter>() where TFilter : unmanaged, IComponent
        {
            _withFilters ??= new List<IComponentStorage>(4);
            _withFilters.Add(_manager.Store.GetOrCreateStorage<TFilter>());
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FilteredQueryBuilder<T1, T2, T3> None<TExclude>() where TExclude : unmanaged, IComponent
        {
            _withoutFilters ??= new List<IComponentStorage>(4);
            _withoutFilters.Add(_manager.Store.GetOrCreateStorage<TExclude>());
            return this;
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

            unsafe
            {
                int minCount;
                int* entities;

                if (count1 <= count2 && count1 <= count3)
                {
                    entities = set1.GetDenseEntityPtr();
                    minCount = count1;
                }
                else if (count2 <= count3)
                {
                    entities = set2.GetDenseEntityPtr();
                    minCount = count2;
                }
                else
                {
                    entities = set3.GetDenseEntityPtr();
                    minCount = count3;
                }

                T1* data1 = set1.GetDataPtr();
                T2* data2 = set2.GetDataPtr();
                T3* data3 = set3.GetDataPtr();
                int guard1 = set1.StructuralVersion;
                int guard2 = set2.StructuralVersion;
                int guard3 = set3.StructuralVersion;

                for (int i = 0; i < minCount; i++)
                {
                    int entity = entities[i];
                    int idx1 = set1.GetDenseIndex(entity);
                    int idx2 = set2.GetDenseIndex(entity);
                    int idx3 = set3.GetDenseIndex(entity);

                    if (idx1 < 0 || idx2 < 0 || idx3 < 0)
                        continue;

                    if (!QueryFilterHelper.PassesFilters(entity, _withFilters, _withoutFilters))
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
