using System;
using UniSpring.DependencyInjection;

namespace UniSpring.Transactions
{
    /// <summary>
    /// Keeps the container buildable when the application has not chosen a
    /// transaction store yet. It is replaced by any registered
    /// <see cref="ITransactionManager"/> instance, or by an implementation
    /// marked <c>[Primary]</c>; using it fails with an actionable message.
    /// </summary>
    [Component]
    internal sealed class UnconfiguredTransactionManager : ITransactionManager
    {
        public ITransaction Begin()
            => throw new InvalidOperationException(
                "No transaction store is configured. Implement " + typeof(ITransactionManager).FullName +
                ", mark the class with [Service] and [Primary], or register an instance with " +
                "ApplicationContext.RegisterInstance<ITransactionManager>(...) before warm-up.");
    }
}
