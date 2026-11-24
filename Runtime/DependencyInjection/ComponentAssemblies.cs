using System;
using System.Collections.Generic;
using System.Reflection;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// The loaded assemblies that can declare beans: the DI core and every
    /// assembly that depends on it, directly or through another assembly. A
    /// stereotype derived from <see cref="ComponentAttribute"/> may live in
    /// between (for example <c>[LogSinkService]</c>), and the compiler then
    /// records no direct reference to the core.
    /// </summary>
    internal static class ComponentAssemblies
    {
        /// <param name="filter">Optional predicate; an assembly it rejects is neither scanned nor followed.</param>
        public static List<Assembly> Find(Func<Assembly, bool> filter)
        {
            var core = typeof(ComponentAssemblies).Assembly;
            var result = new List<Assembly> { core };
            var reached = new HashSet<string>(StringComparer.Ordinal) { core.GetName().Name };
            var pending = new List<KeyValuePair<Assembly, AssemblyName[]>>();

            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < loaded.Length; index++)
            {
                var candidate = loaded[index];
                if (candidate == null || candidate.IsDynamic || candidate == core)
                    continue;
                if (filter != null && !filter(candidate))
                    continue;

                AssemblyName[] references;
                try
                {
                    references = candidate.GetReferencedAssemblies();
                }
                catch
                {
                    // Best-effort scan: an assembly that cannot be inspected is
                    // skipped. This layer is below UniSpring.Logging, so it
                    // stays silent instead of creating a dependency cycle.
                    continue;
                }

                pending.Add(new KeyValuePair<Assembly, AssemblyName[]>(candidate, references));
            }

            var grown = true;
            while (grown)
            {
                grown = false;
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    if (!ReferencesAny(pending[index].Value, reached))
                        continue;

                    result.Add(pending[index].Key);
                    reached.Add(pending[index].Key.GetName().Name);
                    pending.RemoveAt(index);
                    grown = true;
                }
            }

            return result;
        }

        private static bool ReferencesAny(AssemblyName[] references, HashSet<string> names)
        {
            for (var index = 0; index < references.Length; index++)
            {
                if (names.Contains(references[index].Name))
                    return true;
            }

            return false;
        }
    }
}
