using System;
using UnityEngine;

namespace Strada.Core.Data
{
    public abstract class ConfigData : ScriptableObject
    {
        [SerializeField] private string _guid;

        public string Guid
        {
            get
            {
                if (string.IsNullOrEmpty(_guid))
                    _guid = System.Guid.NewGuid().ToString();
                return _guid;
            }
        }

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            if (string.IsNullOrEmpty(_guid))
                _guid = System.Guid.NewGuid().ToString();
        }

        protected virtual void Reset()
        {
            _guid = System.Guid.NewGuid().ToString();
        }
#endif
    }

    public abstract class ConfigData<T> : ConfigData where T : class, new()
    {
        [SerializeField] private T _data;

        public T Data
        {
            get
            {
                if (_data == null)
                    _data = new T();
                return _data;
            }
            set => _data = value ?? throw new ArgumentNullException(nameof(value),
                "ConfigData<T>.Data cannot be set to null; use a default instance instead.");
        }

        public ref T GetDataRef() => ref _data;
    }

    [Serializable]
    public abstract class ConfigDataValue
    {
        public virtual void Validate() { }
    }
}
