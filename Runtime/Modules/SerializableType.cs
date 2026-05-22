using System;
using UnityEngine;

namespace Strada.Core.Modules
{
    /// <summary>
    /// A serializable wrapper for System.Type that can be displayed and selected in the Unity Inspector.
    /// Used for configuring systems and services in ModuleConfig ScriptableObjects.
    /// </summary>
    [Serializable]
    public class SerializableType : ISerializationCallbackReceiver
    {
        [SerializeField] private string _assemblyQualifiedName;

        private Type _cachedType;
        private bool _resolveAttempted;

        /// <summary>
        /// Gets or sets the Type represented by this SerializableType.
        /// </summary>
        public Type Type
        {
            get
            {
                if (_cachedType == null && !_resolveAttempted && !string.IsNullOrEmpty(_assemblyQualifiedName))
                {
                    _resolveAttempted = true;
                    _cachedType = Type.GetType(_assemblyQualifiedName);
                    if (_cachedType == null)
                        Debug.LogWarning($"[SerializableType] Failed to resolve type: {_assemblyQualifiedName}");
                }
                return _cachedType;
            }
            set
            {
                _cachedType = value;
                _resolveAttempted = value != null;
                _assemblyQualifiedName = value?.AssemblyQualifiedName;
            }
        }

        /// <summary>
        /// Gets the assembly qualified name of the type.
        /// </summary>
        public string AssemblyQualifiedName => _assemblyQualifiedName;

        /// <summary>
        /// Resolves the type and returns it only if it is assignable to <typeparamref name="TBase"/>.
        /// Returns <c>null</c> and logs an error otherwise.
        /// </summary>
        /// <remarks>
        /// Use this method instead of the <see cref="Type"/> property when the consuming code
        /// expects a specific base type or interface (defense-in-depth against tampered assets
        /// or asset bundles that may carry an arbitrary assembly-qualified name).
        /// </remarks>
        public Type AsType<TBase>() where TBase : class
        {
            var resolved = Type;
            if (resolved == null) return null;
            if (!typeof(TBase).IsAssignableFrom(resolved))
            {
                Debug.LogError(
                    $"[SerializableType] Type '{resolved.FullName}' is not assignable to {typeof(TBase).FullName}; rejected.");
                return null;
            }
            return resolved;
        }

        /// <summary>
        /// Gets whether this SerializableType has a valid type assigned.
        /// </summary>
        public bool IsValid => Type != null;

        /// <summary>
        /// Gets the simple name of the type (without namespace).
        /// </summary>
        public string TypeName => Type?.Name ?? "(None)";

        /// <summary>
        /// Gets the full name of the type (with namespace).
        /// </summary>
        public string FullTypeName => Type?.FullName ?? "(None)";

        public SerializableType() { }

        public SerializableType(Type type)
        {
            Type = type;
        }

        public void OnBeforeSerialize()
        {
            // Ensure _assemblyQualifiedName is up to date
            if (_cachedType != null)
            {
                _assemblyQualifiedName = _cachedType.AssemblyQualifiedName;
            }
        }

        public void OnAfterDeserialize()
        {
            // Clear cache so it will be resolved fresh on next access
            _cachedType = null;
            _resolveAttempted = false;
        }

        public override string ToString()
        {
            return TypeName;
        }

        public override bool Equals(object obj)
        {
            if (obj is SerializableType other)
            {
                return _assemblyQualifiedName == other._assemblyQualifiedName;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return _assemblyQualifiedName?.GetHashCode() ?? 0;
        }

        public static implicit operator Type(SerializableType serializableType)
        {
            return serializableType?.Type;
        }

        public static implicit operator SerializableType(Type type)
        {
            return new SerializableType(type);
        }
    }
}
