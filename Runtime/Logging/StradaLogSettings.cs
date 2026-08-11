using System;
using System.Collections.Generic;
using UnityEngine;

namespace Strada.Core.Logging
{
    /// <summary>
    /// ScriptableObject storing StradaLog configuration.
    /// Create via Assets > Create > Strada > Log Settings or access via Instance property.
    /// </summary>
    [CreateAssetMenu(fileName = "StradaLogSettings", menuName = "Strada/Log Settings")]
    public sealed class StradaLogSettings : ScriptableObject
    {
        private const string ResourcePath = "StradaLogSettings";
        private const int MinLogEntries = 100;
        private static readonly object _instanceLock = new object();
        private static StradaLogSettings _instance;

        [Header("General")]
        [Tooltip("Toggle log output to the Unity console.")]
        [SerializeField] private bool _showLogs = true;

        [Tooltip("Enable detailed flow logs for debugging.")]
        [SerializeField] private bool _deepLogsEnabled;

        [Tooltip("Maximum number of log entries to store in the buffer.")]
        [Min(MinLogEntries)]
        [SerializeField] private int _maxLogEntries = 1000;

        [Header("Background Colors")]
        [Tooltip("Background color for error log entries.")]
        [SerializeField] private Color _errorBackgroundColor = new Color(0.8f, 0.2f, 0.2f, 0.3f);

        [Tooltip("Background color for warning log entries.")]
        [SerializeField] private Color _warningBackgroundColor = new Color(0.8f, 0.8f, 0.2f, 0.3f);

        [Tooltip("Background color for info log entries.")]
        [SerializeField] private Color _infoBackgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.1f);

        [Tooltip("Background color for deep log entries.")]
        [SerializeField] private Color _deepLogBackgroundColor = new Color(0.2f, 0.4f, 0.8f, 0.2f);

        [Header("Module Colors")]
        [SerializeField] private List<ModuleColorEntry> _moduleColors = new List<ModuleColorEntry>();

        [Header("Module Visibility")]
        [SerializeField] private List<ModuleVisibilityEntry> _moduleVisibility = new List<ModuleVisibilityEntry>();

        /// <summary>
        /// Gets the singleton instance of StradaLogSettings.
        /// Loads from Resources or creates a default instance.
        /// </summary>
        public static StradaLogSettings Instance
        {
            get
            {
                var cached = _instance;
                if (cached != null) return cached;

                // Two threads racing the lazy path each built a settings object and published
                // whichever finished last, so callers could hold different instances.
                lock (_instanceLock)
                {
                    if (_instance != null) return _instance;

                    var loaded = Resources.Load<StradaLogSettings>(ResourcePath);

                    if (loaded == null)
                    {
                        loaded = CreateInstance<StradaLogSettings>();
                        loaded.InitializeDefaults();
                    }

                    _instance = loaded;
                    return loaded;
                }
            }
        }

        /// <summary>
        /// Resolves the settings instance on the main thread during startup.
        /// </summary>
        /// <remarks>
        /// Resources.Load and ScriptableObject.CreateInstance may only be called from Unity's
        /// main thread, but StradaLog is explicitly built for concurrent use and reaches this
        /// property from every log call. Materialising the instance here means a worker thread
        /// can never be the one that takes the lazy path.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void PreloadOnMainThread()
        {
            _ = Instance;
        }

        /// <summary>
        /// Gets or sets whether logs are output to the Unity console.
        /// </summary>
        public bool ShowLogs
        {
            get => _showLogs;
            set => _showLogs = value;
        }

        /// <summary>
        /// Gets or sets whether deep logging is enabled.
        /// </summary>
        public bool DeepLogsEnabled
        {
            get => _deepLogsEnabled;
            set => _deepLogsEnabled = value;
        }

        /// <summary>
        /// Gets or sets the maximum number of log entries to store.
        /// </summary>
        public int MaxLogEntries
        {
            get => _maxLogEntries;
            set => _maxLogEntries = Mathf.Max(MinLogEntries, value);
        }

        /// <summary>
        /// Keeps the serialized field within range.
        /// </summary>
        /// <remarks>
        /// The inspector writes the backing field directly and never goes through the clamping
        /// setter, so a hand-typed 0 would reach StradaLog.AddToBuffer and divide by zero on the
        /// first log call — taking out logging entirely until the asset is edited again.
        /// </remarks>
        private void OnValidate()
        {
            if (_maxLogEntries < MinLogEntries)
                _maxLogEntries = MinLogEntries;
        }

        /// <summary>
        /// Gets the background color for error log entries.
        /// </summary>
        public Color ErrorBackgroundColor => _errorBackgroundColor;

        /// <summary>
        /// Gets the background color for warning log entries.
        /// </summary>
        public Color WarningBackgroundColor => _warningBackgroundColor;

        /// <summary>
        /// Gets the background color for info log entries.
        /// </summary>
        public Color InfoBackgroundColor => _infoBackgroundColor;

        /// <summary>
        /// Gets the background color for deep log entries.
        /// </summary>
        public Color DeepLogBackgroundColor => _deepLogBackgroundColor;

        /// <summary>
        /// Gets the color for a specific module.
        /// </summary>
        public Color GetModuleColor(LogModule module)
        {
            for (int i = 0; i < _moduleColors.Count; i++)
            {
                if (_moduleColors[i].Module == module)
                    return _moduleColors[i].Color;
            }
            return GetDefaultModuleColor(module);
        }

        /// <summary>
        /// Sets the color for a specific module. Colors can be changed for all tiers.
        /// </summary>
        public void SetModuleColor(LogModule module, Color color)
        {
            for (int i = 0; i < _moduleColors.Count; i++)
            {
                if (_moduleColors[i].Module == module)
                {
                    _moduleColors[i] = new ModuleColorEntry { Module = module, Color = color };
                    return;
                }
            }
            _moduleColors.Add(new ModuleColorEntry { Module = module, Color = color });
        }

        /// <summary>
        /// Gets whether a module is visible in the log window tabs.
        /// For locked tiers (Core/StradaModule), always returns the default visibility.
        /// </summary>
        public bool IsModuleVisible(LogModule module)
        {
            // For locked modules, return default visibility from registry
            if (LogModuleRegistry.IsVisibilityLocked(module))
            {
                return LogModuleRegistry.GetDefaultVisible(module);
            }

            for (int i = 0; i < _moduleVisibility.Count; i++)
            {
                if (_moduleVisibility[i].Module == module)
                    return _moduleVisibility[i].IsVisible;
            }
            return LogModuleRegistry.GetDefaultVisible(module);
        }

        /// <summary>
        /// Sets whether a module is visible in the log window tabs.
        /// Only works for Game tier modules; visibility for Core/StradaModule tiers is locked.
        /// </summary>
        /// <returns>True if the visibility was changed, false if the module is locked.</returns>
        public bool SetModuleVisible(LogModule module, bool visible)
        {
            // Reject changes for locked tiers
            if (LogModuleRegistry.IsVisibilityLocked(module))
            {
                return false;
            }

            for (int i = 0; i < _moduleVisibility.Count; i++)
            {
                if (_moduleVisibility[i].Module == module)
                {
                    _moduleVisibility[i] = new ModuleVisibilityEntry { Module = module, IsVisible = visible };
                    return true;
                }
            }
            _moduleVisibility.Add(new ModuleVisibilityEntry { Module = module, IsVisible = visible });
            return true;
        }

        /// <summary>
        /// Gets the background color for a log entry based on its type.
        /// </summary>
        public Color GetBackgroundColor(LogEntry entry)
        {
            if (entry.IsDeepLog)
                return _deepLogBackgroundColor;

            switch (entry.Type)
            {
                case LogType.Error:
                case LogType.Exception:
                    return _errorBackgroundColor;
                case LogType.Warning:
                    return _warningBackgroundColor;
                default:
                    return _infoBackgroundColor;
            }
        }

        /// <summary>
        /// Resets all settings to defaults.
        /// </summary>
        public void ResetToDefaults()
        {
            _showLogs = true;
            _deepLogsEnabled = false;
            _maxLogEntries = 1000;
            _errorBackgroundColor = new Color(0.8f, 0.2f, 0.2f, 0.3f);
            _warningBackgroundColor = new Color(0.8f, 0.8f, 0.2f, 0.3f);
            _infoBackgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.1f);
            _deepLogBackgroundColor = new Color(0.2f, 0.4f, 0.8f, 0.2f);
            _moduleColors.Clear();
            _moduleVisibility.Clear();
            InitializeDefaults();
        }

        /// <summary>
        /// Registers any dynamically stored modules (from color/visibility entries)
        /// with the LogModuleRegistry so they appear in the editor.
        /// </summary>
        public void RegisterStoredModules()
        {
            LogModuleRegistry.EnsureInitialized();

            foreach (var entry in _moduleColors)
            {
                if (!LogModuleRegistry.IsRegistered(entry.Module))
                {
                    var tier = GetTierFromId((int)entry.Module);
                    var name = Enum.IsDefined(typeof(LogModule), entry.Module)
                        ? entry.Module.ToString()
                        : $"Module_{(int)entry.Module}";
                    LogModuleRegistry.RegisterModule(entry.Module, tier, name);
                }
            }

            foreach (var entry in _moduleVisibility)
            {
                if (!LogModuleRegistry.IsRegistered(entry.Module))
                {
                    var tier = GetTierFromId((int)entry.Module);
                    var name = Enum.IsDefined(typeof(LogModule), entry.Module)
                        ? entry.Module.ToString()
                        : $"Module_{(int)entry.Module}";
                    LogModuleRegistry.RegisterModule(entry.Module, tier, name);
                }
            }
        }

        private LogModuleTier GetTierFromId(int moduleId)
        {
            if (moduleId < 100) return LogModuleTier.StradaCore;
            if (moduleId < 1000) return LogModuleTier.StradaModule;
            return LogModuleTier.Game;
        }

        private void InitializeDefaults()
        {
            if (_moduleColors.Count == 0)
            {
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.General, Color = new Color(0.7f, 0.7f, 0.7f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.Core, Color = new Color(0.4f, 0.6f, 0.9f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.Editor, Color = new Color(0.6f, 0.4f, 0.8f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.DI, Color = new Color(0.4f, 0.8f, 0.4f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.ECS, Color = new Color(0.9f, 0.6f, 0.3f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.Sync, Color = new Color(0.3f, 0.8f, 0.8f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.Bootstrap, Color = new Color(0.8f, 0.4f, 0.6f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.Modules, Color = new Color(0.6f, 0.8f, 0.4f) });
                _moduleColors.Add(new ModuleColorEntry { Module = LogModule.Screen, Color = new Color(0.5f, 0.7f, 0.9f) });
            }
        }

        private Color GetDefaultModuleColor(LogModule module)
        {
            int hash = (int)module * 31;
            float h = (hash % 360) / 360f;
            return Color.HSVToRGB(h, 0.5f, 0.8f);
        }

        /// <summary>
        /// Serializable entry for module colors.
        /// </summary>
        [Serializable]
        public struct ModuleColorEntry
        {
            public LogModule Module;
            public Color Color;
        }

        /// <summary>
        /// Serializable entry for module visibility.
        /// </summary>
        [Serializable]
        public struct ModuleVisibilityEntry
        {
            public LogModule Module;
            public bool IsVisible;
        }
    }
}
