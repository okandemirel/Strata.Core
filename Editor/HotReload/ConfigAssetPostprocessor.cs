using System;
using System.Collections.Generic;
using Strada.Core.Data;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.HotReload
{
    /// <summary>
    /// Funnels every detected config change through a single de-duplicating entry point.
    /// </summary>
    /// <remarks>
    /// Saving one CD_ asset is detected twice: ConfigAssetModificationProcessor sees the save,
    /// and the import the save triggers then reaches ConfigAssetPostprocessor for the same path.
    /// HotReloadManager keeps a plain Queue and drains all of it, so the second detection costs a
    /// second full EntityStatePreserver capture/restore pass over every entity in the world plus
    /// a second OnConfigReloaded notification to every dependent service.
    ///
    /// De-duplication is time-based rather than "is it still queued", because HotReloadManager
    /// drains its queue from EditorApplication.update - usually before the modification
    /// processor's delayCall has even run - so the queue is empty again by the time the
    /// duplicate arrives.
    /// </remarks>
    internal static class ConfigChangeDispatcher
    {
        // Comfortably longer than the gap between the two detections for one save (a frame or
        // two), and short enough that a deliberate second save is still honoured.
        private const double DuplicateWindowSeconds = 1.0;

        private static readonly Dictionary<string, double> LastQueuedAt =
            new Dictionary<string, double>(StringComparer.Ordinal);

        private static readonly List<string> ExpiredScratch = new List<string>();

        public static void Queue(string assetPath, ConfigData config)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            var now = EditorApplication.timeSinceStartup;

            if (LastQueuedAt.TryGetValue(assetPath, out var previous) &&
                now - previous < DuplicateWindowSeconds)
            {
                return;
            }

            LastQueuedAt[assetPath] = now;
            PruneExpired(now);

            HotReloadManager.QueueConfigChange(assetPath, config);
        }

        /// <summary>
        /// Drops entries that can no longer suppress anything, so a long editing session does not
        /// accumulate one entry per config asset ever touched.
        /// </summary>
        private static void PruneExpired(double now)
        {
            ExpiredScratch.Clear();

            foreach (var kvp in LastQueuedAt)
            {
                if (now - kvp.Value >= DuplicateWindowSeconds)
                    ExpiredScratch.Add(kvp.Key);
            }

            for (int i = 0; i < ExpiredScratch.Count; i++)
            {
                LastQueuedAt.Remove(ExpiredScratch[i]);
            }

            ExpiredScratch.Clear();
        }
    }

    /// <summary>
    /// Asset postprocessor that detects changes to CD_ config assets during Play Mode.
    /// Queues detected changes for processing by HotReloadManager.
    /// </summary>
    public class ConfigAssetPostprocessor : AssetPostprocessor
    {
        /// <summary>
        /// Called after assets have been imported, deleted, or moved.
        /// </summary>
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (!Application.isPlaying || !HotReloadManager.IsEnabled)
                return;

            foreach (var assetPath in importedAssets)
            {
                ProcessAssetChange(assetPath);
            }

            foreach (var assetPath in movedAssets)
            {
                ProcessAssetChange(assetPath);
            }
        }
        
        private static void ProcessAssetChange(string assetPath)
        {
            if (!assetPath.EndsWith(".asset"))
                return;

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);

            if (asset == null || !asset.name.StartsWith("CD_"))
                return;

            if (asset is ConfigData config)
            {
                ConfigChangeDispatcher.Queue(assetPath, config);
            }
        }
    }
    
    /// <summary>
    /// Modification processor that detects when CD_ assets are saved.
    /// Provides more immediate detection than OnPostprocessAllAssets.
    /// </summary>
    public class ConfigAssetModificationProcessor : AssetModificationProcessor
    {
        /// <summary>
        /// Called when assets are about to be saved.
        /// </summary>
        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (!Application.isPlaying || !HotReloadManager.IsEnabled)
                return paths;

            foreach (var path in paths)
            {
                if (!path.EndsWith(".asset"))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

                if (asset != null && asset.name.StartsWith("CD_") && asset is ConfigData config)
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (Application.isPlaying && HotReloadManager.IsEnabled)
                        {
                            ConfigChangeDispatcher.Queue(path, config);
                        }
                    };
                }
            }
            
            return paths;
        }
    }
}
