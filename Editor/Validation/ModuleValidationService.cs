using System;
using System.Collections.Generic;
using System.Text;
using Strada.Core.Bootstrap;
using Strada.Core.Modules;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Validation
{
    /// <summary>
    /// Centralized service for validating ModuleConfig ScriptableObjects.
    /// Caches validation results for better performance.
    /// </summary>
    public static class ModuleValidationService
    {
        // Keyed by the ModuleConfig reference rather than GetInstanceID(): UnityEngine.Object
        // hashes/compares by instance id anyway, and GetInstanceID() is an error-level
        // obsolete from Unity 6000.5 onwards.
        private static readonly Dictionary<ModuleConfig, ModuleValidationResult> _validationCache = new();
        private static readonly HashSet<int> _pendingValidation = new();

        /// <summary>
        /// Validates a single module config and returns the validation result.
        /// Results are cached until the module is modified.
        /// </summary>
        public static ModuleValidationResult ValidateModule(ModuleConfig module)
        {
            if (module == null)
                return ModuleValidationResult.Invalid("Module is null");

            if (_validationCache.TryGetValue(module, out var cached))
                return cached;

            var result = PerformValidation(module);
            _validationCache[module] = result;
            return result;
        }

        /// <summary>
        /// Validates all enabled modules in the bootstrapper config.
        /// </summary>
        public static List<ModuleValidationResult> ValidateAll(GameBootstrapperConfig bootstrapperConfig)
        {
            var results = new List<ModuleValidationResult>();

            if (bootstrapperConfig == null)
            {
                results.Add(ModuleValidationResult.Invalid("GameBootstrapperConfig is null"));
                return results;
            }

            var enabledModules = new List<ModuleConfig>();
            foreach (var module in bootstrapperConfig.GetEnabledModules())
            {
                enabledModules.Add(module);
            }

            foreach (var module in enabledModules)
            {
                results.Add(ValidateModule(module));
            }

            var dependencyResult = ValidateDependencyGraph(enabledModules);
            if (!dependencyResult.IsValid)
            {
                results.Add(dependencyResult);
            }

            var duplicates = CheckForDuplicates(enabledModules);
            results.AddRange(duplicates);

            return results;
        }

        /// <summary>
        /// Validates the dependency graph for circular dependencies and missing dependencies.
        /// </summary>
        public static ModuleValidationResult ValidateDependencyGraph(IList<ModuleConfig> modules)
        {
            var enabledSet = new HashSet<ModuleConfig>(modules);
            var issues = new List<string>();

            foreach (var module in modules)
            {
                foreach (var dep in module.Dependencies)
                {
                    if (dep != null && !enabledSet.Contains(dep))
                    {
                        issues.Add($"Module '{module.ModuleName}' depends on '{dep.ModuleName}' which is not enabled.");
                    }
                }
            }

            var visited = new HashSet<ModuleConfig>();
            var visiting = new HashSet<ModuleConfig>();

            foreach (var module in modules)
            {
                if (HasCycle(module, visited, visiting, enabledSet, out var cyclePath))
                {
                    var cycleNames = new StringBuilder();
                    for (int i = 0; i < cyclePath.Count; i++)
                    {
                        if (i > 0) cycleNames.Append(" -> ");
                        cycleNames.Append(cyclePath[i].ModuleName);
                    }
                    issues.Add($"Circular dependency detected: {cycleNames}");
                }
            }

            if (issues.Count > 0)
            {
                return new ModuleValidationResult
                {
                    IsValid = false,
                    ModuleName = "Dependency Graph",
                    Errors = issues,
                    Warnings = new List<string>()
                };
            }

            return ModuleValidationResult.Valid("Dependency Graph");
        }

        /// <summary>
        /// Checks for duplicate module configs in the list.
        /// </summary>
        public static List<ModuleValidationResult> CheckForDuplicates(IList<ModuleConfig> modules)
        {
            var results = new List<ModuleValidationResult>();
            var seen = new HashSet<ModuleConfig>();
            var duplicates = new HashSet<ModuleConfig>();

            foreach (var module in modules)
            {
                if (module == null) continue;

                if (!seen.Add(module))
                {
                    duplicates.Add(module);
                }
            }

            foreach (var dup in duplicates)
            {
                results.Add(new ModuleValidationResult
                {
                    IsValid = false,
                    ModuleName = dup.ModuleName,
                    Errors = new List<string> { $"Module '{dup.ModuleName}' is added multiple times to the bootstrapper." },
                    Warnings = new List<string>()
                });
            }

            return results;
        }

        /// <summary>
        /// Invalidates the cache for a specific module.
        /// Call this when a module is modified.
        /// </summary>
        public static void InvalidateCache(ModuleConfig module)
        {
            if (module != null)
            {
                _validationCache.Remove(module);
            }
        }

        /// <summary>
        /// Clears the entire validation cache.
        /// </summary>
        public static void ClearCache()
        {
            _validationCache.Clear();
        }

        /// <summary>
        /// Returns the total number of validation errors across all modules.
        /// </summary>
        public static int GetTotalErrorCount(IEnumerable<ModuleValidationResult> results)
        {
            int total = 0;
            foreach (var r in results)
            {
                total += r.Errors?.Count ?? 0;
            }
            return total;
        }

        /// <summary>
        /// Returns the total number of warnings across all modules.
        /// </summary>
        public static int GetTotalWarningCount(IEnumerable<ModuleValidationResult> results)
        {
            int total = 0;
            foreach (var r in results)
            {
                total += r.Warnings?.Count ?? 0;
            }
            return total;
        }

        private static ModuleValidationResult PerformValidation(ModuleConfig module)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(module.ModuleName))
            {
                errors.Add("Module name is empty or whitespace.");
            }

            if (module.Dependencies != null)
            {
                for (int i = 0; i < module.Dependencies.Count; i++)
                {
                    var dep = module.Dependencies[i];
                    if (dep == null)
                    {
                        warnings.Add($"Dependency at index {i} is null.");
                    }
                    else if (dep == module)
                    {
                        errors.Add("Module cannot depend on itself.");
                    }
                }

                var uniqueDeps = new HashSet<ModuleConfig>();
                foreach (var dep in module.Dependencies)
                {
                    if (dep == null)
                        continue;

                    if (!uniqueDeps.Add(dep))
                    {
                        warnings.Add($"Duplicate dependency: {dep.ModuleName}");
                    }
                }
            }

            var services = module.Services;
            if (services != null)
            {
                for (int i = 0; i < services.Count; i++)
                {
                    var entry = services[i];
                    if (entry.InterfaceType == null && entry.ImplementationType == null)
                    {
                        warnings.Add($"Service entry at index {i} has no type specified.");
                    }
                }
            }

            return new ModuleValidationResult
            {
                IsValid = errors.Count == 0,
                ModuleName = module.ModuleName ?? "Unnamed Module",
                Errors = errors,
                Warnings = warnings
            };
        }

        private static bool HasCycle(
            ModuleConfig current,
            HashSet<ModuleConfig> visited,
            HashSet<ModuleConfig> visiting,
            HashSet<ModuleConfig> enabledSet,
            out List<ModuleConfig> cyclePath)
        {
            cyclePath = null;

            if (visiting.Contains(current))
            {
                cyclePath = new List<ModuleConfig> { current };
                return true;
            }

            if (visited.Contains(current))
                return false;

            visiting.Add(current);

            foreach (var dep in current.Dependencies)
            {
                if (dep != null && enabledSet.Contains(dep))
                {
                    if (HasCycle(dep, visited, visiting, enabledSet, out cyclePath))
                    {
                        cyclePath.Insert(0, current);
                        return true;
                    }
                }
            }

            visiting.Remove(current);
            visited.Add(current);
            return false;
        }
    }

    /// <summary>
    /// Result of validating a module config.
    /// </summary>
    public class ModuleValidationResult
    {
        public bool IsValid { get; set; }
        public string ModuleName { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();

        public static ModuleValidationResult Valid(string moduleName)
        {
            return new ModuleValidationResult
            {
                IsValid = true,
                ModuleName = moduleName,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };
        }

        public static ModuleValidationResult Invalid(string error)
        {
            return new ModuleValidationResult
            {
                IsValid = false,
                ModuleName = "Unknown",
                Errors = new List<string> { error },
                Warnings = new List<string>()
            };
        }

        public override string ToString()
        {
            if (IsValid && Warnings.Count == 0)
                return $"{ModuleName}: Valid";

            if (IsValid)
                return $"{ModuleName}: Valid ({Warnings.Count} warnings)";

            return $"{ModuleName}: Invalid ({Errors.Count} errors, {Warnings.Count} warnings)";
        }
    }
}
