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
            foreach (var callback in work.GetInvocationList())
            {
                if (callback.Method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false))
                    throw new InvalidOperationException("Run cannot execute async void work; use RunAsync.");
            }

            var transaction = Begin(transactions);
            var boundary = TransactionSynchronization.Enter();
            Exception primary = null;
            try
            {
                try
                {
                    work();
                }
                catch (Exception exception)
                {
                    if (rollbackOnError)
                        boundary.RolledBack();
                    FinishAfterFailure(transaction, rollbackOnError, exception);
                    throw;
                }

                CommitOrDrop(transaction, boundary);
            }
            catch (Exception exception)
            {
                primary = exception;
                throw;
            }
            finally
            {
                DisposeAfterCall(transaction, primary);
                boundary.Dispose();
            }
        }

        public static T Run<T>(this ITransactionManager transactions, Func<T> work, bool rollbackOnError = false)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));
            if (typeof(Task).IsAssignableFrom(typeof(T)) || typeof(T).GetMethod("GetAwaiter", Type.EmptyTypes) != null)
            {
                throw new InvalidOperationException(
                    "Run would commit before the returned task or awaitable completes; use RunAsync for asynchronous work.");
            }

            var transaction = Begin(transactions);
            var boundary = TransactionSynchronization.Enter();
            Exception primary = null;
            try
            {
                T result;
                try
                {
                    result = work();
                }
                catch (Exception exception)
                {
                    if (rollbackOnError)
                        boundary.RolledBack();
                    FinishAfterFailure(transaction, rollbackOnError, exception);
                    throw;
                }

                CommitOrDrop(transaction, boundary);
                return result;
            }
            catch (Exception exception)
            {
                primary = exception;
                throw;
            }
            finally
            {
                DisposeAfterCall(transaction, primary);
                boundary.Dispose();
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
            var boundary = TransactionSynchronization.Enter();
            Exception primary = null;
            try
            {
                try
                {
                    var task = work() ?? throw new InvalidOperationException("The transactional work returned no task.");
                    await task.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (rollbackOnError)
                        boundary.RolledBack();
                    FinishAfterFailure(transaction, rollbackOnError, exception);
                    throw;
                }

                CommitOrDrop(transaction, boundary);
            }
            catch (Exception exception)
            {
                primary = exception;
                throw;
            }
            finally
            {
                DisposeAfterCall(transaction, primary);
                boundary.Dispose();
            }
        }

        /// <inheritdoc cref="RunAsync(ITransactionManager, Func{Task}, bool)"/>
        public static async Task<T> RunAsync<T>(
            this ITransactionManager transactions, Func<Task<T>> work, bool rollbackOnError = false)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            var transaction = Begin(transactions);
            var boundary = TransactionSynchronization.Enter();
            Exception primary = null;
            try
            {
                T result;
                try
                {
                    var task = work() ?? throw new InvalidOperationException("The transactional work returned no task.");
                    result = await task.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (rollbackOnError)
                        boundary.RolledBack();
                    FinishAfterFailure(transaction, rollbackOnError, exception);
                    throw;
                }

                CommitOrDrop(transaction, boundary);
                return result;
            }
            catch (Exception exception)
            {
                primary = exception;
                throw;
            }
            finally
            {
                DisposeAfterCall(transaction, primary);
                boundary.Dispose();
            }
        }

        private static ITransaction Begin(ITransactionManager transactions)
        {
            if (transactions == null)
                throw new ArgumentNullException(nameof(transactions));
            return transactions.Begin() ?? throw new InvalidOperationException(
                "ITransactionManager.Begin returned no transaction.");
        }

        /// <summary>The work failed: its exception stays the one the caller sees.</summary>
        private static void FinishAfterFailure(ITransaction transaction, bool rollbackOnError, Exception primary)
        {
            try
            {
                if (rollbackOnError)
                    transaction.Rollback();
                else
                    transaction.Commit();
            }
            catch (Exception cleanup)
            {
                primary.Data["UniSpring.Transaction.CompletionFailure"] = cleanup.ToString();
            }
        }

        /// <summary>A commit that throws (a rollback-only transaction) drops the work held for after the commit.</summary>
        private static void CommitOrDrop(ITransaction transaction, TransactionSynchronization.Boundary boundary)
        {
            try
            {
                transaction.Commit();
            }
            catch
            {
                boundary.RolledBack();
                throw;
            }
        }

        private static void DisposeAfterCall(ITransaction transaction, Exception primary)
        {
            try
            {
                transaction.Dispose();
            }
            catch (Exception cleanup) when (primary != null)
            {
                primary.Data["UniSpring.Transaction.DisposeFailure"] = cleanup.ToString();
            }
        }
    }
}
