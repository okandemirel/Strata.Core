using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Strada.Core.Editor.ModuleGenerator.Models;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.ModuleGenerator.Pipeline.Steps
{
    /// <summary>
    /// Creates assembly definition files.
    /// </summary>
    public class AssemblyDefStep : IGenerationStep
    {
        public string Name => "Assembly Definition";
        public int Order => 20;

        // Same shape FileGenerationStep enforces, but this step runs first (Order 20 vs 30), so
        // without its own check the namespace reaches the .asmdef before anything validates it.
        private static readonly Regex ValidNamespaceRegex =
            new Regex(@"^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*$", RegexOptions.Compiled);

        public bool CanExecute(GenerationContext context)
        {
            return context.Definition.Components.AssemblyDefinition &&
                   context.Definition.ModuleType == ModuleType.Main;
        }

        public StepResult Execute(GenerationContext context)
        {
            var basePath = context.Definition.FullPath;
            var name = context.Definition.ModuleName;
            var ns = context.Definition.FullNamespace;

            if (string.IsNullOrEmpty(ns) || !ValidNamespaceRegex.IsMatch(ns))
                return StepResult.Error($"Invalid namespace '{ns}': must contain only valid C# identifier characters");

            var references = new List<string> { "Strada.Core" };

            foreach (var dep in context.Definition.Dependencies)
            {
                var depAssembly = ModuleDiscovery.FindAssemblyForModule(dep);
                if (!string.IsNullOrEmpty(depAssembly) && !references.Contains(depAssembly))
                {
                    references.Add(depAssembly);
                }
            }

            WriteAsmdef($"{basePath}/{name}.asmdef", ns, ns, references, context,
                autoReferenced: true, overrideReferences: false);
            context.AssemblyDefPath = $"{basePath}/{name}.asmdef";

            if (context.Definition.Components.EditorScripts)
            {
                WriteAsmdef($"{basePath}/Editor/{name}.Editor.asmdef",
                    $"{ns}.Editor", $"{ns}.Editor",
                    new List<string> { ns, "Strada.Core", "Strada.Core.Editor" }, context,
                    includePlatforms: new[] { "Editor" }, autoReferenced: true, overrideReferences: false);
            }

            if (context.Definition.Components.RuntimeTests || context.Definition.Components.EditorTests)
            {
                WriteAsmdef($"{basePath}/Tests/{name}.Tests.asmdef",
                    $"{ns}.Tests", $"{ns}.Tests",
                    new List<string> { ns, "Strada.Core", "UnityEngine.TestRunner", "UnityEditor.TestRunner" }, context,
                    includePlatforms: new[] { "Editor" }, autoReferenced: false, overrideReferences: true,
                    precompiledReferences: new[] { "nunit.framework.dll" },
                    defineConstraints: new[] { "UNITY_INCLUDE_TESTS" });
            }

            context.RequiresRecompilation = true;

            return StepResult.Ok($"Created assembly definitions");
        }

        public void Rollback(GenerationContext context)
        {
            foreach (var file in context.CreatedFiles)
            {
                if (file.EndsWith(".asmdef") && File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            AssetDatabase.Refresh();
        }

        // FRAMEWORK DESIGN: The generated .asmdef sets "allowUnsafeCode": true.
        // Strada's ECS subsystem (SparseSet, EntityCommandBuffer, ParallelComponentJob)
        // uses unsafe pointer access for Burst-compatible hot paths. Modules created from
        // this generator are expected to interoperate with that ECS, so they share the
        // same unsafe-code requirement. Modules that don't need unsafe code can edit
        // the generated .asmdef to flip the flag back to false; the default favours the
        // common case.
        private void WriteAsmdef(string path, string asmName, string rootNamespace,
            List<string> references, GenerationContext context,
            string[] includePlatforms = null,
            bool autoReferenced = true,
            bool overrideReferences = false,
            string[] precompiledReferences = null,
            string[] defineConstraints = null)
        {
            var refsJson = string.Join(",\n        ", references.Select(r => $"\"{EscapeJson(r)}\""));
            var platformsJson = FormatJsonArray(includePlatforms);
            var precompiledJson = FormatJsonArray(precompiledReferences);
            var constraintsJson = FormatJsonArray(defineConstraints);

            var content = $@"{{
    ""name"": ""{EscapeJson(asmName)}"",
    ""rootNamespace"": ""{EscapeJson(rootNamespace)}"",
    ""references"": [
        {refsJson}
    ],
    ""includePlatforms"": [{platformsJson}],
    ""excludePlatforms"": [],
    ""allowUnsafeCode"": true,
    ""overrideReferences"": {overrideReferences.ToString().ToLower()},
    ""precompiledReferences"": [{precompiledJson}],
    ""autoReferenced"": {autoReferenced.ToString().ToLower()},
    ""defineConstraints"": [{constraintsJson}],
    ""versionDefines"": [],
    ""noEngineReferences"": false
}}";

            // This step runs before FileGenerationStep, so its containment guard has not run yet
            // and an unvalidated TargetPath would otherwise write an .asmdef anywhere on disk.
            var fullPath = Path.GetFullPath(path);
            if (!IsInsideAssetsFolder(fullPath))
                throw new InvalidOperationException($"Path outside project: {fullPath}");

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content);
            context.AddCreatedFile(fullPath);
        }

        private static bool IsInsideAssetsFolder(string fullPath)
        {
            string assetsRoot;
            try
            {
                assetsRoot = Path.GetFullPath(Application.dataPath);
            }
            catch (Exception)
            {
                return false;
            }

            assetsRoot = assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

            // Ordinal plus a trailing separator: without the separator "AssetsEvil/" would
            // prefix-match the Assets root, and containment must not be culture sensitive.
            return fullPath.StartsWith(assetsRoot, StringComparison.Ordinal);
        }

        /// <summary>
        /// Escapes a value for embedding in a JSON string literal, so a name containing a quote
        /// or backslash cannot break out and inject sibling keys into the .asmdef.
        /// </summary>
        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        private static string FormatJsonArray(string[] items)
        {
            if (items == null || items.Length == 0) return "";
            return "\n        " + string.Join(",\n        ", items.Select(i => $"\"{EscapeJson(i)}\"")) + "\n    ";
        }
    }
}
