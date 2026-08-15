using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Strada.Core.Editor.Templates
{
    /// <summary>
    /// Detects folder context to offer appropriate Strada templates.
    /// Analyzes folder names and paths to determine which templates are relevant.
    /// </summary>
    public static class TemplateContextDetector
    {
        /// <summary>
        /// Normalizes a path and splits it into parts.
        /// </summary>
        private static string[] NormalizePath(string folderPath)
        {
            return folderPath.Replace('\\', '/').Split('/');
        }

        /// <summary>
        /// Represents a template that can be offered based on context.
        /// </summary>
        public class TemplateInfo
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public TemplateType Type { get; set; }
            public int Priority { get; set; }
        }

        /// <summary>
        /// Types of templates available in Strada.
        /// </summary>
        public enum TemplateType
        {
            Controller,
            Service,
            System,
            Component,
            View,
            Model,
            Config,
            Interface,
            Command,
            Query,
            Event,
            ModuleInstaller
        }

        /// <summary>
        /// Folder context patterns and their associated templates.
        /// </summary>
        private static readonly Dictionary<string, TemplateType[]> FolderContextMap = 
            new Dictionary<string, TemplateType[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Controllers", new[] { TemplateType.Controller } },
            { "Services", new[] { TemplateType.Service } },
            { "Systems", new[] { TemplateType.System } },
            { "Components", new[] { TemplateType.Component } },
            { "Views", new[] { TemplateType.View } },
            { "Models", new[] { TemplateType.Model } },
            { "Data", new[] { TemplateType.Config, TemplateType.Model } },
            { "Interfaces", new[] { TemplateType.Interface } },
            { "Commands", new[] { TemplateType.Command } },
            { "Queries", new[] { TemplateType.Query } },
            { "Events", new[] { TemplateType.Event } },
            { "Config", new[] { TemplateType.Config } },
            { "Configs", new[] { TemplateType.Config } },
            { "Configuration", new[] { TemplateType.Config } },
            { "Scripts", new[] { TemplateType.Controller, TemplateType.Service, TemplateType.System } },
            { "ECS", new[] { TemplateType.System, TemplateType.Component } },
            { "MVCS", new[] { TemplateType.Controller, TemplateType.Service, TemplateType.View, TemplateType.Model } },
        };

        /// <summary>
        /// Template descriptions for each type.
        /// </summary>
        private static readonly Dictionary<TemplateType, string> TemplateDescriptions = 
            new Dictionary<TemplateType, string>
        {
            { TemplateType.Controller, "Controller - Handles input and coordinates Views and Services" },
            { TemplateType.Service, "Service - Contains business logic, shared across modules" },
            { TemplateType.System, "System - ECS system that processes entities each frame" },
            { TemplateType.Component, "Component - ECS component data (unmanaged struct)" },
            { TemplateType.View, "View - MonoBehaviour for UI/visual representation" },
            { TemplateType.Model, "Model - Data container for application state" },
            { TemplateType.Config, "Config - ScriptableObject configuration (CD_ prefix)" },
            { TemplateType.Interface, "Interface - Contract definition" },
            { TemplateType.Command, "Command - MessageBus command message" },
            { TemplateType.Query, "Query - MessageBus query message" },
            { TemplateType.Event, "Event - MessageBus event message" },
            { TemplateType.ModuleInstaller, "Module Installer - Module registration and setup" },
        };

        /// <summary>
        /// Detects the context from a folder path and returns appropriate templates.
        /// </summary>
        /// <param name="folderPath">The folder path to analyze.</param>
        /// <returns>List of templates appropriate for the folder context.</returns>
        public static List<TemplateInfo> DetectContext(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return GetAllTemplates();

            var detectedTypes = new HashSet<TemplateType>();

            var pathParts = NormalizePath(folderPath);
            foreach (var part in pathParts)
            {
                if (FolderContextMap.TryGetValue(part, out var types))
                    detectedTypes.UnionWith(types);
            }

            var isInModule = pathParts.Any(p =>
                p.EndsWith("Module", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("Modules", StringComparison.OrdinalIgnoreCase));

            if (detectedTypes.Count == 0)
            {
                if (isInModule)
                {
                    detectedTypes.Add(TemplateType.Controller);
                    detectedTypes.Add(TemplateType.Service);
                    detectedTypes.Add(TemplateType.System);
                    detectedTypes.Add(TemplateType.Component);
                    detectedTypes.Add(TemplateType.View);
                }
                else
                {
                    return GetAllTemplates();
                }
            }

            return ToTemplateInfoList(detectedTypes.OrderBy(t => (int)t));
        }

        /// <summary>
        /// Gets all available templates.
        /// </summary>
        /// <returns>List of all template types.</returns>
        public static List<TemplateInfo> GetAllTemplates()
        {
            return ToTemplateInfoList((TemplateType[])Enum.GetValues(typeof(TemplateType)));
        }

        /// <summary>
        /// Converts an ordered sequence of template types into TemplateInfo objects.
        /// </summary>
        private static List<TemplateInfo> ToTemplateInfoList(IEnumerable<TemplateType> types)
        {
            var templates = new List<TemplateInfo>();
            int priority = 0;

            foreach (var type in types)
            {
                templates.Add(new TemplateInfo
                {
                    Name = type.ToString(),
                    Description = TemplateDescriptions.TryGetValue(type, out var desc) ? desc : type.ToString(),
                    Type = type,
                    Priority = priority++
                });
            }

            return templates;
        }

        /// <summary>
        /// Gets the current selection's folder path in the Project window.
        /// </summary>
        /// <returns>The selected folder path, or null if no folder is selected.</returns>
        public static string GetSelectedFolderPath()
        {
            var selectedPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            
            if (string.IsNullOrEmpty(selectedPath))
                return null;

            if (File.Exists(selectedPath))
                return Path.GetDirectoryName(selectedPath);

            if (Directory.Exists(selectedPath))
                return selectedPath;

            return null;
        }

        /// <summary>
        /// Extracts the module name from a folder path.
        /// </summary>
        /// <param name="folderPath">The folder path to analyze.</param>
        /// <returns>The module name if found, null otherwise.</returns>
        public static string ExtractModuleName(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return null;

            var pathParts = NormalizePath(folderPath);

            foreach (var part in pathParts)
            {
                if (part.EndsWith("Module", StringComparison.OrdinalIgnoreCase))
                {
                    return part.Replace("Module", "");
                }
            }

            for (int i = 0; i < pathParts.Length - 1; i++)
            {
                if (pathParts[i].Equals("Modules", StringComparison.OrdinalIgnoreCase))
                {
                    var moduleName = pathParts[i + 1];
                    if (moduleName.EndsWith("Module", StringComparison.OrdinalIgnoreCase))
                        return moduleName.Replace("Module", "");
                    return moduleName;
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts the namespace from a folder path.
        /// </summary>
        /// <param name="folderPath">The folder path to analyze.</param>
        /// <returns>The suggested namespace based on folder structure.</returns>
        public static string ExtractNamespace(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return "MyNamespace";

            folderPath = string.Join("/", NormalizePath(folderPath));

            var prefixes = new[] { "Assets/", "Packages/", "Scripts/" };
            foreach (var prefix in prefixes)
            {
                if (folderPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    folderPath = folderPath.Substring(prefix.Length);
                    break;
                }
            }

            var parts = folderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var namespaceParts = new List<string>();

            foreach (var part in parts)
            {
                if (part.Equals("Scripts", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("Runtime", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("Editor", StringComparison.OrdinalIgnoreCase))
                    continue;

                var sanitized = SanitizeForNamespace(part);
                if (!string.IsNullOrEmpty(sanitized))
                    namespaceParts.Add(sanitized);
            }

            return namespaceParts.Count > 0 
                ? string.Join(".", namespaceParts) 
                : "MyNamespace";
        }

        /// <summary>
        /// Sanitizes a string to be valid for use in a namespace.
        /// </summary>
        private static string SanitizeForNamespace(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var result = new System.Text.StringBuilder();
            bool capitalizeNext = true;

            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c))
                {
                    result.Append(capitalizeNext ? char.ToUpper(c) : c);
                    capitalizeNext = false;
                }
                else if (c == '_' || c == '-' || c == ' ')
                {
                    capitalizeNext = true;
                }
            }

            var str = result.ToString();

            if (str.Length > 0 && char.IsDigit(str[0]))
                str = "_" + str;

            return str;
        }

        /// <summary>
        /// Checks if a folder path is within a Strada module.
        /// </summary>
        /// <param name="folderPath">The folder path to check.</param>
        /// <returns>True if the path is within a module.</returns>
        public static bool IsInModule(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return false;

            var pathParts = NormalizePath(folderPath);
            return pathParts.Any(p =>
                p.EndsWith("Module", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("Modules", StringComparison.OrdinalIgnoreCase));
        }
    }
}
