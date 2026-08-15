using System;
using System.Collections.Generic;

namespace Strada.Core.Editor.ModuleGenerator.Models
{
    /// <summary>
    /// Defines a module to be generated.
    /// </summary>
    [Serializable]
    public class ModuleDefinition
    {
        public string ModuleName = "";
        public string Namespace = "Game.Modules";
        public string TargetPath = "Assets/Modules";
        public ModuleType ModuleType = ModuleType.Main;
        public string ParentModuleName = "";
        public string ParentModulePath = "";

        public ComponentSelection Components = new ComponentSelection();
        public List<string> Dependencies = new List<string>();

        public bool RegisterInBootstrapper = true;
        public bool CreateModuleConfigAsset = true;
        public bool OpenFolderAfterCreate = true;

        public string FullNamespace => string.IsNullOrEmpty(ModuleName)
            ? Namespace
            : $"{Namespace}.{ModuleName}";

        public string ModuleFolderName => string.IsNullOrEmpty(ModuleName)
            ? ""
            : $"{ModuleName}Module";

        public string FullPath => string.IsNullOrEmpty(TargetPath) || string.IsNullOrEmpty(ModuleFolderName)
            ? ""
            : $"{TargetPath}/{ModuleFolderName}";

        public void Reset()
        {
            ModuleName = "";
            ParentModuleName = "";
            ParentModulePath = "";
            Components = new ComponentSelection();
            Dependencies.Clear();
        }

        public void ApplyTypeDefaults()
        {
            Components = ComponentSelection.GetDefaultsForType(ModuleType);
        }
    }

    /// <summary>
    /// Module type determines the structure and defaults.
    /// </summary>
    public enum ModuleType
    {
        /// <summary>
        /// Standalone module with full structure and assembly definition.
        /// </summary>
        Main,

        /// <summary>
        /// Child module that inherits parent's assembly definition.
        /// </summary>
        Sub,

        /// <summary>
        /// Screen/UI focused module with View and Mediator.
        /// </summary>
        Screen,

        /// <summary>
        /// Test module for unit and integration testing.
        /// </summary>
        Test
    }

    /// <summary>
    /// Selected components to generate for the module.
    /// </summary>
    [Serializable]
    public class ComponentSelection
    {
        public bool ModuleConfig = true;
        public bool AssemblyDefinition = true;

        public bool ServiceInterface = true;
        public bool Service = true;
        public bool Controller = true;
        public bool Model = false;
        public bool View = false;

        public bool EcsSystem = false;
        public bool EcsComponent = false;
        public bool EntityMediator = false;

        public bool ConfigData = true;
        public bool ValueObject = true;
        public bool Events = false;
        public bool Signals = false;

        public bool RuntimeTests = false;
        public bool EditorTests = false;
        public bool EditorScripts = false;

        public bool FolderArt = false;
        public bool FolderPrefabs = true;
        public bool FolderResources = true;
        public bool FolderScenes = false;
        public bool FolderSprites = false;
        public bool FolderAudio = false;

        public static ComponentSelection GetDefaultsForType(ModuleType type)
        {
            return type switch
            {
                // Main uses all class defaults as-is
                ModuleType.Main => new ComponentSelection(),
                ModuleType.Sub => new ComponentSelection
                {
                    ModuleConfig = false,
                    AssemblyDefinition = false,
                    ConfigData = false,
                    ValueObject = false
                },
                ModuleType.Screen => new ComponentSelection
                {
                    ModuleConfig = false,
                    AssemblyDefinition = false,
                    ServiceInterface = false,
                    Service = false,
                    View = true,
                    EntityMediator = true,
                    ConfigData = false,
                    ValueObject = false
                },
                ModuleType.Test => new ComponentSelection
                {
                    ModuleConfig = false,
                    ServiceInterface = false,
                    Service = false,
                    Controller = false,
                    RuntimeTests = true,
                    EditorTests = true
                },
                _ => new ComponentSelection()
            };
        }

        public bool HasAnyEcs => EcsSystem || EcsComponent || EntityMediator;
        public bool HasAnyMvcs => ServiceInterface || Service || Controller || Model || View;
        public bool HasAnyData => ConfigData || ValueObject || Events || Signals;
        public bool HasAnyTests => RuntimeTests || EditorTests;
    }
}
