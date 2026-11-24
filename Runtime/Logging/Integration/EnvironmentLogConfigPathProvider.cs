using System;
using System.IO;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Services;
using UnityEngine;

namespace UniSpring.Logging.Integration
{
    /// <summary>
    /// Where <c>logging.properties</c> is read from:
    /// 1. the environment variable from <see cref="LoggingPathOptions.EnvironmentVariable"/>;
    /// 2. the editor: <c>Application.dataPath</c> + <see cref="LoggingPathOptions.EditorRelativePath"/>;
    /// 3. a player: <c>persistentDataPath/logging.properties</c> when it exists
    ///    (a file put on the device overrides the shipped one), else
    ///    <c>StreamingAssets/logging.properties</c> (optional in a player).
    /// When no file is found, <see cref="FileLogConfigurationSource"/> falls
    /// back to the <c>Resources</c> asset named by
    /// <see cref="LoggingPathOptions.ResourceName"/>.
    /// </summary>
    [Service]
    public sealed class EnvironmentLogConfigPathProvider : ILogConfigPathProvider
    {
        private readonly LoggingPathOptions m_Options;

        public EnvironmentLogConfigPathProvider(LoggingPathOptions options)
        {
            m_Options = options ?? new LoggingPathOptions();
        }

        public bool IsRequired
        {
            get
            {
                if (!Application.isEditor)
                    return !string.IsNullOrWhiteSpace(ExternalPath());

                return m_Options.RequiredInEditor || !string.IsNullOrWhiteSpace(ExternalPath());
            }
        }

        public string GetPath()
        {
            var externalPath = ExternalPath();
            if (!string.IsNullOrWhiteSpace(externalPath))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(externalPath.Trim()));

            if (Application.isEditor)
                return Path.Combine(Application.dataPath, EditorRelativePath());

            var persistent = Path.Combine(Application.persistentDataPath, FileName());
            if (File.Exists(persistent))
                return persistent;

            // Not a readable file on Android and WebGL (it is inside the APK /
            // behind HTTP); the Resources fallback covers those platforms.
            return Path.Combine(Application.streamingAssetsPath, FileName());
        }

        private string EditorRelativePath()
        {
            return string.IsNullOrWhiteSpace(m_Options.EditorRelativePath)
                ? FileName()
                : m_Options.EditorRelativePath.Replace('\\', '/').TrimStart('/');
        }

        private string FileName()
        {
            return string.IsNullOrWhiteSpace(m_Options.FileName)
                ? "logging.properties"
                : m_Options.FileName;
        }

        private string ExternalPath()
        {
            if (string.IsNullOrWhiteSpace(m_Options.EnvironmentVariable))
                return null;
            return Environment.GetEnvironmentVariable(m_Options.EnvironmentVariable);
        }
    }
}
