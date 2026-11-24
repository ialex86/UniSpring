using System;
using System.Reflection;
using UniSpring.DependencyInjection;

namespace UniSpring.Transactions
{
    /// <summary>
    /// The advice behind <see cref="TransactionalAttribute"/>: a
    /// <see cref="ITransactionManager.Begin"/> around the whole call, and
    /// around its task for an async method. The transaction is committed when
    /// the call ends; with <see cref="TransactionalAttribute.RollbackOnError"/>
    /// it is rolled back instead when the method throws.
    /// <para>
    /// The container creates this interceptor during warm-up, so the package
    /// ships a manager that refuses to begin until the application registers
    /// its own. Mark an <see cref="ITransactionManager"/> implementation with
    /// <c>[Service]</c> and <c>[Primary]</c>, or pass an instance to
    /// <c>RegisterInstance&lt;ITransactionManager&gt;</c> before warm-up.
    /// </para>
    /// </summary>
    [Component]
    [Lifetime(BeanLifetime.Application)]
    public sealed class TransactionInterceptor : ScopeInterceptor
    {
        private readonly ITransactionManager m_Transactions;

        public TransactionInterceptor(ITransactionManager transactions)
        {
            m_Transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        }

        protected override bool Applies(MethodInfo targetMethod)
            => targetMethod.IsDefined(typeof(TransactionalAttribute), true);

        protected override IDisposable Open(IMethodInvocation invocation)
        {
            var transaction = m_Transactions.Begin() ?? throw new InvalidOperationException(
                "ITransactionManager.Begin returned no transaction for " +
                invocation.TargetMethod.DeclaringType?.FullName + "." + invocation.TargetMethod.Name + ".");
            return new Scope(transaction, RollbackOnError(invocation.TargetMethod), TransactionSynchronization.Enter());
        }

        protected override void Complete(IDisposable scope, Exception error)
        {
            var transaction = (Scope)scope;
            if (error != null && transaction.RollbackOnError)
                transaction.Rollback();
            else
                transaction.Commit();
        }

        private static bool RollbackOnError(MethodInfo targetMethod)
        {
            var attribute = targetMethod.GetCustomAttribute<TransactionalAttribute>(true);
            return attribute != null && attribute.RollbackOnError;
        }

        private sealed class Scope : IDisposable
        {
            private readonly ITransaction m_Transaction;
            private readonly TransactionSynchronization.Boundary m_Boundary;
            private bool m_Finished;

            public Scope(ITransaction transaction, bool rollbackOnError, TransactionSynchronization.Boundary boundary)
            {
                m_Transaction = transaction;
                RollbackOnError = rollbackOnError;
                m_Boundary = boundary;
            }

            public bool RollbackOnError { get; }

            public void Commit()
            {
                if (m_Finished)
                    return;
                m_Finished = true;
                try
                {
                    m_Transaction.Commit();
                }
                catch
                {
                    m_Boundary.RolledBack();
                    throw;
                }
            }

            public void Rollback()
            {
                if (m_Finished)
                    return;
                m_Finished = true;
                m_Boundary.RolledBack();
                m_Transaction.Rollback();
            }

            public void Dispose()
            {
                try
                {
                    m_Transaction.Dispose();
                }
                finally
                {
                    m_Boundary.Dispose();
                }
            }
        }
    }
}
