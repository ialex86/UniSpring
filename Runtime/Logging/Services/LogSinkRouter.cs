using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Integration;
using UnityEngine;

namespace UniSpring.Logging.Services
{
    /// <summary>
    /// Sends each entry to the sinks selected for its source. Every registered
    /// <see cref="ILogSink"/> is active by default; <c>logging.properties</c>
    /// can narrow the set globally or per source. A failing sink never breaks
    /// logging: it is reported and skipped, and is disabled after repeated
    /// consecutive failures.
    /// </summary>
    public sealed class LogSinkRouter
    {
        private const int MaxConsecutiveFailures = 3;

        private sealed class SinkEntry
        {
            public SinkEntry(ILogSink sink, string name, bool defaultEnabled, int order)
            {
                Sink = sink;
                Name = name;
                DefaultEnabled = defaultEnabled;
                Order = order;
            }

            public ILogSink Sink { get; }
            public string Name { get; }
            public bool DefaultEnabled { get; }
            public int Order { get; }

            private int m_Failures;
            private int m_Disabled;

            public bool IsDisabled => Volatile.Read(ref m_Disabled) != 0;

            public int RecordFailure() => Interlocked.Increment(ref m_Failures);

            public void RecordSuccess() => Interlocked.Exchange(ref m_Failures, 0);

            public void Disable() => Volatile.Write(ref m_Disabled, 1);
        }

        private sealed class Snapshot
        {
            public Snapshot(SinkEntry[] globalSinks, Dictionary<string, SinkEntry[]> sourceSinks)
            {
                GlobalSinks = globalSinks;
                SourceSinks = sourceSinks;
            }

            public SinkEntry[] GlobalSinks { get; }

            public Dictionary<string, SinkEntry[]> SourceSinks { get; }

            public ConcurrentDictionary<string, SinkEntry[]> Cache { get; } =
                new ConcurrentDictionary<string, SinkEntry[]>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly SinkEntry[] m_AllSinks;
        private readonly SinkEntry[] m_DefaultSinks;
        private readonly Dictionary<string, SinkEntry> m_ByName =
            new Dictionary<string, SinkEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> m_ReportedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private volatile Snapshot m_Snapshot;

        public LogSinkRouter(IEnumerable<ILogSink> sinks)
        {
            if (sinks == null)
                throw new ArgumentNullException(nameof(sinks));

            var entries = new List<SinkEntry>();
            foreach (var sink in sinks)
            {
                if (sink == null)
                    continue;

                var type = sink.GetType();
                var service = type.GetCustomAttribute<LogSinkServiceAttribute>(false);
                var metadata = type.GetCustomAttribute<LogSinkAttribute>(false);
                if (service != null && metadata != null)
                {
                    Report(
                        "Sink '" + type.FullName +
                        "' has both [LogSinkService] and [LogSink]; [LogSinkService] wins.");
                }

                var name = service != null
                    ? (string.IsNullOrWhiteSpace(service.Name) ? type.Name : service.Name.Trim())
                    : (string.IsNullOrWhiteSpace(metadata?.Name) ? type.Name : metadata.Name.Trim());
                var defaultEnabled = service != null ? service.Default : metadata?.Default ?? true;
                var order = type.GetCustomAttribute<OrderAttribute>(false)?.Value ?? 0;
                entries.Add(new SinkEntry(sink, name, defaultEnabled, order));
            }

            m_AllSinks = entries
                .OrderBy(entry => entry.Order)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            m_DefaultSinks = m_AllSinks.Where(entry => entry.DefaultEnabled).ToArray();

            for (var index = 0; index < m_AllSinks.Length; index++)
                RegisterNames(m_AllSinks[index]);

            m_Snapshot = new Snapshot(
                m_DefaultSinks,
                new Dictionary<string, SinkEntry[]>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Applies a new selection. Call it after the configuration is loaded;
        /// until then every sink that does not opt out is used.
        /// </summary>
        public void Reload(LoggingSettings settings)
        {
            settings = settings ?? new LoggingSettings();

            var global = ParseSelection(settings.SinkSelection, m_DefaultSinks, "sinks");
            var bySource = new Dictionary<string, SinkEntry[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in settings.SourceSinkSelections)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                var source = pair.Key.Trim();
                bySource[source] = ParseSelection(pair.Value, global, "sinks." + source);
            }

            m_Snapshot = new Snapshot(global, bySource);
        }

        public void Write(LogEntry entry)
        {
            if (entry == null)
                return;

            var sinks = Resolve(entry.Source);
            for (var index = 0; index < sinks.Length; index++)
            {
                var sink = sinks[index];
                if (sink.IsDisabled)
                    continue;

                try
                {
                    sink.Sink.Write(entry);
                    sink.RecordSuccess();
                }
                catch (Exception exception)
                {
                    OnSinkFailure(sink, exception);
                }
            }
        }

        private SinkEntry[] Resolve(string source)
        {
            var snapshot = m_Snapshot;
            var key = source ?? GameLogSource.Unknown;
            return snapshot.Cache.GetOrAdd(key, name => ResolveUncached(snapshot, name));
        }

        private static SinkEntry[] ResolveUncached(Snapshot snapshot, string source)
        {
            if (snapshot.SourceSinks.TryGetValue(source, out var exact))
                return exact;

            if (snapshot.SourceSinks.TryGetValue(GameLogSource.ShortName(source), out var byFile))
                return byFile;

            for (var scope = GameLogSource.ParentOf(source); scope != null; scope = GameLogSource.ParentOf(scope))
            {
                if (snapshot.SourceSinks.TryGetValue(scope, out var byScope))
                    return byScope;
            }

            return snapshot.GlobalSinks;
        }

        private SinkEntry[] ParseSelection(string expression, SinkEntry[] fallback, string owner)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return fallback;

            var tokens = expression.Split(',');
            var useAll = false;
            var sawNone = false;
            var positives = new List<string>();
            var negatives = new List<string>();

            for (var index = 0; index < tokens.Length; index++)
            {
                var token = tokens[index].Trim();
                if (token.Length == 0)
                    continue;

                if (token.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    sawNone = true;
                    continue;
                }

                if (token == "*" || token.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    useAll = true;
                    continue;
                }

                if (token[0] == '-')
                {
                    var removed = token.Substring(1).Trim();
                    if (removed.Length == 0)
                    {
                        Report("Ignored an empty sink exclusion in " + owner + ".");
                        continue;
                    }

                    negatives.Add(removed);
                    continue;
                }

                positives.Add(token);
            }

            if (sawNone)
            {
                if (useAll || positives.Count > 0 || negatives.Count > 0)
                    Report("'none' in " + owner + " ignores the other sink names.");
                return Array.Empty<SinkEntry>();
            }

            var selected = new List<SinkEntry>();
            var seen = new HashSet<SinkEntry>();

            if (useAll || (positives.Count == 0 && negatives.Count > 0))
            {
                for (var index = 0; index < m_AllSinks.Length; index++)
                {
                    if (seen.Add(m_AllSinks[index]))
                        selected.Add(m_AllSinks[index]);
                }
            }

            for (var index = 0; index < positives.Count; index++)
            {
                SinkEntry entry;
                if (!m_ByName.TryGetValue(positives[index], out entry))
                {
                    Report("Unknown log sink '" + positives[index] + "' in " + owner + ".");
                    continue;
                }

                if (seen.Add(entry))
                    selected.Add(entry);
            }

            for (var index = 0; index < negatives.Count; index++)
            {
                SinkEntry entry;
                if (!m_ByName.TryGetValue(negatives[index], out entry))
                {
                    Report("Unknown log sink '" + negatives[index] + "' in " + owner + ".");
                    continue;
                }

                if (seen.Remove(entry))
                    selected.Remove(entry);
            }

            if (!useAll && positives.Count > 0 && selected.Count == 0)
            {
                Report("No known log sink in " + owner + "; the global selection is used.");
                return fallback;
            }

            var result = new SinkEntry[selected.Count];
            for (var index = 0; index < selected.Count; index++)
                result[index] = selected[index];
            return result;
        }

        private void RegisterNames(SinkEntry entry)
        {
            RegisterName(entry.Name, entry);

            var type = entry.Sink.GetType();
            RegisterName(type.Name, entry);
            if (!string.IsNullOrWhiteSpace(type.FullName))
                RegisterName(type.FullName, entry);
        }

        private void RegisterName(string name, SinkEntry entry)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            SinkEntry existing;
            if (m_ByName.TryGetValue(name, out existing))
            {
                if (!ReferenceEquals(existing, entry) && m_ReportedNames.Add(name))
                {
                    Report("Log sink name '" + name + "' is ambiguous; the first registration is used.");
                }

                return;
            }

            m_ByName[name] = entry;
        }

        private void OnSinkFailure(SinkEntry sink, Exception exception)
        {
            var failures = sink.RecordFailure();
            if (failures == 1)
            {
                Debug.LogError(
                    "UniSpring logging: sink '" + sink.Name + "' failed and was skipped. " + exception);
            }

            if (failures >= MaxConsecutiveFailures)
            {
                sink.Disable();
                Debug.LogError(
                    "UniSpring logging: sink '" + sink.Name + "' disabled after " +
                    failures + " consecutive failures.");
            }
        }

        private static void Report(string message)
        {
            Debug.LogWarning("UniSpring logging: " + message);
        }
    }
}
