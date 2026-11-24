using UniSpring.Logging.Integration;
using UnityEditor;

namespace UniSpring.Logging.Editor
{
    /// <summary>
    /// Editor composition root for logging. Edit-mode tools (bakers, importers,
    /// editor windows) run without the runtime container, so without this they
    /// would only reach the startup fallback and ignore logging.properties.
    /// It installs only when no backend is active: a running container's
    /// backend always wins and is restored when that container is disposed.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorGameLogBootstrap
    {
        static EditorGameLogBootstrap()
        {
            InstallIfMissing();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode)
                return;
            InstallIfMissing();
        }

        private static void InstallIfMissing()
        {
            if (GameLog.HasInstalledBackend)
                return;
            GameLog.TryInstallBackend(DefaultGameLogBackend.Create());
        }
    }
}
