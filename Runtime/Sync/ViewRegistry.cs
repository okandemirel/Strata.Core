using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.DI;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;

namespace Strada.Core.Sync
{
    public sealed class ViewRegistry : IDisposable
    {
        private readonly Dictionary<long, EntityView> _entityToView = new(256);
        private readonly HashSet<EntityView> _allViews = new(256);
        private readonly EntityManager _entityManager;
        private readonly IContainer _container;
        private bool _disposed;
        private List<EntityView> _allViewsCache;
        private bool _cacheInvalid = true;

        public int ViewCount => _allViews.Count;
        public IReadOnlyList<EntityView> AllViews
        {
            get
            {
                if (_cacheInvalid || _allViewsCache == null)
                {
                    _allViewsCache = new List<EntityView>(_allViews);
                    _cacheInvalid = false;
                }
                return _allViewsCache;
            }
        }

        public ViewRegistry(EntityManager entityManager, IContainer container)
        {
            _entityManager = entityManager;
            _container = container;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetEntityKey(Entity entity) => ((long)entity.Index << 32) | (uint)entity.Version;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Register(EntityView view, Entity entity)
        {
            if (_disposed) return;
            if (view == null) return;

            if (!view.IsBound)
            {
                view.Bind(_container, _entityManager, entity);
            }

            _entityToView[GetEntityKey(entity)] = view;
            _allViews.Add(view);
            _cacheInvalid = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Unregister(EntityView view)
        {
            if (_disposed) return;
            if (view == null) return;

            if (view.IsBound)
            {
                _entityToView.Remove(GetEntityKey(view.Entity));
            }

            if (_allViews.Remove(view))
            {
                _cacheInvalid = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Unregister(Entity entity)
        {
            if (_disposed) return;

            var key = GetEntityKey(entity);
            if (_entityToView.TryGetValue(key, out var view))
            {
                _entityToView.Remove(key);
                if (_allViews.Remove(view))
                {
                    _cacheInvalid = true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityView GetView(Entity entity)
        {
            return _entityToView.TryGetValue(GetEntityKey(entity), out var view) ? view : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetView<T>(Entity entity) where T : EntityView
        {
            return _entityToView.TryGetValue(GetEntityKey(entity), out var view) ? view as T : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetView(Entity entity, out EntityView view)
        {
            return _entityToView.TryGetValue(GetEntityKey(entity), out view);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetView<T>(Entity entity, out T view) where T : EntityView
        {
            if (_entityToView.TryGetValue(GetEntityKey(entity), out var baseView))
            {
                view = baseView as T;
                return view != null;
            }

            view = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasView(Entity entity)
        {
            return _entityToView.ContainsKey(GetEntityKey(entity));
        }

        public void SyncAll()
        {
            // Iterate the snapshot, not the live set: a sync handler is allowed to spawn or
            // despawn views, which mutates _allViews and throws mid-enumeration. The snapshot
            // is only rebuilt when membership actually changes (see _cacheInvalid), so this
            // costs nothing per frame.
            var views = AllViews;
            for (int i = 0; i < views.Count; i++)
            {
                views[i].SyncBindings();
            }
        }

        public void ForceSyncAll()
        {
            var views = AllViews;
            for (int i = 0; i < views.Count; i++)
            {
                views[i].ForceSyncBindings();
            }
        }

        public void Clear()
        {
            foreach (var view in _allViews)
            {
                view.Unbind();
            }

            _entityToView.Clear();
            _allViews.Clear();
            _cacheInvalid = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Clear();
        }
    }
}
