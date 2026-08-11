using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Strada.Core.Logging
{
    /// <summary>
    /// Module-aware debug logging system for Strada.
    /// Provides methods mirroring Unity's Debug API with module categorization.
    /// </summary>
    public static class StradaLog
    {
        private static readonly object _lock = new object();
        private static readonly List<LogEntry> _logBuffer = new List<LogEntry>();
        private static int _bufferHead;
        private static int _totalCount;

        [ThreadStatic]
        private static StringBuilder t_stringBuilder;
        private const int StringBuilderCapacity = 256;

        /// <summary>
        /// Event raised when a new log entry is added.
        /// </summary>
        public static event Action<LogEntry> OnLogAdded;

        /// <summary>
        /// Gets the current log entries.
        /// </summary>
        public static IReadOnlyList<LogEntry> LogEntries
        {
            get
            {
                lock (_lock)
                {
                    var result = new List<LogEntry>(_logBuffer.Count);
                    for (int i = 0; i < _logBuffer.Count; i++)
                    {
                        result.Add(_logBuffer[i]);
                    }
                    return result;
                }
            }
        }

        /// <summary>
        /// Gets the total number of logs recorded since startup.
        /// </summary>
        public static int TotalLogCount
        {
            get
            {
                lock (_lock)
                {
                    return _totalCount;
                }
            }
        }

        /// <summary>
        /// Logs an info message to the General module.
        /// </summary>
        public static void Log(object message)
        {
            Log(message, LogModule.General);
        }

        /// <summary>
        /// Logs an info message to a specific module.
        /// </summary>
        public static void Log(object message, LogModule module)
        {
            LogInternal(message?.ToString() ?? "null", LogType.Info, module, false);
        }

        /// <summary>
        /// Logs a warning message to the General module.
        /// </summary>
        public static void LogWarning(object message)
        {
            LogWarning(message, LogModule.General);
        }

        /// <summary>
        /// Logs a warning message to a specific module.
        /// </summary>
        public static void LogWarning(object message, LogModule module)
        {
            LogInternal(message?.ToString() ?? "null", LogType.Warning, module, false);
        }

        /// <summary>
        /// Logs an error message to the General module.
        /// </summary>
        public static void LogError(object message)
        {
            LogError(message, LogModule.General);
        }

        /// <summary>
        /// Logs an error message to a specific module.
        /// </summary>
        public static void LogError(object message, LogModule module)
        {
            LogInternal(message?.ToString() ?? "null", LogType.Error, module, false);
        }

        /// <summary>
        /// Logs an exception to the General module.
        /// </summary>
        public static void LogException(Exception exception)
        {
            LogException(exception, LogModule.General);
        }

        /// <summary>
        /// Logs an exception to a specific module.
        /// </summary>
        public static void LogException(Exception exception, LogModule module)
        {
            var message = exception != null
                ? $"{exception.GetType().Name}: {exception.Message}"
                : "null exception";
            LogInternal(message, LogType.Exception, module, false);
        }

        /// <summary>
        /// Logs a deep (detailed) message for flow analysis.
        /// Only active when DeepLogsEnabled is true in settings.
        /// </summary>
        public static void LogDeep(object message, LogModule module)
        {
            if (!StradaLogSettings.Instance.DeepLogsEnabled)
                return;

            LogInternal(message?.ToString() ?? "null", LogType.Info, module, true);
        }

        /// <summary>
        /// Clears all log entries from the buffer.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _logBuffer.Clear();
                _bufferHead = 0;
            }
        }

        /// <summary>
        /// Gets log entries filtered by module.
        /// </summary>
        public static List<LogEntry> GetEntriesByModule(LogModule module)
        {
            var result = new List<LogEntry>();
            lock (_lock)
            {
                for (int i = 0; i < _logBuffer.Count; i++)
                {
                    if (_logBuffer[i].Module == module)
                        result.Add(_logBuffer[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Gets log entries filtered by type.
        /// </summary>
        public static List<LogEntry> GetEntriesByType(LogType type)
        {
            var result = new List<LogEntry>();
            lock (_lock)
            {
                for (int i = 0; i < _logBuffer.Count; i++)
                {
                    if (_logBuffer[i].Type == type)
                        result.Add(_logBuffer[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Gets the count of log entries for a specific module.
        /// </summary>
        public static int GetCountByModule(LogModule module)
        {
            int count = 0;
            lock (_lock)
            {
                for (int i = 0; i < _logBuffer.Count; i++)
                {
                    if (_logBuffer[i].Module == module)
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Gets the count of log entries for a specific type.
        /// </summary>
        public static int GetCountByType(LogType type)
        {
            int count = 0;
            lock (_lock)
            {
                for (int i = 0; i < _logBuffer.Count; i++)
                {
                    if (_logBuffer[i].Type == type)
                        count++;
                }
            }
            return count;
        }

        private static void LogInternal(string message, LogType type, LogModule module, bool isDeepLog)
        {
            // Environment.StackTrace is a full managed stack walk plus a multi-KB string.
            // It used to run on EVERY log call, including calls that were about to be
            // discarded. Only diagnostic severities actually need it, and only in builds
            // where someone can read it.
            string stackTrace = ShouldCaptureStackTrace(type) ? Environment.StackTrace : string.Empty;
            var entry = new LogEntry(message, type, module, stackTrace, isDeepLog);

            AddToBuffer(entry);

            if (StradaLogSettings.Instance.ShowLogs)
            {
                var formattedMessage = FormatMessage(message, module, isDeepLog);
                OutputToUnityConsole(formattedMessage, type);
            }

            try
            {
                OnLogAdded?.Invoke(entry);
            }
            catch (Exception ex)
            {
                // A misbehaving log subscriber must not break logging, but swallowing it
                // silently hid real errors. Report it through Unity directly (not through
                // StradaLog, which would re-enter this method).
                UnityEngine.Debug.LogWarning($"[Strada] A StradaLog.OnLogAdded subscriber threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Stack traces are captured for diagnostic severities only, and only where they can
        /// be read. In a release player build this returns false for every severity, which
        /// also keeps developer paths and internal type layout out of shipped logs.
        /// </summary>
        private static bool ShouldCaptureStackTrace(LogType type)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return type != LogType.Info;
#else
            return false;
#endif
        }

        private static void AddToBuffer(LogEntry entry)
        {
            lock (_lock)
            {
                var maxEntries = StradaLogSettings.Instance.MaxLogEntries;

                if (_logBuffer.Count < maxEntries)
                {
                    _logBuffer.Add(entry);
                }
                else
                {
                    _logBuffer[_bufferHead] = entry;
                    _bufferHead = (_bufferHead + 1) % maxEntries;
                }

                _totalCount++;
            }
        }

        private static string FormatMessage(string message, LogModule module, bool isDeepLog)
        {
            var sb = t_stringBuilder;
            if (sb == null)
            {
                sb = new StringBuilder(StringBuilderCapacity);
                t_stringBuilder = sb;
            }
            else
            {
                sb.Clear();
            }

            sb.Append("[Strada][");
            sb.Append(module.ToString());
            sb.Append(']');
            if (isDeepLog)
            {
                sb.Append("[DEEP]");
            }
            sb.Append(' ');
            sb.Append(message);

            return sb.ToString();
        }

        private static void OutputToUnityConsole(string message, LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    Debug.LogWarning(message);
                    break;
                case LogType.Error:
                case LogType.Exception:
                    Debug.LogError(message);
                    break;
                default:
                    Debug.Log(message);
                    break;
            }
        }
    }
}
