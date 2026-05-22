using System.Collections.Generic;
using System.IO;
using System.Linq;
using Strada.Core.Editor.ModuleGenerator.Models;
using UnityEditor;

namespace Strada.Core.Editor.ModuleGenerator.Pipeline.Steps
{
    /// <summary>
    /// Creates assembly definition files.
    /// </summary>
    public class AssemblyDefStep : IGenerationStep
    {
        public string Name => "Assembly Definition";
        public int Order => 20;

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
            var refsJson = string.Join(",\n        ", references.Select(r => $"\"{r}\""));
            var platformsJson = FormatJsonArray(includePlatforms);
            var precompiledJson = FormatJsonArray(precompiledReferences);
            var constraintsJson = FormatJsonArray(defineConstraints);

            var content = $@"{{
    ""name"": ""{asmName}"",
    ""rootNamespace"": ""{rootNamespace}"",
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

            File.WriteAllText(path, content);
            context.AddCreatedFile(path);
        }

        private static string FormatJsonArray(string[] items)
        {
            if (items == null || items.Length == 0) return "";
            return "\n        " + string.Join(",\n        ", items.Select(i => $"\"{i}\"")) + "\n    ";
        }
    }
}
