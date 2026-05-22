using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Strada.Core.Modules;

namespace Strada.Core.Bootstrap
{
    /// <summary>
    /// Central configuration ScriptableObject for the Strada framework.
    /// Contains all modules and their configurations, similar to Quantum 3's SimulationConfig.
    ///
    /// Modules are initialized in priority order (lower values first).
    /// </summary>
    [CreateAssetMenu(fileName = "GameBootstrapperConfig", menuName = "Strada/Game Bootstrapper Config")]
    public class GameBootstrapperConfig : ScriptableObject
    {
        [Header("Modules")]
        [Tooltip("List of modules to load. Modules are initialized in priority order (lower values first).")]
        [SerializeField] private List<ModuleEntry> _modules = new();

        [Header("Settings")]
        [Tooltip("Enable verbose logging during bootstrap")]
        [SerializeField] private bool _verboseLogging = false;

        [Tooltip("Validate module dependencies and configurations on start")]
        [SerializeField] private bool _validateOnStart = true;

        [Tooltip("Fail initialization if validation errors are found")]
        [SerializeField] private bool _failOnValidationError = true;

        [Tooltip("Use async initialization (yields between module initialization)")]
        [SerializeField] private bool _asyncInitialization = true;

        /// <summary>
        /// Gets the list of module entries.
        /// </summary>
        public IReadOnlyList<ModuleEntry> Modules => _modules;

        /// <summary>
        /// Gets whether verbose logging is enabled.
        /// </summary>
        public bool VerboseLogging => _verboseLogging;

        /// <summary>
        /// Gets whether to validate on start.
        /// </summary>
        public bool ValidateOnStart => _validateOnStart;

        /// <summary>
        /// Gets whether to fail on validation errors.
        /// </summary>
        public bool FailOnValidationError => _failOnValidationError;

        /// <summary>
        /// Gets whether to use async initialization.
        /// </summary>
        public bool AsyncInitialization => _asyncInitialization;

        /// <summary>
        /// Gets all enabled modules sorted by priority (lower values first).
        /// </summary>
        public IEnumerable<ModuleConfig> GetEnabledModules()
        {
            return _modules
                .Where(m => m != null && m.IsActiveAndValid)
                .OrderBy(m => m.Priority)
                .Select(m => m.Config);
        }

        /// <summary>
        /// Gets the count of enabled modules.
        /// </summary>
        public int EnabledModuleCount => _modules.Count(m => m != null && m.IsActiveAndValid);

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        /// <param name="errors">List to receive validation errors.</param>
        /// <returns>True if validation passed, false otherwise.</returns>
        public bool Validate(out List<string> errors)
        {
            errors = new List<string>();

            // Check for null entries
            if (_modules.Any(m => m == null))
            {
                errors.Add("Module list contains null entries");
            }

            // Check for null configs
            foreach (var entry in _modules.Where(m => m != null && m.Enabled))
            {
                if (entry.Config == null)
                {
                    errors.Add($"Module entry is enabled but has no config assigned");
                }
            }

            // Check for duplicate modules
            var enabledModules = _modules
                .Where(m => m?.IsActiveAndValid == true)
                .Select(m => m.Config)
                .ToList();

            var duplicates = enabledModules
                .GroupBy(m => m)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.ModuleName);

            foreach (var dup in duplicates)
            {
                errors.Add($"Module '{dup}' is added multiple times");
            }

            // Check for circular dependencies
            if (HasCircularDependencies(out var cyclePath))
            {
                errors.Add($"Circular dependency detected: {cyclePath}");
            }

            // Check for missing dependencies
            var moduleSet = new HashSet<ModuleConfig>(enabledModules);
            foreach (var module in enabledModules)
            {
                foreach (var dep in module.Dependencies)
                {
                    if (dep != null && !moduleSet.Contains(dep))
                    {
                        errors.Add($"Module '{module.ModuleName}' depends on '{dep.ModuleName}' which is not enabled");
                    }
                }
            }

            return errors.Count == 0;
        }

        /// <summary>
        /// Checks for circular dependencies between modules.
        /// </summary>
        private bool HasCircularDependencies(out string cyclePath)
        {
            cyclePath = null;
            var visited = new HashSet<ModuleConfig>();
            var visiting = new HashSet<ModuleConfig>();
            var path = new List<string>();

            foreach (var entry in _modules.Where(m => m?.IsActiveAndValid == true))
            {
                if (DetectCycle(entry.Config, visited, visiting, path))
                {
                    cyclePath = string.Join(" -> ", path);
                    return true;
                }
            }

            return false;
        }

        private bool DetectCycle(ModuleConfig module, HashSet<ModuleConfig> visited,
            HashSet<ModuleConfig> visiting, List<string> path)
        {
            if (module == null) return false;

            if (visiting.Contains(module))
            {
                path.Add(module.ModuleName);
                return true;
            }

            if (visited.Contains(module)) return false;

            visiting.Add(module);
            path.Add(module.ModuleName);

            foreach (var dep in module.Dependencies)
            {
                if (DetectCycle(dep, visited, visiting, path))
                {
                    return true;
                }
            }

            visiting.Remove(module);
            path.RemoveAt(path.Count - 1);
            visited.Add(module);
            return false;
        }

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            // Remove null entries
            _modules?.RemoveAll(m => m == null);
        }

        /// <summary>
        /// Editor method: Adds a module to the config.
        /// </summary>
        public void EditorAddModule(ModuleConfig config)
        {
            _modules ??= new List<ModuleEntry>();
            _modules.Add(new ModuleEntry(config));
            UnityEditor.EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Editor method: Removes a module from the config.
        /// </summary>
        public void EditorRemoveModule(ModuleConfig config)
        {
            _modules?.RemoveAll(m => m?.Config == config);
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
