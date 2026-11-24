using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Integration;
using UniSpring.Logging.Services;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityDebug = UnityEngine.Debug;

namespace UniSpring.Logging
{
    /// <summary>
    /// Project logging facade. The source is taken from <c>[CallerFilePath]</c>:
    /// never pass the <c>caller</c> argument yourself. To log a failure with its
    /// stack trace use <c>Error(message, exception)</c> / <c>Warn(message, exception)</c>.
    /// </summary>
    public static class GameLog
    {
        private static readonly IGameLogger FallbackBackend = new UnityFallbackGameLogger();
        private static IGameLogger s_Backend;
        private static int s_EmptyCallerReported;

        [HideInCallstack]
        public static void Debug(object message, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Debug, Source(caller), message, context);

        [HideInCallstack]
        public static void Info(object message, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Info, Source(caller), message, context);

        [HideInCallstack]
        public static void Warn(object message, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Warn, Source(caller), message, context);

        [HideInCallstack]
        public static void Error(object message, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Error, Source(caller), message, context);

        [HideInCallstack]
        public static void Debug(Func<object> messageFactory, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Debug, Source(caller), messageFactory, context);

        [HideInCallstack]
        public static void Info(Func<object> messageFactory, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Info, Source(caller), messageFactory, context);

        [HideInCallstack]
        public static void Warn(Func<object> messageFactory, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Warn, Source(caller), messageFactory, context);

        [HideInCallstack]
        public static void Error(Func<object> messageFactory, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Error, Source(caller), messageFactory, context);

        /// <summary>A handled failure: message and exception as one entry.</summary>
        [HideInCallstack]
        public static void Error(object message, Exception exception, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Error, Source(caller), message, exception, context);

        /// <summary>A recoverable failure: message and exception as one entry.</summary>
        [HideInCallstack]
        public static void Warn(object message, Exception exception, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Warn, Source(caller), message, exception, context);

        /// <summary>A debug-level trace of an expected failure (kept quiet unless enabled).</summary>
        [HideInCallstack]
        public static void Debug(object message, Exception exception, Object context = null, [CallerFilePath] string caller = "")
            => Backend.Log(GameLogLevel.Debug, Source(caller), message, exception, context);

        [HideInCallstack]
        public static void Error(Exception exception, Object context = null, [CallerFilePath] string caller = "")
            => Backend.LogException(Source(caller), exception, context);

        [HideInCallstack]
        public static bool IsEnabled(GameLogLevel level, [CallerFilePath] string caller = "")
            => Backend.IsEnabled(level, Source(caller));

        [HideInCallstack]
        public static bool IsDebugEnabled([CallerFilePath] string caller = "")
            => IsEnabled(GameLogLevel.Debug, caller);

        [HideInCallstack]
        public static void Reload()
            => Backend.Reload();

        /// <summary>
        /// Applies source naming options (for example the source root and
        /// ignored folders). Call before the first log entry.
        /// </summary>
        public static void ConfigureSource(GameLogSourceOptions options)
            => GameLogSource.Configure(options);

        internal static bool HasInstalledBackend => Volatile.Read(ref s_Backend) != null;

        /// <summary>
        /// Installs a custom backend and returns the one it replaced (or null).
        /// Pair it with <see cref="RestoreBackend"/> when the custom backend is
        /// no longer needed.
        /// </summary>
        public static IGameLogger InstallBackend(IGameLogger backend)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));
            return Interlocked.Exchange(ref s_Backend, backend);
        }

        /// <summary>Installs only when no backend is active; used by the editor bootstrap.</summary>
        internal static bool TryInstallBackend(IGameLogger backend)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));
            return Interlocked.CompareExchange(ref s_Backend, backend, null) == null;
        }

        /// <summary>
        /// Puts <paramref name="previous"/> back, but only while <paramref name="installed"/>
        /// is still the active backend: a newer installation is never undone.
        /// </summary>
        public static void RestoreBackend(IGameLogger installed, IGameLogger previous)
        {
            if (installed == null)
                return;
            Interlocked.CompareExchange(ref s_Backend, previous, installed);
        }

        /// <summary>
        /// Start of a play session. The default backend is installed and its
        /// configuration read here, on the main thread, so
        /// <c>logging.properties</c> applies from the first entry even when no
        /// container is ever built. A container's logger replaces it later.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetBackend()
        {
            IGameLogger backend;
            try
            {
                backend = DefaultGameLogBackend.Create();
                backend.Reload();
            }
            catch (Exception exception)
            {
                backend = null;
                UnityDebug.LogException(exception);
            }

            Interlocked.Exchange(ref s_Backend, backend);
        }

        private static IGameLogger Backend => Volatile.Read(ref s_Backend) ?? FallbackBackend;

        private static string Source(string caller)
        {
            if (string.IsNullOrWhiteSpace(caller))
                ReportEmptyCaller();
            return GameLogSource.FromCallerPath(caller);
        }

        private static void ReportEmptyCaller()
        {
            if (Interlocked.Exchange(ref s_EmptyCallerReported, 1) != 0)
                return;
            if (!UnityDebug.isDebugBuild)
                return;
            Backend.Log(
                GameLogLevel.Warn,
                "GameLog",
                "A GameLog call passed an explicit empty caller argument: its entries are logged as " +
                GameLogSource.Unknown + " and per-source levels do not apply. Remove the argument.\n" +
                Environment.StackTrace);
        }

        private sealed class UnityFallbackGameLogger : IGameLogger
        {
            [HideInCallstack]
            public void Reload()
            {
            }

            [HideInCallstack]
            public bool IsEnabled(GameLogLevel level, string source)
            {
                return level >= GameLogLevel.Info && level < GameLogLevel.None;
            }

            [HideInCallstack]
            public void Log(GameLogLevel level, string source, object message, object context = null)
            {
                Log(level, source, message, null, context);
            }

            [HideInCallstack]
            public void Log(GameLogLevel level, string source, object message, Exception exception, object context = null)
            {
                if (!IsEnabled(level, source))
                    return;

                var unityContext = context as Object;
                var line = LogMessageFormat.Line(level, source, message, exception);
                if (exception != null && level == GameLogLevel.Error)
                {
                    UnityDebug.LogException(new GameLogException(line, exception), unityContext);
                    return;
                }

                var text = exception != null ? line + "\n" + exception : line;
                if (level == GameLogLevel.Error)
                {
                    UnityDebug.LogError(text, unityContext);
                    return;
                }

                if (level == GameLogLevel.Warn)
                {
                    UnityDebug.LogWarning(text, unityContext);
                    return;
                }

                UnityDebug.LogFormat(LogType.Log, LogOption.NoStacktrace, unityContext, "{0}", text);
            }

            [HideInCallstack]
            public void Log(
                GameLogLevel level,
                string source,
                Func<object> messageFactory,
                object context = null)
            {
                if (!IsEnabled(level, source))
                    return;

                object message;
                try
                {
                    message = messageFactory != null ? messageFactory() : "<null message factory>";
                }
                catch (Exception exception)
                {
                    Log(GameLogLevel.Error, source, "Log message factory failed.", exception, context);
                    return;
                }

                Log(level, source, message, null, context);
            }

            [HideInCallstack]
            public void LogException(string source, Exception exception, object context = null)
            {
                Log(GameLogLevel.Error, source, null, exception, context);
            }
        }
    }
}
