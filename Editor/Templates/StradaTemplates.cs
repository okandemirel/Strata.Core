using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Templates
{
    /// <summary>
    /// Provides code templates for Strada framework components.
    /// Generates properly structured code with correct namespaces and using statements.
    /// </summary>
    public static class StradaTemplates
    {
        // The class name is interpolated verbatim into both the generated C# source and the
        // output file name, so anything that is not a bare C# identifier either produces a
        // file that cannot compile ("New System") or escapes the folder ("../../evil").
        private static readonly Regex ValidClassNameRegex =
            new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>
        /// Returns true if <paramref name="className"/> is a bare C# identifier and is therefore
        /// safe to interpolate into generated source and into a file name.
        /// </summary>
        public static bool IsValidClassName(string className)
        {
            return !string.IsNullOrEmpty(className) && ValidClassNameRegex.IsMatch(className);
        }

        /// <summary>
        /// Generates an ISystem implementation template.
        /// </summary>
        /// <param name="className">The name of the system class.</param>
        /// <param name="namespaceName">The namespace for the class.</param>
        /// <param name="systemOrder">The system execution order (default 0).</param>
        /// <returns>The generated code.</returns>
        public static string GenerateSystem(string className, string namespaceName, int systemOrder = 0)
        {
            if (!className.EndsWith("System"))
                className += "System";

            var usings = UsingStatementGenerator.GenerateUsingBlock(
                TemplateContextDetector.TemplateType.System);

            var sb = new StringBuilder();
            sb.AppendLine(usings);
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// ECS System that processes entities each frame.");
            sb.AppendLine("    /// Inherits from SystemBase for automatic EntityManager and MessageBus injection.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    [SystemOrder({systemOrder})]");
            sb.AppendLine($"    public class {className} : SystemBase");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Called once when the system is first created.");
            sb.AppendLine("        /// EntityManager and MessageBus are available via inherited properties.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        protected override void OnInitialize()");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Called every frame to process entities.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <param name=\"deltaTime\">The time elapsed since the last frame.</param>");
            sb.AppendLine("        protected override void OnUpdate(float deltaTime)");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Called when the system is disposed.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        protected override void OnDispose()");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a Controller template.
        /// </summary>
        public static string GenerateController(string className, string namespaceName)
        {
            if (!className.EndsWith("Controller"))
                className += "Controller";

            var usings = UsingStatementGenerator.GenerateUsingBlock(
                TemplateContextDetector.TemplateType.Controller);

            var sb = new StringBuilder();
            sb.AppendLine(usings);
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Controller that handles input and coordinates between Views and Services.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public class {className} : Controller");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void Initialize()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.Initialize();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override void Tick(float deltaTime)");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override void Shutdown()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.Shutdown();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a Service template.
        /// </summary>
        public static string GenerateService(string className, string namespaceName)
        {
            if (!className.EndsWith("Service"))
                className += "Service";

            var usings = UsingStatementGenerator.GenerateUsingBlock(
                TemplateContextDetector.TemplateType.Service);

            var sb = new StringBuilder();
            sb.AppendLine(usings);
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Service containing business logic, shared across modules.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public class {className} : Service");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void Initialize()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.Initialize();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override void Shutdown()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.Shutdown();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a Component template (unmanaged struct).
        /// </summary>
        public static string GenerateComponent(string className, string namespaceName)
        {
            if (!className.EndsWith("Component"))
                className += "Component";

            var usings = UsingStatementGenerator.GenerateUsingBlock(
                TemplateContextDetector.TemplateType.Component);

            var sb = new StringBuilder();
            sb.AppendLine(usings);
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// ECS Component data. Must be an unmanaged struct (no reference types).");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    [StructLayout(LayoutKind.Sequential)]");
            sb.AppendLine($"    public struct {className} : IComponent");
            sb.AppendLine("    {");
            sb.AppendLine("        public int Value;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a View template.
        /// </summary>
        public static string GenerateView(string className, string namespaceName)
        {
            if (!className.EndsWith("View"))
                className += "View";

            var usings = UsingStatementGenerator.GenerateUsingBlock(
                TemplateContextDetector.TemplateType.View);

            var sb = new StringBuilder();
            sb.AppendLine(usings);
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// View component for UI/visual representation.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public class {className} : View");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void Initialize()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.Initialize();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        protected override void OnShow()");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        protected override void OnHide()");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a ConfigData template.
        /// </summary>
        public static string GenerateConfig(string className, string namespaceName)
        {
            if (!className.StartsWith("CD_"))
                className = "CD_" + className;

            var usings = UsingStatementGenerator.GenerateUsingBlock(
                TemplateContextDetector.TemplateType.Config);

            var sb = new StringBuilder();
            sb.AppendLine(usings);
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Configuration data asset.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    [CreateAssetMenu(fileName = \"{className}\", menuName = \"Strada/Config/{className.Replace("CD_", "")}\")]");
            sb.AppendLine($"    public class {className} : ScriptableObject");
            sb.AppendLine("    {");
            sb.AppendLine("        [Header(\"Configuration\")]");
            sb.AppendLine("        [SerializeField] private int _value = 0;");
            sb.AppendLine();
            sb.AppendLine("        public int Value => _value;");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Validates the configuration data.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <returns>True if valid, false otherwise.</returns>");
            sb.AppendLine("        public virtual bool Validate()");
            sb.AppendLine("        {");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a Command template for MessageBus.
        /// </summary>
        public static string GenerateCommand(string className, string namespaceName)
        {
            return GenerateMessageStruct(className, namespaceName, "Command",
                TemplateContextDetector.TemplateType.Command,
                "Command message for MessageBus.",
                "Use with bus.Send() to dispatch commands.");
        }

        /// <summary>
        /// Generates an Event template for MessageBus.
        /// </summary>
        public static string GenerateEvent(string className, string namespaceName)
        {
            return GenerateMessageStruct(className, namespaceName, "Event",
                TemplateContextDetector.TemplateType.Event,
                "Event message for MessageBus.",
                "Use with bus.Publish() to broadcast events.");
        }

        /// <summary>
        /// Generates a readonly struct message template (shared by Command and Event).
        /// </summary>
        private static string GenerateMessageStruct(
            string className, string namespaceName, string suffix,
            TemplateContextDetector.TemplateType templateType,
            string summaryLine1, string summaryLine2)
        {
            if (!className.EndsWith(suffix))
                className += suffix;

            var usings = UsingStatementGenerator.GenerateUsingBlock(templateType);

            var sb = new StringBuilder();
            sb.AppendLine(usings);
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// {summaryLine1}");
            sb.AppendLine($"    /// {summaryLine2}");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public readonly struct {className}");
            sb.AppendLine("    {");
            sb.AppendLine("        public readonly int Value;");
            sb.AppendLine();
            sb.AppendLine($"        public {className}(int value)");
            sb.AppendLine("        {");
            sb.AppendLine("            Value = value;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a template based on the template type.
        /// </summary>
        public static string GenerateTemplate(
            TemplateContextDetector.TemplateType templateType,
            string className,
            string namespaceName)
        {
            return templateType switch
            {
                TemplateContextDetector.TemplateType.System => GenerateSystem(className, namespaceName),
                TemplateContextDetector.TemplateType.Controller => GenerateController(className, namespaceName),
                TemplateContextDetector.TemplateType.Service => GenerateService(className, namespaceName),
                TemplateContextDetector.TemplateType.Component => GenerateComponent(className, namespaceName),
                TemplateContextDetector.TemplateType.View => GenerateView(className, namespaceName),
                TemplateContextDetector.TemplateType.Config => GenerateConfig(className, namespaceName),
                TemplateContextDetector.TemplateType.Command => GenerateCommand(className, namespaceName),
                TemplateContextDetector.TemplateType.Event => GenerateEvent(className, namespaceName),
                _ => GenerateGenericClass(className, namespaceName)
            };
        }

        /// <summary>
        /// Generates a generic class template.
        /// </summary>
        private static string GenerateGenericClass(string className, string namespaceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// {className} class.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public class {className}");
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Creates a file from a template at the specified path.
        /// </summary>
        public static bool CreateFileFromTemplate(
            TemplateContextDetector.TemplateType templateType,
            string className,
            string folderPath)
        {
            // Validate at the API boundary rather than trusting the UI: this is public and is
            // also reached from StradaContextMenus, and className is a sink for both the emitted
            // source and the file path.
            if (!IsValidClassName(className))
            {
                Debug.LogError(
                    $"[Strada] Invalid class name '{className}': must be a C# identifier " +
                    "(letters, digits and underscore, not starting with a digit).");
                return false;
            }

            try
            {
                var namespaceName = TemplateContextDetector.ExtractNamespace(folderPath);
                var code = GenerateTemplate(templateType, className, namespaceName);

                var fileName = GetFileNameForTemplate(templateType, className);
                var filePath = Path.Combine(folderPath, fileName);

                // folderPath comes from the Project window selection and is not canonicalised
                // anywhere upstream, so a "../.." segment would otherwise write outside the
                // project. Same containment check FileGenerationStep applies.
                if (!IsInsideAssetsFolder(filePath))
                {
                    Debug.LogError($"[Strada] Refusing to write outside the Assets folder: {filePath}");
                    return false;
                }

                // File.WriteAllText truncates unconditionally; without this the user silently
                // loses an existing source file (reachable from 'Generate Controller', where the
                // name is derived from an existing type).
                if (File.Exists(filePath))
                {
                    var uniquePath = AssetDatabase.GenerateUniqueAssetPath(filePath);
                    if (string.IsNullOrEmpty(uniquePath) || uniquePath == filePath)
                    {
                        Debug.LogError($"[Strada] File already exists, not overwriting: {filePath}");
                        return false;
                    }

                    Debug.LogWarning($"[Strada] {filePath} already exists; creating {uniquePath} instead.");
                    filePath = uniquePath;
                }

                Directory.CreateDirectory(folderPath);

                File.WriteAllText(filePath, code);

                AssetDatabase.Refresh();

                Debug.Log($"[Strada] Created {templateType} template: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Strada] Failed to create template: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns true if the (possibly relative) path resolves to a location inside the
        /// project's Assets folder.
        /// </summary>
        private static bool IsInsideAssetsFolder(string path)
        {
            string fullPath;
            string assetsRoot;
            try
            {
                fullPath = Path.GetFullPath(path);
                assetsRoot = Path.GetFullPath(Application.dataPath);
            }
            catch (Exception)
            {
                // Malformed path (invalid characters, too long) is not containable, so reject it.
                return false;
            }

            assetsRoot = assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

            // A trailing separator is required: without it "AssetsEvil/x.cs" prefix-matches
            // "Assets". Ordinal, because path containment must never be culture sensitive.
            return fullPath.StartsWith(assetsRoot, StringComparison.Ordinal);
        }

        /// <summary>
        /// Gets the appropriate file name for a template type.
        /// </summary>
        private static string GetFileNameForTemplate(
            TemplateContextDetector.TemplateType templateType,
            string className)
        {
            var suffix = templateType switch
            {
                TemplateContextDetector.TemplateType.System => "System",
                TemplateContextDetector.TemplateType.Controller => "Controller",
                TemplateContextDetector.TemplateType.Service => "Service",
                TemplateContextDetector.TemplateType.Component => "Component",
                TemplateContextDetector.TemplateType.View => "View",
                TemplateContextDetector.TemplateType.Config => "",
                TemplateContextDetector.TemplateType.Command => "Command",
                TemplateContextDetector.TemplateType.Event => "Event",
                _ => ""
            };

            if (templateType == TemplateContextDetector.TemplateType.Config)
            {
                if (!className.StartsWith("CD_"))
                    className = "CD_" + className;
            }
            else if (!string.IsNullOrEmpty(suffix) && !className.EndsWith(suffix))
            {
                className += suffix;
            }

            return className + ".cs";
        }
    }
}
