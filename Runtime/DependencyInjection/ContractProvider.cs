using System;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Supplies beans for contracts that no scanned class implements: a module that builds its objects on demand
    /// (for example a repository made from an interface the application declares) registers one as a bean. The
    /// container asks the providers only when a contract has no bean and no registered instance; what a provider
    /// creates is an application bean, created once and shared by every scope.
    /// </summary>
    public interface IContractProvider
    {
        /// <summary>Whether this provider makes the bean for <paramref name="contract"/>.</summary>
        bool Provides(Type contract);

        /// <summary>
        /// Creates the bean. Called once per contract, on the container's thread; take what it needs from
        /// <paramref name="context"/> (the root container).
        /// </summary>
        object Create(Type contract, ApplicationContext context);
    }
}
