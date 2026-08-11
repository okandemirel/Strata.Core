using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Strada.Core.ECS;
using Strada.Core.ECS.Systems;
using Strada.Core.ECS.World;
using UnityEngine;

namespace Strada.Core.Modules
{
    /// <summary>
    /// Discovers system types at runtime using reflection.
    /// Scans assemblies for classes marked with [StradaSystem] or inheriting from SystemBase.
    /// </summary>
    public static class RuntimeSystemDiscovery
    {
        private static readonly Dictionary<string, List<SystemInfo>> _cachedSystems = new();
        private static bool _cacheInitialized;

        /// <summary>
        /// Discovers all system types in the current AppDomain.
        /// </summary>
        /// <param name="filterModule">Optional module name to filter results.</param>
        /// <returns>Collection of discovered system information.</returns>
        public static IEnumerable<SystemInfo> DiscoverSystems(string filterModule = null)
        {
            EnsureCacheInitialized();

            if (string.IsNullOrEmpty(filterModule))
            {
                return _cachedSystems.Values.SelectMany(list => list);
            }

            if (_cachedSystems.TryGetValue(filterModule, out var systems))
            {
                return systems;
            }

            return Enumerable.Empty<SystemInfo>();
        }

        /// <summary>
        /// Gets all unique module names from discovered systems.
        /// </summary>
        public static IEnumerable<string> GetDiscoveredModules()
        {
            EnsureCacheInitialized();
            return _cachedSystems.Keys.Where(k => !string.IsNullOrEmpty(k));
        }

        /// <summary>
        /// Clears the discovery cache, forcing a re-scan on next access.
        /// </summary>
        public static void ClearCache()
        {
            _cachedSystems.Clear();
            _cacheInitialized = false;
        }

        /// <summary>
        /// Forces a re-scan of all assemblies.
        /// </summary>
        public static void Refresh()
        {
            ClearCache();
            EnsureCacheInitialized();
        }

        /// <summary>
        /// Discovers systems in a specific assembly.
        /// </summary>
        /// <param name="assembly">The assembly to scan.</param>
        /// <returns>Collection of discovered system information.</returns>
        public static IEnumerable<SystemInfo> DiscoverSystemsInAssembly(Assembly assembly)
        {
            if (assembly == null)
                yield break;

            foreach (var type in GetSystemTypesFromAssembly(assembly))
            {
                var info = CreateSystemInfo(type);
                if (info.HasValue)
                    yield return info.Value;
            }
        }

        /// <summary>
        /// Converts discovered systems to SystemEntry objects for use in ModuleConfig.
        /// </summary>
        /// <param name="systems">The systems to convert.</param>
        /// <returns>Collection of SystemEntry objects.</returns>
        public static IEnumerable<SystemEntry> ToSystemEntries(IEnumerable<SystemInfo> systems)
        {
            return systems.Select(SystemEntry.FromSystemInfo);
        }

        private static void EnsureCacheInitialized()
        {
            if (_cacheInitialized)
                return;

            // The flag is only raised once the scan has completed. Setting it first meant a throw
            // partway through left the cache flagged as ready while holding whatever the loop had
            // gathered before the failure, and every later call returned those partial results
            // silently.
            try
            {
                ScanAllAssemblies();
            }
            catch
            {
                _cachedSystems.Clear();
                throw;
            }

            _cacheInitialized = true;
        }

        private static void ScanAllAssemblies()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                if (ShouldSkipAssembly(assembly))
                    continue;

                foreach (var type in GetSystemTypesFromAssembly(assembly))
                {
                    var info = CreateSystemInfo(type);
                    if (info.HasValue)
                    {
                        var moduleName = info.Value.Module ?? "";
                        if (!_cachedSystems.TryGetValue(moduleName, out var list))
                        {
                            list = new List<SystemInfo>();
                            _cachedSystems[moduleName] = list;
                        }
                        list.Add(info.Value);
                    }
                }
            }
        }

        private static bool ShouldSkipAssembly(Assembly assembly)
        {
            // GetTypes() on a Reflection.Emit assembly that still has unfinished TypeBuilders
            // throws NotSupportedException, and the Editor domain routinely hosts such assemblies
            // (serializer/proxy/property-test generators). ModuleRegistry.DiscoverModules already
            // skips them for the same reason.
            if (assembly.IsDynamic)
                return true;

            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
                return true;

            return name.StartsWith("System", StringComparison.Ordinal) ||
                   name.StartsWith("Microsoft", StringComparison.Ordinal) ||
                   name.StartsWith("Unity.", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                   name.StartsWith("mscorlib", StringComparison.Ordinal) ||
                   name.StartsWith("netstandard", StringComparison.Ordinal) ||
                   name.StartsWith("Mono.", StringComparison.Ordinal);
        }

        private static IEnumerable<Type> GetSystemTypesFromAssembly(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch (Exception ex)
            {
                // GetTypes() can also throw NotSupportedException (dynamic assemblies) or
                // TypeLoadException/FileNotFoundException (a broken dependency). Those escaped the
                // iterator and aborted discovery for every remaining assembly; one unreadable
                // assembly should only cost that assembly.
                Debug.LogWarning(
                    $"[RuntimeSystemDiscovery] Skipping assembly '{assembly.GetName().Name}': {ex.Message}");
                types = Array.Empty<Type>();
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                if (!typeof(ISystem).IsAssignableFrom(type))
                    continue;

                yield return type;
            }
        }

        private static SystemInfo? CreateSystemInfo(Type type)
        {
            var stradaAttr = type.GetCustomAttribute<StradaSystemAttribute>();
            var phaseAttr = type.GetCustomAttribute<UpdatePhaseAttribute>();
            var orderAttr = type.GetCustomAttribute<ExecutionOrderAttribute>();
            var categoryAttr = type.GetCustomAttribute<SystemCategoryAttribute>();
            var descriptionAttr = type.GetCustomAttribute<SystemDescriptionAttribute>();

            string module = stradaAttr?.Module ?? "";
            string category = stradaAttr?.Category ?? categoryAttr?.Category ?? "";
            string description = stradaAttr?.Description ?? descriptionAttr?.Description ?? "";
            var phase = stradaAttr?.Phase ?? phaseAttr?.Phase ?? UpdatePhase.Update;
            int order = stradaAttr?.Order ?? orderAttr?.Order ?? 0;

            return new SystemInfo(type, module, category, description, phase, order);
        }

        /// <summary>
        /// Validates system dependencies based on RequiresSystem attributes.
        /// </summary>
        /// <param name="enabledSystems">The currently enabled system types.</param>
        /// <param name="errors">Output list of validation errors.</param>
        /// <returns>True if all dependencies are satisfied.</returns>
        public static bool ValidateSystemDependencies(IEnumerable<Type> enabledSystems, out List<string> errors)
        {
            errors = new List<string>();
            var enabledSet = new HashSet<Type>(enabledSystems);

            foreach (var systemType in enabledSet)
            {
                var requirements = systemType.GetCustomAttributes<RequiresSystemAttribute>();
                foreach (var req in requirements)
                {
                    if (!enabledSet.Contains(req.SystemType))
                    {
                        errors.Add($"{systemType.Name} requires {req.SystemType.Name} but it is not enabled.");
                    }
                }
            }

            return errors.Count == 0;
        }

        /// <summary>
        /// Sorts systems based on RunBefore/RunAfter attributes.
        /// </summary>
        /// <param name="systems">The systems to sort.</param>
        /// <returns>Sorted list of systems.</returns>
        public static List<SystemInfo> TopologicalSort(IEnumerable<SystemInfo> systems)
        {
            var systemList = systems.ToList();
            var typeToIndex = new Dictionary<Type, int>();
            for (int i = 0; i < systemList.Count; i++)
                typeToIndex[systemList[i].Type] = i;

            var graph = new List<int>[systemList.Count];
            var inDegree = new int[systemList.Count];
            for (int i = 0; i < systemList.Count; i++)
                graph[i] = new List<int>();

            for (int i = 0; i < systemList.Count; i++)
            {
                var type = systemList[i].Type;

                foreach (var attr in type.GetCustomAttributes<RunBeforeAttribute>())
                {
                    if (typeToIndex.TryGetValue(attr.SystemType, out int targetIdx))
                    {
                        graph[i].Add(targetIdx);
                        inDegree[targetIdx]++;
                    }
                }

                foreach (var attr in type.GetCustomAttributes<RunAfterAttribute>())
                {
                    if (typeToIndex.TryGetValue(attr.SystemType, out int targetIdx))
                    {
                        graph[targetIdx].Add(i);
                        inDegree[i]++;
                    }
                }
            }

            var result = new List<SystemInfo>();
            var queue = new Queue<int>();

            for (int i = 0; i < systemList.Count; i++)
            {
                if (inDegree[i] == 0)
                    queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                result.Add(systemList[node]);

                foreach (int neighbor in graph[node])
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }

            if (result.Count != systemList.Count)
            {
                Debug.LogWarning("[RuntimeSystemDiscovery] Circular dependency detected in system ordering. Using original order.");
                return systemList;
            }

            return result;
        }
    }
}
