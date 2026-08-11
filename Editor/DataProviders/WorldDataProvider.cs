using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.World;
using Strada.Core.Editor.DataProviders.Models;
using UnityEngine;

// Both Strada.Core.ECS.World and the Models namespace above declare an UpdatePhase, so an
// unqualified reference to either is ambiguous in this file.
using RuntimeUpdatePhase = Strada.Core.ECS.World.UpdatePhase;

namespace Strada.Core.Editor.DataProviders
{
    /// <summary>
    /// Provides access to ECS World data for editor tools.
    /// Connects to World.Current at runtime.
    /// </summary>
    public class WorldDataProvider : EditorDataProviderBase<WorldSnapshot>, IWorldDataProvider
    {
        private static WorldDataProvider _instance;

        /// <summary>
        /// Gets the singleton instance of the WorldDataProvider.
        /// </summary>
        public static WorldDataProvider Instance => _instance ??= new WorldDataProvider();

        private WorldDataProvider() { }

        /// <summary>
        /// Gets whether the World is available (Play Mode with active World).
        /// </summary>
        public override bool IsAvailable =>
            Application.isPlaying && World.Current != null;

        /// <summary>
        /// Gets all entity IDs in the current world.
        /// </summary>
        public IEnumerable<int> GetEntityIds()
        {
            if (!IsAvailable) return Enumerable.Empty<int>();

            try
            {
                return World.Current.EntityManager.GetAllEntities();
            }
            catch
            {
                return Enumerable.Empty<int>();
            }
        }

        /// <summary>
        /// Gets all component types registered in the world.
        /// </summary>
        public IEnumerable<Type> GetComponentTypes()
        {
            if (!IsAvailable) return Enumerable.Empty<Type>();

            try
            {
                return World.Current.EntityManager.Store.GetComponentTypes();
            }
            catch
            {
                return Enumerable.Empty<Type>();
            }
        }

        /// <summary>
        /// Gets a component value as a boxed object.
        /// </summary>
        public object GetComponentBoxed(int entityId, Type componentType)
        {
            if (!IsAvailable) return null;

            try
            {
                return World.Current.EntityManager.Store.GetComponentBoxed(entityId, componentType);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets a component value from a boxed object.
        /// </summary>
        public void SetComponentBoxed(int entityId, Type componentType, object value)
        {
            if (!IsAvailable) return;

            try
            {
                World.Current.EntityManager.Store.SetComponentBoxed(entityId, componentType, value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldDataProvider] Failed to set component: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if an entity exists.
        /// </summary>
        public bool EntityExists(int entityId)
        {
            if (!IsAvailable) return false;

            try
            {
                // O(1). This previously materialised the full entity list and scanned it
                // linearly on every call, so a caller looping over N entities paid O(N^2)
                // time and N list allocations.
                return World.Current.EntityManager.ExistsIndex(entityId);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets all components attached to an entity.
        /// </summary>
        public IEnumerable<ComponentInfo> GetEntityComponents(int entityId)
        {
            if (!IsAvailable) yield break;

            var componentTypes = GetComponentTypes();
            var store = World.Current.EntityManager.Store;

            foreach (var componentType in componentTypes)
            {
                if (store.HasComponent(entityId, componentType))
                {
                    var value = store.GetComponentBoxed(entityId, componentType);
                    yield return new ComponentInfo
                    {
                        ComponentType = componentType,
                        Value = value,
                        Fields = ExtractFieldValues(componentType, value)
                    };
                }
            }
        }

        protected override WorldSnapshot FetchData()
        {
            var world = World.Current;
            if (world == null) return null;

            var snapshot = new WorldSnapshot
            {
                Timestamp = DateTime.Now,
                EntityCount = world.EntityManager.EntityCount,
                ComponentTypeCount = world.EntityManager.Store.GetComponentTypes().Count(),
                Entities = new List<EntityInfo>(),
                Systems = new List<Models.SystemInfo>()
            };

            foreach (var entityId in world.EntityManager.GetAllEntities())
            {
                var entityInfo = new EntityInfo
                {
                    Id = entityId,
                    Version = GetEntityVersion(entityId),
                    Components = GetEntityComponents(entityId).ToList()
                };
                snapshot.Entities.Add(entityInfo);
            }

            snapshot.Systems = ExtractSystemInfo(world);
            snapshot.SystemCount = snapshot.Systems.Count;

            return snapshot;
        }

        // EntityManager stores versions in a NativeArray<int> named _versions. The field this
        // used to reflect, "_entityVersions", has never existed, so the lookup always returned
        // null and every displayed Version was silently 0.
        private static readonly FieldInfo EntityVersionsField =
            typeof(EntityManager).GetField("_versions", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool _versionFieldWarningLogged;

        private int GetEntityVersion(int entityId)
        {
            if (EntityVersionsField == null)
            {
                // Logged once: a rename in EntityManager must surface here rather than degrade
                // every entity's version to zero forever.
                if (!_versionFieldWarningLogged)
                {
                    _versionFieldWarningLogged = true;
                    Debug.LogWarning("[WorldDataProvider] EntityManager._versions not found; entity versions will read 0.");
                }
                return 0;
            }

            try
            {
                var entityManager = World.Current.EntityManager;

                if (EntityVersionsField.GetValue(entityManager) is Unity.Collections.NativeArray<int> versions)
                {
                    if (versions.IsCreated && entityId >= 0 && entityId < versions.Length)
                        return versions[entityId];
                }
            }
            catch (Exception ex)
            {
                if (!_versionFieldWarningLogged)
                {
                    _versionFieldWarningLogged = true;
                    Debug.LogWarning($"[WorldDataProvider] Failed to read entity versions: {ex.Message}");
                }
            }

            return 0;
        }

        private List<FieldValue> ExtractFieldValues(Type componentType, object value)
        {
            var fields = new List<FieldValue>();
            if (value == null) return fields;

            try
            {
                foreach (var field in componentType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    fields.Add(new FieldValue
                    {
                        Name = field.Name,
                        FieldType = field.FieldType,
                        Value = field.GetValue(value)
                    });
                }
            }
            catch { }

            return fields;
        }

        private static bool _systemsFieldWarningLogged;

        private List<Models.SystemInfo> ExtractSystemInfo(World world)
        {
            var systems = new List<Models.SystemInfo>();

            try
            {
                var scheduler = world.SystemScheduler;
                if (scheduler == null)
                    return systems;

                // SystemScheduler groups systems in a List<ISystem>[] indexed by UpdatePhase.
                // The field previously reflected here, "_systems", does not exist, so the list
                // and SystemCount were always empty. Reading the per-phase array also gives us
                // the real phase instead of hardcoding Update.
                var systemsByPhaseField = scheduler.GetType().GetField("_systemsByPhase",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (!(systemsByPhaseField?.GetValue(scheduler) is System.Collections.IList[] systemsByPhase))
                {
                    if (!_systemsFieldWarningLogged)
                    {
                        _systemsFieldWarningLogged = true;
                        Debug.LogWarning("[WorldDataProvider] SystemScheduler._systemsByPhase not found; the system list will be empty.");
                    }
                    return systems;
                }

                for (int phaseIndex = 0; phaseIndex < systemsByPhase.Length; phaseIndex++)
                {
                    var phaseSystems = systemsByPhase[phaseIndex];
                    if (phaseSystems == null) continue;

                    var phase = ToEditorPhase((RuntimeUpdatePhase)phaseIndex);

                    foreach (var system in phaseSystems)
                    {
                        if (system == null) continue;

                        systems.Add(new Models.SystemInfo
                        {
                            SystemType = system.GetType(),
                            Name = system.GetType().Name,
                            Phase = phase,
                            IsEnabled = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_systemsFieldWarningLogged)
                {
                    _systemsFieldWarningLogged = true;
                    Debug.LogWarning($"[WorldDataProvider] Failed to read the system list: {ex.Message}");
                }
            }

            return systems;
        }

        private static Models.UpdatePhase ToEditorPhase(RuntimeUpdatePhase phase)
        {
            // The runtime and editor enums do not share ordinals: runtime Initialization has no
            // editor counterpart beyond PreUpdate.
            return phase switch
            {
                RuntimeUpdatePhase.Initialization => Models.UpdatePhase.PreUpdate,
                RuntimeUpdatePhase.LateUpdate => Models.UpdatePhase.LateUpdate,
                RuntimeUpdatePhase.FixedUpdate => Models.UpdatePhase.FixedUpdate,
                _ => Models.UpdatePhase.Update
            };
        }
    }

    /// <summary>
    /// Extended interface for world data provider.
    /// </summary>
    public interface IWorldDataProvider : IEditorDataProvider<WorldSnapshot>
    {
        IEnumerable<int> GetEntityIds();
        IEnumerable<Type> GetComponentTypes();
        object GetComponentBoxed(int entityId, Type componentType);
        void SetComponentBoxed(int entityId, Type componentType, object value);
    }
}
