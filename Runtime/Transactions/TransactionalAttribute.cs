using System;

namespace UniSpring.Transactions
{
    /// <summary>
    /// Declarative transaction, in the spirit of Spring's <c>@Transactional</c>:
    /// the method runs inside one <see cref="ITransactionManager.Begin"/>, so
    /// every write it makes, in any module, lands as one step.
    /// <para>
    /// Put it on a bean method that implements an interface method; the
    /// container advises calls made through that interface. A call the bean
    /// makes to its own method, a private method, a MonoBehaviour or a class
    /// resolved by its class is not advised — use
    /// <see cref="ITransactionManager.Begin"/> there. An async method
    /// (<c>Task</c>, <c>Task&lt;T&gt;</c>) keeps its transaction until the task
    /// completes. <c>ValueTask</c> and iterators are refused at the bean's
    /// creation, because their work would outlive the transaction.
    /// </para>
    /// <para>
    /// By default the transaction commits when the call ends, also when the
    /// method throws: modules change their in-memory state as they go, so
    /// there is usually nothing left to roll back. Set
    /// <see cref="RollbackOnError"/> for a manager that can discard its
    /// changes.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class TransactionalAttribute : Attribute
    {
        /// <summary>
        /// When true, a method that throws rolls the transaction back instead
        /// of committing it. The exception still reaches the caller. False by
        /// default, matching a store that cannot undo in-memory changes.
        /// </summary>
        public bool RollbackOnError { get; set; }
    }
}
