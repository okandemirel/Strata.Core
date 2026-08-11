using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.DI;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Strada.Core.Sync
{
    public interface IComponentBinding : IDisposable
    {
        void Sync();
        void Push();
        bool IsDirty { get; }
        Type ComponentType { get; }
        BindingSyncState SyncState { get; }
        string LastError { get; }
    }

    public abstract class EntityView : MonoBehaviour, IDisposable
    {
        private Entity _entity;
        private EntityManager _entityManager;
        private IContainer _container;
        private List<IComponentBinding> _bindings;
        private bool _bound;
        private bool _disposed;

        public Entity Entity => _entity;
        public bool IsBound => _bound;

        protected EntityManager EntityManager => _entityManager;
        protected IContainer Container => _container;

        public void Bind(IContainer container, EntityManager entityManager, Entity entity)
        {
            if (_bound)
            {
                // Re-binding to the same entity is a harmless no-op. Re-binding to a different
                // one used to be silently ignored, which left the view reading the previous
                // entity's components while ViewRegistry recorded it as representing the new
                // one — cross-entity data corruption with no diagnostic.
                if (_entity == entity) return;

                throw new InvalidOperationException(
                    $"{GetType().Name} is already bound to Entity({_entity.Index},{_entity.Version}); " +
                    $"call Unbind() before binding it to Entity({entity.Index},{entity.Version}).");
            }

            _container = container;
            _entityManager = entityManager;
            _entity = entity;
            _bindings = new List<IComponentBinding>(4);
            _bound = true;

            OnBind();
        }

        public void Unbind()
        {
            if (!_bound) return;

            OnUnbind();

            foreach (var binding in _bindings)
                binding.Dispose();
            _bindings.Clear();

            _entity = default;
            _entityManager = null;
            _container = null;
            _bound = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SyncBindings()
        {
            if (!_bound) return;

            for (int i = 0; i < _bindings.Count; i++)
            {
                if (_bindings[i].IsDirty)
                    _bindings[i].Sync();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForceSyncBindings()
        {
            if (!_bound) return;

            for (int i = 0; i < _bindings.Count; i++)
            {
                _bindings[i].Sync();
            }
        }

        protected abstract void OnBind();

        protected virtual void OnUnbind() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected ComponentBinding<T> BindComponent<T>() where T : unmanaged, IComponent
        {
            var binding = new ComponentBinding<T>(_entityManager, _entity);
            _bindings.Add(binding);
            return binding;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected ComponentBinding<T> BindComponent<T>(T initialValue) where T : unmanaged, IComponent
        {
            var binding = new ComponentBinding<T>(_entityManager, _entity, initialValue);
            _bindings.Add(binding);
            return binding;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void AddBinding(IComponentBinding binding)
        {
            _bindings.Add(binding);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected new T GetComponent<T>() where T : unmanaged, IComponent
        {
            return _entityManager.GetComponent<T>(_entity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void SetComponent<T>(T component) where T : unmanaged, IComponent
        {
            _entityManager.SetComponent(_entity, component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool HasComponent<T>() where T : unmanaged, IComponent
        {
            return _entityManager.HasComponent<T>(_entity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected T Resolve<T>() where T : class
        {
            return _container?.Resolve<T>();
        }

        protected virtual void OnDestroy()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Unbind();
            GC.SuppressFinalize(this);
        }
    }

    public abstract class EntityView<TComponent> : EntityView where TComponent : unmanaged, IComponent
    {
        protected ComponentBinding<TComponent> PrimaryBinding { get; private set; }

        protected override void OnBind()
        {
            PrimaryBinding = BindComponent<TComponent>();
            PrimaryBinding.OnChanged += OnComponentChanged;
        }

        protected override void OnUnbind()
        {
            if (PrimaryBinding != null)
                PrimaryBinding.OnChanged -= OnComponentChanged;
        }

        protected virtual void OnComponentChanged(TComponent component) { }
    }

    public sealed class ComponentBinding<T> : IComponentBinding where T : unmanaged, IComponent
    {
        private EntityManager _entityManager;
        private Entity _entity;
        private T _cachedValue;
        // Starts dirty so the first Sync establishes the baseline and publishes the initial
        // value; afterwards it is set by MarkDirty and cleared by Sync.
        private bool _dirty = true;
        private bool _disposed;
        private BindingSyncState _syncState = BindingSyncState.NotSynced;
        private string _lastError;

        public event Action<T> OnChanged;

        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _cachedValue;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                _cachedValue = value;
                _entityManager.SetComponent(_entity, value);
                OnChanged?.Invoke(value);
            }
        }

        public bool IsDirty => _dirty;
        public Type ComponentType => typeof(T);
        public BindingSyncState SyncState => _syncState;
        public string LastError => _lastError;

        public ComponentBinding(EntityManager entityManager, Entity entity)
        {
            _entityManager = entityManager;
            _entity = entity;

            if (_entityManager.HasComponent<T>(entity))
                _cachedValue = _entityManager.GetComponent<T>(entity);
        }

        public ComponentBinding(EntityManager entityManager, Entity entity, T initialValue)
        {
            _entityManager = entityManager;
            _entity = entity;
            _cachedValue = initialValue;

            if (!_entityManager.HasComponent<T>(entity))
                _entityManager.AddComponent(entity, initialValue);
            else
                _entityManager.SetComponent(entity, initialValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Sync()
        {
            try
            {
                if (_entityManager == null)
                {
                    _syncState = BindingSyncState.Error;
                    _lastError = "EntityManager is null";
                    return;
                }

                if (!_entityManager.Exists(_entity))
                {
                    _syncState = BindingSyncState.EntityDestroyed;
                    _lastError = "Entity no longer exists";
                    return;
                }

                if (!_entityManager.HasComponent<T>(_entity))
                {
                    _syncState = BindingSyncState.Error;
                    _lastError = $"Entity does not have component {typeof(T).Name}";
                    return;
                }

                var current = _entityManager.GetComponent<T>(_entity);

                // Only publish an actual change. This used to fire OnChanged on every sync
                // regardless of the value, so in ForceAll mode every handler on every view
                // ran every frame — a 100% false-positive rate that dragged UI and render
                // work behind it.
                bool changed = _dirty || !BitwiseEquals(current, _cachedValue);

                _cachedValue = current;
                _dirty = false;
                _syncState = BindingSyncState.Synced;
                _lastError = null;

                if (changed)
                    OnChanged?.Invoke(current);
            }
            catch (Exception ex)
            {
                _syncState = BindingSyncState.Error;
                _lastError = ex.Message;
            }
        }

        /// <summary>
        /// Forces the next <see cref="Sync"/> to publish, even if the component's bits are
        /// unchanged. Required by <see cref="ViewSyncMode.DirtyOnly"/>, which only visits
        /// bindings that have been marked.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>
        /// Bitwise equality. T is constrained to unmanaged, so comparing the bits avoids the
        /// boxing that EqualityComparer&lt;T&gt;.Default incurs for a struct that does not
        /// implement IEquatable&lt;T&gt; — which is every IComponent in practice. Any bit
        /// change counts as a change, which is the desired semantic for view synchronisation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool BitwiseEquals(T a, T b)
        {
            return UnsafeUtility.MemCmp(&a, &b, UnsafeUtility.SizeOf<T>()) == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push()
        {
            _entityManager.SetComponent(_entity, _cachedValue);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            OnChanged = null;
            _entityManager = null;
        }
    }
}
