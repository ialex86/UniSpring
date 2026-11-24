using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Services;

namespace UniSpring.Logging.Integration
{
    /// <summary>
    /// The logger used while no container owns logging: the same object graph
    /// the container builds, composed by hand with the default
    /// <see cref="LoggingPathOptions"/> and the Unity sink. It makes
    /// <c>logging.properties</c> apply from the first log entry, before (or
    /// without) a container; a container's logger replaces it when it is
    /// built and brings custom options and sinks with it.
    /// </summary>
    internal static class DefaultGameLogBackend
    {
        public static IGameLogger Create()
        {
            var options = new LoggingPathOptions();
            var strategies = new StrategyRegistry<GameLogLevel, IUnityLogLevelStrategy>(
                new IUnityLogLevelStrategy[]
                {
                    new UnityPlainLogLevelStrategy(),
                    new UnityWarningLogLevelStrategy(),
                    new UnityErrorLogLevelStrategy(),
                });
            return new GameLogService(
                new FileLogConfigurationSource(
                    new EnvironmentLogConfigPathProvider(options),
                    new LoggingPropertiesParser(),
                    options),
                new LogLevelResolver(),
                new UnityLogSink(strategies));
        }
    }
}
