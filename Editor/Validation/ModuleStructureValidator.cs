using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Validation
{
    /// <summary>
    /// Validates module folder structure and naming conventions.
    /// Requirements: 14.6
    /// </summary>
    public class ModuleStructureValidator
    {
        private static readonly string[] RequiredFolders =
        {
            "Controllers",
            "Data",
            "Interfaces",
            "Systems"
        };

        private static readonly string[] OptionalFolders =
        {
            "Data/UnityObjects",
            "Data/ValueObjects",
            "Views",
            "Services",
            "Commands",
            "Signals",
            "StateMachines"
        };

        private static readonly Regex PascalCasePattern = new Regex(@"^[A-Z][a-zA-Z0-9]*$", RegexOptions.Compiled);

        [MenuItem("Strada/Validate Module Structure")]
        public static void ValidateAllModules()
        {
            var modulesPath = Path.Combine(Application.dataPath, "Modules");
            if (!Directory.Exists(modulesPath))
            {
                Debug.LogWarning("[Strada] No Modules folder found at Assets/Modules");
                return;
            }

            var modules = Directory.GetDirectories(modulesPath);
            if (modules.Length == 0)
            {
                Debug.Log("[Strada] No modules found in Assets/Modules");
                return;
            }

            var issues = new List<ValidationIssue>();

            foreach (var modulePath in modules)
            {
                issues.AddRange(ValidateModule(modulePath));
            }

            if (issues.Count == 0)
            {
                Debug.Log($"[Strada] All {modules.Length} modules passed validation ✓");
            }
            else
            {
                Debug.LogWarning($"[Strada] Found {issues.Count} issues in module structure:");
                foreach (var issue in issues)
                {
                    switch (issue.Severity)
                    {
                        case ValidationSeverity.Error:
                            Debug.LogError($"  ERROR: {issue.Message}");
                            break;
                        case ValidationSeverity.Warning:
                            Debug.LogWarning($"  WARNING: {issue.Message}");
                            break;
                        default:
                            Debug.Log($"  INFO: {issue.Message}");
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Validates a single module's structure.
        /// </summary>
        public static IEnumerable<ValidationIssue> ValidateModule(string modulePath)
        {
            var issues = new List<ValidationIssue>();
            var moduleName = Path.GetFileName(modulePath);

            if (!moduleName.EndsWith("Module"))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    $"Module folder '{moduleName}' should end with 'Module' (e.g., {moduleName}Module)",
                    $"Rename folder to '{moduleName}Module'")
                    .WithFile(modulePath));
            }

            var baseName = moduleName.Replace("Module", "");
            if (!string.IsNullOrEmpty(baseName) && !IsPascalCase(baseName))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    $"Module name '{moduleName}' should follow PascalCase convention",
                    "Use PascalCase for module names (e.g., PlayerModule, InventoryModule)")
                    .WithFile(modulePath));
            }

            var scriptsPath = Path.Combine(modulePath, "Scripts");
            if (!Directory.Exists(scriptsPath))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    $"Module '{moduleName}' is missing required 'Scripts' folder",
                    "Create a 'Scripts' folder inside the module directory")
                    .WithFile(modulePath));
                return issues;
            }

            foreach (var required in RequiredFolders)
            {
                var folderPath = Path.Combine(scriptsPath, required);
                if (!Directory.Exists(folderPath))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        $"Module '{moduleName}' is missing required folder: Scripts/{required}",
                        $"Create folder: {Path.Combine(scriptsPath, required)}")
                        .WithFile(scriptsPath));
                }
            }

            var assemblyDefPath = Path.Combine(scriptsPath, $"{baseName}.asmdef");
            if (!File.Exists(assemblyDefPath))
            {
                var altPath = Path.Combine(scriptsPath, $"{moduleName}.asmdef");
                if (!File.Exists(altPath))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        $"Module '{moduleName}' is missing assembly definition file",
                        $"Create an assembly definition file at: {assemblyDefPath}")
                        .WithFile(scriptsPath));
                }
            }

            issues.AddRange(ValidateFileNaming(scriptsPath, moduleName));

            return issues;
        }

        /// <summary>
        /// Validates file naming conventions within a module.
        /// </summary>
        private static IEnumerable<ValidationIssue> ValidateFileNaming(string scriptsPath, string moduleName)
        {
            var issues = new List<ValidationIssue>();
            var csFiles = Directory.GetFiles(scriptsPath, "*.cs", SearchOption.AllDirectories);
            var modulePrefix = moduleName.Replace("Module", "");

            foreach (var file in csFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);

                if (!IsPascalCase(fileName))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        $"File '{fileName}.cs' in '{moduleName}' should follow PascalCase convention",
                        "Rename file to use PascalCase")
                        .WithFile(file));
                }

                if (fileName.Contains("MonoBehaviour") && !fileName.StartsWith(modulePrefix))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Info,
                        $"MonoBehaviour '{fileName}' in '{moduleName}' could be prefixed with '{modulePrefix}'",
                        $"Consider renaming to '{modulePrefix}{fileName}'")
                        .WithFile(file));
                }
            }

            return issues;
        }

        /// <summary>
        /// Checks if a string follows PascalCase convention.
        /// </summary>
        public static bool IsPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (!char.IsUpper(name[0]))
                return false;

            if (name.Contains("__") || name.StartsWith("_") || name.EndsWith("_"))
                return false;

            return PascalCasePattern.IsMatch(name.Replace("_", ""));
        }

    }
}
