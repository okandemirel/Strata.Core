using System;
using System.Collections.Generic;
using System.Linq;
using Strada.Core.DI;

namespace Strada.Core.Editor.DataProviders.Models
{
    /// <summary>
    /// Snapshot of the DI container state.
    /// </summary>
    public class ContainerSnapshot
    {
        public DateTime Timestamp { get; set; }
        public int RegistrationCount { get; set; }
        public int SingletonCount { get; set; }
        public int TransientCount { get; set; }
        public int ScopedCount { get; set; }
        public List<ServiceRegistrationInfo> Registrations { get; set; } = new List<ServiceRegistrationInfo>();
    }

    /// <summary>
    /// Information about a service registration in the DI container.
    /// </summary>
    public class ServiceRegistrationInfo
    {
        public Type ServiceType { get; set; }
        public Type ImplementationType { get; set; }
        public Lifetime Lifetime { get; set; }
        public Type[] Dependencies { get; set; } = Array.Empty<Type>();
        public bool HasInstance { get; set; }
        public string SourceFile { get; set; }
        public int SourceLine { get; set; }
    }

    /// <summary>
    /// Snapshot of the ECS World state.
    /// </summary>
    public class WorldSnapshot
    {
        public DateTime Timestamp { get; set; }
        public int EntityCount { get; set; }
        public int ComponentTypeCount { get; set; }
        public int SystemCount { get; set; }
        public List<EntityInfo> Entities { get; set; } = new List<EntityInfo>();
        public List<SystemInfo> Systems { get; set; } = new List<SystemInfo>();
    }

    /// <summary>
    /// Information about an entity in the ECS World.
    /// </summary>
    public class EntityInfo
    {
        public int Id { get; set; }
        public int Version { get; set; }
        public List<ComponentInfo> Components { get; set; } = new List<ComponentInfo>();
    }

    /// <summary>
    /// Information about a component attached to an entity.
    /// </summary>
    public class ComponentInfo
    {
        public Type ComponentType { get; set; }
        public object Value { get; set; }
        public List<FieldValue> Fields { get; set; } = new List<FieldValue>();
    }

    /// <summary>
    /// A field value within a component.
    /// </summary>
    public class FieldValue
    {
        public string Name { get; set; }
        public Type FieldType { get; set; }
        public object Value { get; set; }
    }

    /// <summary>
    /// Information about a system in the ECS World.
    /// </summary>
    public class SystemInfo
    {
        public Type SystemType { get; set; }
        public string Name { get; set; }
        public UpdatePhase Phase { get; set; }
        public bool IsEnabled { get; set; }
    }

    /// <summary>
    /// Update phase for systems.
    /// </summary>
    public enum UpdatePhase
    {
        PreUpdate,
        Update,
        LateUpdate,
        FixedUpdate
    }

    /// <summary>
    /// Snapshot of the module registry state.
    /// </summary>
    public class ModuleSnapshot
    {
        public DateTime Timestamp { get; set; }
        public int ModuleCount { get; set; }
        public List<ModuleInfoData> Modules { get; set; } = new List<ModuleInfoData>();
        public bool HasCircularDependency { get; set; }
        public List<string> CircularDependencyPath { get; set; }
    }

    /// <summary>
    /// Information about a registered module.
    /// </summary>
    public class ModuleInfoData
    {
        public Type ModuleType { get; set; }
        public string Name { get; set; }
        public int Priority { get; set; }
        public List<Type> Dependencies { get; set; } = new List<Type>();
        public bool IsInitialized { get; set; }
    }

    /// <summary>
    /// Snapshot of the MessageBus state.
    /// </summary>
    public class BusSnapshot
    {
        public DateTime Timestamp { get; set; }
        public bool IsLogging { get; set; }
        public int TotalMessageCount { get; set; }
        public List<MessageLogEntry> LogEntries { get; set; } = new List<MessageLogEntry>();
        public Dictionary<Type, int> SubscriberCounts { get; set; } = new Dictionary<Type, int>();
    }

    /// <summary>
    /// A logged message from MessageBus.
    /// </summary>
    public class MessageLogEntry
    {
        public DateTime Timestamp { get; set; }
        public MessageKind Kind { get; set; }
        public Type MessageType { get; set; }
        public object Payload { get; set; }
        public int SubscriberCount { get; set; }
        public bool HasHandler { get; set; }
        public double ProcessingTimeMs { get; set; }
    }

    /// <summary>
    /// Kind of message in MessageBus.
    /// </summary>
    public enum MessageKind
    {
        Event,
        Command,
        Query
    }

    /// <summary>
    /// Filter for message log entries.
    /// </summary>
    public class MessageFilter
    {
        public string TypePattern { get; set; }
        public MessageKind? Kind { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int MaxResults { get; set; } = 1000;
    }

    /// <summary>
    /// Represents a dependency graph for visualization.
    /// </summary>
    public class DependencyGraph
    {
        public List<DependencyNode> Nodes { get; set; } = new List<DependencyNode>();
        public List<DependencyEdge> Edges { get; set; } = new List<DependencyEdge>();
        public bool HasCycle { get; set; }
        public List<Type> CyclePath { get; set; }

        /// <summary>
        /// Detects cycles in this dependency graph using DFS and marks affected edges.
        /// </summary>
        public void DetectCycles()
        {
            var visited = new HashSet<Type>();
            var recursionStack = new HashSet<Type>();
            var path = new List<Type>();

            // Adjacency built once. The DFS used to rescan the whole edge list at every node
            // (Edges.Where(e => e.Source == current)), making detection O(V*E) — and it also
            // allocated an enumerator per node.
            var adjacency = new Dictionary<Type, List<DependencyEdge>>(Nodes.Count);
            foreach (var edge in Edges)
            {
                if (!adjacency.TryGetValue(edge.Source, out var list))
                {
                    list = new List<DependencyEdge>();
                    adjacency[edge.Source] = list;
                }
                list.Add(edge);
            }

            foreach (var node in Nodes)
            {
                if (!visited.Contains(node.ServiceType))
                {
                    if (DetectCyclesDFS(node.ServiceType, visited, recursionStack, path, adjacency))
                    {
                        HasCycle = true;
                        CyclePath = new List<Type>(path);

                        for (int i = 0; i < path.Count - 1; i++)
                        {
                            var edge = Edges.FirstOrDefault(e =>
                                e.Source == path[i] && e.Target == path[i + 1]);
                            if (edge != null)
                                edge.IsCircular = true;
                        }
                        return;
                    }
                }
            }
        }

        private bool DetectCyclesDFS(Type current,
            HashSet<Type> visited, HashSet<Type> recursionStack, List<Type> path,
            Dictionary<Type, List<DependencyEdge>> adjacency)
        {
            visited.Add(current);
            recursionStack.Add(current);
            path.Add(current);

            if (!adjacency.TryGetValue(current, out var outgoingEdges))
                outgoingEdges = EmptyEdges;

            for (int i = 0; i < outgoingEdges.Count; i++)
            {
                var edge = outgoingEdges[i];
                if (!visited.Contains(edge.Target))
                {
                    if (DetectCyclesDFS(edge.Target, visited, recursionStack, path, adjacency))
                        return true;
                }
                else if (recursionStack.Contains(edge.Target))
                {
                    path.Add(edge.Target);
                    return true;
                }
            }

            path.Remove(current);
            recursionStack.Remove(current);
            return false;
        }

        private static readonly List<DependencyEdge> EmptyEdges = new List<DependencyEdge>();
    }

    /// <summary>
    /// A node in the dependency graph.
    /// </summary>
    public class DependencyNode
    {
        public Type ServiceType { get; set; }
        public Type ImplementationType { get; set; }
        public Lifetime Lifetime { get; set; }
    }

    /// <summary>
    /// An edge in the dependency graph.
    /// </summary>
    public class DependencyEdge
    {
        public Type Source { get; set; }
        public Type Target { get; set; }
        public bool IsCircular { get; set; }
    }

    /// <summary>
    /// A timing sample for a single system execution.
    /// </summary>
    public struct SystemTimingSample
    {
        public Type SystemType;
        public UpdatePhase Phase;
        public double ExecutionTimeMs;
        public long Timestamp;
    }

    /// <summary>
    /// Aggregated metrics for a system over a sample window.
    /// </summary>
    public class SystemMetrics
    {
        public Type SystemType { get; set; }
        public UpdatePhase Phase { get; set; }
        public double AverageMs { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }
        public double StandardDeviation { get; set; }
        public int SampleCount { get; set; }
        public double LastExecutionMs { get; set; }
        
        /// <summary>
        /// Gets the threshold level for this system's last execution time.
        /// </summary>
        /// <param name="config">The threshold configuration to use.</param>
        /// <returns>The threshold level classification.</returns>
        public ThresholdLevel GetThresholdLevel(ThresholdConfiguration config)
        {
            return ThresholdClassifier.Classify(LastExecutionMs, config);
        }
        
        /// <summary>
        /// Gets the threshold level for this system's last execution time.
        /// </summary>
        /// <param name="warningThresholdMs">Warning threshold in milliseconds.</param>
        /// <param name="criticalThresholdMs">Critical threshold in milliseconds.</param>
        /// <returns>The threshold level classification.</returns>
        public ThresholdLevel GetThresholdLevel(double warningThresholdMs, double criticalThresholdMs)
        {
            return ThresholdClassifier.Classify(LastExecutionMs, warningThresholdMs, criticalThresholdMs);
        }
    }

    /// <summary>
    /// Snapshot of profiling data for export.
    /// </summary>
    public class ProfilingSnapshot
    {
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; }
        public List<SystemTimingSampleData> Samples { get; set; } = new List<SystemTimingSampleData>();
        public List<SystemMetricsData> Metrics { get; set; } = new List<SystemMetricsData>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Serializable version of SystemTimingSample for JSON export.
    /// </summary>
    public class SystemTimingSampleData
    {
        public string SystemTypeName { get; set; }
        public string Phase { get; set; }
        public double ExecutionTimeMs { get; set; }
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Serializable version of SystemMetrics for JSON export.
    /// </summary>
    public class SystemMetricsData
    {
        public string SystemTypeName { get; set; }
        public string Phase { get; set; }
        public double AverageMs { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }
        public double StandardDeviation { get; set; }
        public int SampleCount { get; set; }
    }

    /// <summary>
    /// Threshold level for system execution time classification.
    /// Used to determine visual highlighting in the profiler.
    /// </summary>
    public enum ThresholdLevel
    {
        /// <summary>
        /// Execution time is within acceptable limits.
        /// </summary>
        Normal,
        
        /// <summary>
        /// Execution time exceeds warning threshold but is below critical.
        /// </summary>
        Warning,
        
        /// <summary>
        /// Execution time exceeds critical threshold.
        /// </summary>
        Critical
    }

    /// <summary>
    /// Configuration for threshold-based highlighting.
    /// </summary>
    public class ThresholdConfiguration
    {
        /// <summary>
        /// Warning threshold in milliseconds. Default is 1.0ms.
        /// </summary>
        public double WarningThresholdMs { get; set; } = 1.0;
        
        /// <summary>
        /// Critical threshold in milliseconds. Default is 5.0ms.
        /// </summary>
        public double CriticalThresholdMs { get; set; } = 5.0;
        
        /// <summary>
        /// Creates a default threshold configuration.
        /// </summary>
        public static ThresholdConfiguration Default => new ThresholdConfiguration();
    }

    /// <summary>
    /// Utility class for classifying execution times against thresholds.
    /// </summary>
    public static class ThresholdClassifier
    {
        /// <summary>
        /// Classifies an execution time against the given thresholds.
        /// </summary>
        /// <param name="executionTimeMs">The execution time in milliseconds.</param>
        /// <param name="warningThresholdMs">The warning threshold in milliseconds.</param>
        /// <param name="criticalThresholdMs">The critical threshold in milliseconds.</param>
        /// <returns>The threshold level classification.</returns>
        public static ThresholdLevel Classify(double executionTimeMs, double warningThresholdMs, double criticalThresholdMs)
        {
            if (executionTimeMs >= criticalThresholdMs)
                return ThresholdLevel.Critical;
            if (executionTimeMs >= warningThresholdMs)
                return ThresholdLevel.Warning;
            return ThresholdLevel.Normal;
        }
        
        /// <summary>
        /// Classifies an execution time using a threshold configuration.
        /// </summary>
        /// <param name="executionTimeMs">The execution time in milliseconds.</param>
        /// <param name="config">The threshold configuration.</param>
        /// <returns>The threshold level classification.</returns>
        public static ThresholdLevel Classify(double executionTimeMs, ThresholdConfiguration config)
        {
            return Classify(executionTimeMs, config.WarningThresholdMs, config.CriticalThresholdMs);
        }
        
    }
}
