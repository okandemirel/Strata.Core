using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Strada.Core.Editor.ModuleGenerator.Models;
using Strada.Core.Modules;
using UnityEditor;

namespace Strada.Core.Editor.ModuleGenerator
{
    public static class ModuleDiscovery
    {
        private static readonly string[] ScreenPatterns = { "Screen", "View", "UI", "Panel", "Dialog", "Popup" };
        private static readonly string[] TestPatterns = { "Test", "Tests", "Spec" };

        public static List<ModuleInfoData> DiscoverModules()
        {
            var allModules = new Dictionary<string, ModuleInfoData>();

            DiscoverFromFolderStructureRecursive(allModules, "Assets/Modules", null, 0);
            EnrichFromInstallers(allModules);
            EnrichFromModuleConfigs(allModules);

            var rootModules = new List<ModuleInfoData>();
            foreach (var module in allModules.Values)
            {
                if (module.Parent == null)
                {
                    rootModules.Add(module);
                }
            }
            rootModules.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            return rootModules;
        }

        public static List<ModuleInfoData> GetFlatList()
        {
            var result = new List<ModuleInfoData>();
            var rootModules = DiscoverModules();

            foreach (var module in rootModules)
            {
                AddToFlatList(result, module);
            }

            return result;
        }

        private static void AddToFlatList(List<ModuleInfoData> list, ModuleInfoData module)
        {
            list.Add(module);
            var sortedChildren = new List<ModuleInfoData>(module.SubModules);
            sortedChildren.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            foreach (var child in sortedChildren)
            {
                AddToFlatList(list, child);
            }
        }

        private static void DiscoverFromFolderStructureRecursive(
            Dictionary<string, ModuleInfoData> allModules,
            string searchPath,
            ModuleInfoData parent,
            int depth)
        {
            if (!Directory.Exists(searchPath))
                return;

            var directories = Directory.GetDirectories(searchPath);

            foreach (var dir in directories)
            {
                var folderName = Path.GetFileName(dir);

                if (folderName.StartsWith(".") || folderName == "Editor" || folderName == "Tests" ||
                    folderName == "Scripts" || folderName == "Resources" || folderName == "Prefabs")
                    continue;

                if (IsModuleFolder(folderName))
                {
                    var moduleName = ExtractModuleName(folderName);
                    var moduleType = DetermineModuleType(folderName, parent);

                    var module = new ModuleInfoData
                    {
                        Name = moduleName,
                        Path = dir,
                        Type = moduleType,
                        Parent = parent,
                        Depth = depth,
                        IsExpanded = depth == 0
                    };

                    var key = dir.ToLowerInvariant();
                    allModules[key] = module;

                    if (parent != null)
                    {
                        parent.SubModules.Add(module);
                    }

                    DiscoverFromFolderStructureRecursive(allModules, dir, module, depth + 1);
                }
            }
        }

        private static bool IsModuleFolder(string folderName)
        {
            return folderName.Contains("Module") ||
                   ScreenPatterns.Any(p => folderName.Contains(p) && !folderName.StartsWith("-"));
        }

        private static string ExtractModuleName(string folderName)
        {
            return folderName.EndsWith("Module")
                ? folderName.Substring(0, folderName.Length - "Module".Length)
                : folderName;
        }

        private static ModuleType DetermineModuleType(string folderName, ModuleInfoData parent)
        {
            for (int i = 0; i < TestPatterns.Length; i++)
            {
                if (folderName.Contains(TestPatterns[i]))
                    return ModuleType.Test;
            }

            for (int i = 0; i < ScreenPatterns.Length; i++)
            {
                if (folderName.Contains(ScreenPatterns[i]))
                    return ModuleType.Screen;
            }

            if (parent != null)
                return ModuleType.Sub;

            return ModuleType.Main;
        }

        private static void EnrichFromInstallers(Dictionary<string, ModuleInfoData> allModules)
        {
            var moduleConfigType = typeof(ModuleConfig);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract || !moduleConfigType.IsAssignableFrom(type))
                            continue;

                        var name = type.Name.Replace("ModuleConfig", "").Replace("Module", "");

                        ModuleInfoData module = null;
                        foreach (var m in allModules.Values)
                        {
                            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                module = m;
                                break;
                            }
                        }

                        if (module != null)
                        {
                            module.HasInstaller = true;
                            module.Namespace = type.Namespace;
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (var type in ex.Types)
                    {
                        if (type == null || !type.IsClass || type.IsAbstract || !moduleConfigType.IsAssignableFrom(type))
                            continue;

                        var name = type.Name.Replace("ModuleConfig", "").Replace("Module", "");

                        ModuleInfoData module = null;
                        foreach (var m in allModules.Values)
                        {
                            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                module = m;
                                break;
                            }
                        }

                        if (module != null)
                        {
                            module.HasInstaller = true;
                            module.Namespace = type.Namespace;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static void EnrichFromModuleConfigs(Dictionary<string, ModuleInfoData> allModules)
        {
            var guids = AssetDatabase.FindAssets("t:ModuleConfig");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var config = AssetDatabase.LoadAssetAtPath<ModuleConfig>(path);

                if (config != null)
                {
                    var name = config.name.Replace("ModuleConfig", "").Replace("Module", "");
                    var modulePath = Path.GetDirectoryName(path);

                    ModuleInfoData module = null;
                    foreach (var m in allModules.Values)
                    {
                        if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
                            (m.Path != null && modulePath != null && m.Path.Contains(modulePath)))
                        {
                            module = m;
                            break;
                        }
                    }

                    if (module != null)
                    {
                        module.HasModuleConfig = true;
                    }
                }
            }
        }

        public static bool ModuleExists(string moduleName)
        {
            var modules = GetFlatList();
            return modules.Any(m =>
                string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        }

        public static string FindAssemblyForModule(string moduleName)
        {
            var moduleConfigType = typeof(ModuleConfig);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    Type matchedType = null;
                    foreach (var t in assembly.GetTypes())
                    {
                        if ((t.Name == moduleName ||
                             t.Name == moduleName + "Module" ||
                             t.Name == moduleName + "ModuleConfig") &&
                            moduleConfigType.IsAssignableFrom(t))
                        {
                            matchedType = t;
                            break;
                        }
                    }

                    if (matchedType != null)
                        return assembly.GetName().Name;
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Type matchedType = null;
                    foreach (var t in ex.Types)
                    {
                        if (t == null)
                            continue;

                        if ((t.Name == moduleName ||
                             t.Name == moduleName + "Module" ||
                             t.Name == moduleName + "ModuleConfig") &&
                            moduleConfigType.IsAssignableFrom(t))
                        {
                            matchedType = t;
                            break;
                        }
                    }

                    if (matchedType != null)
                        return assembly.GetName().Name;
                }
                catch (Exception)
                {
                }
            }

            return null;
        }
    }
}
