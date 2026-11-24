using System;
using UniSpring.Logging.Domain;

namespace UniSpring.Logging.Dto
{
    public sealed class LogEntry
    {
        public GameLogLevel Level { get; set; }
        public string Source { get; set; }
        public object Message { get; set; }
        public Exception Exception { get; set; }
        public object Context { get; set; }
    }
}
