using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Strada.Core.Modules
{
    /// <summary>
    /// Registry for discovering and managing module installers.
    /// Automatically finds all IModuleInstaller implementations and determines initialization order.
    /// </summary>
    /// <remarks>
    /// <para>DEPRECATED: This class is part of the legacy module system.</para>
    /// <para>Please use ModuleConfig ScriptableObjects with GameBootstrapperConfig instead.</para>
    /// <para>This class will be removed in v2.0.</para>
    /// </remarks>
    [Obsolete("Use ModuleConfig ScriptableObjects with GameBootstrapperConfig instead. This class will be removed in v2.0.")]
    public class ModuleRegistry
    {
        private readonly List<ModuleInfo> _modules = new List<ModuleInfo>();
        private readonly HashSet<Type> _registeredTypes = new HashSet<Type>();

        /// <summary>
        /// Gets the collection of registered modules.
        /// </summary>
        public IReadOnlyList<ModuleInfo> Modules => _modules;

        /// <summary>
        /// Discovers all IModuleInstaller implementations in loaded assemblies.
        /// </summary>
        /// <param name="assemblyFilter">Optional filter to limit which assemblies are scanned. If null, all assemblies are scanned.</param>
        public void DiscoverModules(Func<Assembly, bool> assemblyFilter = null)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic)
                    continue;

                var name = assembly.GetName().Name;
                if (!name.StartsWith("Strada.") && !name.StartsWith("Game.") && name != "Assembly-CSharp")
                    continue;

                if (assemblyFilter != null && !assemblyFilter(assembly))
                {
                    continue;
                }

                try
                {
                    DiscoverModulesInAssembly(assembly);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ModuleRegistry] Failed to scan assembly {assembly.GetName().Name}: {ex.Message}");
                }
            }

            SortModulesByDependencies();
        }

        /// <summary>
        /// Manually registers a module installer.
        /// </summary>
        /// <param name="installer">The module installer to register.</param>
        /// <param name="priority">Optional priority for initialization order. Lower values initialize first.</param>
        public void RegisterModule(IModuleInstaller installer, int priority = 0)
        {
            if (installer == null)
            {
                throw new ArgumentNullException(nameof(installer));
            }

            var type = installer.GetType();

            if (_registeredTypes.Contains(type))
            {
                UnityEngine.Debug.LogWarning($"[ModuleRegistry] Module {type.Name} is already registered. Skipping.");
                return;
            }

            var moduleInfo = new ModuleInfo
            {
                Installer = installer,
                Type = type,
                Name = type.Name,
                Priority = priority,
                Dependencies = ExtractDependencies(type)
            };

            _modules.Add(moduleInfo);
            _registeredTypes.Add(type);
        }

        /// <summary>
        /// Clears all registered modules.
        /// </summary>
        public void Clear()
        {
            _modules.Clear();
            _registeredTypes.Clear();
        }

        /// <summary>
        /// Sorts modules by their dependencies using topological sort.
        /// Call this after manually registering modules to ensure correct initialization order.
        /// </summary>
        public void Sort()
        {
            SortModulesByDependencies();
        }

        /// <summary>
        /// Validates that all modules can be initialized without circular dependencies.
        /// </summary>
        /// <returns>True if validation succeeds, false otherwise.</returns>
        public bool Validate(out string errorMessage)
        {
            errorMessage = null;

            var dependencyGraph = BuildDependencyGraph();
            if (HasCircularDependency(dependencyGraph, out var cycle))
            {
                errorMessage = $"Circular module dependency detected: {string.Join(" -> ", cycle)}";
                return false;
            }

            return true;
        }

        private void DiscoverModulesInAssembly(Assembly assembly)
        {
            var installerType = typeof(IModuleInstaller);
            var types = assembly.GetTypes()
                .Where(t => installerType.IsAssignableFrom(t) &&
                           t.IsClass &&
                           !t.IsAbstract &&
                           // An open generic (MyModule<T>) has a parameterless constructor but
                           // cannot be instantiated; without this it reached Activator and
                           // logged an error for every generic module installer in the project.
                           !t.ContainsGenericParameters &&
                           t.GetConstructor(Type.EmptyTypes) != null);

            foreach (var type in types)
            {
                try
                {
                    var installer = (IModuleInstaller)Activator.CreateInstance(type);
                    var priority = GetModulePriority(type);
                    RegisterModule(installer, priority);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[ModuleRegistry] Failed to create instance of {type.Name}: {ex.Message}");
                }
            }
        }

        private int GetModulePriority(Type type)
        {
            var attribute = type.GetCustomAttribute<ModulePriorityAttribute>();
            return attribute?.Priority ?? 0;
        }

        private List<Type> ExtractDependencies(Type moduleType)
        {
            var dependencies = new List<Type>();
            var attribute = moduleType.GetCustomAttribute<ModuleDependsOnAttribute>();

            if (attribute != null)
            {
                dependencies.AddRange(attribute.Dependencies);
            }

            return dependencies;
        }

        private void SortModulesByDependencies()
        {
            var sorted = new List<ModuleInfo>();
            var visited = new HashSet<Type>();
            var visiting = new HashSet<Type>();

            foreach (var module in _modules)
            {
                if (!visited.Contains(module.Type))
                {
                    TopologicalSort(module, visited, visiting, sorted);
                }
            }

            _modules.Clear();
            _modules.AddRange(sorted);
        }

        private void TopologicalSort(ModuleInfo module, HashSet<Type> visited, HashSet<Type> visiting, List<ModuleInfo> sorted)
        {
            if (visiting.Contains(module.Type))
            {
                return;
            }

            if (visited.Contains(module.Type))
            {
                return;
            }

            visiting.Add(module.Type);

            foreach (var dependencyType in module.Dependencies)
            {
                var dependencyModule = _modules.FirstOrDefault(m => m.Type == dependencyType);
                if (dependencyModule != null)
                {
                    TopologicalSort(dependencyModule, visited, visiting, sorted);
                }
            }

            visiting.Remove(module.Type);
            visited.Add(module.Type);
            sorted.Add(module);
        }

        private Dictionary<Type, List<Type>> BuildDependencyGraph()
        {
            var graph = new Dictionary<Type, List<Type>>();

            foreach (var module in _modules)
            {
                graph[module.Type] = new List<Type>(module.Dependencies);
            }

            return graph;
        }

        private bool HasCircularDependency(Dictionary<Type, List<Type>> graph, out List<string> cycle)
        {
            cycle = null;
            var visited = new HashSet<Type>();
            var recursionStack = new HashSet<Type>();
            var path = new List<Type>();

            foreach (var node in graph.Keys)
            {
                if (HasCircularDependencyRecursive(node, graph, visited, recursionStack, path))
                {
                    cycle = path.Select(t => t.Name).ToList();
                    return true;
                }
            }

            return false;
        }

        private bool HasCircularDependencyRecursive(
            Type node,
            Dictionary<Type, List<Type>> graph,
            HashSet<Type> visited,
            HashSet<Type> recursionStack,
            List<Type> path)
        {
            if (recursionStack.Contains(node))
            {
                path.Add(node);
                return true;
            }

            if (visited.Contains(node))
            {
                return false;
            }

            visited.Add(node);
            recursionStack.Add(node);
            path.Add(node);

            if (graph.TryGetValue(node, out var dependencies))
            {
                foreach (var dependency in dependencies)
                {
                    if (HasCircularDependencyRecursive(dependency, graph, visited, recursionStack, path))
                    {
                        return true;
                    }
                }
            }

            path.Remove(node);
            recursionStack.Remove(node);
            return false;
        }
    }

    /// <summary>
    /// Contains information about a registered module.
    /// </summary>
    /// <remarks>DEPRECATED: Part of the legacy module system. This class will be removed in v2.0.</remarks>
    [Obsolete("Part of the legacy module system. This class will be removed in v2.0.")]
    public class ModuleInfo
    {
        /// <summary>
        /// The module installer instance.
        /// </summary>
        public IModuleInstaller Installer { get; set; }

        /// <summary>
        /// The type of the module installer.
        /// </summary>
        public Type Type { get; set; }

        /// <summary>
        /// The name of the module.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The initialization priority. Lower values initialize first.
        /// </summary>
        public int Priority { get; set; }

        /// <summary>
        /// The list of module types this module depends on.
        /// </summary>
        public List<Type> Dependencies { get; set; } = new List<Type>();
    }

    /// <summary>
    /// Attribute to specify module initialization priority.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ModulePriorityAttribute : Attribute
    {
        /// <summary>
        /// The priority value. Lower values initialize first.
        /// </summary>
        public int Priority { get; }

        /// <summary>
        /// Creates a new module priority attribute.
        /// </summary>
        /// <param name="priority">The priority value. Lower values initialize first.</param>
        public ModulePriorityAttribute(int priority)
        {
            Priority = priority;
        }
    }

    /// <summary>
    /// Attribute to specify module dependencies.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ModuleDependsOnAttribute : Attribute
    {
        /// <summary>
        /// The types of modules that this module depends on.
        /// </summary>
        public Type[] Dependencies { get; }

        /// <summary>
        /// Creates a new module dependency attribute.
        /// </summary>
        /// <param name="dependencies">The module types this module depends on.</param>
        public ModuleDependsOnAttribute(params Type[] dependencies)
        {
            Dependencies = dependencies ?? Array.Empty<Type>();
        }
    }
}
