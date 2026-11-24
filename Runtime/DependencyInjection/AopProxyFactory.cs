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
        private static readonly MethodInfo s_CreateProxy = FindCreateProxy();

        private readonly Dictionary<Type, Dictionary<Type, IReadOnlyDictionary<MethodInfo, MethodAdvice>>> m_Plans =
            new Dictionary<Type, Dictionary<Type, IReadOnlyDictionary<MethodInfo, MethodAdvice>>>();
        private readonly Dictionary<Type, bool> m_Advised = new Dictionary<Type, bool>();
        private readonly HashSet<Type> m_Validated = new HashSet<Type>();
        private readonly ConditionalWeakTable<object, Dictionary<Type, object>> m_Proxies =
            new ConditionalWeakTable<object, Dictionary<Type, object>>();
        private IMethodInterceptor[] m_Interceptors;
        private bool m_Collecting;

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
            if (instance == null || !IsReady || instance is AopProxy)
                return instance;
            var implementation = instance.GetType();
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
            try
            {
                proxy = s_CreateProxy.MakeGenericMethod(contract, typeof(AopProxy)).Invoke(null, null);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "UniSpring DI could not make a proxy of " + instance.GetType().FullName + " as " + contract.FullName +
                    " (run-time proxies need Reflection.Emit: Mono, not IL2CPP; the interface must be visible).",
                    exception.InnerException ?? exception);
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
