using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.DI;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.Logging;
using UnityEngine;

namespace Strada.Core.Sync
{
    public sealed class ViewPool<TView> : IViewPool, IDisposable where TView : EntityView
    {
        private readonly Stack<TView> _available;
        // Membership guard: without it a second Despawn(view) pushes the same instance onto
        // the free stack twice, and two entities then silently share one view.
        private readonly HashSet<TView> _pooled;
        private readonly List<TView> _active;
        private readonly Dictionary<long, int> _entityToActiveIndex;
        private readonly GameObject _prefab;
        private readonly Transform _poolRoot;
        private readonly Transform _activeRoot;
        private readonly IContainer _container;
        private readonly EntityManager _entityManager;
        private readonly ViewRegistry _registry;
        private readonly int _maxSize;
        private int _totalCreated;
        private bool _disposed;

        public int AvailableCount => _available.Count;
        public int ActiveCount => _active.Count;
        public int TotalCreated => _totalCreated;

        public ViewPool(
            GameObject prefab,
            IContainer container,
            EntityManager entityManager,
            ViewRegistry registry,
            Transform poolRoot = null,
            Transform activeRoot = null,
            int initialSize = 0,
            int maxSize = 1000)
        {
            _prefab = prefab;
            _container = container;
            _entityManager = entityManager;
            _registry = registry;
            _poolRoot = poolRoot;
            _activeRoot = activeRoot;
            _maxSize = maxSize;
            _available = new Stack<TView>(Math.Max(initialSize, 16));
            _pooled = new HashSet<TView>();
            _active = new List<TView>(Math.Max(initialSize, 16));
            _entityToActiveIndex = new Dictionary<long, int>(Math.Max(initialSize, 16));

            Prewarm(initialSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetEntityKey(Entity entity) => ((long)entity.Index << 32) | (uint)entity.Version;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TView Spawn(Entity entity, Transform parent = null)
        {
            TView view = null;

            // The pool root is a plain GameObject in the direct-construction path, so a scene
            // load destroys the pooled instances while the Stack still holds their managed
            // wrappers. Dereferencing one of those throws MissingReferenceException from an
            // unrelated spawn call site — skip the corpses and fall through to instantiation.
            while (_available.Count > 0)
            {
                var candidate = _available.Pop();
                _pooled.Remove(candidate);
                if (candidate == null) continue;

                view = candidate;
                view.gameObject.SetActive(true);
                break;
            }

            if (view == null)
            {
                var go = UnityEngine.Object.Instantiate(_prefab);
                view = go.GetComponent<TView>();
                if (view == null)
                {
                    UnityEngine.Object.Destroy(go);
                    throw new InvalidOperationException($"Prefab '{_prefab.name}' is missing required component '{typeof(TView).Name}'");
                }
                _totalCreated++;
            }

            var viewTransform = view.transform;
            viewTransform.SetParent(parent ?? _activeRoot, false);

            // SetParent(worldPositionStays: false) preserves the local TRS, and Despawn
            // reparents the same way, so a reused view carries whatever pose its previous
            // user left it at. Restore the prefab's pose; the position overload overwrites
            // the world pose afterwards as it already did.
            var prefabTransform = _prefab.transform;
            viewTransform.localPosition = prefabTransform.localPosition;
            viewTransform.localRotation = prefabTransform.localRotation;
            viewTransform.localScale = prefabTransform.localScale;

            view.Bind(_container, _entityManager, entity);
            _registry?.Register(view, entity);

            var entityKey = GetEntityKey(entity);
            if (!_entityToActiveIndex.TryAdd(entityKey, _active.Count))
            {
                // The map is one-to-one on the entity key. A second live view for the same
                // entity hides the first from Despawn(Entity) and used to corrupt the
                // swap-remove bookkeeping outright; Despawn(TView) now resolves by identity,
                // but the caller still cannot address both views by entity.
                StradaLog.LogWarning(
                    $"ViewPool<{typeof(TView).Name}>: a view is already active for Entity({entity.Index},{entity.Version}). " +
                    "Despawn(Entity) will only reach the most recently spawned one.",
                    LogModule.Sync);
                _entityToActiveIndex[entityKey] = _active.Count;
            }
            _active.Add(view);

            return view;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TView Spawn(Entity entity, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var view = Spawn(entity, parent);
            view.transform.SetPositionAndRotation(position, rotation);
            return view;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Despawn(TView view)
        {
            if (view == null) return;
            if (_disposed) return;

            if (_pooled.Contains(view))
            {
                StradaLog.LogWarning(
                    $"ViewPool<{typeof(TView).Name}>.Despawn called twice for the same view; ignoring the second call.",
                    LogModule.Sync);
                return;
            }

            var entity = view.Entity;
            var entityKey = GetEntityKey(entity);

            _registry?.Unregister(view);
            view.Unbind();

            // O(1) removal using swap-remove pattern, but only once the mapped slot is confirmed
            // to hold THIS view: two views spawned for one entity share a key, and trusting the
            // key blindly swap-removes the wrong element and drops a still-active view.
            int index;
            if (_entityToActiveIndex.TryGetValue(entityKey, out int mapped)
                && mapped < _active.Count
                && ReferenceEquals(_active[mapped], view))
            {
                index = mapped;
                _entityToActiveIndex.Remove(entityKey);
            }
            else
            {
                index = _active.IndexOf(view);
            }

            if (index >= 0)
            {
                int lastIndex = _active.Count - 1;
                if (index < lastIndex)
                {
                    // Swap with last element
                    var lastView = _active[lastIndex];
                    _active[index] = lastView;
                    _entityToActiveIndex[GetEntityKey(lastView.Entity)] = index;
                }
                _active.RemoveAt(lastIndex);
            }

            if (_available.Count < _maxSize)
            {
                view.gameObject.SetActive(false);
                if (_poolRoot != null)
                    view.transform.SetParent(_poolRoot, false);
                _available.Push(view);
                _pooled.Add(view);
            }
            else
            {
                UnityEngine.Object.Destroy(view.gameObject);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Despawn(Entity entity)
        {
            var entityKey = GetEntityKey(entity);
            if (_entityToActiveIndex.TryGetValue(entityKey, out int index) && index < _active.Count)
            {
                Despawn(_active[index]);
                return;
            }
            StradaLog.LogWarning($"[ViewPool<{typeof(TView).Name}>] No match for Entity({entity.Index},{entity.Version}) in {_active.Count} active views", LogModule.Sync);
        }

        public void DespawnAll()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Despawn(_active[i]);
            }
        }

        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var go = UnityEngine.Object.Instantiate(_prefab);
                var view = go.GetComponent<TView>();
                if (view == null)
                {
                    UnityEngine.Object.Destroy(go);
                    throw new InvalidOperationException($"Prefab '{_prefab.name}' is missing required component '{typeof(TView).Name}'");
                }

                go.SetActive(false);
                if (_poolRoot != null)
                    view.transform.SetParent(_poolRoot, false);

                _available.Push(view);
                _totalCreated++;
            }
        }

        public void Clear()
        {
            DespawnAll();
            _entityToActiveIndex.Clear();

            while (_available.Count > 0)
            {
                var view = _available.Pop();
                if (view != null && view.gameObject != null)
                    UnityEngine.Object.Destroy(view.gameObject);
            }
            _pooled.Clear();

            _totalCreated = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Clear();
        }
    }

    public static class ViewPoolFactory
    {
        public static ViewPool<TView> Create<TView>(
            GameObject prefab,
            IContainer container,
            EntityManager entityManager,
            ViewRegistry registry = null,
            int initialSize = 0,
            int maxSize = 1000) where TView : EntityView
        {
            var poolRoot = new GameObject($"Pool_{typeof(TView).Name}").transform;
            var activeRoot = new GameObject($"Active_{typeof(TView).Name}").transform;
            poolRoot.gameObject.SetActive(false);

            return new ViewPool<TView>(
                prefab,
                container,
                entityManager,
                registry,
                poolRoot,
                activeRoot,
                initialSize,
                maxSize);
        }
    }
}
