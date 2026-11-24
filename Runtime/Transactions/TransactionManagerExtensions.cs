using System;
using System.Threading.Tasks;

namespace UniSpring.Transactions
{
    /// <summary>
    /// The transaction advice as a plain call, for code that
    /// <see cref="TransactionalAttribute"/> cannot reach: IL2CPP builds (no
    /// run-time proxies), MonoBehaviours, private methods and classes resolved
    /// by their own type. The rules are the same as for the attribute: the
    /// transaction commits when the work ends, also when it throws, unless
    /// <c>rollbackOnError</c> is set; it is always disposed.
    /// </summary>
    public static class TransactionManagerExtensions
    {
        public static void Run(this ITransactionManager transactions, Action work, bool rollbackOnError = false)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            var transaction = Begin(transactions);
            try
            {
                try
                {
                    work();
                }
                catch
                {
                    Finish(transaction, rollbackOnError);
                    throw;
                }

                transaction.Commit();
            }
            finally
            {
                transaction.Dispose();
            }
        }

        public static T Run<T>(this ITransactionManager transactions, Func<T> work, bool rollbackOnError = false)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));
            if (typeof(Task).IsAssignableFrom(typeof(T)))
            {
                throw new InvalidOperationException(
                    "Run would commit before the returned task completes; use RunAsync for asynchronous work.");
            }

            var transaction = Begin(transactions);
            try
            {
                T result;
                try
                {
                    result = work();
                }
                catch
                {
                    Finish(transaction, rollbackOnError);
                    throw;
                }

                transaction.Commit();
                return result;
            }
            finally
            {
                transaction.Dispose();
            }
        }

        /// <summary>
        /// Keeps the transaction until the task of <paramref name="work"/>
        /// completes. Commit, rollback and dispose run on the thread that
        /// completes it.
        /// </summary>
        public static async Task RunAsync(
            this ITransactionManager transactions, Func<Task> work, bool rollbackOnError = false)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            var transaction = Begin(transactions);
            try
            {
                try
                {
                    var task = work() ?? throw new InvalidOperationException("The transactional work returned no task.");
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    Finish(transaction, rollbackOnError);
                    throw;
                }

                transaction.Commit();
            }
            finally
            {
                transaction.Dispose();
            }
        }

        /// <inheritdoc cref="RunAsync(ITransactionManager, Func{Task}, bool)"/>
        public static async Task<T> RunAsync<T>(
            this ITransactionManager transactions, Func<Task<T>> work, bool rollbackOnError = false)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            var transaction = Begin(transactions);
            try
            {
                T result;
                try
                {
                    var task = work() ?? throw new InvalidOperationException("The transactional work returned no task.");
                    result = await task.ConfigureAwait(false);
                }
                catch
                {
                    Finish(transaction, rollbackOnError);
                    throw;
                }

                transaction.Commit();
                return result;
            }
            finally
            {
                transaction.Dispose();
            }
        }

        private static ITransaction Begin(ITransactionManager transactions)
        {
            if (transactions == null)
                throw new ArgumentNullException(nameof(transactions));
            return transactions.Begin() ?? throw new InvalidOperationException(
                "ITransactionManager.Begin returned no transaction.");
        }

        private static void Finish(ITransaction transaction, bool rollbackOnError)
        {
            if (rollbackOnError)
                transaction.Rollback();
            else
                transaction.Commit();
        }
    }
}
