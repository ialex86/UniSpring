using System;
using System.Collections.Generic;
using System.Reflection;
using UniSpring.DependencyInjection;

namespace UniSpring.Data.Internal
{
    /// <summary>
    /// The object behind a repository interface: every interface method has a handler prepared when the
    /// repository was created. Public only because <see cref="DispatchProxy"/> derives the generated type from it.
    /// </summary>
    public class RepositoryProxy : DispatchProxy
    {
        private IReadOnlyDictionary<MethodInfo, Func<object[], object>> m_Handlers;
        private string m_Contract;

        internal void Initialize(string contract, IReadOnlyDictionary<MethodInfo, Func<object[], object>> handlers)
        {
            m_Contract = contract;
            m_Handlers = handlers;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            Func<object[], object> handler;
            if (m_Handlers.TryGetValue(method, out handler))
                return handler(args);
            throw new NotSupportedException(m_Contract + "." + method.Name + " is not a repository method.");
        }

        internal static object Create(Type contract, IReadOnlyDictionary<MethodInfo, Func<object[], object>> handlers)
        {
            object proxy;
            if (GeneratedProxies.TryCreate(contract, new Handler(contract.FullName, handlers), out proxy))
                return proxy;
            try
            {
                proxy = CreateMethod().MakeGenericMethod(contract, typeof(RepositoryProxy)).Invoke(null, null);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "UniSpring Data could not implement " + contract.FullName + " (run-time proxies need Mono). " +
                    "On IL2CPP the implementation is generated in the editor into UniSpring.Generated.cs: " +
                    "regenerate it (Tools > UniSpring > Generate IL2CPP Proxies).",
                    (exception as TargetInvocationException)?.InnerException ?? exception);
            }

            var repository = (RepositoryProxy)proxy;
            repository.Initialize(contract.FullName, handlers);
            return repository;
        }

        /// <summary>The handlers behind a generated implementation of a repository or catalog interface.</summary>
        private sealed class Handler : IProxyHandler
        {
            private readonly string m_Contract;
            private readonly IReadOnlyDictionary<MethodInfo, Func<object[], object>> m_Handlers;

            public Handler(string contract, IReadOnlyDictionary<MethodInfo, Func<object[], object>> handlers)
            {
                m_Contract = contract;
                m_Handlers = handlers;
            }

            public object Invoke(MethodInfo method, object[] arguments, Func<object, object[], object> direct)
            {
                Func<object[], object> handler;
                if (GeneratedProxies.TryFind(m_Handlers, method, out handler))
                    return handler(arguments);
                throw new NotSupportedException(m_Contract + "." + method.Name + " is not a repository method.");
            }
        }

        private static MethodInfo CreateMethod()
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
