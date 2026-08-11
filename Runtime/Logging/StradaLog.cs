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
                    int count = _logBuffer.Count;
                    var result = new List<LogEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        result.Add(_logBuffer[OldestFirstIndex(i, count)]);
                    }
                    return result;
                }
            }
        }

        /// <summary>
        /// Maps a chronological position onto the physical slot that holds it.
        /// </summary>
        /// <remarks>
        /// The buffer is circular: once it is full AddToBuffer overwrites the slot at
        /// _bufferHead and advances it, so _bufferHead holds the OLDEST entry and raw slot
        /// order reads [newest block][oldest block]. Walking 0..Count-1 therefore returned
        /// entries out of chronological order after the first wrap. Callers must hold _lock.
        /// </remarks>
        private static int OldestFirstIndex(int i, int count)
        {
            // A MaxLogEntries change can shrink the list under a stale head; fall back to
            // physical order rather than indexing outside it.
            int head = _bufferHead < count ? _bufferHead : 0;
            int index = head + i;
            return index < count ? index : index - count;
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
        /// Logs an info message carrying a value type, without boxing it.
        /// </summary>
        /// <remarks>
        /// The object-typed overloads box every value-type argument at the call site. These
        /// generic siblings bind the argument by value and reach ToString through a constrained
        /// call, so logging an int, a float or an enum costs no allocation beyond the string.
        /// </remarks>
        public static void Log<T>(T message, LogModule module) where T : struct
        {
            LogInternal(message.ToString(), LogType.Info, module, false);
        }

        /// <summary>
        /// Logs an info message carrying a value type to the General module, without boxing it.
        /// </summary>
        public static void Log<T>(T message) where T : struct
        {
            LogInternal(message.ToString(), LogType.Info, LogModule.General, false);
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
        /// Logs a warning carrying a value type, without boxing it.
        /// </summary>
        public static void LogWarning<T>(T message, LogModule module) where T : struct
        {
            LogInternal(message.ToString(), LogType.Warning, module, false);
        }

        /// <summary>
        /// Logs a warning carrying a value type to the General module, without boxing it.
        /// </summary>
        public static void LogWarning<T>(T message) where T : struct
        {
            LogInternal(message.ToString(), LogType.Warning, LogModule.General, false);
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
        /// Logs an error carrying a value type, without boxing it.
        /// </summary>
        public static void LogError<T>(T message, LogModule module) where T : struct
        {
            LogInternal(message.ToString(), LogType.Error, module, false);
        }

        /// <summary>
        /// Logs an error carrying a value type to the General module, without boxing it.
        /// </summary>
        public static void LogError<T>(T message) where T : struct
        {
            LogInternal(message.ToString(), LogType.Error, LogModule.General, false);
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
        /// Logs a deep message built on demand, only when deep logging is enabled.
        /// </summary>
        /// <remarks>
        /// The eager overloads have already interpolated (and boxed) their argument by the time
        /// the DeepLogsEnabled check runs, so a disabled deep log still costs a string. Passing a
        /// factory defers that work until it is known to be needed.
        /// </remarks>
        public static void LogDeep(Func<string> messageFactory, LogModule module)
        {
            if (messageFactory == null)
                return;

            if (!StradaLogSettings.Instance.DeepLogsEnabled)
                return;

            LogInternal(messageFactory() ?? "null", LogType.Info, module, true);
        }

        /// <summary>
        /// Logs a deep message carrying a value type, without boxing it.
        /// </summary>
        public static void LogDeep<T>(T message, LogModule module) where T : struct
        {
            if (!StradaLogSettings.Instance.DeepLogsEnabled)
                return;

            LogInternal(message.ToString(), LogType.Info, module, true);
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
                int count = _logBuffer.Count;
                for (int i = 0; i < count; i++)
                {
                    var entry = _logBuffer[OldestFirstIndex(i, count)];
                    if (entry.Module == module)
                        result.Add(entry);
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
                int count = _logBuffer.Count;
                for (int i = 0; i < count; i++)
                {
                    var entry = _logBuffer[OldestFirstIndex(i, count)];
                    if (entry.Type == type)
                        result.Add(entry);
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
            // A stack walk is a multi-KB string. It used to run on EVERY log call, including
            // calls that were about to be discarded. Only diagnostic severities actually need
            // it, and only in builds where someone can read it.
            //
            // It must be Unity's own format, not Environment.StackTrace: both LogEntry's parser
            // and the editor log window locate the source file by the " (at Assets/Foo.cs:42)"
            // token that StackTraceUtility emits. The BCL rendering has no such token, so file
            // and line never resolved and IDE navigation from the log window was dead.
            string stackTrace = ShouldCaptureStackTrace(type) ? StackTraceUtility.ExtractStackTrace() : string.Empty;
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
                // The inspector writes the serialized field directly and bypasses the clamping
                // setter, so MaxLogEntries can arrive as 0 (or negative) — which would make the
                // `% maxEntries` below divide by zero on the very first log call and take out
                // logging entirely. Clamp defensively at the point of use.
                var maxEntries = StradaLogSettings.Instance.MaxLogEntries;
                if (maxEntries < 1) maxEntries = 1;

                // The setting can also be lowered while the buffer already holds more than that.
                if (_logBuffer.Count > maxEntries)
                {
                    _logBuffer.RemoveRange(maxEntries, _logBuffer.Count - maxEntries);
                    _bufferHead = 0;
                }

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
