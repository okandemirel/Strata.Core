using System.IO;
using System.Linq;
using Strada.Core.Editor.Templates;
using Strada.Core.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor
{
    /// <summary>
    /// Context menu integrations for Strada Framework.
    /// Provides right-click actions on MonoBehaviours, folders, and assets.
    /// </summary>
    public static class StradaContextMenus
    {
        /// <summary>
        /// Context menu item for inspecting a MonoBehaviour's Strada bindings.
        /// </summary>
        [MenuItem("CONTEXT/MonoBehaviour/Strada/Inspect Bindings", false, 1000)]
        private static void InspectBindings(MenuCommand command)
        {
            var target = command.context as MonoBehaviour;
            if (target == null) return;

            var type = target.GetType();
            
            if (type.Name.Contains("EntityMediator") || type.Name.Contains("Mediator"))
            {
                Selection.activeObject = target;
                EditorGUIUtility.PingObject(target);
                Debug.Log($"[Strada] Inspecting bindings for: {target.name}");
            }
            else
            {
                Debug.Log($"[Strada] {target.name} is not an EntityMediator. Select an EntityMediator to inspect bindings.");
            }
        }

        /// <summary>
        /// Context menu item for opening the Entity Inspector for a linked entity.
        /// </summary>
        [MenuItem("CONTEXT/MonoBehaviour/Strada/Open Entity Inspector", false, 1001)]
        private static void OpenEntityInspectorForComponent(MenuCommand command)
        {
            StradaEntityInspectorWindow.ShowWindow();
        }

        /// <summary>
        /// Context menu item for generating a Controller for this View.
        /// </summary>
        [MenuItem("CONTEXT/MonoBehaviour/Strada/Generate Controller", false, 1010)]
        private static void GenerateControllerForView(MenuCommand command)
        {
            var target = command.context as MonoBehaviour;
            if (target == null) return;

            var viewName = target.GetType().Name;
            var controllerName = viewName.Replace("View", "Controller");
            
            if (!controllerName.EndsWith("Controller"))
            {
                controllerName = viewName + "Controller";
            }

            var script = MonoScript.FromMonoBehaviour(target);
            var scriptPath = AssetDatabase.GetAssetPath(script);
            var folderPath = Path.GetDirectoryName(scriptPath);

            var controllersPath = folderPath?.Replace("Views", "Controllers");
            if (controllersPath != null && !Directory.Exists(controllersPath))
            {
                controllersPath = folderPath;
            }

            var namespaceName = target.GetType().Namespace ?? "Game";
            StradaTemplates.CreateFileFromTemplate(
                TemplateContextDetector.TemplateType.Controller,
                controllerName,
                controllersPath ?? "Assets");
            
            Debug.Log($"[Strada] Generated controller: {controllerName}");
        }

        /// <summary>
        /// Validation for Generate Controller menu item.
        /// </summary>
        [MenuItem("CONTEXT/MonoBehaviour/Strada/Generate Controller", true)]
        private static bool GenerateControllerForViewValidate(MenuCommand command)
        {
            var target = command.context as MonoBehaviour;
            if (target == null) return false;

            var typeName = target.GetType().Name;
            return typeName.Contains("View") || typeName.Contains("Mediator");
        }

        /// <summary>
        /// Context menu for validating module structure in the selected folder.
        /// </summary>
        [MenuItem("Assets/Strada/Validate Module Structure", false, 100)]
        private static void ValidateModuleStructure()
        {
            var folderPath = GetSelectedFolderPath();
            if (string.IsNullOrEmpty(folderPath))
            {
                Debug.LogWarning("[Strada] Please select a module folder to validate.");
                return;
            }

            var moduleName = Path.GetFileName(folderPath);
            var issues = new System.Collections.Generic.List<string>();

            var requiredFiles = new[]
            {
                $"{moduleName}Module.cs",
                $"{moduleName}.asmdef"
            };

            foreach (var file in requiredFiles)
            {
                var filePath = Path.Combine(folderPath, file);
                if (!File.Exists(filePath))
                {
                    issues.Add($"Missing: {file}");
                }
            }

            // Directly under the module root: there is no Scripts/ layer.
            var recommendedFolders = new[]
            {
                "Controllers",
                "Services",
                "Systems",
                "Components"
            };

            foreach (var folder in recommendedFolders)
            {
                var fullPath = Path.Combine(folderPath, folder);
                if (!Directory.Exists(fullPath))
                {
                    issues.Add($"Recommended folder missing: {folder}");
                }
            }

            if (issues.Count == 0)
            {
                EditorUtility.DisplayDialog("Module Validation",
                    $"Module '{moduleName}' structure is valid!",
                    "OK");
            }
            else
            {
                var message = $"Module '{moduleName}' has {issues.Count} issue(s):\n\n" +
                              string.Join("\n", issues.Select(i => $"• {i}"));
                EditorUtility.DisplayDialog("Module Validation",
                    message,
                    "OK");
            }
        }

        /// <summary>
        /// Validation for Validate Module Structure menu item.
        /// </summary>
        [MenuItem("Assets/Strada/Validate Module Structure", true)]
        private static bool ValidateModuleStructureValidate()
        {
            var folderPath = GetSelectedFolderPath();
            if (string.IsNullOrEmpty(folderPath)) return false;

            var folderName = Path.GetFileName(folderPath);
            return folderName.EndsWith("Module") ||
                   Directory.Exists(Path.Combine(folderPath, "Services")) ||
                   Directory.Exists(Path.Combine(folderPath, "Systems"));
        }

        /// <summary>
        /// Context menu for opening the Config Data Manager filtered to configs in this folder.
        /// </summary>
        [MenuItem("Assets/Strada/Manage Configs in Folder", false, 101)]
        private static void ManageConfigsInFolder()
        {
            var folderPath = GetSelectedFolderPath();
            StradaConfigDataManagerWindow.ShowWindow();
            Debug.Log($"[Strada] Opening Config Manager for: {folderPath}");
        }

        /// <summary>
        /// Context menu for validating a CD_ config asset.
        /// </summary>
        [MenuItem("Assets/Strada/Validate Config", false, 200)]
        private static void ValidateSelectedConfig()
        {
            var selected = Selection.activeObject as ScriptableObject;
            if (selected == null || !selected.name.StartsWith("CD_"))
            {
                Debug.LogWarning("[Strada] Please select a CD_ config asset to validate.");
                return;
            }

            var validateMethod = selected.GetType().GetMethod("Validate");
            if (validateMethod == null)
            {
                Debug.Log($"[Strada] {selected.name} has no Validate() method.");
                return;
            }

            try
            {
                var result = validateMethod.Invoke(selected, null);
                if (result is bool boolResult)
                {
                    if (boolResult)
                    {
                        Debug.Log($"[Strada] ✓ {selected.name} validation passed!");
                    }
                    else
                    {
                        Debug.LogWarning($"[Strada] ✗ {selected.name} validation failed!");
                    }
                }
                else if (result is string errorMsg)
                {
                    if (string.IsNullOrEmpty(errorMsg))
                    {
                        Debug.Log($"[Strada] ✓ {selected.name} validation passed!");
                    }
                    else
                    {
                        Debug.LogWarning($"[Strada] ✗ {selected.name}: {errorMsg}");
                    }
                }
                else
                {
                    Debug.Log($"[Strada] ✓ {selected.name} validation completed.");
                }
                
                EditorUtility.SetDirty(selected);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Strada] Validation error for {selected.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Validation for Validate Config menu item.
        /// </summary>
        [MenuItem("Assets/Strada/Validate Config", true)]
        private static bool ValidateSelectedConfigValidate()
        {
            var selected = Selection.activeObject as ScriptableObject;
            return selected != null && selected.name.StartsWith("CD_");
        }

        /// <summary>
        /// Gets the currently selected folder path in the Project window.
        /// </summary>
        private static string GetSelectedFolderPath()
        {
            var obj = Selection.activeObject;
            if (obj == null)
                return "Assets";

            var path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path))
                return "Assets";

            return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        }
    }
}
