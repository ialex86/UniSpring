using System;
using System.Collections.Generic;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;
using UnityEngine;

namespace UniSpring.Logging.Services
{
    public interface IGameLogger
    {
        void Reload();

        bool IsEnabled(GameLogLevel level, string source);

        void Log(GameLogLevel level, string source, object message, object context = null);

        void Log(GameLogLevel level, string source, Func<object> messageFactory, object context = null);

        /// <summary>A message and the exception it reports, written as one entry.</summary>
        void Log(GameLogLevel level, string source, object message, Exception exception, object context = null);

        void LogException(string source, Exception exception, object context = null);
    }

    [Service]
    public sealed class GameLogService : IGameLogger
    {
        private readonly ILogConfigurationSource m_ConfigurationSource;
        private readonly LogLevelResolver m_LevelResolver;
        private readonly LogSinkRouter m_Router;
        private readonly object m_InitializationLock = new object();
        private volatile bool m_Initialized;

        [Autowired]
        public GameLogService(
            ILogConfigurationSource configurationSource,
            LogLevelResolver levelResolver,
            IReadOnlyList<ILogSink> sinks)
            : this(configurationSource, levelResolver, new LogSinkRouter(sinks))
        {
        }

        /// <summary>
        /// Single-sink construction for manual composition and tests. The sink
        /// still goes through the router, so the configuration can silence it.
        /// </summary>
        public GameLogService(
            ILogConfigurationSource configurationSource,
            LogLevelResolver levelResolver,
            ILogSink sink)
            : this(configurationSource, levelResolver, new LogSinkRouter(new[] { sink }))
        {
        }

        private GameLogService(
            ILogConfigurationSource configurationSource,
            LogLevelResolver levelResolver,
            LogSinkRouter router)
        {
            m_ConfigurationSource = configurationSource;
            m_LevelResolver = levelResolver;
            m_Router = router;
        }

        [HideInCallstack]
        public void Reload()
        {
            LoggingConfiguration configuration;
            try
            {
                configuration = m_ConfigurationSource.Load();
            }
            catch (Exception exception)
            {
                m_LevelResolver.SetSettings(new LoggingSettings());
                m_Router.Reload(new LoggingSettings());
                m_Initialized = true;
                m_Router.Write(new LogEntry
                {
                    Level = GameLogLevel.Error,
                    Source = "GameLog",
                    Message = "Failed to load logging configuration. Built-in defaults are used.",
                    Exception = exception
                });
                return;
            }

            var settings = configuration.Settings ?? new LoggingSettings();
            m_LevelResolver.SetSettings(settings);
            m_Router.Reload(settings);
            m_Initialized = true;

            if (!configuration.Exists)
            {
                m_Router.Write(new LogEntry
                {
                    // In a player build the file is optional: no config is the normal case.
                    Level = configuration.Required ? GameLogLevel.Warn : GameLogLevel.Info,
                    Source = "GameLog",
                    Message = $"Logging config not found: {configuration.Path}. Built-in defaults are used."
                });
            }
        }

        [HideInCallstack]
        public bool IsEnabled(GameLogLevel level, string source)
        {
            EnsureInitialized();
            return m_LevelResolver.IsEnabled(source, level);
        }

        [HideInCallstack]
        public void Log(GameLogLevel level, string source, object message, object context = null)
        {
            EnsureInitialized();
            if (!m_LevelResolver.IsEnabled(source, level))
                return;

            m_Router.Write(new LogEntry
            {
                Level = level,
                Source = source,
                Message = message,
                Context = context
            });
        }

        [HideInCallstack]
        public void Log(GameLogLevel level, string source, Func<object> messageFactory, object context = null)
        {
            EnsureInitialized();
            if (!m_LevelResolver.IsEnabled(source, level))
                return;

            object message;
            try
            {
                message = messageFactory != null ? messageFactory() : "<null message factory>";
            }
            catch (Exception exception)
            {
                m_Router.Write(new LogEntry
                {
                    Level = GameLogLevel.Error,
                    Source = source,
                    Message = "Log message factory failed.",
                    Exception = exception,
                    Context = context
                });
                return;
            }

            m_Router.Write(new LogEntry
            {
                Level = level,
                Source = source,
                Message = message,
                Context = context
            });
        }

        [HideInCallstack]
        public void Log(GameLogLevel level, string source, object message, Exception exception, object context = null)
        {
            EnsureInitialized();
            if (!m_LevelResolver.IsEnabled(source, level))
                return;

            m_Router.Write(new LogEntry
            {
                Level = level,
                Source = source,
                Message = message,
                Exception = exception,
                Context = context
            });
        }

        [HideInCallstack]
        public void LogException(string source, Exception exception, object context = null)
        {
            EnsureInitialized();
            if (!m_LevelResolver.IsEnabled(source, GameLogLevel.Error))
                return;

            m_Router.Write(new LogEntry
            {
                Level = GameLogLevel.Error,
                Source = source,
                Exception = exception,
                Context = context
            });
        }

        [HideInCallstack]
        private void EnsureInitialized()
        {
            if (m_Initialized)
                return;
            lock (m_InitializationLock)
            {
                if (m_Initialized)
                    return;
                Reload();
            }
        }
    }

    /// <summary>
    /// Makes the container's logger the GameLog backend for the container's
    /// lifetime; disposing the container restores whatever was active before
    /// (for example the editor backend), so a second container never fails.
    /// The constructor has no side effects: installation happens in the
    /// explicit [PostConstruct] step, which runs before any later bean is
    /// created (Order -1000), so warm-up logs already reach this backend.
    /// </summary>
    [Service]
    [Order(-1000)]
    internal sealed class GameLogBackendInstaller : IDisposable
    {
        private readonly IGameLogger m_Logger;
        private IDisposable m_Registration;
        private bool m_Installed;

        public GameLogBackendInstaller(IGameLogger logger)
        {
            m_Logger = logger;
        }

        [PostConstruct]
        internal void Install()
        {
            if (m_Installed)
                return;
            // On the container's thread (Unity's main thread at runtime): the
            // config path and the Resources fallback use Unity APIs that fail
            // on a worker thread, so the configuration must not be loaded by
            // whichever thread happens to log first.
            m_Logger.Reload();
            m_Registration = GameLog.RegisterBackend(m_Logger);
            m_Installed = true;
        }

        public void Dispose()
        {
            if (!m_Installed)
                return;
            m_Registration?.Dispose();
            m_Registration = null;
            m_Installed = false;
        }
    }
}
