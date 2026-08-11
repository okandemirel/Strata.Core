using System;
using System.Collections.Generic;
using Strada.Core.Communication;
using Strada.Core.DI;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.Patterns;

namespace Strada.Core.Sync
{
    public abstract class EntityMediator<TView> : IDisposable where TView : View
    {
        private readonly List<IComponentBinding> _bindings = new(8);
        private readonly List<IDisposable> _disposables = new(4);
        // Bind-scoped subscription tokens. Cleared on Unbind so that bind/unbind cycles
        // do not leak handlers on the bus or accumulate tokens for the mediator's lifetime.
        private readonly List<IDisposable> _bindSubscriptions = new(4);
        private EntityManager _entities;
        private IContainer _container;
        private IEventBus _bus;
        private TView _view;
        private Entity _entity;
        private bool _bound;
        private bool _disposed;

        protected EntityManager Entities => _entities;
        protected IContainer Container => _container;
        protected IEventBus EventBus => _bus;
        protected TView View => _view;
        protected Entity Entity => _entity;
        public bool IsBound => _bound;

        public IReadOnlyList<IComponentBinding> Bindings => _bindings;

        public void Initialize(IContainer container)
        {
            _container = container;
            _container.TryResolve(out _entities);
            _container.TryResolve(out _bus);
            OnInitialize();
        }

        public void Bind(Entity entity, TView view)
        {
            if (_bound) Unbind();

            _entity = entity;
            _view = view;
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

            for (int i = _bindSubscriptions.Count - 1; i >= 0; i--)
                _bindSubscriptions[i].Dispose();
            _bindSubscriptions.Clear();

            // Drained here, not only in Dispose: a pooled mediator is Unbound and rented
            // again, and Initialize re-runs OnInitialize each time. Without this every
            // rent/release cycle left one more subscription registered forever.
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();

            _entity = default;
            _view = null;
            _bound = false;
        }

        public void SyncBindings()
        {
            if (!_bound) return;
            for (int i = 0; i < _bindings.Count; i++)
                _bindings[i].Sync();
        }

        public void PushBindings()
        {
            if (!_bound) return;
            for (int i = 0; i < _bindings.Count; i++)
                _bindings[i].Push();
        }

        public void UpdateMediator(float deltaTime)
        {
            if (!_bound) return;
            SyncBindings();
            OnUpdate(deltaTime);
        }

        protected virtual void OnInitialize() { }
        protected abstract void OnBind();
        protected abstract void OnUnbind();
        protected virtual void OnUpdate(float deltaTime) { }

        protected ComponentBinding<TComponent, TProperty> Bind<TComponent, TProperty>(
            Func<TComponent, TProperty> selector,
            Action<TProperty> onChanged)
            where TComponent : unmanaged, IComponent
        {
            var binding = new ComponentBinding<TComponent, TProperty>(_entities, _entity, selector, onChanged);
            _bindings.Add(binding);
            return binding;
        }

        protected ComponentBinding<TComponent, TProperty> Bind<TComponent, TProperty>(
            Func<TComponent, TProperty> selector,
            Func<TComponent, TProperty, TComponent> setter,
            Action<TProperty> onChanged)
            where TComponent : unmanaged, IComponent
        {
            var binding = new ComponentBinding<TComponent, TProperty>(_entities, _entity, selector, setter, onChanged);
            _bindings.Add(binding);
            return binding;
        }

        protected AutoSyncBinding<TComponent> AutoSync<TComponent>(Action<TComponent> onChanged)
            where TComponent : unmanaged, IComponent
        {
            var binding = new AutoSyncBinding<TComponent>(_entities, _entity, onChanged);
            _bindings.Add(binding);
            return binding;
        }

        protected T Resolve<T>() where T : class => _container?.Resolve<T>();

        protected T GetComponent<T>() where T : unmanaged, IComponent
            => _entities.GetComponent<T>(_entity);

        protected void SetComponent<T>(T component) where T : unmanaged, IComponent
            => _entities.SetComponent(_entity, component);

        protected bool HasComponent<T>() where T : unmanaged, IComponent
            => _entities.HasComponent<T>(_entity);

        protected void AddDisposable(IDisposable disposable)
        {
            _disposables.Add(disposable);
        }

        protected void OnComponentChanged<T>(Action<ComponentChanged<T>> handler)
            where T : unmanaged, IComponent
        {
            if (_bus == null) return;

            var entity = _entity;
            Action<ComponentChanged<T>> filter = e =>
            {
                if (e.Entity == entity)
                    handler(e);
            };

            // Token-based bind-scoped subscription: the token goes into _bindSubscriptions
            // so Unbind removes the handler from the bus, preventing leaks across
            // bind/unbind cycles on pooled mediators.
            _bindSubscriptions.Add(_bus.Subscribe(filter));
        }

        protected void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct
        {
            if (_bus == null) return;
            _bindSubscriptions.Add(_bus.Subscribe(handler));
        }

        protected void Publish<TEvent>(TEvent message) where TEvent : struct
        {
            _bus?.Publish(message);
        }

        protected void SendSignal<TSignal>(TSignal signal) where TSignal : struct
        {
            _bus?.Send(signal);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Unbind();

            foreach (var d in _disposables)
                d.Dispose();
            _disposables.Clear();

            OnDispose();
        }

        protected virtual void OnDispose() { }
    }
}
