using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// The container's AOP: finds which methods of a bean type its interceptors advise and hands the bean out through
    /// an <see cref="AopProxy"/> for each interface it is resolved by. One per root context, shared by its scopes;
    /// used on the context's thread only (the proxies themselves are safe on any thread).
    /// </summary>
    internal sealed class AopProxyFactory
    {
        // Looked up on first use: a container without advised beans never needs it, and must still
        // build where run-time proxies are unavailable (IL2CPP).
        private static MethodInfo s_CreateProxy;

        private readonly Dictionary<Type, Dictionary<Type, IReadOnlyDictionary<MethodInfo, MethodAdvice>>> m_Plans =
            new Dictionary<Type, Dictionary<Type, IReadOnlyDictionary<MethodInfo, MethodAdvice>>>();
        private readonly Dictionary<Type, bool> m_Advised = new Dictionary<Type, bool>();
        private readonly HashSet<Type> m_Validated = new HashSet<Type>();
        private readonly ConditionalWeakTable<object, Dictionary<Type, object>> m_Proxies =
            new ConditionalWeakTable<object, Dictionary<Type, object>>();
        private IMethodInterceptor[] m_Interceptors;
        private bool m_Collecting;
        private readonly HashSet<Type> m_EarlyExposures = new HashSet<Type>();

        /// <summary>The interceptors are known: from here on beans are checked and proxied.</summary>
        public bool IsReady => m_Interceptors != null;

        /// <summary>
        /// Takes the interceptors from <paramref name="resolveAll"/> once. While they (and what they depend on) are
        /// being created, beans are handed out as they are.
        /// </summary>
        public void Collect(Func<IReadOnlyList<object>> resolveAll)
        {
            if (m_Interceptors != null || m_Collecting)
                return;
            m_Collecting = true;
            try
            {
                var beans = resolveAll();
                var interceptors = new IMethodInterceptor[beans.Count];
                for (var index = 0; index < beans.Count; index++)
                    interceptors[index] = (IMethodInterceptor)beans[index];
                Use(interceptors);
                ReportEarlyExposures();
            }
            finally
            {
                m_Collecting = false;
            }
        }

        public void Use(IMethodInterceptor[] interceptors)
        {
            m_Interceptors = interceptors ?? throw new ArgumentNullException(nameof(interceptors));
        }

        /// <summary>
        /// A bean handed out before the interceptors were known (to an interceptor, or inside the configure hook)
        /// went out without a proxy. That is allowed, but when the bean turns out to have advised methods the holder
        /// of that reference calls them without advice: say so once, as Spring does for beans that are "not eligible
        /// for auto-proxying". Everyone who resolves the bean later gets the proxy.
        /// </summary>
        private void ReportEarlyExposures()
        {
            foreach (var implementation in m_EarlyExposures)
            {
                if (!IsAdvised(implementation))
                    continue;
                UnityEngine.Debug.LogWarning(
                    "UniSpring DI: " + implementation.FullName + " has advised methods but was handed out before " +
                    "the interceptors were ready (to an interceptor's dependency chain or inside the configure " +
                    "hook). That reference is not proxied: calls made through it skip the advice. Beans resolved " +
                    "later get the proxy.");
            }

            m_EarlyExposures.Clear();
        }

        /// <summary>
        /// Refuses a bean type whose advised method no interface method maps to: its advice could never run.
        /// </summary>
        public void Validate(Type implementation)
        {
            if (!IsAdvised(implementation) || m_Validated.Contains(implementation))
                return;
            var mapped = InterfaceTargets(implementation);
            foreach (var method in OwnMethods(implementation))
            {
                if (mapped.Contains(method.MethodHandle) || Chain(method).Length == 0)
                    continue;
                throw new InvalidOperationException(
                    "UniSpring DI: " + implementation.FullName + "." + method.Name + " is advised (" +
                    Chain(method)[0].GetType().Name + ") but implements no interface method. Only calls through an " +
                    "interface the bean is resolved by are intercepted; a private method or a call the bean makes to " +
                    "itself never is. Put the advice on the interface method that is called from outside.");
            }

            m_Validated.Add(implementation);
        }

        /// <summary>The bean as <paramref name="contract"/>: through a proxy when the contract has advised methods.</summary>
        public object Advise(object instance, Type contract)
        {
            if (instance == null || instance is AopProxy || instance is GeneratedProxy)
                return instance;
            var implementation = instance.GetType();
            if (!IsReady)
            {
                m_EarlyExposures.Add(implementation);
                return instance;
            }

            if (!IsAdvised(implementation))
                return instance;
            if (!contract.IsInterface)
            {
                if (contract == typeof(object))
                    return instance;
                throw new InvalidOperationException(
                    "UniSpring DI: " + implementation.FullName + " has advised methods, so it is handed out through " +
                    "interfaces only; resolve it as one of them instead of " + contract.FullName + ".");
            }

            var plan = PlanFor(implementation, contract);
            if (plan.Count == 0)
                return instance;
            var proxies = m_Proxies.GetOrCreateValue(instance);
            if (proxies.TryGetValue(contract, out var existing))
                return existing;
            var proxy = CreateProxy(contract, instance, plan);
            proxies.Add(contract, proxy);
            return proxy;
        }

        private bool IsAdvised(Type implementation)
        {
            if (m_Interceptors == null || m_Interceptors.Length == 0)
                return false;
            if (m_Advised.TryGetValue(implementation, out var advised))
                return advised;
            advised = false;
            foreach (var method in OwnMethods(implementation))
                advised |= Chain(method).Length > 0;
            m_Advised.Add(implementation, advised);
            return advised;
        }

        private IReadOnlyDictionary<MethodInfo, MethodAdvice> PlanFor(Type implementation, Type contract)
        {
            if (!m_Plans.TryGetValue(implementation, out var byContract))
            {
                byContract = new Dictionary<Type, IReadOnlyDictionary<MethodInfo, MethodAdvice>>();
                m_Plans.Add(implementation, byContract);
            }

            if (byContract.TryGetValue(contract, out var plan))
                return plan;
            var advice = new Dictionary<MethodInfo, MethodAdvice>();
            foreach (var face in WithBaseInterfaces(contract))
            {
                var map = implementation.GetInterfaceMap(face);
                for (var index = 0; index < map.InterfaceMethods.Length; index++)
                {
                    var chain = Chain(map.TargetMethods[index]);
                    if (chain.Length > 0)
                        advice[map.InterfaceMethods[index]] = new MethodAdvice(map.TargetMethods[index], chain);
                }
            }

            byContract.Add(contract, advice);
            return advice;
        }

        private IMethodInterceptor[] Chain(MethodInfo targetMethod)
        {
            var chain = new List<IMethodInterceptor>();
            foreach (var interceptor in m_Interceptors)
            {
                if (interceptor.Matches(targetMethod))
                    chain.Add(interceptor);
            }

            return chain.ToArray();
        }

        private static object CreateProxy(Type contract, object instance, IReadOnlyDictionary<MethodInfo, MethodAdvice> plan)
        {
            object proxy;
            if (GeneratedProxies.TryCreate(contract, new AdvisedHandler(instance, plan), out proxy))
                return proxy;
            try
            {
                var create = s_CreateProxy ?? (s_CreateProxy = FindCreateProxy());
                proxy = create.MakeGenericMethod(contract, typeof(AopProxy)).Invoke(null, null);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "UniSpring DI could not make a proxy of " + instance.GetType().FullName + " as " + contract.FullName +
                    " (run-time proxies need Reflection.Emit: Mono, not IL2CPP; the interface must be visible). " +
                    "On IL2CPP the proxy is generated in the editor into UniSpring.Generated.cs: regenerate it " +
                    "(Tools > UniSpring > Generate IL2CPP Proxies) and check the editor log for why this interface " +
                    "was left out. A bean gets proxies when one of its methods carries an attribute.",
                    (exception as TargetInvocationException)?.InnerException ?? exception);
            }

            ((AopProxy)proxy).Initialize(instance, plan);
            return proxy;
        }

        /// <summary>By handle: an inherited method seen from the base and from the bean type is one method.</summary>
        private static HashSet<RuntimeMethodHandle> InterfaceTargets(Type implementation)
        {
            var targets = new HashSet<RuntimeMethodHandle>();
            foreach (var face in implementation.GetInterfaces())
            {
                foreach (var target in implementation.GetInterfaceMap(face).TargetMethods)
                    targets.Add(target.MethodHandle);
            }

            return targets;
        }

        private static IEnumerable<Type> WithBaseInterfaces(Type contract)
        {
            yield return contract;
            foreach (var face in contract.GetInterfaces())
                yield return face;
        }

        /// <summary>Instance methods the type and its bases declare (not those of <see cref="object"/>).</summary>
        private static IEnumerable<MethodInfo> OwnMethods(Type implementation)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;
            for (var current = implementation; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var method in current.GetMethods(flags))
                    yield return method;
            }
        }

        private static MethodInfo FindCreateProxy()
        {
            foreach (var method in typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == nameof(DispatchProxy.Create) && method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 2)
                    return method;
            }

            throw new MissingMethodException(typeof(DispatchProxy).FullName, "Create<T, TProxy>");
        }
    }
}
