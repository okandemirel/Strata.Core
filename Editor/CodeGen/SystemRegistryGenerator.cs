using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Strada.Core.ECS;
using UnityEditor;
using UnityEngine;

using Strada.Core.ECS.Systems;

namespace Strada.Core.Editor.CodeGen
{
    public static class SystemRegistryGenerator
    {
        private const string GeneratedFile = "GeneratedSystemRegistry.cs";

        [MenuItem("Strada/Generate System Registry")]
        public static void GenerateSystemRegistry()
        {
            var systems = FindAllSystems();
            if (systems.Count == 0)
            {
                Debug.Log("[Strada] No ISystem implementations found.");
                return;
            }

            var code = GenerateRegistryCode(systems);

            StradaCodeGenerator.EnsureGeneratedFolder();

            var path = Path.Combine(StradaCodeGenerator.GeneratedFolder, GeneratedFile);
            File.WriteAllText(path, code);
            AssetDatabase.Refresh();

            Debug.Log($"[Strada] Generated system registry with {systems.Count} systems at {path}");
        }

        private static List<SystemInfo> FindAllSystems()
        {
            var result = new List<SystemInfo>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || assembly.FullName.StartsWith("Unity") || assembly.FullName.StartsWith("System"))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsAbstract || type.IsInterface)
                            continue;

                        if (!typeof(ISystem).IsAssignableFrom(type))
                            continue;

                        if (!IsEmittable(type))
                            continue;

                        var attr = type.GetCustomAttribute<SystemOrderAttribute>();
                        int order = attr?.Order ?? 0;
                        result.Add(new SystemInfo(type, order));
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (var type in ex.Types)
                    {
                        if (type == null)
                            continue;

                        if (type.IsAbstract || type.IsInterface)
                            continue;

                        if (!typeof(ISystem).IsAssignableFrom(type))
                            continue;

                        if (!IsEmittable(type))
                            continue;

                        var attr = type.GetCustomAttribute<SystemOrderAttribute>();
                        int order = attr?.Order ?? 0;
                        result.Add(new SystemInfo(type, order));
                    }

                    if (StradaCodeGenSettings.VerboseLogging)
                    {
                        string firstExceptionMessage = null;
                        foreach (var loaderEx in ex.LoaderExceptions)
                        {
                            if (loaderEx != null)
                            {
                                firstExceptionMessage = loaderEx.Message;
                                break;
                            }
                        }
                        Debug.LogWarning($"[Strada] Partial type load from assembly '{assembly.GetName().Name}': {firstExceptionMessage}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Strada] Failed to scan assembly '{assembly.GetName().Name}': {ex.Message}");
                }
            }

            result.Sort((a, b) => a.Order.CompareTo(b.Order));
            return result;
        }

        /// <summary>
        /// Returns true if the type can legally appear as a <c>typeof(...)</c> / generic argument
        /// in the generated registry.
        /// </summary>
        /// <remarks>
        /// The registry is written to Assets/Strada.Generated and compiled into the predefined
        /// Assembly-CSharp, so a type that is internal, private or nested inside a non-public type
        /// is simply not nameable from there. Emitting it produced three compile errors per type
        /// (the typeof, the Register and the Resolve) and broke the whole project build. Open
        /// generic definitions cannot be emitted either, since Resolve&lt;Foo&lt;&gt;&gt;() is not
        /// valid C#.
        /// </remarks>
        private static bool IsEmittable(Type type)
        {
            if (type.ContainsGenericParameters)
            {
                if (StradaCodeGenSettings.VerboseLogging)
                    Debug.LogWarning($"[Strada] Skipping open generic system '{type.FullName}' - cannot be emitted into the registry.");
                return false;
            }

            // IsVisible is true only when the type and every enclosing type are public.
            if (!type.IsVisible)
            {
                if (StradaCodeGenSettings.VerboseLogging)
                    Debug.LogWarning($"[Strada] Skipping system '{type.FullName}' - it is not publicly visible from Assembly-CSharp.");
                return false;
            }

            return true;
        }

        private static string GenerateRegistryCode(List<SystemInfo> systems)
        {
            // Defence in depth: the type name is interpolated straight into compiled C#, so a
            // name that is not a plain (possibly generic) identifier path is dropped rather than
            // emitted three times over as a typeof, a Register and a Resolve.
            var typeNames = new List<string>(systems.Count);
            foreach (var s in systems)
            {
                var typeName = StradaCodeGenerator.GetFullTypeName(s.Type);
                if (!IsValidTypeName(typeName))
                {
                    Debug.LogWarning($"[Strada] Skipping system with unrepresentable type name '{typeName}'.");
                    continue;
                }

                typeNames.Add(typeName);
            }

            var sb = new StringBuilder();

            sb.AppendLine("// Auto-generated by Strada System Registry Generator");
            sb.AppendLine("// Do not modify this file manually");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Strada.Core.ECS;");
            sb.AppendLine("using Strada.Core.DI;");
            sb.AppendLine();
            sb.AppendLine("namespace Strada.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public static class GeneratedSystemRegistry");
            sb.AppendLine("    {");
            sb.AppendLine("        public static readonly Type[] SystemTypes = new Type[]");
            sb.AppendLine("        {");

            foreach (var typeName in typeNames)
            {
                sb.AppendLine($"            typeof({typeName}),");
            }

            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        public static void RegisterAll(IContainerBuilder builder)");
            sb.AppendLine("        {");

            foreach (var typeName in typeNames)
            {
                sb.AppendLine($"            builder.Register<{typeName}>(Lifetime.Singleton);");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public static List<ISystem> CreateAll(IContainer container)");
            sb.AppendLine("        {");
            sb.AppendLine("            var systems = new List<ISystem>(SystemTypes.Length);");

            foreach (var typeName in typeNames)
            {
                sb.AppendLine($"            systems.Add(container.Resolve<{typeName}>());");
            }

            sb.AppendLine("            return systems;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static readonly Regex ValidTypeNameRegex = new Regex(@"^[\w.<>,\s]+$", RegexOptions.Compiled);

        private static bool IsValidTypeName(string typeName)
        {
            return !string.IsNullOrEmpty(typeName) && ValidTypeNameRegex.IsMatch(typeName);
        }

        private struct SystemInfo
        {
            public Type Type;
            public int Order;

            public SystemInfo(Type type, int order)
            {
                Type = type;
                Order = order;
            }
        }
    }

}
