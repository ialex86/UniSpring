using System;
using System.Collections.Generic;
using UniSpring.Logging.Domain;

namespace UniSpring.Logging.Dto
{
    public sealed class LoggingSettings
    {
        public GameLogLevel RootLevel { get; set; } = GameLogLevel.Info;

        public Dictionary<string, GameLogLevel> ClassLevels { get; } =
            new Dictionary<string, GameLogLevel>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Global sink selection: <c>all</c>, <c>*</c>, <c>none</c>, a
        /// comma-separated list of sink names, or <c>all,-Name</c>. Null or
        /// empty selects every sink whose <c>[LogSink]</c> does not opt out.
        /// </summary>
        public string SinkSelection { get; set; }

        /// <summary>
        /// Per-source sink selection. Keys follow the same lookup rules as
        /// <see cref="ClassLevels"/>: full source, file name, enclosing scope.
        /// </summary>
        public Dictionary<string, string> SourceSinkSelections { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
