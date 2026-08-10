using System.Collections;
using System.Diagnostics;

namespace MHServerEmu.Core.Logging
{
    /// <summary>
    /// Manages <see cref="Logger"/> and <see cref="LogTarget"/> instances.
    /// </summary>
    public static class LogManager
    {
        private static readonly Dictionary<string, Logger> _loggerDict = new();
        private static readonly HashSet<LogTarget> _targets = new();

        private static readonly DateTime _logTimeBase;
        private static readonly Stopwatch _logTimeStopwatch;

        public static bool Enabled { get; set; }

        public static DateTime LogTimeNow { get => _logTimeBase.Add(_logTimeStopwatch.Elapsed); }

        static LogManager()
        {
            // Use a base datetime + stopwatch to get more accurate timing and not poll system time on every log message
            _logTimeBase = DateTime.Now;
            _logTimeStopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Creates or returns existing <see cref="Logger"/> instance with the same name as the caller's <see cref="Type"/>.
        /// </summary>
        public static Logger CreateLogger()
        {
            StackFrame stackFrame = new(1, false);
            string callerName = stackFrame.GetMethod().DeclaringType.Name;
            return callerName == null
                ? throw new Exception("LogManager failed to get caller name when creating a logger.")
                : CreateLogger(callerName);
        }

        /// <summary>
        /// Creates or returns existing <see cref="Logger"/> instance with the specified name.
        /// </summary>
        public static Logger CreateLogger(string name)
        {
            lock (_loggerDict)
            {
                if (_loggerDict.TryGetValue(name, out Logger logger) == false)
                {
                    logger = new(name);
                    _loggerDict.Add(name, logger);
                }

                return logger;
            }
        }

        /// <summary>
        /// Attaches a new <see cref="LogTarget"/> to route <see cref="LogMessage"/> instances to. Returns <see langword="true"/> if successful.
        /// </summary>
        public static bool AttachTarget(LogTarget target)
        {
            lock (_targets)
                return _targets.Add(target);
        }

        /// <summary>
        /// Detaches a <see cref="LogTarget"/> from log message routing. Returns <see langword="true"/> if successful.
        /// </summary>
        public static bool DetachTarget(LogTarget target)
        {
            lock (_targets)
                return _targets.Remove(target);
        }

        /// <summary>
        /// Iterates through all attached <see cref="LogTarget"/> instances that accepts the provided <see cref="LogMessage"/>.
        /// </summary>
        internal static Iterator IterateTargets(in LogMessage message)
        {
            LogTarget[] targets;
            lock (_targets)
                targets = _targets.ToArray();

            return new(message.Level, message.Channels, targets);
        }

        internal readonly struct Iterator
        {
            private readonly LoggingLevel _loggingLevel;
            private readonly LogChannels _channels;
            private readonly LogTarget[] _targets;

            public Iterator(LoggingLevel loggingLevel, LogChannels channels, LogTarget[] targets)
            {
                _loggingLevel = loggingLevel;
                _channels = channels;
                _targets = targets;
            }

            public readonly Enumerator GetEnumerator()
            {
                return new(_loggingLevel, _channels, _targets);
            }

            public struct Enumerator : IEnumerator<LogTarget>
            {
                private readonly LoggingLevel _loggingLevel;
                private readonly LogChannels _channels;
                private readonly LogTarget[] _targets;
                private int _index;

                public LogTarget Current { get; private set; }
                object IEnumerator.Current { get => Current; }

                public Enumerator(LoggingLevel loggingLevel, LogChannels channels, LogTarget[] targets)
                {
                    _loggingLevel = loggingLevel;
                    _channels = channels;
                    _targets = targets;
                    _index = -1;
                }

                public bool MoveNext()
                {
                    while (++_index < _targets.Length)
                    {
                        LogTarget target = _targets[_index];

                        if (_loggingLevel < target.MinimumLevel || _loggingLevel > target.MaximumLevel)
                            continue;

                        if ((_channels & target.Channels) == LogChannels.None)
                            continue;

                        Current = target;
                        return true;
                    }

                    return false;
                }

                public void Reset()
                {
                    _index = -1;
                }

                public void Dispose()
                {
                }
            }
        }
    }
}
