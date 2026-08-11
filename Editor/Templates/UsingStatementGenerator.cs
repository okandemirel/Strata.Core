using System;
using System.Collections.Generic;
using System.Linq;

namespace Strada.Core.Editor.Templates
{
    /// <summary>
    /// Generates required using statements for Strada templates.
    /// Analyzes template dependencies and adds appropriate using statements.
    /// </summary>
    public static class UsingStatementGenerator
    {
        /// <summary>
        /// Required using statements for each template type.
        /// </summary>
        private static readonly Dictionary<TemplateContextDetector.TemplateType, string[]> TemplateUsings =
            new Dictionary<TemplateContextDetector.TemplateType, string[]>
        {
            {
                TemplateContextDetector.TemplateType.Controller,
                new[]
                {
                    "System",
                    "Strada.Core.DI.Attributes",
                    "Strada.Core.Patterns",
                    "Strada.Core.Patterns.Interfaces"
                }
            },
            {
                TemplateContextDetector.TemplateType.Service,
                new[]
                {
                    "System",
                    "Strada.Core.DI.Attributes",
                    "Strada.Core.Patterns",
                    "Strada.Core.Patterns.Interfaces"
                }
            },
            {
                TemplateContextDetector.TemplateType.System,
                new[]
                {
                    "System",
                    "Strada.Core.ECS",
                    "Strada.Core.ECS.Systems"
                }
            },
            {
                TemplateContextDetector.TemplateType.Component,
                new[]
                {
                    "System",
                    "System.Runtime.InteropServices",
                    "Strada.Core.ECS"
                }
            },
            {
                TemplateContextDetector.TemplateType.View,
                new[]
                {
                    "System",
                    "UnityEngine",
                    "Strada.Core.Patterns",
                    "Strada.Core.Patterns.Interfaces"
                }
            },
            {
                TemplateContextDetector.TemplateType.Model,
                new[]
                {
                    "System",
                    "Strada.Core.Patterns.Interfaces"
                }
            },
            {
                TemplateContextDetector.TemplateType.Config,
                new[]
                {
                    "System",
                    "UnityEngine",
                    "Strada.Core.Data"
                }
            },
            {
                TemplateContextDetector.TemplateType.Interface,
                new[]
                {
                    "System"
                }
            },
            {
                TemplateContextDetector.TemplateType.Command,
                new[]
                {
                    "System",
                    "Strada.Core.Communication"
                }
            },
            {
                TemplateContextDetector.TemplateType.Query,
                new[]
                {
                    "System",
                    "Strada.Core.Communication"
                }
            },
            {
                TemplateContextDetector.TemplateType.Event,
                new[]
                {
                    "System",
                    "Strada.Core.Communication"
                }
            },
            {
                TemplateContextDetector.TemplateType.ModuleInstaller,
                new[]
                {
                    "System",
                    "Strada.Core.DI",
                    "Strada.Core.Modules"
                }
            }
        };

        /// <summary>
        /// Common using statements that may be needed based on code patterns.
        /// </summary>
        private static readonly Dictionary<string, string[]> PatternUsings =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "List", new[] { "System.Collections.Generic" } },
            { "Dictionary", new[] { "System.Collections.Generic" } },
            { "HashSet", new[] { "System.Collections.Generic" } },
            { "Queue", new[] { "System.Collections.Generic" } },
            { "Stack", new[] { "System.Collections.Generic" } },
            { "IEnumerable", new[] { "System.Collections.Generic" } },
            { "IReadOnlyList", new[] { "System.Collections.Generic" } },
            { "Task", new[] { "System.Threading.Tasks" } },
            { "CancellationToken", new[] { "System.Threading" } },
            { "Regex", new[] { "System.Text.RegularExpressions" } },
            { "StringBuilder", new[] { "System.Text" } },
            { "Debug.Log", new[] { "UnityEngine" } },
            { "MonoBehaviour", new[] { "UnityEngine" } },
            { "ScriptableObject", new[] { "UnityEngine" } },
            { "GameObject", new[] { "UnityEngine" } },
            { "Transform", new[] { "UnityEngine" } },
            { "Vector2", new[] { "UnityEngine" } },
            { "Vector3", new[] { "UnityEngine" } },
            { "Quaternion", new[] { "UnityEngine" } },
            { "Color", new[] { "UnityEngine" } },
            { "Mathf", new[] { "UnityEngine" } },
            { "[Inject]", new[] { "Strada.Core.DI.Attributes" } },
            { "IContainer", new[] { "Strada.Core.DI" } },
            { "ReactiveProperty", new[] { "Strada.Core.Reactive" } },
            { "ReactiveCollection", new[] { "Strada.Core.Reactive" } },
            { "MessageBus", new[] { "Strada.Core.Communication" } },
            { "EntityManager", new[] { "Strada.Core.ECS" } },
            { "World", new[] { "Strada.Core.ECS.World" } },
            { "IComponent", new[] { "Strada.Core.ECS" } },
            { "ISystem", new[] { "Strada.Core.ECS" } },
            { "SystemBase", new[] { "Strada.Core.ECS.Systems" } },
            { "EntityMediator", new[] { "Strada.Core.Sync" } },
            { "ComponentBinding", new[] { "Strada.Core.Sync" } },
            { "Linq", new[] { "System.Linq" } },
            { ".Select(", new[] { "System.Linq" } },
            { ".Where(", new[] { "System.Linq" } },
            { ".FirstOrDefault(", new[] { "System.Linq" } },
            { ".ToList(", new[] { "System.Linq" } },
            { ".ToArray(", new[] { "System.Linq" } },
        };

        /// <summary>
        /// Gets the required using statements for a template type.
        /// </summary>
        /// <param name="templateType">The template type.</param>
        /// <returns>Array of required using statements.</returns>
        public static string[] GetUsingsForTemplate(TemplateContextDetector.TemplateType templateType)
        {
            if (TemplateUsings.TryGetValue(templateType, out var usings))
                return usings;

            return new[] { "System" };
        }

        /// <summary>
        /// Analyzes code content and returns additional required using statements.
        /// </summary>
        /// <param name="codeContent">The code content to analyze.</param>
        /// <returns>Array of additional using statements needed.</returns>
        public static string[] AnalyzeCodeForUsings(string codeContent)
        {
            if (string.IsNullOrEmpty(codeContent))
                return Array.Empty<string>();

            var requiredUsings = new HashSet<string>();

            foreach (var pattern in PatternUsings)
            {
                if (codeContent.Contains(pattern.Key))
                {
                    foreach (var usingStatement in pattern.Value)
                    {
                        requiredUsings.Add(usingStatement);
                    }
                }
            }

            return requiredUsings.ToArray();
        }

        /// <summary>
        /// Generates the complete using statements block for a template.
        /// </summary>
        /// <param name="templateType">The template type.</param>
        /// <param name="additionalUsings">Additional using statements to include.</param>
        /// <returns>Formatted using statements block.</returns>
        public static string GenerateUsingBlock(
            TemplateContextDetector.TemplateType templateType,
            params string[] additionalUsings)
        {
            var allUsings = new HashSet<string>(GetUsingsForTemplate(templateType));

            if (additionalUsings != null)
            {
                foreach (var u in additionalUsings)
                {
                    if (!string.IsNullOrEmpty(u))
                        allUsings.Add(u);
                }
            }

            return FormatUsingBlock(allUsings);
        }

        /// <summary>
        /// Generates the complete using statements block with code analysis.
        /// </summary>
        /// <param name="templateType">The template type.</param>
        /// <param name="codeContent">The code content to analyze for additional usings.</param>
        /// <returns>Formatted using statements block.</returns>
        public static string GenerateUsingBlockWithAnalysis(
            TemplateContextDetector.TemplateType templateType,
            string codeContent)
        {
            return GenerateUsingBlock(templateType, AnalyzeCodeForUsings(codeContent));
        }

        /// <summary>
        /// Formats a set of using statements into a properly ordered block.
        /// </summary>
        /// <param name="usings">The using statements to format.</param>
        /// <returns>Formatted using statements block.</returns>
        public static string FormatUsingBlock(IEnumerable<string> usings)
        {
            if (usings == null)
                return string.Empty;

            var usingList = usings.Where(u => !string.IsNullOrEmpty(u)).ToList();

            // Group by prefix: System, Unity, Strada, then everything else
            var groups = new[]
            {
                usingList.Where(u => u.StartsWith("System")).OrderBy(u => u),
                usingList.Where(u => u.StartsWith("Unity")).OrderBy(u => u),
                usingList.Where(u => u.StartsWith("Strada")).OrderBy(u => u),
                usingList.Where(u => !u.StartsWith("System") && !u.StartsWith("Unity") && !u.StartsWith("Strada")).OrderBy(u => u),
            };

            var result = new List<string>();

            foreach (var group in groups)
            {
                var items = group.Select(u => $"using {u};").ToList();
                if (items.Count == 0)
                    continue;
                if (result.Count > 0)
                    result.Add("");
                result.AddRange(items);
            }

            return string.Join("\n", result);
        }

        /// <summary>
        /// Merges existing using statements with new ones, avoiding duplicates.
        /// </summary>
        /// <param name="existingUsings">Existing using statements.</param>
        /// <param name="newUsings">New using statements to add.</param>
        /// <returns>Merged and formatted using block.</returns>
        public static string MergeUsings(string existingUsings, IEnumerable<string> newUsings)
        {
            var allUsings = new HashSet<string>();

            if (!string.IsNullOrEmpty(existingUsings))
            {
                var lines = existingUsings.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
                    {
                        var ns = trimmed.Substring(6, trimmed.Length - 7).Trim();
                        allUsings.Add(ns);
                    }
                }
            }

            if (newUsings != null)
            {
                foreach (var u in newUsings)
                {
                    if (!string.IsNullOrEmpty(u))
                        allUsings.Add(u);
                }
            }

            return FormatUsingBlock(allUsings);
        }
    }
}
