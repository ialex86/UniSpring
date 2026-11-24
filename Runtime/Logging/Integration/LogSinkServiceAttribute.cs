using System;
using UniSpring.DependencyInjection;

namespace UniSpring.Logging.Integration
{
    /// <summary>
    /// Registers a log sink as a DI service and gives the router its name and
    /// default participation in one attribute. Equivalent to <c>[Service]</c>
    /// plus <c>[LogSink]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class LogSinkServiceAttribute : ServiceAttribute
    {
        public LogSinkServiceAttribute()
        {
        }

        public LogSinkServiceAttribute(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Stable name used in <c>logging.properties</c>. The type name is used
        /// when this is not set.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// False keeps the sink out of the default set. It is then used only
        /// when selected explicitly or through <c>all</c>/<c>*</c>.
        /// </summary>
        public bool Default { get; set; } = true;
    }
}
