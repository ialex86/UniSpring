using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;

namespace UniSpring.DependencyInjection.Editor
{
    /// <summary>
    /// Builds the link.xml that keeps everything UniSpring reaches by reflection:
    /// <list type="bullet">
    /// <item>every bean (any attribute derived from <see cref="ComponentAttribute"/>, so stereotypes added later are
    /// covered without changes here, plus <see cref="ComponentFamilyAttribute"/> families) and every type that
    /// declares <c>[Autowired]</c> members or <c>[PostConstruct]</c> methods;</item>
    /// <item>the types those beans name as generic arguments of their interfaces (the records and domain types of
    /// mappers and validators, the events of listeners): serializers and query methods read their members;</item>
    /// <item>the stored types of <c>[Repository]</c> and <c>[Catalog]</c> interfaces, and
    /// <c>[ConfigurationProperties]</c> classes;</item>
    /// <item>the generated IL2CPP proxies.</item>
    /// </list>
    /// The same rules as the container's own scan are used (<see cref="ComponentAssemblies"/>,
    /// <see cref="ComponentScanner"/>).
    /// </summary>
    internal static class LinkXmlGenerator
    {
        private const BindingFlags DeclaredMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly string[] DataContracts = { "UniSpring.Data.RepositoryAttribute", "UniSpring.Data.CatalogAttribute" };
        private const string ConfigurationProperties = "UniSpring.Data.ConfigurationPropertiesAttribute";

        public static string Generate()
        {
            return Generate(ComponentAssemblies.Find(null));
        }

        public static string Generate(IEnumerable<Assembly> assemblies)
        {
            var core = typeof(ComponentAssemblies).Assembly;
            var kept = new Dictionary<Assembly, SortedSet<string>>();
            foreach (var assembly in assemblies)
            {
                if (assembly == core)
                    continue;
                foreach (var type in Types(assembly))
                    Collect(type, kept);
            }

            var sorted = kept.Keys.Concat(new[] { core }).Distinct()
                .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal).ToList();
            var xml = new StringBuilder();
            xml.Append("<linker>\n");
            foreach (var assembly in sorted)
            {
                var name = SecurityElement.Escape(assembly.GetName().Name);
                if (assembly == core)
                {
                    xml.Append("  <assembly fullname=\"").Append(name)
                        .Append("\" preserve=\"all\" ignoreIfMissing=\"1\"/>\n");
                    continue;
                }

                // Editor-only assemblies are loaded here but are not part of a player build.
                xml.Append("  <assembly fullname=\"").Append(name).Append("\" ignoreIfMissing=\"1\">\n");
                foreach (var typeName in kept[assembly])
                {
                    xml.Append("    <type fullname=\"").Append(SecurityElement.Escape(typeName))
                        .Append("\" preserve=\"all\"/>\n");
                }

                xml.Append("  </assembly>\n");
            }

            // Run-time proxies of advised beans (Mono).
            xml.Append("  <assembly fullname=\"System.Reflection.DispatchProxy\" preserve=\"all\" ignoreIfMissing=\"1\"/>\n");
            xml.Append("</linker>\n");
            return xml.ToString();
        }

        private static void Collect(Type type, Dictionary<Assembly, SortedSet<string>> kept)
        {
            try
            {
                if (ComponentScanner.IsComponent(type))
                {
                    Keep(type, kept);
                    foreach (var face in type.GetInterfaces().Where(face => face.IsGenericType))
                        KeepArguments(face, kept);
                    return;
                }

                if (type.IsInterface && HasAttribute(type, DataContracts))
                {
                    Keep(type, kept);
                    foreach (var face in type.GetInterfaces().Where(face => face.IsGenericType))
                        KeepArguments(face, kept);
                    return;
                }

                if (HasAttribute(type, ConfigurationProperties) || HasInjectionPoints(type) ||
                    type.Namespace != null && type.Namespace.StartsWith("UniSpring.Generated", StringComparison.Ordinal))
                    Keep(type, kept);
            }
            catch (Exception)
            {
                // A type whose attributes cannot be loaded cannot be a bean either.
            }
        }

        private static bool HasInjectionPoints(Type type)
        {
            var members = type.GetMembers(DeclaredMembers);
            for (var index = 0; index < members.Length; index++)
            {
                if (members[index].IsDefined(typeof(AutowiredAttribute), false) ||
                    members[index].IsDefined(typeof(PostConstructAttribute), false))
                    return true;
            }

            return false;
        }

        private static void KeepArguments(Type type, Dictionary<Assembly, SortedSet<string>> kept)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                var plain = argument.IsArray ? argument.GetElementType() : argument;
                if (plain == null || plain.IsGenericParameter)
                    continue;
                if (plain.IsGenericType)
                {
                    KeepArguments(plain, kept);
                    continue;
                }

                if (!IsPlatformType(plain))
                    Keep(plain, kept);
            }
        }

        private static bool IsPlatformType(Type type)
        {
            var name = type.Assembly.GetName().Name;
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEngine", StringComparison.Ordinal) || name.StartsWith("Unity.", StringComparison.Ordinal);
        }

        private static void Keep(Type type, Dictionary<Assembly, SortedSet<string>> kept)
        {
            if (type.FullName == null)
                return;
            SortedSet<string> names;
            if (!kept.TryGetValue(type.Assembly, out names))
                kept.Add(type.Assembly, names = new SortedSet<string>(StringComparer.Ordinal));
            // link.xml separates a nested type from its owner with '/'.
            names.Add(type.FullName.Replace('+', '/'));
        }

        private static bool HasAttribute(Type type, params string[] names)
            => type.GetCustomAttributesData().Any(data => names.Contains(data.AttributeType.FullName));

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
    }
}
