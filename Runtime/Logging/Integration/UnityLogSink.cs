using System;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Services;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UniSpring.Logging.Integration
{
    [LogSinkService]
    public sealed class UnityLogSink : ILogSink
    {
        private readonly StrategyRegistry<GameLogLevel, IUnityLogLevelStrategy> m_LevelStrategies;

        [Autowired]
        public UnityLogSink(StrategyRegistry<GameLogLevel, IUnityLogLevelStrategy> levelStrategies)
        {
            m_LevelStrategies = levelStrategies ?? throw new ArgumentNullException(nameof(levelStrategies));
        }

        [HideInCallstack]
        public void Write(LogEntry entry)
        {
            if (entry == null)
                return;
            if (!m_LevelStrategies.TryGet(entry.Level, out var strategy))
                return;

            var context = entry.Context as Object;
            var source = string.IsNullOrWhiteSpace(entry.Source) ? GameLogSource.Unknown : entry.Source;
            var line = LogMessageFormat.Line(entry.Level, source, entry.Message, entry.Exception);
            if (entry.Exception != null)
            {
                strategy.WriteException(line, entry.Exception, context);
                return;
            }

            strategy.Write(line, context);
        }
    }
}
