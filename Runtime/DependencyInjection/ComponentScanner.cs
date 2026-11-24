using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UniSpring.DependencyInjection
{
    internal static class ComponentScanner
    {
        public static List<BeanDefinition> Scan(IEnumerable<Assembly> assemblies)
        {
            if (assemblies == null)
                throw new ArgumentNullException(nameof(assemblies));

            var orderedAssemblies = assemblies
                .Where(assembly => assembly != null && !assembly.IsDynamic)
                .Distinct()
                .OrderBy(assembly => assembly.FullName, StringComparer.Ordinal)
                .ToList();

            var types = new List<Type>();
            for (var assemblyIndex = 0; assemblyIndex < orderedAssemblies.Count; assemblyIndex++)
                types.AddRange(GetLoadableTypes(orderedAssemblies[assemblyIndex]));
            return Scan(types);
        }

        /// <summary>Scans an explicit set of types (used by focused tests).</summary>
        public static List<BeanDefinition> Scan(IEnumerable<Type> candidateTypes)
        {
            if (candidateTypes == null)
                throw new ArgumentNullException(nameof(candidateTypes));

            var definitions = new List<BeanDefinition>();
            {
                foreach (var type in candidateTypes)
                {
                    if (!IsComponent(type))
                        continue;

                    if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                    {
                        throw new InvalidOperationException(
                            "UniSpring DI component " + type.FullName +
                            " derives from UnityEngine.Object. Unity-owned objects must request Autowire instead.");
                    }

                    var order = type
                        .GetCustomAttributes(typeof(OrderAttribute), false)
                        .OfType<OrderAttribute>()
                        .FirstOrDefault();
                    var lifetime = type
                        .GetCustomAttributes(typeof(LifetimeAttribute), false)
                        .OfType<LifetimeAttribute>()
                        .FirstOrDefault();

                    definitions.Add(new BeanDefinition
                    {
                        ImplementationType = type,
                        Order = order != null ? order.Value : 0,
                        Primary = type.IsDefined(typeof(PrimaryAttribute), false),
                        Lifetime = lifetime != null
                            ? lifetime.Value
                            : BeanLifetime.Application
                    });
                }
            }

            definitions.Sort(BeanDefinitionOrdering.Compare);
            return definitions;
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null).ToArray();
            }
        }

        internal static bool IsComponent(Type type)
        {
            if (type == null ||
                type.IsAbstract ||
                type.IsInterface ||
                type.ContainsGenericParameters)
            {
                return false;
            }

            return type.IsDefined(typeof(ComponentAttribute), false) ||
                   type.IsDefined(typeof(ServiceAttribute), false) ||
                   type.IsDefined(typeof(StrategyAttribute), false) ||
                   type.IsDefined(typeof(ComponentFamilyAttribute), true);
        }
    }
}
