using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace UniSpring.DependencyInjection.Editor
{
    /// <summary>
    /// Hands the generated link.xml to the Unity linker on every player
    /// build, so managed code stripping keeps beans that are only created by
    /// reflection. Nothing has to be configured in the consuming project.
    /// </summary>
    internal sealed class UniSpringLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var directory = Path.GetFullPath(Path.Combine("Library", "UniSpring"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "link.xml");
            File.WriteAllText(path, LinkXmlGenerator.Generate());
            return path;
        }
    }
}
