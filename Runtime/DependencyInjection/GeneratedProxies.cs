using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// What a generated proxy calls for every interface method. Support for code UniSpring generates for IL2CPP
    /// (<c>UniSpring.Generated.cs</c>); not meant to be implemented or called by game code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IProxyHandler
    {
        /// <summary>
        /// Handles a call of <paramref name="method"/> (the interface method; a generic method closed over the
        /// call's type arguments). <paramref name="direct"/> calls the same method on a target object without
        /// reflection; handlers without a target ignore it.
        /// </summary>
        object Invoke(MethodInfo method, object[] arguments, Func<object, object[], object> direct);
    }

    /// <summary>
    /// The base of the proxies UniSpring generates in the editor for IL2CPP, which cannot create proxy types at
    /// run time. Support for generated code; not meant to be derived from by game code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class GeneratedProxy
    {
        private readonly IProxyHandler m_Handler;

        protected GeneratedProxy(IProxyHandler handler)
        {
            m_Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        protected object Invoke(MethodInfo method, object[] arguments, Func<object, object[], object> direct)
            => m_Handler.Invoke(method, arguments, direct);

        /// <summary>The interface method the generator saw, found again by name and signature.</summary>
        protected static MethodInfo MethodOf(Type declaring, string name, string signature)
            => GeneratedProxies.MethodOf(declaring, name, signature);
    }

    /// <summary>Names the generated proxy of an interface in its assembly. Written by the generator.</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class GeneratedProxyAttribute : Attribute
    {
        public GeneratedProxyAttribute(Type contract, Type proxy)
        {
            Contract = contract;
            Proxy = proxy;
        }

        public Type Contract { get; }
        public Type Proxy { get; }
    }

    /// <summary>
    /// The generated proxies of the loaded assemblies, by interface. A proxy made here is used instead of a
    /// run-time <see cref="System.Reflection.DispatchProxy"/>: on IL2CPP it is the only way to have one.
    /// </summary>
    internal static class GeneratedProxies
    {
        /// <summary>Assemblies the package's tests compile from generated code; only those tests use them.</summary>
        internal const string TestAssemblyPrefix = "UniSpring.GeneratedProxyTest";

        private static readonly object s_Gate = new object();
        private static Dictionary<Type, Type> s_Proxies;
        private static Dictionary<Type, Type> s_Override;

        /// <summary>Tests: use exactly these proxies (null: the loaded assemblies' again).</summary>
        internal static void Override(Dictionary<Type, Type> proxies)
        {
            lock (s_Gate)
                s_Override = proxies;
        }

        /// <summary>The proxies an assembly declares.</summary>
        internal static Dictionary<Type, Type> Declared(Assembly assembly)
        {
            var proxies = new Dictionary<Type, Type>();
            foreach (GeneratedProxyAttribute attribute in assembly.GetCustomAttributes(typeof(GeneratedProxyAttribute), false))
            {
                if (attribute.Contract != null && attribute.Proxy != null)
                    proxies[attribute.Contract] = attribute.Proxy;
            }

            return proxies;
        }

        public static bool TryCreate(Type contract, IProxyHandler handler, out object proxy)
        {
            proxy = null;
            var type = Find(contract);
            if (type == null)
                return false;
            proxy = Activator.CreateInstance(type, handler);
            return true;
        }

        public static Type Find(Type contract)
        {
            Type proxy;
            return Proxies().TryGetValue(contract, out proxy) ? proxy : null;
        }

        private static Dictionary<Type, Type> Proxies()
        {
            lock (s_Gate)
            {
                if (s_Override != null)
                    return s_Override;
                if (s_Proxies != null)
                    return s_Proxies;
                var proxies = new Dictionary<Type, Type>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Dictionary<Type, Type> declared;
                    try
                    {
                        if (assembly.GetName().Name.StartsWith(TestAssemblyPrefix, StringComparison.Ordinal))
                            continue;
                        declared = Declared(assembly);
                    }
                    catch (Exception)
                    {
                        continue; // an assembly whose attributes cannot be loaded has no proxies for us
                    }

                    foreach (var pair in declared)
                        proxies[pair.Key] = pair.Value;
                }

                s_Proxies = proxies;
                return proxies;
            }
        }

        /// <summary>
        /// Looks a method up in a table made from another reflection call (an interface map, another
        /// <c>GetMethods</c>): the same method may come back as a different <see cref="MethodInfo"/> object.
        /// </summary>
        public static bool TryFind<T>(IReadOnlyDictionary<MethodInfo, T> table, MethodInfo method, out T value)
        {
            if (table.TryGetValue(method, out value))
                return true;
            foreach (var pair in table)
            {
                if (pair.Key.MethodHandle == method.MethodHandle && pair.Key.DeclaringType == method.DeclaringType)
                {
                    value = pair.Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Generic arity and parameter types: what tells overloads apart, the same in the editor and the player.</summary>
        public static string Signature(MethodInfo method)
        {
            var text = new StringBuilder();
            text.Append(method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0).Append('(');
            var parameters = method.GetParameters();
            for (var index = 0; index < parameters.Length; index++)
            {
                if (index > 0)
                    text.Append(',');
                text.Append(parameters[index].ParameterType);
            }

            return text.Append(')').ToString();
        }

        public static MethodInfo MethodOf(Type declaring, string name, string signature)
        {
            MethodInfo only = null;
            var named = 0;
            foreach (var method in declaring.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (method.Name != name)
                    continue;
                if (Signature(method) == signature)
                    return method;
                named++;
                only = method;
            }

            // A runtime that spells type names differently still finds a method that has no overloads.
            if (named == 1)
                return only;

            throw new MissingMethodException(
                "UniSpring: the generated proxy of " + declaring.FullName + " names " + name + signature +
                ", which the interface no longer has. Regenerate the proxies (Tools > UniSpring > Generate IL2CPP Proxies).");
        }
    }

    /// <summary>A bean behind a generated proxy: advised methods go through their chain, the others straight to the bean.</summary>
    internal sealed class AdvisedHandler : IProxyHandler
    {
        private readonly object m_Target;
        private readonly IReadOnlyDictionary<MethodInfo, MethodAdvice> m_Advice;

        public AdvisedHandler(object target, IReadOnlyDictionary<MethodInfo, MethodAdvice> advice)
        {
            m_Target = target;
            m_Advice = advice;
        }

        public object Invoke(MethodInfo method, object[] arguments, Func<object, object[], object> direct)
        {
            MethodAdvice advice;
            if (GeneratedProxies.TryFind(m_Advice, AopProxy.Definition(method), out advice))
                return new MethodInvocation(m_Target, method, advice, arguments, direct).Proceed();
            return direct(m_Target, arguments);
        }
    }
}
