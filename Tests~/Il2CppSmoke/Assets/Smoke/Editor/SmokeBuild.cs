using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UniSpringSmoke.Editor
{
    /// <summary>Batch-mode steps of Tests~/il2cpp-smoke.sh.</summary>
    public static class SmokeBuild
    {
        private const string ScenePath = "Assets/Smoke/Smoke.unity";

        /// <summary>Step 1: IL2CPP with high stripping, a scene with the runner, and the generated proxies.</summary>
        public static void Prepare()
        {
            var target = NamedBuildTarget.Standalone;
            PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.High);
            PlayerSettings.productName = "UniSpringSmoke";

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Smoke").AddComponent<SmokeRunner>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            if (!EditorApplication.ExecuteMenuItem("Tools/UniSpring/Generate IL2CPP Proxies"))
                throw new InvalidOperationException("The UniSpring proxy generator menu is missing.");
        }

        /// <summary>Step 2 (a fresh editor, so the generated files are imported): the macOS IL2CPP player.</summary>
        public static void Player()
        {
            var output = Environment.GetEnvironmentVariable("UNISPRING_SMOKE_OUTPUT") ?? "Build/UniSpringSmoke.app";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None
            });
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                throw new InvalidOperationException("The IL2CPP build failed: " + report.summary.result);
            Debug.Log("UniSpring smoke player built: " + Path.GetFullPath(output));
        }
    }
}
