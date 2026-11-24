using System;
using System.Collections.Generic;
using System.Reflection;

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

        internal static RepositoryProxy Create(Type contract, IReadOnlyDictionary<MethodInfo, Func<object[], object>> handlers)
        {
            object proxy;
            try
            {
                proxy = CreateMethod().MakeGenericMethod(contract, typeof(RepositoryProxy)).Invoke(null, null);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "UniSpring Data could not implement " + contract.FullName + " (repository interfaces are " +
                    "implemented by run-time proxies: Mono, not IL2CPP).",
                    (exception as TargetInvocationException)?.InnerException ?? exception);
            }

            var repository = (RepositoryProxy)proxy;
            repository.Initialize(contract.FullName, handlers);
            return repository;
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
