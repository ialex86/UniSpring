using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// A bean seen through one interface: an advised method runs through its interceptor chain, every other method
    /// goes straight to the bean. Made by the container (or <see cref="AopProxies.Create{T}"/>); public only because
    /// <see cref="DispatchProxy"/> derives the generated proxy type from it.
    /// </summary>
    public class AopProxy : DispatchProxy
    {
        private object m_Target;
        private IReadOnlyDictionary<MethodInfo, MethodAdvice> m_Advice;

        internal void Initialize(object target, IReadOnlyDictionary<MethodInfo, MethodAdvice> advice)
        {
            m_Target = target ?? throw new ArgumentNullException(nameof(target));
            m_Advice = advice ?? throw new ArgumentNullException(nameof(advice));
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (m_Advice.TryGetValue(Definition(method), out var advice))
                return new MethodInvocation(m_Target, method, advice, args).Proceed();
            return Call(method, m_Target, args);
        }

        private static MethodInfo Definition(MethodInfo method)
            => method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;

        /// <summary>Calls the bean through the interface method (virtual dispatch), with its own exception.</summary>
        internal static object Call(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException ?? exception).Throw();
                throw;
            }
        }
    }

    /// <summary>What advises one interface method of a bean type.</summary>
    internal sealed class MethodAdvice
    {
        public readonly MethodInfo TargetMethod;
        public readonly IMethodInterceptor[] Chain;

        public MethodAdvice(MethodInfo targetMethod, IMethodInterceptor[] chain)
        {
            TargetMethod = targetMethod;
            Chain = chain;
        }
    }

    internal sealed class MethodInvocation : IMethodInvocation
    {
        private readonly MethodAdvice m_Advice;
        private int m_Next;

        public MethodInvocation(object target, MethodInfo method, MethodAdvice advice, object[] arguments)
        {
            Target = target;
            Method = method;
            m_Advice = advice;
            Arguments = arguments;
        }

        public MethodInfo Method { get; }
        public MethodInfo TargetMethod => m_Advice.TargetMethod;
        public object Target { get; }
        public object[] Arguments { get; }

        public object Proceed()
        {
            if (m_Next < m_Advice.Chain.Length)
                return m_Advice.Chain[m_Next++].Invoke(this);
            return AopProxy.Call(Method, Target, Arguments);
        }
    }

    /// <summary>
    /// Proxies made by hand, with interceptors chosen by the caller: for unit tests of a service created with
    /// <c>new</c>, which the container would otherwise have advised. Game code gets its proxies from the container.
    /// </summary>
    public static class AopProxies
    {
        /// <summary>The target behind a proxy for <typeparamref name="T"/>, or the target itself when nothing matches.</summary>
        public static T Create<T>(T target, params IMethodInterceptor[] interceptors)
            where T : class
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            var factory = new AopProxyFactory();
            factory.Use(interceptors ?? Array.Empty<IMethodInterceptor>());
            return (T)factory.Advise(target, typeof(T));
        }
    }
}
