using UniSpring.DependencyInjection;

namespace UniSpring.Logging.Dto
{
    /// <summary>
    /// Where the logging configuration file is read from. Registered as a
    /// component, so a consumer can replace it with
    /// <c>ApplicationContext.RegisterInstance(new LoggingPathOptions { ... })</c>
    /// before warm-up.
    /// </summary>
    [Component]
    public sealed class LoggingPathOptions
    {
        /// <summary>Environment variable that points at an explicit config file.</summary>
        public string EnvironmentVariable { get; set; } = "UNISPRING_LOG_CONFIG";

        /// <summary>Config file name used in player builds.</summary>
        public string FileName { get; set; } = "logging.properties";

        /// <summary>
        /// Name of a <c>TextAsset</c> in a <c>Resources</c> folder, read when no
        /// config file is found. The default matches
        /// <c>Assets/Resources/logging.properties.txt</c>. It is the way to ship
        /// a config on Android and WebGL, where StreamingAssets is not a
        /// folder on disk. Empty disables the lookup.
        /// </summary>
        public string ResourceName { get; set; } = "logging.properties";

        /// <summary>
        /// Editor path relative to <c>Application.dataPath</c>, for example
        /// <c>Settings/logging.properties</c>.
        /// </summary>
        public string EditorRelativePath { get; set; } = "Settings/logging.properties";

        /// <summary>When false, a missing config file in the editor is not a warning.</summary>
        public bool RequiredInEditor { get; set; } = true;
    }
}
