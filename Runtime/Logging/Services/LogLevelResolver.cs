using System;
using System.Collections.Concurrent;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;

namespace UniSpring.Logging.Services
{
    /// <summary>
    /// Effective level of a source. Lookup order: the full dotted source, its
    /// file name (backwards-compatible keys), then each enclosing scope
    /// (<c>Inventory.Presentation</c>, <c>Inventory</c>), then root.
    /// Thread-safe: settings and their cache are swapped together.
    /// </summary>
    [Service]
    public sealed class LogLevelResolver
    {
        private sealed class Snapshot
        {
            public readonly LoggingSettings Settings;
            public readonly ConcurrentDictionary<string, GameLogLevel> Cache =
                new ConcurrentDictionary<string, GameLogLevel>(StringComparer.OrdinalIgnoreCase);

            public Snapshot(LoggingSettings settings)
            {
                Settings = settings;
            }
        }

        private volatile Snapshot m_Snapshot = new Snapshot(new LoggingSettings());

        public void SetSettings(LoggingSettings settings)
        {
            m_Snapshot = new Snapshot(settings ?? new LoggingSettings());
        }

        public bool IsEnabled(string source, GameLogLevel messageLevel)
        {
            var effectiveLevel = Resolve(source);
            return effectiveLevel != GameLogLevel.None && messageLevel >= effectiveLevel;
        }

        private GameLogLevel Resolve(string source)
        {
            var snapshot = m_Snapshot;
            var key = source ?? GameLogSource.Unknown;
            return snapshot.Cache.GetOrAdd(key, name => ResolveUncached(snapshot.Settings, name));
        }

        private static GameLogLevel ResolveUncached(LoggingSettings settings, string source)
        {
            var levels = settings.ClassLevels;
            if (levels.TryGetValue(source, out var exact))
                return exact;
            if (levels.TryGetValue(GameLogSource.ShortName(source), out var byFile))
                return byFile;
            for (var scope = GameLogSource.ParentOf(source); scope != null; scope = GameLogSource.ParentOf(scope))
            {
                if (levels.TryGetValue(scope, out var byScope))
                    return byScope;
            }

            return settings.RootLevel;
        }
    }
}
