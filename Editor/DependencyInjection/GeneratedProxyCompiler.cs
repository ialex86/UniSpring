using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor.Compilation;
using Assembly = System.Reflection.Assembly;

// AssemblyBuilder is deprecated but still the only in-editor way to compile with Unity's own compiler and
// references; it serves the package's tests only.
#pragma warning disable CS0618

namespace UniSpring.DependencyInjection.Editor
{
    /// <summary>
    /// For the package's tests: compiles generated proxy source with Unity's compiler into a throw-away assembly
    /// and loads it, so the generated code runs in the editor as it would in a player. Poll <see cref="Done"/>
    /// from a <c>[UnityTest]</c>; the build runs on the editor's update loop.
    /// </summary>
    internal sealed class GeneratedProxyCompiler
    {
        private readonly string m_Output;
        private readonly List<string> m_Errors = new List<string>();

        private GeneratedProxyCompiler(string output)
        {
            m_Output = output;
        }

        public bool Done { get; private set; }
        public IReadOnlyList<string> Errors => m_Errors;
        public Assembly Assembly { get; private set; }

        public static GeneratedProxyCompiler Start(string source, params Assembly[] references)
        {
            var folder = Path.Combine("Library", "UniSpringProxyTests");
            Directory.CreateDirectory(folder);
            var name = GeneratedProxies.TestAssemblyPrefix + Guid.NewGuid().ToString("N");
            var script = Path.Combine(folder, name + ".cs");
            File.WriteAllText(script, source);
            var compiler = new GeneratedProxyCompiler(Path.Combine(folder, name + ".dll"));
            var builder = new AssemblyBuilder(compiler.m_Output, script) { flags = AssemblyBuilderFlags.EditorAssembly };
            var defaults = new HashSet<string>(builder.defaultReferences.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            builder.additionalReferences = references
                .Select(reference => reference.Location)
                .Where(location => !string.IsNullOrEmpty(location) && !defaults.Contains(Path.GetFileName(location)))
                .Distinct()
                .ToArray();
            builder.buildFinished += compiler.Finished;
            if (!builder.Build())
            {
                compiler.m_Errors.Add("Unity refused to start the build (another compilation is running).");
                compiler.Done = true;
            }

            return compiler;
        }

        private void Finished(string path, CompilerMessage[] messages)
        {
            foreach (var message in messages.Where(message => message.type == CompilerMessageType.Error))
                m_Errors.Add(message.message);
            if (m_Errors.Count == 0)
                Assembly = Assembly.LoadFrom(m_Output);
            Done = true;
        }
    }
}
