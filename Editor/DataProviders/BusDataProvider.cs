using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Strada.Core.Communication;
using Strada.Core.ECS.World;
using Strada.Core.Editor.DataProviders.Models;
using UnityEngine;

namespace Strada.Core.Editor.DataProviders
{
    /// <summary>
    /// Provides access to MessageBus message data for editor tools.
    /// Hooks into MessageBus for message interception and logging.
    /// </summary>
    public class BusDataProvider : EditorDataProviderBase<BusSnapshot>, IBusDataProvider
    {
        private static BusDataProvider _instance;
        private readonly List<MessageLogEntry> _logEntries = new List<MessageLogEntry>();
        private readonly object _logLock = new object();
        private bool _isLogging;
        private const int MaxLogEntries = 1000;

        /// <summary>
        /// Gets the singleton instance of the BusDataProvider.
        /// </summary>
        public static BusDataProvider Instance => _instance ??= new BusDataProvider();

        private BusDataProvider() { }

        /// <summary>
        /// Gets whether the MessageBus is available.
        /// </summary>
        public override bool IsAvailable
        {
            get
            {
                if (!Application.isPlaying) return false;
                return World.Current?.EventBus != null;
            }
        }

        /// <summary>
        /// Gets whether message logging is currently active.
        /// </summary>
        public bool IsLogging => _isLogging;

        /// <summary>
        /// Starts logging messages from MessageBus.
        /// </summary>
        public void StartLogging()
        {
            if (_isLogging) return;
            _isLogging = true;
        }

        /// <summary>
        /// Stops logging messages.
        /// </summary>
        public void StopLogging()
        {
            _isLogging = false;
        }

        /// <summary>
        /// Clears all logged messages.
        /// </summary>
        public void ClearLog()
        {
            lock (_logLock)
            {
                _logEntries.Clear();
            }
            RaiseDataChanged();
        }

        /// <summary>
        /// Gets log entries matching the specified filter.
        /// </summary>
        public IReadOnlyList<MessageLogEntry> GetLogEntries(MessageFilter filter = null)
        {
            lock (_logLock)
            {
                var results = new List<MessageLogEntry>();
                CollectFilteredEntries(results, filter);
                return results;
            }
        }

        /// <summary>
        /// Populates the provided list with log entries matching the filter, avoiding new allocations.
        /// </summary>
        public void GetLogEntriesNonAlloc(List<MessageLogEntry> results, MessageFilter filter = null)
        {
            lock (_logLock)
            {
                results.Clear();
                CollectFilteredEntries(results, filter);
            }
        }

        private void CollectFilteredEntries(List<MessageLogEntry> results, MessageFilter filter)
        {
            if (filter == null)
            {
                results.AddRange(_logEntries);
                return;
            }

            var filtered = _logEntries.AsEnumerable();

            if (filter.Kind.HasValue)
                filtered = filtered.Where(e => e.Kind == filter.Kind.Value);

            if (!string.IsNullOrEmpty(filter.TypePattern))
            {
                filtered = filtered.Where(e =>
                    MatchesTypePattern(e.MessageType?.Name, filter.TypePattern));
            }

            if (filter.StartTime.HasValue)
                filtered = filtered.Where(e => e.Timestamp >= filter.StartTime.Value);

            if (filter.EndTime.HasValue)
                filtered = filtered.Where(e => e.Timestamp <= filter.EndTime.Value);

            foreach (var entry in filtered.Take(filter.MaxResults))
            {
                results.Add(entry);
            }
        }

        /// <summary>
        /// Checks if a message type name matches the filter pattern.
        /// Supports wildcards (* for any characters, ? for single character) and partial matches.
        /// </summary>
        /// <param name="typeName">The type name to check.</param>
        /// <param name="pattern">The filter pattern.</param>
        /// <returns>True if the type matches the pattern.</returns>
        public static bool MatchesTypePattern(string typeName, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (string.IsNullOrEmpty(typeName)) return false;

            if (pattern.Contains("*") || pattern.Contains("?"))
            {
                try
                {
                    var regexPattern = "^" + Regex.Escape(pattern)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";
                    return Regex.IsMatch(typeName, regexPattern, RegexOptions.IgnoreCase,
                        TimeSpan.FromMilliseconds(100));
                }
                catch
                {
                    return typeName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            return typeName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Gets the subscriber count for a specific event type.
        /// </summary>
        public int GetSubscriberCount(Type messageType)
        {
            if (!IsAvailable) return 0;

            try
            {
                var bus = World.Current.EventBus;

                var method = typeof(EventBus).GetMethod("GetSubscriberCount");
                if (method != null)
                {
                    var genericMethod = method.MakeGenericMethod(messageType);
                    return (int)genericMethod.Invoke(bus, null);
                }
            }
            catch { }

            return 0;
        }

        /// <summary>
        /// Logs a message entry. Called by message interceptors.
        /// </summary>
        public void LogMessage(MessageLogEntry entry)
        {
            if (!_isLogging) return;

            lock (_logLock)
            {
                _logEntries.Add(entry);

                if (_logEntries.Count > MaxLogEntries)
                {
                    var excess = _logEntries.Count - MaxLogEntries;
                    _logEntries.RemoveRange(0, excess);
                }
            }

            RaiseDataChanged();
        }

        /// <summary>
        /// Logs an event publication.
        /// </summary>
        public void LogEvent<T>(T evt, int subscriberCount) where T : struct
        {
            LogMessage(new MessageLogEntry
            {
                Timestamp = DateTime.Now,
                Kind = MessageKind.Event,
                MessageType = typeof(T),
                Payload = evt,
                SubscriberCount = subscriberCount,
                HasHandler = subscriberCount > 0
            });
        }

        /// <summary>
        /// Logs a command send.
        /// </summary>
        public void LogCommand<T>(T command, bool hasHandler) where T : struct
        {
            LogMessage(new MessageLogEntry
            {
                Timestamp = DateTime.Now,
                Kind = MessageKind.Command,
                MessageType = typeof(T),
                Payload = command,
                HasHandler = hasHandler
            });
        }

        /// <summary>
        /// Logs a query execution.
        /// </summary>
        public void LogQuery<TQuery, TResult>(TQuery query, double processingTimeMs)
            where TQuery : struct
        {
            LogMessage(new MessageLogEntry
            {
                Timestamp = DateTime.Now,
                Kind = MessageKind.Query,
                MessageType = typeof(TQuery),
                Payload = query,
                HasHandler = true,
                ProcessingTimeMs = processingTimeMs
            });
        }

        /// <summary>
        /// Checks if a command type has a registered handler.
        /// </summary>
        public bool HasCommandHandler(Type commandType)
        {
            if (!IsAvailable) return false;

            try
            {
                var bus = World.Current.EventBus;
                var busType = typeof(EventBus);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;

                var handlersField = busType.GetField("_commandHandlers", flags);
                var maxIdField = busType.GetField("_maxCommandId", flags);

                if (handlersField == null || maxIdField == null)
                    return false;

                var handlers = (object[])handlersField.GetValue(bus);
                var maxId = (int)maxIdField.GetValue(bus);

                var typeIdType = busType.GetNestedType("CommandTypeId`1", BindingFlags.NonPublic)
                    ?.MakeGenericType(commandType);

                if (typeIdType == null) return false;

                var idField = typeIdType.GetField("Id", BindingFlags.Public | BindingFlags.Static);
                if (idField == null) return false;

                var id = (int)idField.GetValue(null);
                return id <= maxId && handlers[id] != null;
            }
            catch
            {
                return false;
            }
        }

        protected override BusSnapshot FetchData()
        {
            var snapshot = new BusSnapshot
            {
                Timestamp = DateTime.Now,
                IsLogging = _isLogging,
                SubscriberCounts = new Dictionary<Type, int>()
            };

            lock (_logLock)
            {
                snapshot.TotalMessageCount = _logEntries.Count;
                snapshot.LogEntries = _logEntries.ToList();
            }

            return snapshot;
        }

        protected override void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
        {
            base.OnPlayModeStateChanged(state);

            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
            {
                lock (_logLock)
                {
                    _logEntries.Clear();
                }
                _isLogging = false;
            }
        }

        public struct SubscriberInfo
        {
            public object Target;
            public MethodInfo Method;
        }

        public List<SubscriberInfo> GetSubscriberDetails(Type eventType)
        {
            var results = new List<SubscriberInfo>();
            if (!IsAvailable) return results;

            try
            {
                var bus = World.Current.EventBus;
                var busType = typeof(EventBus);
                
                var channelsField = busType.GetField("_eventChannels", BindingFlags.NonPublic | BindingFlags.Instance);
                var channels = channelsField?.GetValue(bus) as object[];
                if (channels == null) return results;

            
                var typeIdType = busType.GetNestedType("EventTypeId`1", BindingFlags.NonPublic)?.MakeGenericType(eventType);
                var idField = typeIdType?.GetField("Id", BindingFlags.Public | BindingFlags.Static);
                if (idField == null) return results;

                var id = (int)idField.GetValue(null);
                if (id >= channels.Length || channels[id] == null) return results;

                var channel = channels[id];
                var channelType = channel.GetType();
                
                var handlersField = channelType.GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance);
                var countField = channelType.GetField("_count", BindingFlags.NonPublic | BindingFlags.Instance);
                
                var handlers = handlersField?.GetValue(channel) as Array;
                var count = (int)(countField?.GetValue(channel) ?? 0);

                if (handlers != null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var deleg = handlers.GetValue(i) as Delegate;
                        if (deleg != null)
                        {
                            results.Add(new SubscriberInfo
                            {
                                Target = deleg.Target,
                                Method = deleg.Method
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BusDataProvider] Failed to get subscribers for {eventType.Name}: {ex.Message}");
            }

            return results;
        }
    }

    /// <summary>
    /// Extended interface for bus data provider.
    /// </summary>
    public interface IBusDataProvider : IEditorDataProvider<BusSnapshot>
    {
        void StartLogging();
        void StopLogging();
        IReadOnlyList<MessageLogEntry> GetLogEntries(MessageFilter filter);
        void GetLogEntriesNonAlloc(List<MessageLogEntry> results, MessageFilter filter);
        int GetSubscriberCount(Type messageType);
        List<BusDataProvider.SubscriberInfo> GetSubscriberDetails(Type eventType);
    }
}
