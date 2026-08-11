using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Strada.Core.Editor.ModuleGenerator.Models;
using UnityEngine;

namespace Strada.Core.Editor.ModuleGenerator
{
    public partial class StradaModuleGenerator
    {
        private static readonly HashSet<string> ReservedNames = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
            "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
            "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
            "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
            "object", "operator", "out", "override", "params", "private", "protected", "public",
            "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
            "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
            "volatile", "while",
            "System", "Unity", "UnityEngine", "UnityEditor", "Strada", "Module", "Test", "Editor"
        };

        private static readonly Regex PascalCaseRegex = new Regex(@"^[A-Z][a-zA-Z0-9]*$");

        private bool ValidateAll()
        {
            _validationMessages.Clear();

            ValidateModuleName();
            ValidateNamespace();
            ValidateTargetPath();
            ValidateModuleType();
            ValidateComponents();

            return !_validationMessages.Exists(m => m.Severity == ValidationSeverity.Error);
        }

        private void ValidateModuleName()
        {
            var name = _moduleDefinition.ModuleName;

            if (string.IsNullOrEmpty(name))
            {
                _validationMessages.Add(ValidationMessage.Error("Module name is required.", "ModuleName"));
                return;
            }

            if (name.Length < 2)
            {
                _validationMessages.Add(ValidationMessage.Error("Module name must be at least 2 characters.", "ModuleName"));
                return;
            }

            if (name.Length > 64)
            {
                _validationMessages.Add(ValidationMessage.Error("Module name must be 64 characters or less.", "ModuleName"));
                return;
            }

            if (!char.IsUpper(name[0]))
            {
                _validationMessages.Add(ValidationMessage.Error("Module name must start with an uppercase letter (PascalCase).", "ModuleName"));
                return;
            }

            if (!PascalCaseRegex.IsMatch(name))
            {
                _validationMessages.Add(ValidationMessage.Error("Module name must be PascalCase (letters and numbers only).", "ModuleName"));
                return;
            }

            if (ReservedNames.Contains(name))
            {
                _validationMessages.Add(ValidationMessage.Error($"'{name}' is a reserved name.", "ModuleName"));
                return;
            }

            if (ModuleDiscovery.ModuleExists(name))
            {
                _validationMessages.Add(ValidationMessage.Error($"Module '{name}' already exists.", "ModuleName"));
                return;
            }

            if (name.Length > 40)
            {
                _validationMessages.Add(ValidationMessage.Warning("Consider using a shorter module name.", "ModuleName"));
            }
        }

        private void ValidateNamespace()
        {
            var ns = _moduleDefinition.Namespace;

            if (string.IsNullOrEmpty(ns))
            {
                _validationMessages.Add(ValidationMessage.Error("Namespace is required.", "Namespace"));
                return;
            }

            var parts = ns.Split('.');
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part))
                {
                    _validationMessages.Add(ValidationMessage.Error("Namespace contains empty segments.", "Namespace"));
                    return;
                }

                if (!char.IsLetter(part[0]))
                {
                    _validationMessages.Add(ValidationMessage.Error($"Namespace segment '{part}' must start with a letter.", "Namespace"));
                    return;
                }
            }

            if (parts.Length > 6)
            {
                _validationMessages.Add(ValidationMessage.Warning("Very deep namespace hierarchy may cause issues.", "Namespace"));
            }
        }

        private void ValidateTargetPath()
        {
            var path = _moduleDefinition.TargetPath;

            if (string.IsNullOrEmpty(path))
            {
                _validationMessages.Add(ValidationMessage.Error("Target path is required.", "TargetPath"));
                return;
            }

            if (!IsInsideAssetsFolder(path))
            {
                _validationMessages.Add(ValidationMessage.Error("Target path must be within the Assets folder.", "TargetPath"));
                return;
            }

            if (!Directory.Exists(path))
            {
                _validationMessages.Add(ValidationMessage.Warning($"Target path '{path}' does not exist. It will be created.", "TargetPath"));
            }

            var fullPath = _moduleDefinition.FullPath;
            if (!string.IsNullOrEmpty(fullPath) && Directory.Exists(fullPath))
            {
                _validationMessages.Add(ValidationMessage.Error($"Folder '{fullPath}' already exists.", "TargetPath"));
            }
        }

        /// <summary>
        /// Returns true if the (possibly relative) path resolves to a location inside the
        /// project's Assets folder.
        /// </summary>
        /// <remarks>
        /// The path has to be canonicalised first: an uncanonicalised StartsWith("Assets") test
        /// is satisfied by both "Assets/../../anything" and "AssetsEvil/anything", and the
        /// generator hands this path straight to Directory.CreateDirectory and File.WriteAllText.
        /// </remarks>
        internal static bool IsInsideAssetsFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string fullPath;
            string assetsRoot;
            try
            {
                fullPath = Path.GetFullPath(path);
                assetsRoot = Path.GetFullPath(Application.dataPath);
            }
            catch (Exception)
            {
                // Malformed path (invalid characters, too long) cannot be proven contained.
                return false;
            }

            assetsRoot = assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

            // Ordinal, because path containment must never depend on the current culture, and
            // with the trailing separator so "AssetsEvil" cannot prefix-match "Assets".
            return fullPath.StartsWith(assetsRoot, StringComparison.Ordinal);
        }

        private void ValidateModuleType()
        {
            if (_moduleDefinition.ModuleType == ModuleType.Sub)
            {
                if (string.IsNullOrEmpty(_moduleDefinition.ParentModuleName))
                {
                    _validationMessages.Add(ValidationMessage.Error("Sub modules require a parent module.", "ParentModule"));
                }
            }
        }

        private void ValidateComponents()
        {
            var components = _moduleDefinition.Components;

            if (!components.ModuleConfig &&
                !components.Service &&
                !components.Controller &&
                !components.EcsSystem &&
                !components.EcsComponent &&
                !components.Model &&
                !components.View &&
                !components.ConfigData &&
                !components.RuntimeTests &&
                !components.EditorTests)
            {
                _validationMessages.Add(ValidationMessage.Warning("No components selected to generate.", "Components"));
            }

            if (components.Service && !components.ServiceInterface)
            {
                _validationMessages.Add(ValidationMessage.Info("Service selected without interface. Interface is recommended.", "Components"));
            }

            if (components.EntityMediator && !components.EcsComponent)
            {
                _validationMessages.Add(ValidationMessage.Warning("Entity Mediator typically requires a Component.", "Components"));
            }

            if (components.ConfigData && !components.ValueObject)
            {
                _validationMessages.Add(ValidationMessage.Info("ConfigData selected without ValueObject. Consider adding ValueObject.", "Components"));
            }
        }
    }
}
