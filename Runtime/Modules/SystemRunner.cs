using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.Communication;
using Strada.Core.DI;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Systems;
using Strada.Core.ECS.World;
using Strada.Core.Logging;
using Strada.Core.Sync;
using UnityEngine;

namespace Strada.Core.Modules
{
    /// <summary>
    /// Config-driven system runner that instantiates and executes ECS systems
    /// based on ModuleConfig definitions. Replaces direct SystemScheduler usage
    /// for modular system configuration.
    /// </summary>
    public sealed class SystemRunner : IDisposable
    {
        private readonly List<SystemInstance>[] _systemsByPhase;
        private readonly List<SystemInstance> _allSystems;
        private readonly EntityManager _entityManager;
        private readonly EventBus _eventBus;
        private readonly EntityHandleRegistry _handleRegistry;
        private readonly IContainer _container;
        private bool _initialized;
        private bool _disposed;

        /// <summary>
        /// Wrapper that holds system instance along with its configuration.
        /// </summary>
        private struct SystemInstance
        {
            public readonly ISystem System;
            public readonly int Order;
            public readonly string Name;

            /// <summary>
            /// Set once this system has thrown. A system that faults every frame would otherwise
            /// write one stack trace per frame for the rest of the run, so only the first is logged.
            /// </summary>
            public bool Faulted;

            public SystemInstance(ISystem system, int order, string name)
            {
                System = system;
                Order = order;
                Name = name;
                Faulted = false;
            }
        }

        /// <summary>
        /// Creates a new SystemRunner.
        /// </summary>
        /// <param name="entityManager">The entity manager for system injection.</param>
        /// <param name="eventBus">The event bus for system injection.</param>
        /// <param name="handleRegistry">The entity handle registry for system injection.</param>
        /// <param name="container">The DI container for resolving system dependencies.</param>
        public SystemRunner(EntityManager entityManager, EventBus eventBus, EntityHandleRegistry handleRegistry, IContainer container = null)
        {
            _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
            _eventBus = eventBus;
            _handleRegistry = handleRegistry;
            _container = container;

            int phaseCount = Enum.GetValues(typeof(UpdatePhase)).Length;
            _systemsByPhase = new List<SystemInstance>[phaseCount];
            for (int i = 0; i < phaseCount; i++)
                _systemsByPhase[i] = new List<SystemInstance>(8);
            _allSystems = new List<SystemInstance>(32);
        }

        /// <summary>
        /// Gets the total number of registered systems.
        /// </summary>
        public int SystemCount => _allSystems.Count;

        /// <summary>
        /// Gets all registered systems.
        /// </summary>
        public IReadOnlyList<ISystem> GetAllSystems()
        {
            var result = new List<ISystem>(_allSystems.Count);
            foreach (var instance in _allSystems)
                result.Add(instance.System);
            return result;
        }

        /// <summary>
        /// Adds systems from a ModuleConfig to this runner.
        /// </summary>
        /// <param name="config">The module configuration containing system entries.</param>
        public void AddSystemsFromConfig(ModuleConfig config)
        {
            ThrowIfDisposed();

            if (config == null || !config.Enabled)
                return;

            // GetEnabledSystems() is the accessor that also filters out null list elements.
            // Walking config.Systems directly reproduced only the Enabled/IsValid predicates, so a
            // null entry — which OnValidate only strips in the Editor — dereferenced here.
            foreach (var entry in config.GetEnabledSystems())
            {
                var system = CreateSystem(entry);
                if (system != null)
                {
                    AddSystem(system, entry.Phase, entry.Order, entry.DisplayName);
                }
            }
        }

        /// <summary>
        /// Adds systems from multiple ModuleConfigs, ordered by their priority.
        /// </summary>
        /// <param name="configs">The module configurations to process.</param>
        public void AddSystemsFromConfigs(IEnumerable<ModuleConfig> configs)
        {
            ThrowIfDisposed();

            if (configs == null)
                return;

            // A ModuleConfig listed twice would otherwise have every one of its systems
            // instantiated and ticked twice for the life of the process.
            var seen = new HashSet<ModuleConfig>();
            foreach (var config in configs)
            {
                if (config == null || !seen.Add(config))
                    continue;

                AddSystemsFromConfig(config);
            }
        }

        /// <summary>
        /// Adds a system instance directly.
        /// </summary>
        /// <param name="system">The system to add.</param>
        /// <param name="phase">The update phase for this system.</param>
        /// <param name="order">The execution order within the phase.</param>
        /// <param name="name">Optional display name for debugging.</param>
        public void AddSystem(ISystem system, UpdatePhase phase = UpdatePhase.Update, int order = 0, string name = null)
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            // Without this a system added after Dispose() is injected, initialized and inserted
            // into lists that will never be drained again — it can never be disposed, because the
            // _disposed short-circuit makes a second Dispose() a no-op.
            ThrowIfDisposed();

            if (_initialized)
            {
                StradaLog.LogWarning("Adding system after initialization. System will be initialized immediately.", LogModule.Modules);
                InjectSystem(system);
                system.Initialize();
            }

            // C# does not range-check enum casts and Unity deserializes enums as raw ints without
            // validating them, so a hand-edited asset or `AddSystem(sys, (UpdatePhase)99)` would
            // index past _systemsByPhase and abort the whole bootstrap.
            int phaseIndex = (int)phase;
            if ((uint)phaseIndex >= (uint)_systemsByPhase.Length)
            {
                StradaLog.LogError(
                    $"System '{name ?? system.GetType().Name}' has an out-of-range UpdatePhase ({phaseIndex}); defaulting to Update.",
                    LogModule.Modules);
                phaseIndex = (int)UpdatePhase.Update;
            }

            var instance = new SystemInstance(system, order, name ?? system.GetType().Name);
            var phaseList = _systemsByPhase[phaseIndex];

            int insertIndex = 0;
            for (int i = 0; i < phaseList.Count; i++)
            {
                if (phaseList[i].Order > order)
                    break;
                insertIndex++;
            }
            phaseList.Insert(insertIndex, instance);
            _allSystems.Add(instance);
        }

        /// <summary>
        /// Initializes all registered systems.
        /// </summary>
        public void Initialize()
        {
            ThrowIfDisposed();
            if (_initialized) return;
            _initialized = true;

            foreach (var instance in _allSystems)
            {
                InjectSystem(instance.System);
            }

            var initSystems = _systemsByPhase[(int)UpdatePhase.Initialization];
            InitializePhase(initSystems);

            for (int phase = 1; phase < _systemsByPhase.Length; phase++)
            {
                InitializePhase(_systemsByPhase[phase]);
            }
        }

        private static void InitializePhase(List<SystemInstance> systems)
        {
            for (int i = 0; i < systems.Count; i++)
            {
                try
                {
                    systems[i].System.Initialize();
                }
                catch (Exception ex)
                {
                    // Without isolation, one system throwing in Initialize leaves every system
                    // ordered after it uninitialized while the bootstrap still reports success.
                    StradaLog.LogError($"System '{systems[i].Name}' threw during Initialize.", LogModule.Modules);
                    Debug.LogException(ex);
                }
            }
        }

        /// <summary>
        /// Updates systems in the Update phase.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(float deltaTime)
        {
            RunPhase((int)UpdatePhase.Update, deltaTime);
        }

        /// <summary>
        /// Updates systems in the LateUpdate phase.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void LateUpdate(float deltaTime)
        {
            RunPhase((int)UpdatePhase.LateUpdate, deltaTime);
        }

        /// <summary>
        /// Updates systems in the FixedUpdate phase.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FixedUpdate(float fixedDeltaTime)
        {
            RunPhase((int)UpdatePhase.FixedUpdate, fixedDeltaTime);
        }

        /// <summary>
        /// Ticks every system in one phase, isolating each call so that a system which throws
        /// cannot stop the systems ordered after it. Without this, a single throwing system
        /// silently skipped every later system in its phase on every frame for the rest of the run.
        /// </summary>
        private void RunPhase(int phase, float deltaTime)
        {
            // A late frame can still arrive after teardown; degrade quietly rather than
            // touching cleared lists.
            if (_disposed) return;

            var systems = _systemsByPhase[phase];
            for (int i = 0; i < systems.Count; i++)
            {
                try
                {
                    systems[i].System.Update(deltaTime);
                }
                catch (Exception ex)
                {
                    var instance = systems[i];
                    if (!instance.Faulted)
                    {
                        // Only the first throw is reported. A system that faults every frame would
                        // otherwise write one stack trace per frame for the rest of the run — the
                        // exception is still surfaced through Unity exactly as it was when it
                        // escaped to the MonoBehaviour boundary.
                        instance.Faulted = true;
                        systems[i] = instance;
                        StradaLog.LogError(
                            $"System '{instance.Name}' threw during Update; further exceptions from it are not reported.",
                            LogModule.Modules);
                        Debug.LogException(ex);
                    }
                }
            }
        }

        /// <summary>
        /// Disposes all systems in reverse order.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = _allSystems.Count - 1; i >= 0; i--)
            {
                try
                {
                    _allSystems[i].System.Dispose();
                }
                catch (Exception ex)
                {
                    // Keep draining: one system throwing here previously skipped the disposal of
                    // every system registered before it and left the phase lists populated.
                    StradaLog.LogError($"System '{_allSystems[i].Name}' threw during Dispose.", LogModule.Modules);
                    Debug.LogException(ex);
                }
            }

            _allSystems.Clear();
            for (int i = 0; i < _systemsByPhase.Length; i++)
                _systemsByPhase[i].Clear();
        }

        /// <summary>
        /// Gets debug information about registered systems.
        /// </summary>
        public string GetDebugInfo()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SystemRunner Debug Info ===");
            sb.AppendLine($"Total Systems: {_allSystems.Count}");
            sb.AppendLine($"Initialized: {_initialized}");
            sb.AppendLine();

            string[] phaseNames = Enum.GetNames(typeof(UpdatePhase));
            for (int phase = 0; phase < _systemsByPhase.Length; phase++)
            {
                var systems = _systemsByPhase[phase];
                if (systems.Count == 0) continue;

                sb.AppendLine($"[{phaseNames[phase]}] ({systems.Count} systems):");
                for (int i = 0; i < systems.Count; i++)
                {
                    sb.AppendLine($"  {i + 1}. {systems[i].Name} (order: {systems[i].Order})");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SystemRunner));
        }

        private ISystem CreateSystem(SystemEntry entry)
        {
            var systemType = entry.GetSystemType();
            if (systemType == null)
            {
                StradaLog.LogWarning($"System type is null for entry: {entry.DisplayName}", LogModule.Modules);
                return null;
            }

            if (_container != null && _container.IsRegistered(systemType))
            {
                return _container.Resolve(systemType) as ISystem;
            }

            if (!typeof(ISystem).IsAssignableFrom(systemType))
                throw new InvalidOperationException($"Type {systemType.Name} does not implement ISystem");

            return Activator.CreateInstance(systemType) as ISystem;
        }

        private void InjectSystem(ISystem system)
        {
            if (system is SystemBase systemBase)
            {
                systemBase.Inject(_entityManager, _eventBus, _handleRegistry);
            }
        }
    }
}
