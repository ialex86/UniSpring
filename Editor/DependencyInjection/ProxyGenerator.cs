using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using Assembly = System.Reflection.Assembly;
using UnityEngine;

namespace UniSpring.DependencyInjection.Editor
{
    /// <summary>
    /// Keeps the IL2CPP proxies of the project's assemblies up to date: after every compilation (when the active
    /// target builds with IL2CPP, or the files already exist) each player assembly under <c>Assets</c> that needs
    /// proxies gets a <c>UniSpring.Generated.cs</c> next to its asmdef (<c>Assets/</c> for Assembly-CSharp). The file
    /// is compiled into IL2CPP players only. An IL2CPP build whose files are out of date fails before it starts:
    /// writing them during the build would recompile the scripts and abort it.
    /// </summary>
    [InitializeOnLoad]
    internal static class ProxyGenerator
    {
        public const string FileName = "UniSpring.Generated.cs";
        private const string RepositoryAttribute = "UniSpring.Data.RepositoryAttribute";
        private const string CatalogAttribute = "UniSpring.Data.CatalogAttribute";

        static ProxyGenerator()
        {
            EditorApplication.delayCall += () =>
            {
                if (UsesIl2Cpp(EditorUserBuildSettings.selectedBuildTargetGroup) || Plan().Any(file => File.Exists(file.Path)))
                    Generate(false);
            };
        }

        [MenuItem("Tools/UniSpring/Generate IL2CPP Proxies")]
        private static void GenerateFromMenu() => Generate(true);

        /// <summary>Writes the files that changed (and removes those no longer needed); returns how many.</summary>
        public static int Generate(bool report)
        {
            var changed = 0;
            var log = new StringBuilder();
            foreach (var file in Plan())
            {
                foreach (var skipped in file.Skipped)
                    log.Append("\n  ").Append(skipped);
                var current = File.Exists(file.Path) ? File.ReadAllText(file.Path) : null;
                if (file.Source == null)
                {
                    if (current != null && current.StartsWith(ProxySourceWriter.Header, StringComparison.Ordinal))
                    {
                        AssetDatabase.DeleteAsset(file.Path);
                        changed++;
                    }

                    continue;
                }

                if (current == file.Source)
                    continue;
                File.WriteAllText(file.Path, file.Source);
                changed++;
            }

            if (changed > 0)
                AssetDatabase.Refresh();
            if (log.Length > 0)
                Debug.LogWarning("UniSpring: these interfaces get no IL2CPP proxy (on IL2CPP resolving a bean through one of them fails):" + log);
            if (report)
                Debug.Log("UniSpring: IL2CPP proxies generated; " + changed + " file(s) changed.");
            return changed;
        }

        /// <summary>The files that are missing or differ from what would be generated now.</summary>
        public static List<string> Stale()
        {
            var stale = new List<string>();
            foreach (var file in Plan())
            {
                var current = File.Exists(file.Path) ? File.ReadAllText(file.Path) : null;
                if (file.Source != null && current != file.Source)
                    stale.Add(file.Path);
            }

            return stale;
        }

        internal sealed class GeneratedFile
        {
            public string Assembly;
            public string Path;
            public string Source;
            public List<string> Skipped = new List<string>();
        }

        /// <summary>One entry per player assembly that references UniSpring and lives under Assets.</summary>
        internal static List<GeneratedFile> Plan()
        {
            var files = new List<GeneratedFile>();
            var core = typeof(ComponentAttribute).Assembly.GetName().Name;
            var players = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Where(assembly => assembly.allReferences.Any(reference => Path.GetFileNameWithoutExtension(reference) == core))
                .ToDictionary(assembly => assembly.name);
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies().OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal))
            {
                var name = loaded.GetName().Name;
                if (!players.ContainsKey(name))
                    continue;
                var folder = Folder(name);
                if (folder == null)
                    continue;
                var file = new GeneratedFile { Assembly = name, Path = folder + "/" + FileName };
                var contracts = Contracts(loaded, file.Skipped);
                var hints = AotTypes(loaded);
                file.Source = contracts.Count == 0 && hints.Count == 0
                    ? null
                    : ProxySourceWriter.Write(name, contracts, hints, ProxySourceWriter.PlayerGuard);
                files.Add(file);
            }

            return files;
        }

        private static string Folder(string assemblyName)
        {
            var asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            var folder = asmdef != null ? Path.GetDirectoryName(asmdef)?.Replace('\\', '/') : assemblyName == "Assembly-CSharp" ? "Assets" : null;
            // Only the project's own code: packages are read-only.
            return folder != null && (folder == "Assets" || folder.StartsWith("Assets/", StringComparison.Ordinal)) ? folder : null;
        }

        /// <summary>
        /// The interfaces of <paramref name="assembly"/> that need a proxy: its repository and catalog interfaces,
        /// and every interface of a bean that looks advised (a method carries an attribute that is not the
        /// compiler's or the platform's).
        /// </summary>
        internal static List<Type> Contracts(Assembly assembly, List<string> skipped)
        {
            var contracts = new List<Type>();
            foreach (var type in Types(assembly))
            {
                try
                {
                    if (type.IsInterface && HasAttribute(type, RepositoryAttribute, CatalogAttribute))
                        Add(contracts, type, assembly, skipped);
                    else if (type.IsClass && !type.IsAbstract && ComponentScanner.IsComponent(type) && LooksAdvised(type))
                    {
                        foreach (var face in type.GetInterfaces())
                            Add(contracts, face, assembly, skipped);
                    }
                }
                catch (Exception exception)
                {
                    skipped.Add(type.FullName + ": " + exception.Message);
                }
            }

            return contracts;
        }

        /// <summary>
        /// Strategy registries with a value-type argument that beans of <paramref name="assembly"/> take in a
        /// constructor or an [Autowired] member: the container makes them with <c>Activator</c>, which IL2CPP can
        /// only do for an instantiation it compiled.
        /// </summary>
        internal static List<Type> AotTypes(Assembly assembly)
        {
            var found = new List<Type>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var type in Types(assembly))
            {
                try
                {
                    if (!type.IsClass || !ComponentScanner.IsComponent(type) && !typeof(MonoBehaviour).IsAssignableFrom(type))
                        continue;
                    var wanted = type.GetConstructors(flags).SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType)
                        .Concat(type.GetFields(flags).Where(field => field.IsDefined(typeof(AutowiredAttribute), false)).Select(field => field.FieldType))
                        .Concat(type.GetProperties(flags).Where(property => property.IsDefined(typeof(AutowiredAttribute), false)).Select(property => property.PropertyType));
                    foreach (var candidate in wanted)
                    {
                        if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(StrategyRegistry<,>) &&
                            candidate.GetGenericArguments().Any(argument => argument.IsValueType) &&
                            ProxySourceWriter.IsVisible(candidate, assembly) && !found.Contains(candidate))
                            found.Add(candidate);
                    }
                }
                catch (Exception)
                {
                    // A type whose members cannot be loaded is no bean either.
                }
            }

            return found;
        }

        private static void Add(List<Type> contracts, Type contract, Assembly assembly, List<string> skipped)
        {
            if (contracts.Contains(contract))
                return;
            var problem = ProxySourceWriter.WhyNot(contract, assembly);
            if (problem != null)
            {
                skipped.Add(contract.FullName + " (" + assembly.GetName().Name + "): " + problem);
                return;
            }

            contracts.Add(contract);
        }

        private static bool HasAttribute(Type type, params string[] names)
            => type.GetCustomAttributesData().Any(data => names.Contains(data.AttributeType.FullName));

        internal static bool LooksAdvised(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var method in current.GetMethods(flags))
                {
                    if (method.GetCustomAttributesData().Any(data => IsAdvice(data.AttributeType)))
                        return true;
                }
            }

            return false;
        }

        private static bool IsAdvice(Type attribute)
        {
            var space = attribute.Namespace ?? "";
            return !(space == "System" || space.StartsWith("System.", StringComparison.Ordinal) ||
                     space == "UnityEngine" || space.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                     space.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                     space.StartsWith("JetBrains", StringComparison.Ordinal) ||
                     space.StartsWith("NUnit", StringComparison.Ordinal) ||
                     attribute == typeof(PostConstructAttribute) || attribute == typeof(AutowiredAttribute));
        }

        private static IEnumerable<Type> Types(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
        }

        internal static bool UsesIl2Cpp(BuildTargetGroup group)
        {
            try
            {
                return PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group)) ==
                       ScriptingImplementation.IL2CPP;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>An IL2CPP build with missing or stale proxies stops before it starts, saying what to do.</summary>
    internal sealed class ProxyBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!ProxyGenerator.UsesIl2Cpp(report.summary.platformGroup))
                return;
            var stale = ProxyGenerator.Stale();
            if (stale.Count == 0)
                return;
            throw new BuildFailedException(
                "UniSpring: the IL2CPP proxies are out of date (" + string.Join(", ", stale) + "). Run Tools > UniSpring > " +
                "Generate IL2CPP Proxies (or let the editor finish compiling), commit the files and build again.");
        }
    }
}
