using System;
using System.Collections.Generic;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Optional container-side check of the "Api-only between modules" rule:
    /// a bean may depend on a type from another module only when that type
    /// lives in the other module's <c>*.Api</c> assembly.
    ///
    /// The policy is described by <see cref="ModuleBoundaryOptions"/> and is
    /// disabled unless the options enable it.
    /// </summary>
    internal static class ModuleBoundaryPolicy
    {
        public static string Check(
            Type consumer,
            Type dependency,
            ModuleBoundaryOptions options)
        {
            if (consumer == null || dependency == null || options == null || !options.Enabled)
                return null;

            var offending = FindOffendingType(consumer.Assembly.GetName().Name, dependency, options);
            if (offending == null)
                return null;

            return consumer.FullName + " (" + consumer.Assembly.GetName().Name + ") depends on " +
                   offending.FullName + " (" + offending.Assembly.GetName().Name +
                   "), which is not part of that module's Api assembly.";
        }

        private static Type FindOffendingType(
            string consumerAssembly,
            Type dependency,
            ModuleBoundaryOptions options)
        {
            if (dependency.IsArray)
                return FindOffendingType(consumerAssembly, dependency.GetElementType(), options);

            if (dependency.IsGenericType)
            {
                foreach (var argument in dependency.GetGenericArguments())
                {
                    var nested = FindOffendingType(consumerAssembly, argument, options);
                    if (nested != null)
                        return nested;
                }
            }

            return IsAllowed(consumerAssembly, dependency.Assembly.GetName().Name, options)
                ? null
                : dependency;
        }

        private static bool IsAllowed(
            string consumerAssembly,
            string dependencyAssembly,
            ModuleBoundaryOptions options)
        {
            if (string.Equals(consumerAssembly, dependencyAssembly, StringComparison.Ordinal))
                return true;

            if (EndsWith(dependencyAssembly, options.ApiSuffix))
                return true;

            if (options.SharedAssemblies != null && options.SharedAssemblies.Contains(dependencyAssembly))
                return true;

            if (IsPlatformAssembly(dependencyAssembly))
                return true;

            // Only assemblies that follow the module layout have internals to
            // protect. Legacy assemblies without a layer suffix are not modules
            // in this sense, so depending on them is not a boundary breach.
            if (!IsModuleInternalAssembly(dependencyAssembly, options))
                return true;

            return string.Equals(
                ModuleOf(consumerAssembly),
                ModuleOf(dependencyAssembly),
                StringComparison.Ordinal);
        }

        private static bool IsModuleInternalAssembly(
            string name,
            ModuleBoundaryOptions options)
        {
            if (options.InternalLayerSuffixes == null)
                return false;

            for (var index = 0; index < options.InternalLayerSuffixes.Length; index++)
            {
                if (EndsWith(name, options.InternalLayerSuffixes[index]))
                    return true;
            }

            return false;
        }

        private static bool EndsWith(string value, string suffix)
        {
            return !string.IsNullOrEmpty(suffix) &&
                   value.EndsWith(suffix, StringComparison.Ordinal);
        }

        private static bool IsPlatformAssembly(string name)
        {
            return name == "mscorlib" ||
                   name == "netstandard" ||
                   name.StartsWith("System", StringComparison.Ordinal) ||
                   name.StartsWith("Unity", StringComparison.Ordinal);
        }

        private static string ModuleOf(string assemblyName)
        {
            var separator = assemblyName.LastIndexOf('.');
            return separator > 0 ? assemblyName.Substring(0, separator) : assemblyName;
        }
    }
}
