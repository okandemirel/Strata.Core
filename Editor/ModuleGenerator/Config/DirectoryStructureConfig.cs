using System;
using System.Collections.Generic;
using System.IO;
using Strada.Core.Editor.ModuleGenerator.Models;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.ModuleGenerator.Config
{
    /// <summary>
    /// Configuration for module folder structure.
    /// </summary>
    public class DirectoryStructureConfig : ScriptableObject
    {
        private const string ConfigPath = "Assets/Editor/StradaDirectoryConfig.asset";

        // Component folders sit directly under the module root. There is no
        // Scripts/ layer: it grouped every code folder behind one name that says
        // nothing about the module, while Editor/, Tests/ and Resources/ already
        // lived at the root — so the tree was split by "is it code" rather than
        // by concern, and reading a module meant opening Scripts/ first.
        [SerializeField] private List<FolderEntry> _folders = new List<FolderEntry>
        {
            new FolderEntry { Path = "Interfaces", RequiredComponent = ComponentType.ServiceInterface },
            new FolderEntry { Path = "Services", RequiredComponent = ComponentType.Service },
            new FolderEntry { Path = "Controllers", RequiredComponent = ComponentType.Controller },
            new FolderEntry { Path = "Commands", RequiredComponent = ComponentType.Commands },
            new FolderEntry { Path = "Models", RequiredComponent = ComponentType.Model },
            new FolderEntry { Path = "Views", RequiredComponent = ComponentType.View },
            new FolderEntry { Path = "Systems", RequiredComponent = ComponentType.EcsSystem },
            new FolderEntry { Path = "Components", RequiredComponent = ComponentType.EcsComponent },
            new FolderEntry { Path = "Events", RequiredComponent = ComponentType.Events },
            new FolderEntry { Path = "Signals", RequiredComponent = ComponentType.Signals },
            new FolderEntry { Path = "Data/UnityObjects", RequiredComponent = ComponentType.ConfigData },
            new FolderEntry { Path = "Data/ValueObjects", RequiredComponent = ComponentType.ValueObject },
            new FolderEntry { Path = "Editor", RequiredComponent = ComponentType.EditorScripts },
            new FolderEntry { Path = "Resources/Configs", IsOptional = true },
            new FolderEntry { Path = "Resources/Prefabs", IsOptional = true },
            new FolderEntry { Path = "Tests/Runtime", RequiredComponent = ComponentType.RuntimeTests },
            new FolderEntry { Path = "Tests/Editor", RequiredComponent = ComponentType.EditorTests },
            new FolderEntry { Path = "Scenes", IsOptional = true },
        };

        public IReadOnlyList<FolderEntry> Folders => _folders;

        private static DirectoryStructureConfig _instance;

        public static DirectoryStructureConfig GetOrCreateConfig()
        {
            if (_instance != null)
                return _instance;

            _instance = AssetDatabase.LoadAssetAtPath<DirectoryStructureConfig>(ConfigPath);

            if (_instance == null)
            {
                var guids = AssetDatabase.FindAssets("t:DirectoryStructureConfig");
                if (guids.Length > 0)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _instance = AssetDatabase.LoadAssetAtPath<DirectoryStructureConfig>(path);
                }
            }

            if (_instance == null)
            {
                _instance = CreateInstance<DirectoryStructureConfig>();

                var directory = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                AssetDatabase.CreateAsset(_instance, ConfigPath);
                AssetDatabase.SaveAssets();
            }

            return _instance;
        }

        public List<string> GetFoldersForModule(ComponentSelection components)
        {
            var result = new List<string>();

            foreach (var folder in _folders)
            {
                if (folder.IsMandatory)
                {
                    result.Add(folder.Path);
                    continue;
                }

                if (folder.RequiredComponent != ComponentType.None && IsComponentSelected(components, folder.RequiredComponent))
                {
                    result.Add(folder.Path);
                }
            }

            return result;
        }

        private bool IsComponentSelected(ComponentSelection components, ComponentType type)
        {
            return type switch
            {
                ComponentType.ServiceInterface => components.ServiceInterface,
                ComponentType.Service => components.Service,
                ComponentType.Controller => components.Controller,
                ComponentType.Commands => components.Commands,
                ComponentType.Model => components.Model,
                ComponentType.View => components.View,
                ComponentType.EcsSystem => components.EcsSystem,
                ComponentType.EcsComponent => components.EcsComponent,
                ComponentType.EntityMediator => components.EntityMediator,
                ComponentType.ConfigData => components.ConfigData,
                ComponentType.ValueObject => components.ValueObject,
                ComponentType.Events => components.Events,
                ComponentType.Signals => components.Signals,
                ComponentType.RuntimeTests => components.RuntimeTests,
                ComponentType.EditorTests => components.EditorTests,
                ComponentType.EditorScripts => components.EditorScripts,
                _ => false
            };
        }
    }

    [Serializable]
    public class FolderEntry
    {
        public string Path;
        public bool IsMandatory;
        public bool IsOptional;
        public ComponentType RequiredComponent = ComponentType.None;
    }

    public enum ComponentType
    {
        None,
        ModuleConfig,
        AssemblyDefinition,
        ServiceInterface,
        Service,
        Controller,
        Commands,
        Model,
        View,
        EcsSystem,
        EcsComponent,
        EntityMediator,
        ConfigData,
        ValueObject,
        Events,
        Signals,
        RuntimeTests,
        EditorTests,
        EditorScripts
    }
}
