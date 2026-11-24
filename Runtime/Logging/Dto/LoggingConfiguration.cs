namespace UniSpring.Logging.Dto
{
    public sealed class LoggingConfiguration
    {
        public string Path { get; set; }
        public bool Exists { get; set; }

        /// <summary>False when a missing file is expected (player builds without a config).</summary>
        public bool Required { get; set; } = true;
        public LoggingSettings Settings { get; set; }
    }
}
