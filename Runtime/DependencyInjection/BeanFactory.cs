using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace UniSpring.DependencyInjection
{
    internal sealed class BeanFactory
    {
        /// <summary>
        /// Reflection metadata per type, computed once: autowired fields,
        /// settable properties, [PostConstruct] methods and the constructor.
        /// Autowiring a prefab instance no longer walks the type hierarchy.
        /// </summary>
        private sealed class InjectionPlan
        {
            public ConstructorInfo Constructor;
            public Type[] ConstructorParameters;
            public FieldInfo[] Fields;
            public PropertyInfo[] Properties;
            public MethodInfo[] PostConstruct;
        }

        private static readonly ConcurrentDictionary<Type, InjectionPlan> s_Plans =
            new ConcurrentDictionary<Type, InjectionPlan>();

        private readonly Func<Type, object> m_Resolver;
        private readonly Action<Type, Type> m_OnDependency;

        public BeanFactory(Func<Type, object> dependencyResolver)
            : this(dependencyResolver, null)
        {
        }

        /// <param name="onDependency">Called with (consumer, dependency) before each resolution.</param>
        public BeanFactory(Func<Type, object> dependencyResolver, Action<Type, Type> onDependency)
        {
            m_Resolver = dependencyResolver ?? throw new ArgumentNullException(nameof(dependencyResolver));
            m_OnDependency = onDependency;
        }

        public object Create(BeanDefinition definition)
        {
            var type = definition.ImplementationType;
            var plan = PlanFor(type, true);
            var arguments = new object[plan.ConstructorParameters.Length];
            for (var index = 0; index < arguments.Length; index++)
                arguments[index] = Resolve(type, plan.ConstructorParameters[index]);

            object instance;
            try
            {
                instance = plan.Constructor.Invoke(arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "UniSpring DI failed to create bean " + type.FullName + ".",
                    exception.InnerException ?? exception);
            }

            Inject(instance, plan);
            return instance;
        }

        public void Autowire(object target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            Inject(target, PlanFor(target.GetType(), false));
        }

        private void Inject(object target, InjectionPlan plan)
        {
            var type = target.GetType();
            for (var index = 0; index < plan.Fields.Length; index++)
                plan.Fields[index].SetValue(target, Resolve(type, plan.Fields[index].FieldType));
            for (var index = 0; index < plan.Properties.Length; index++)
            {
                var property = plan.Properties[index];
                property.GetSetMethod(true).Invoke(target, new[] { Resolve(type, property.PropertyType) });
            }

            for (var index = 0; index < plan.PostConstruct.Length; index++)
            {
                try
                {
                    plan.PostConstruct[index].Invoke(target, null);
                }
                catch (TargetInvocationException exception)
                {
                    throw new InvalidOperationException(
                        "UniSpring DI [PostConstruct] " + type.FullName + "." + plan.PostConstruct[index].Name + " failed.",
                        exception.InnerException ?? exception);
                }
            }
        }

        private object Resolve(Type consumer, Type dependency)
        {
            m_OnDependency?.Invoke(consumer, dependency);
            return m_Resolver(dependency);
        }

        private static InjectionPlan PlanFor(Type type, bool needsConstructor)
        {
            var plan = s_Plans.GetOrAdd(type, BuildPlan);
            if (needsConstructor && plan.Constructor == null)
                SelectConstructor(type); // throws the precise reason
            return plan;
        }

        private static InjectionPlan BuildPlan(Type type)
        {
            var fields = new List<FieldInfo>();
            var properties = new List<PropertyInfo>();
            var postConstruct = new List<MethodInfo>();
            var postConstructSlots = new HashSet<MethodInfo>();
            foreach (var current in EnumerateTypeHierarchy(type))
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var field in current.GetFields(flags))
                {
                    if (!field.IsDefined(typeof(AutowiredAttribute), true))
                        continue;
                    if (field.IsInitOnly)
                        throw new InvalidOperationException(
                            "[Autowired] cannot target readonly field " + current.FullName + "." + field.Name + ".");
                    fields.Add(field);
                }

                foreach (var property in current.GetProperties(flags))
                {
                    if (!property.IsDefined(typeof(AutowiredAttribute), true))
                        continue;
                    if (property.GetSetMethod(true) == null || property.GetIndexParameters().Length != 0)
                        throw new InvalidOperationException(
                            "[Autowired] property must have a setter: " + current.FullName + "." + property.Name + ".");
                    properties.Add(property);
                }

                foreach (var method in current.GetMethods(flags))
                {
                    if (!method.IsDefined(typeof(PostConstructAttribute), true))
                        continue;
                    if (method.GetParameters().Length != 0)
                        throw new InvalidOperationException(
                            "[PostConstruct] method must have no parameters: " + current.FullName + "." + method.Name + ".");
                    // The attribute is inherited: an override and the virtual method it overrides are one
                    // callback, and invoking both would run the override twice.
                    if (!postConstructSlots.Add(method.GetBaseDefinition()))
                        continue;
                    postConstruct.Add(method);
                }
            }

            var constructor = TrySelectConstructor(type);
            return new InjectionPlan
            {
                Constructor = constructor,
                ConstructorParameters = constructor != null
                    ? Array.ConvertAll(constructor.GetParameters(), parameter => parameter.ParameterType)
                    : Type.EmptyTypes,
                Fields = fields.ToArray(),
                Properties = properties.ToArray(),
                PostConstruct = postConstruct.ToArray(),
            };
        }

        private static ConstructorInfo TrySelectConstructor(Type type)
        {
            if (type.IsAbstract || typeof(UnityEngine.Object).IsAssignableFrom(type))
                return null;
            try
            {
                return SelectConstructor(type);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static ConstructorInfo SelectConstructor(Type type)
        {
            var constructors = type.GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

            ConstructorInfo autowired = null;
            var autowiredCount = 0;
            for (var index = 0; index < constructors.Length; index++)
            {
                if (!constructors[index].IsDefined(typeof(AutowiredAttribute), true))
                    continue;

                autowired = constructors[index];
                autowiredCount++;
            }

            if (autowiredCount == 1)
                return autowired;

            if (autowiredCount > 1)
            {
                throw new InvalidOperationException(
                    "UniSpring DI bean " + type.FullName +
                    " has more than one [Autowired] constructor.");
            }

            if (constructors.Length == 1)
                return constructors[0];

            ConstructorInfo publicConstructor = null;
            var publicCount = 0;
            for (var index = 0; index < constructors.Length; index++)
            {
                if (!constructors[index].IsPublic)
                    continue;

                publicConstructor = constructors[index];
                publicCount++;
            }

            if (publicCount == 1)
                return publicConstructor;

            throw new InvalidOperationException(
                "UniSpring DI bean " + type.FullName +
                " has multiple constructors. Mark exactly one with [Autowired].");
        }

        /// <summary>
        /// Own members only: Unity's base classes (MonoBehaviour, Component,
        /// Object) never carry [Autowired] and are not walked.
        /// </summary>
        private static IEnumerable<Type> EnumerateTypeHierarchy(Type type)
        {
            for (var current = type;
                 current != null && current != typeof(object) &&
                 current != typeof(UnityEngine.MonoBehaviour) && current != typeof(UnityEngine.Object) &&
                 current != typeof(UnityEngine.Component) && current != typeof(UnityEngine.Behaviour) &&
                 current != typeof(UnityEngine.ScriptableObject);
                 current = current.BaseType)
            {
                yield return current;
            }
        }
    }
}
