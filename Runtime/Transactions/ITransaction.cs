using System;

namespace UniSpring.Transactions
{
    /// <summary>
    /// One unit of work opened by <see cref="ITransactionManager.Begin"/>.
    /// While it is open, the changes of the calling flow are held back and
    /// handed over as one step by <see cref="Commit"/>.
    /// <para>
    /// Disposing a transaction that was not committed releases it without
    /// handing its changes over; an implementation may then discard them.
    /// <see cref="TransactionalAttribute"/> commits when the advised call
    /// ends, so a transaction it opened always reaches the store.
    /// </para>
    /// </summary>
    public interface ITransaction : IDisposable
    {
        /// <summary>
        /// Hands the held changes over as one step. For an implementation
        /// that merges concurrent transactions this may only mark the flow as
        /// done; the last flow of a merged transaction does the hand-over.
        /// </summary>
        void Commit();

        /// <summary>
        /// Discards the changes this transaction still holds. Not called by
        /// <see cref="TransactionalAttribute"/> unless the method is marked
        /// with <see cref="TransactionalAttribute.RollbackOnError"/>. A store
        /// that cannot undo in-memory changes may treat it as
        /// <see cref="Commit"/>.
        /// </summary>
        void Rollback();
    }
}
