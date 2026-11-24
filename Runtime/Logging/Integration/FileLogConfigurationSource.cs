using System.IO;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Services;
using UnityEngine;

namespace UniSpring.Logging.Integration
{
    /// <summary>
    /// Reads the configuration from the file named by the path provider. When
    /// that file does not exist, the <c>TextAsset</c> named by
    /// <see cref="LoggingPathOptions.ResourceName"/> is read from
    /// <c>Resources</c> instead: it works on every platform, including Android
    /// and WebGL where StreamingAssets cannot be read as a file.
    /// </summary>
    [Service]
    public sealed class FileLogConfigurationSource : ILogConfigurationSource
    {
        private readonly ILogConfigPathProvider m_PathProvider;
        private readonly LoggingPropertiesParser m_Parser;
        private readonly LoggingPathOptions m_Options;

        public FileLogConfigurationSource(
            ILogConfigPathProvider pathProvider,
            LoggingPropertiesParser parser)
            : this(pathProvider, parser, null)
        {
        }

        [Autowired]
        public FileLogConfigurationSource(
            ILogConfigPathProvider pathProvider,
            LoggingPropertiesParser parser,
            LoggingPathOptions options)
        {
            m_PathProvider = pathProvider;
            m_Parser = parser;
            m_Options = options ?? new LoggingPathOptions();
        }

        public LoggingConfiguration Load()
        {
            var path = m_PathProvider.GetPath();
            if (File.Exists(path))
            {
                return new LoggingConfiguration
                {
                    Path = path,
                    Exists = true,
                    Settings = m_Parser.Parse(File.ReadLines(path))
                };
            }

            var resourceName = m_Options.ResourceName;
            if (!string.IsNullOrWhiteSpace(resourceName))
            {
                var asset = Resources.Load<TextAsset>(resourceName.Trim());
                if (asset != null)
                {
                    return new LoggingConfiguration
                    {
                        Path = "Resources/" + resourceName.Trim(),
                        Exists = true,
                        Settings = m_Parser.Parse(asset.text.Split('\n'))
                    };
                }
            }

            return new LoggingConfiguration
            {
                Path = path,
                Exists = false,
                Required = m_PathProvider.IsRequired,
                Settings = new LoggingSettings()
            };
        }
    }
}
