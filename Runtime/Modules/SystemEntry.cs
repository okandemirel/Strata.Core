using System;
using UnityEngine;
using Strada.Core.ECS;
using Strada.Core.ECS.World;

namespace Strada.Core.Modules
{
    /// <summary>
    /// Serializable entry for configuring an ECS system in a ModuleConfig.
    /// Allows enabling/disabling systems and configuring their update phase and order via the Inspector.
    /// </summary>
    [Serializable]
    public class SystemEntry
    {
        [Tooltip("The system type to instantiate")]
        [SerializeField] private SerializableType _systemType;

        [Tooltip("When this system updates")]
        [SerializeField] private UpdatePhase _phase = UpdatePhase.Update;

        [Tooltip("Execution order within the phase (lower values execute first)")]
        [SerializeField] private int _order = 0;

        [Tooltip("Whether this system is enabled")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("Optional category for organizing systems in the Inspector")]
        [SerializeField] private string _category = "";

        [Tooltip("Optional description shown as tooltip")]
        [SerializeField, TextArea(1, 3)] private string _description = "";

        /// <summary>
        /// Gets the system type.
        /// </summary>
        public SerializableType SystemType => _systemType;

        /// <summary>
        /// Gets the update phase for this system.
        /// </summary>
        public UpdatePhase Phase => _phase;

        /// <summary>
        /// Gets the execution order within the phase.
        /// </summary>
        public int Order => _order;

        /// <summary>
        /// Gets whether this system is enabled.
        /// </summary>
        public bool Enabled => _enabled;

        /// <summary>
        /// Gets the category for Inspector organization.
        /// </summary>
        public string Category => _category;

        /// <summary>
        /// Gets the description tooltip.
        /// </summary>
        public string Description => _description;

        /// <summary>
        /// Gets the resolved Type of the system, validated against <see cref="ISystem"/>.
        /// Returns <c>null</c> if the serialized type does not implement <see cref="ISystem"/>
        /// (an error is logged in that case).
        /// </summary>
        public Type GetSystemType() => _systemType?.AsType<ISystem>();

        /// <summary>
        /// Gets whether this entry has a valid system type assigned.
        /// </summary>
        public bool IsValid => _systemType != null && _systemType.IsValid;

        /// <summary>
        /// Gets the display name for this system (type name or "(None)").
        /// </summary>
        public string DisplayName => _systemType?.TypeName ?? "(None)";

        public SystemEntry() { }

        public SystemEntry(Type systemType, UpdatePhase phase = UpdatePhase.Update, int order = 0, bool enabled = true)
        {
            _systemType = new SerializableType(systemType);
            _phase = phase;
            _order = order;
            _enabled = enabled;
        }

        /// <summary>
        /// Creates a SystemEntry from a SystemInfo discovered via attributes.
        /// </summary>
        public static SystemEntry FromSystemInfo(SystemInfo info)
        {
            return new SystemEntry
            {
                _systemType = new SerializableType(info.Type),
                _phase = info.Phase,
                _order = info.Order,
                _category = info.Category,
                _description = info.Description,
                _enabled = true
            };
        }
    }

    /// <summary>
    /// Information about a discovered system type.
    /// Used by RuntimeSystemDiscovery and source generation.
    /// </summary>
    public readonly struct SystemInfo
    {
        public readonly Type Type;
        public readonly string Module;
        public readonly string Category;
        public readonly string Description;
        public readonly UpdatePhase Phase;
        public readonly int Order;

        public SystemInfo(Type type, string module, string category, string description, UpdatePhase phase, int order)
        {
            Type = type;
            Module = module ?? "";
            Category = category ?? "";
            Description = description ?? "";
            Phase = phase;
            Order = order;
        }

        public SystemInfo(Type type, UpdatePhase phase, int order)
            : this(type, "", "", "", phase, order) { }
    }
}
