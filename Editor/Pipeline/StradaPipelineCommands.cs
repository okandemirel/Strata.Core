using System;
using System.Collections.Generic;
using System.Linq;
using Strada.Core.Bootstrap;
using Strada.Core.Editor.DataProviders;
using Unity.Pipeline.Commands;

namespace Strada.Core.Editor.Pipeline
{
    /// <summary>
    /// Exposes Strada's runtime state to the Unity CLI, so a terminal or an agent can inspect
    /// a running game without stopping play mode or attaching a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This assembly only compiles when <c>com.unity.pipeline</c> is installed: the asmdef
    /// carries a <c>versionDefines</c> entry that raises STRADA_PIPELINE, and a
    /// <c>defineConstraints</c> on the same define. With the package absent the whole
    /// assembly is skipped, so its references cannot fail to resolve and consumers who do not
    /// want an experimental package are unaffected. Nothing in Runtime/ or the main Editor
    /// assembly depends on this.
    /// </para>
    /// <para>
    /// Every command here is READ-ONLY. Driving a live world from outside the frame loop is a
    /// good way to corrupt it — component storages are not thread-safe, and structural changes
    /// during iteration are exactly what the ECS query guard exists to catch. Reads are
    /// answered on the main thread (<c>MainThreadRequired</c> defaults to true) and report
    /// state; they do not mutate it.
    /// </para>
    /// </remarks>
    public static class StradaPipelineCommands
    {
        [Serializable]
        public class WorldStats
        {
            public bool WorldAvailable;
            public int EntityCount;
            public int ComponentTypeCount;
            public long NativeBytes;
            public double NativeBytesPerEntity;
            public string[] ComponentTypes;
            public string Note;
        }

        [Serializable]
        public class SystemInfo
        {
            public string Name;
            public double LastExecutionMs;
        }

        [Serializable]
        public class SystemsResponse
        {
            public bool WorldAvailable;
            public int Count;
            public SystemInfo[] Systems;
        }

        [Serializable]
        public class ServiceInfo
        {
            public string ServiceType;
            public string ImplementationType;
            public string Lifetime;
        }

        [Serializable]
        public class ContainerResponse
        {
            public bool ContainerAvailable;
            public int RegistrationCount;
            public bool HasCircularDependency;
            public string[] CyclePath;
            public ServiceInfo[] Registrations;
        }

        /// <summary>
        /// Entity and component-storage totals for the live world.
        /// </summary>
        /// <remarks>
        /// NativeBytes comes from the storages' own allocated capacity. It is reported
        /// separately from anything the managed heap would show because component data lives
        /// in NativeArrays, which the GC cannot see at all — a per-entity memory figure taken
        /// from GC statistics measures managed overhead and misses the storage entirely.
        /// </remarks>
        [CliCommand("strada_world", "Entity count, component types and native storage bytes for the running Strada world")]
        public static WorldStats GetWorldStats()
        {
            var provider = WorldDataProvider.Instance;
            if (!provider.IsAvailable)
            {
                return new WorldStats
                {
                    WorldAvailable = false,
                    Note = "No world is running. Enter play mode with a GameBootstrapper in the scene."
                };
            }

            var types = provider.GetComponentTypes().ToArray();
            int entityCount = provider.GetEntityIds().Count();
            long nativeBytes = GameBootstrapper.World?.EntityManager?.Store?.AllocatedBytes ?? 0;

            return new WorldStats
            {
                WorldAvailable = true,
                EntityCount = entityCount,
                ComponentTypeCount = types.Length,
                NativeBytes = nativeBytes,
                NativeBytesPerEntity = entityCount > 0 ? nativeBytes / (double)entityCount : 0,
                ComponentTypes = types.Select(t => t.Name).ToArray()
            };
        }

        /// <summary>
        /// The systems the running world executed, with how long each took on its last frame.
        /// </summary>
        /// <remarks>
        /// Sorted slowest first, because the reason to ask this question from a terminal is
        /// almost always "what is eating the frame". The timings are only populated in
        /// Editor/Development builds, where the scheduler instruments each phase.
        /// </remarks>
        [CliCommand("strada_systems", "List the ECS systems on the running Strada world with their last frame time, slowest first")]
        public static SystemsResponse GetSystems()
        {
            var scheduler = GameBootstrapper.World?.SystemScheduler;
            if (scheduler == null)
                return new SystemsResponse { WorldAvailable = false, Count = 0, Systems = Array.Empty<SystemInfo>() };

            var systems = scheduler.LastExecutionTimes
                .Select(kvp => new SystemInfo { Name = kvp.Key.Name, LastExecutionMs = kvp.Value })
                .OrderByDescending(s => s.LastExecutionMs)
                .ToArray();

            return new SystemsResponse
            {
                WorldAvailable = true,
                Count = systems.Length,
                Systems = systems
            };
        }

        /// <summary>
        /// DI registrations, plus the result of the container's cycle check.
        /// </summary>
        [CliCommand("strada_container", "List Strada DI container registrations and report any dependency cycle")]
        public static ContainerResponse GetContainer(
            [CliArg("filter", "Only include registrations whose service type name contains this substring")] string filter = "")
        {
            var provider = ContainerDataProvider.Instance;
            if (!provider.IsAvailable)
                return new ContainerResponse { ContainerAvailable = false, Registrations = Array.Empty<ServiceInfo>() };

            var registrations = provider.GetRegistrations();
            var selected = string.IsNullOrWhiteSpace(filter)
                ? registrations
                : registrations.Where(r =>
                      r.ServiceType != null &&
                      r.ServiceType.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            bool hasCycle = provider.HasCircularDependency(out var cycle);

            return new ContainerResponse
            {
                ContainerAvailable = true,
                RegistrationCount = selected.Count,
                HasCircularDependency = hasCycle,
                CyclePath = hasCycle && cycle != null ? cycle.Select(t => t.Name).ToArray() : Array.Empty<string>(),
                Registrations = selected.Select(r => new ServiceInfo
                {
                    ServiceType = r.ServiceType?.Name,
                    ImplementationType = r.ImplementationType?.Name,
                    Lifetime = r.Lifetime.ToString()
                }).ToArray()
            };
        }
    }
}
