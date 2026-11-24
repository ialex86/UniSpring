using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security;
using System.Text;

namespace UniSpring.DependencyInjection.Editor
{
    /// <summary>
    /// Builds the link.xml that keeps everything the container reaches by
    /// reflection: every bean (any attribute derived from
    /// <see cref="ComponentAttribute"/>, so stereotypes added later such as
    /// <c>[LogSinkService]</c> are covered without changes here, plus
    /// <see cref="ComponentFamilyAttribute"/> families) and every type that
    /// declares <c>[Autowired]</c> members or <c>[PostConstruct]</c> methods.
    /// The same rules as the container's own scan are used
    /// (<see cref="ComponentAssemblies"/>, <see cref="ComponentScanner"/>).
    /// </summary>
    internal static class LinkXmlGenerator
    {
        private const BindingFlags DeclaredMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static string Generate()
        {
            return Generate(ComponentAssemblies.Find(null));
        }

        public static string Generate(IEnumerable<Assembly> assemblies)
        {
            var core = typeof(ComponentAssemblies).Assembly;
            var sorted = new List<Assembly>(assemblies);
            sorted.Sort((left, right) => string.CompareOrdinal(left.GetName().Name, right.GetName().Name));

            var xml = new StringBuilder();
            xml.Append("<linker>\n");
            for (var index = 0; index < sorted.Count; index++)
            {
                var assembly = sorted[index];
                var name = SecurityElement.Escape(assembly.GetName().Name);
                if (assembly == core)
                {
                    xml.Append("  <assembly fullname=\"").Append(name)
                        .Append("\" preserve=\"all\" ignoreIfMissing=\"1\"/>\n");
                    continue;
                }

                var types = PreservedTypes(assembly);
                if (types.Count == 0)
                    continue;

                // Editor-only assemblies are loaded here but are not part of a player build.
                xml.Append("  <assembly fullname=\"").Append(name).Append("\" ignoreIfMissing=\"1\">\n");
                for (var typeIndex = 0; typeIndex < types.Count; typeIndex++)
                {
                    xml.Append("    <type fullname=\"").Append(SecurityElement.Escape(types[typeIndex]))
                        .Append("\" preserve=\"all\"/>\n");
                }

                xml.Append("  </assembly>\n");
            }

            // Run-time proxies of advised beans (Mono).
            xml.Append("  <assembly fullname=\"System.Reflection.DispatchProxy\" preserve=\"all\" ignoreIfMissing=\"1\"/>\n");
            xml.Append("</linker>\n");
            return xml.ToString();
        }

        private static List<string> PreservedTypes(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = Array.FindAll(exception.Types, type => type != null);
            }

            var names = new List<string>();
            for (var index = 0; index < types.Length; index++)
            {
                var type = types[index];
                if (type.FullName == null || !NeedsPreserving(type))
                    continue;
                // link.xml separates a nested type from its owner with '/'.
                names.Add(type.FullName.Replace('+', '/'));
            }

            names.Sort(string.CompareOrdinal);
            return names;
        }

        private static bool NeedsPreserving(Type type)
        {
            try
            {
                if (ComponentScanner.IsComponent(type))
                    return true;

                var members = type.GetMembers(DeclaredMembers);
                for (var index = 0; index < members.Length; index++)
                {
                    if (members[index].IsDefined(typeof(AutowiredAttribute), false) ||
                        members[index].IsDefined(typeof(PostConstructAttribute), false))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                // A type whose attributes cannot be loaded cannot be a bean either.
                return false;
            }
        }
    }
}
