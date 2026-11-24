using System;

namespace UniSpring.Logging.Integration
{
    /// <summary>
    /// Names a log sink and controls whether it is active when the
    /// configuration does not select sinks explicitly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class LogSinkAttribute : Attribute
    {
        public LogSinkAttribute()
        {
        }

        public LogSinkAttribute(string name)
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
