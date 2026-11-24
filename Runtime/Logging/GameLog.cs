using System;
using System.Collections.Generic;
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

        // Installations in order; the newest one that is still installed is the active backend. Removing one
        // from the middle (containers disposed out of order) never brings a removed logger back.
        private static readonly object s_BackendLock = new object();
        private static readonly List<BackendRegistration> s_Registrations = new List<BackendRegistration>();
        private static IGameLogger s_BaseBackend;

        private sealed class BackendRegistration : IDisposable
        {
            public readonly IGameLogger Logger;
            public readonly IGameLogger Previous;

            public BackendRegistration(IGameLogger logger, IGameLogger previous)
            {
                Logger = logger;
                Previous = previous;
            }

            public void Dispose()
            {
                lock (s_BackendLock)
                {
                    if (s_Registrations.Remove(this))
                        PublishBackend();
                }
            }
        }

        private static void PublishBackend()
        {
            var count = s_Registrations.Count;
            Volatile.Write(ref s_Backend, count == 0 ? s_BaseBackend : s_Registrations[count - 1].Logger);
        }

        private static BackendRegistration PushBackend(IGameLogger backend)
        {
            var registration = new BackendRegistration(backend, s_Backend);
            s_Registrations.Add(registration);
            PublishBackend();
            return registration;
        }

        /// <summary>Installs a backend until the returned handle is disposed (used by the container).</summary>
        internal static IDisposable RegisterBackend(IGameLogger backend)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));
            lock (s_BackendLock)
                return PushBackend(backend);
        }

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
            lock (s_BackendLock)
                return PushBackend(backend).Previous;
        }

        /// <summary>Installs only when no backend is active; used by the editor bootstrap.</summary>
        internal static bool TryInstallBackend(IGameLogger backend)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));
            lock (s_BackendLock)
            {
                if (s_Backend != null)
                    return false;
                s_BaseBackend = backend;
                PublishBackend();
                return true;
            }
        }

        /// <summary>
        /// Removes the installation made by <c>InstallBackend(installed)</c> that returned
        /// <paramref name="previous"/>, even when a newer backend is active. The newest
        /// installation that is still in place stays (or becomes) the active backend.
        /// </summary>
        public static void RestoreBackend(IGameLogger installed, IGameLogger previous)
        {
            if (installed == null)
                return;
            lock (s_BackendLock)
            {
                for (var index = s_Registrations.Count - 1; index >= 0; index--)
                {
                    var registration = s_Registrations[index];
                    if (!ReferenceEquals(registration.Logger, installed) ||
                        !ReferenceEquals(registration.Previous, previous))
                    {
                        continue;
                    }

                    s_Registrations.RemoveAt(index);
                    PublishBackend();
                    break;
                }
            }
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

            lock (s_BackendLock)
            {
                s_Registrations.Clear();
                s_BaseBackend = backend;
                PublishBackend();
            }
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
