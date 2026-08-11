using System;

namespace Strada.Core.Logging
{
    /// <summary>
    /// Represents the type of a log entry.
    /// </summary>
    public enum LogType
    {
        Info = 0,
        Warning = 1,
        Error = 2,
        Exception = 3
    }

    /// <summary>
    /// Represents a single log entry in the StradaLog system.
    /// Contains message, module, timestamp, and source location for IDE navigation.
    /// </summary>
    public sealed class LogEntry
    {
        /// <summary>
        /// The log message content.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// The type of log (Info, Warning, Error, Exception).
        /// </summary>
        public LogType Type { get; }

        /// <summary>
        /// The module that generated this log entry.
        /// </summary>
        public LogModule Module { get; }

        /// <summary>
        /// The timestamp when this log entry was created.
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// The full stack trace at the time of logging.
        /// </summary>
        public string StackTrace { get; }

        /// <summary>
        /// The source file path extracted from the stack trace.
        /// </summary>
        public string FilePath { get; private set; }

        /// <summary>
        /// The line number in the source file.
        /// </summary>
        public int LineNumber { get; private set; }

        /// <summary>
        /// Whether this is a deep log entry (detailed flow logging).
        /// </summary>
        public bool IsDeepLog { get; }

        /// <summary>
        /// Creates a new log entry.
        /// </summary>
        public LogEntry(string message, LogType type, LogModule module, string stackTrace, bool isDeepLog = false)
        {
            Message = message;
            Type = type;
            Module = module;
            Timestamp = DateTime.Now;
            StackTrace = stackTrace;
            IsDeepLog = isDeepLog;
            FilePath = string.Empty;
            LineNumber = 0;

            ParseStackTrace(stackTrace);
        }

        private void ParseStackTrace(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return;

            var lines = stackTrace.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Unity's StackTraceUtility renders frames as "Ns.Type:Method ()", so the frames
                // belonging to the logger itself carry a colon, not a dot, after the type name.
                if (line.Contains("StradaLog.") || line.Contains("StradaLog:") || line.Contains("UnityEngine.Debug"))
                    continue;

                // " (at Assets/Foo.cs:42)" is Unity's format. Mono's BCL rendering instead ends
                // with "[0x00000] in /path/Foo.cs:42", so both tokens are accepted: a LogEntry
                // can be constructed from either kind of trace.
                int pathStart;
                int atIndex = line.IndexOf(" (at ", StringComparison.Ordinal);
                if (atIndex >= 0)
                {
                    pathStart = atIndex + 5;
                }
                else
                {
                    int inIndex = line.IndexOf("] in ", StringComparison.Ordinal);
                    if (inIndex < 0)
                        continue;
                    pathStart = inIndex + 5;
                }

                int colonIndex = line.LastIndexOf(':');
                if (colonIndex <= pathStart)
                    continue;

                int closeParenIndex = line.IndexOf(')', colonIndex);
                if (closeParenIndex < 0)
                    closeParenIndex = line.Length;

                FilePath = line.Substring(pathStart, colonIndex - pathStart);

                // The BCL form has no trailing ')', so the number runs to the end of the line and
                // picks up the '\r' of a CRLF-separated trace.
                var lineNumberStr = line.Substring(colonIndex + 1, closeParenIndex - colonIndex - 1).Trim();
                if (int.TryParse(lineNumberStr, out int parsedLine))
                {
                    LineNumber = parsedLine;
                }

                break;
            }
        }

        /// <summary>
        /// Gets a formatted string representation of the log entry.
        /// </summary>
        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] [{Module}] {Message}";
        }
    }
}
