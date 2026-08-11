using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.Sync;
using Strada.Core.Patterns.Interfaces;

namespace Strada.Core.Patterns
{
    public abstract class Model : IModel, IInitializable, IDisposable
    {
        private readonly List<IDisposable> _disposables = new(4);
        private bool _initialized;
        private bool _disposed;

        protected bool IsInitialized => _initialized;

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            OnInitialize();
        }

        protected virtual void OnInitialize() { }

        protected virtual void OnDispose() { }

        public virtual bool Validate() => _initialized;

        protected ReactiveProperty<T> CreateProperty<T>(T initialValue = default)
        {
            var property = new ReactiveProperty<T>(initialValue);
            _disposables.Add(property);
            return property;
        }

        protected ReactiveCollection<T> CreateCollection<T>()
        {
            var collection = new ReactiveCollection<T>();
            _disposables.Add(collection);
            return collection;
        }

        protected void AddDisposable(IDisposable disposable)
        {
            _disposables.Add(disposable);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            OnDispose();

            // LIFO, matching Base.Dispose, and isolated per item: _disposed is already set so
            // Dispose cannot be retried, and one throwing entry would otherwise strand every
            // remaining reactive property and user disposable for good.
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    _disposables[i]?.Dispose();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
            }
            _disposables.Clear();

            GC.SuppressFinalize(this);
        }
    }

    public abstract class Model<TData> : Model where TData : class, new()
    {
        private ReactiveProperty<TData> _dataProperty;

        protected TData Data => _dataProperty?.Value;

        protected IReadOnlyReactiveProperty<TData> DataProperty => _dataProperty;

        protected override void OnInitialize()
        {
            base.OnInitialize();
            _dataProperty = CreateProperty(new TData());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void SetData(TData data)
        {
            _dataProperty.Value = data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void UpdateData(Action<TData> updater)
        {
            updater(Data);
            _dataProperty.Notify();
        }

        public override bool Validate() => base.Validate() && Data != null;
    }

    public abstract class ReactiveModel : Model
    {
        private readonly Dictionary<string, object> _properties = new(8);

        protected ReactiveProperty<T> Property<T>(string name, T initialValue = default)
        {
            if (_properties.TryGetValue(name, out var existing))
            {
                if (existing is not ReactiveProperty<T>)
                    throw new InvalidOperationException($"Property type mismatch: expected {typeof(T)}, got {existing.GetType()}");
                return (ReactiveProperty<T>)existing;
            }

            var property = CreateProperty(initialValue);
            _properties[name] = property;
            return property;
        }

        protected IReadOnlyReactiveProperty<T> GetProperty<T>(string name)
        {
            if (!_properties.TryGetValue(name, out var property))
                return null;

            // Mirrors Property<T>: the dictionary is keyed by name only, so a mismatched T would
            // otherwise surface as a bare InvalidCastException naming neither the property nor
            // the type that was actually stored.
            if (property is IReadOnlyReactiveProperty<T> typed)
                return typed;

            throw new InvalidOperationException(
                $"Property '{name}' type mismatch: expected {typeof(T)}, got {property.GetType()}");
        }
    }
}
