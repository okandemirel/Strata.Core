using System;
using System.Collections.Generic;
using Strada.Core.ECS;
using Strada.Core.ECS.World;
using UnityEngine;

namespace Strada.Core.Editor.HotReload
{
    /// <summary>
    /// Captures and restores entity state during hot reload operations.
    /// Preserves entity IDs, versions, and component values.
    /// </summary>
    public static class EntityStatePreserver
    {
        /// <summary>
        /// Captures the current state of all entities in the active world.
        /// </summary>
        /// <returns>A snapshot of the world state, or null if no world is active.</returns>
        public static WorldStateSnapshot CaptureState()
        {
            var world = World.Current;
            if (world == null || world.EntityManager == null)
            {
                return null;
            }
            
            var snapshot = new WorldStateSnapshot
            {
                Timestamp = DateTime.Now,
                Entities = new List<EntityStateSnapshot>()
            };
            
            try
            {
                var entityManager = world.EntityManager;
                var store = entityManager.Store;
                var entityIndices = entityManager.GetAllEntities();
                var componentTypes = store.GetComponentTypes();
                
                foreach (var entityIndex in entityIndices)
                {
                    var entitySnapshot = new EntityStateSnapshot
                    {
                        EntityIndex = entityIndex,
                        Components = new Dictionary<string, object>()
                    };

                    foreach (var componentType in componentTypes)
                    {
                        try
                        {
                            if (!store.HasComponent(entityIndex, componentType))
                                continue;
                                
                            var componentValue = store.GetComponentBoxed(entityIndex, componentType);
                            if (componentValue != null)
                            {
                                // Store the boxed struct itself, NOT JsonUtility.ToJson of it.
                                // ECS components are bare `struct X : IComponent` with no
                                // [Serializable], so JsonUtility emits "{}" for them and the
                                // matching FromJson on restore hands back default(T) - which
                                // used to be written straight back into live storage, silently
                                // zeroing every component in the world on each config save.
                                // GetComponentBoxed already returns a fresh box, so this is a
                                // copy, and no domain reload happens between capture and restore.
                                entitySnapshot.Components[componentType.FullName] = componentValue;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[EntityStatePreserver] Failed to capture component {componentType.Name} on entity {entityIndex}: {ex.Message}");
                        }
                    }
                    
                    snapshot.Entities.Add(entitySnapshot);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[EntityStatePreserver] Failed to capture world state: {ex.Message}");
                return null;
            }
            
            return snapshot;
        }
        
        /// <summary>
        /// Restores entity state from a snapshot.
        /// </summary>
        /// <param name="snapshot">The snapshot to restore from.</param>
        /// <returns>True if restoration was successful.</returns>
        public static bool RestoreState(WorldStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return true;
            }
            
            var world = World.Current;
            if (world == null || world.EntityManager == null)
            {
                Debug.LogWarning("[EntityStatePreserver] Cannot restore state: No active world");
                return false;
            }
            
            var entityManager = world.EntityManager;
            var store = entityManager.Store;
            var restoredCount = 0;
            var failedCount = 0;

            var activeEntities = new HashSet<int>(entityManager.GetAllEntities());
            
            foreach (var entitySnapshot in snapshot.Entities)
            {
                try
                {
                    var entityIndex = entitySnapshot.EntityIndex;

                    if (!activeEntities.Contains(entityIndex))
                    {
                        continue;
                    }

                    foreach (var kvp in entitySnapshot.Components)
                    {
                        var componentTypeName = kvp.Key;
                        var capturedValue = kvp.Value;

                        if (capturedValue == null)
                            continue;

                        try
                        {
                            var componentType = FindComponentType(componentTypeName);
                            if (componentType == null)
                            {
                                Debug.LogWarning($"[EntityStatePreserver] Component type not found: {componentTypeName}");
                                continue;
                            }

                            if (!store.HasComponent(entityIndex, componentType))
                            {
                                continue;
                            }

                            var componentValue = capturedValue;

                            // A string entry can only come from an external producer using the
                            // old JSON representation; honour it, but never for our own captures.
                            if (capturedValue is string componentJson)
                            {
                                if (string.IsNullOrEmpty(componentJson))
                                    continue;

                                componentValue = JsonUtility.FromJson(componentJson, componentType);
                            }

                            // Writing a value of the wrong type would corrupt the component
                            // storage rather than throw, so check before handing it over.
                            if (componentValue != null && componentType.IsInstanceOfType(componentValue))
                            {
                                store.SetComponentBoxed(entityIndex, componentType, componentValue);
                            }
                            else if (componentValue != null)
                            {
                                Debug.LogWarning(
                                    $"[EntityStatePreserver] Captured value for {componentTypeName} is a " +
                                    $"{componentValue.GetType().FullName}; skipping restore.");
                                failedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[EntityStatePreserver] Failed to restore component {componentTypeName}: {ex.Message}");
                            failedCount++;
                        }
                    }
                    
                    restoredCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[EntityStatePreserver] Failed to restore entity {entitySnapshot.EntityIndex}: {ex.Message}");
                    failedCount++;
                }
            }
            
            if (failedCount > 0)
            {
                Debug.LogWarning($"[EntityStatePreserver] Restored {restoredCount} entities, {failedCount} failures");
            }
            
            return failedCount == 0;
        }
        
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        
        private static Type FindComponentType(string fullTypeName)
        {
            if (_typeCache.TryGetValue(fullTypeName, out var cachedType))
            {
                return cachedType;
            }
            
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullTypeName);
                    if (type != null && typeof(IComponent).IsAssignableFrom(type))
                    {
                        _typeCache[fullTypeName] = type;
                        return type;
                    }
                }
                catch
                {
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Clears the type cache. Call when assemblies are reloaded.
        /// </summary>
        public static void ClearTypeCache()
        {
            _typeCache.Clear();
        }
    }
}
