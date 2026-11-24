namespace UniSpring.Transactions
{
    /// <summary>
    /// Starts the transaction of the calling flow. This is the single port the
    /// transaction advice needs; the storage behind it (a database, a save
    /// file, an in-memory journal) is the application's choice.
    /// <para>
    /// Propagation is Spring's <c>REQUIRED</c>: calling <see cref="Begin"/>
    /// while a transaction is open in the flow joins it, and only the
    /// outermost one commits. The flow includes every async call the caller
    /// makes, so an implementation keeps the ambient transaction in an
    /// <c>AsyncLocal</c>.
    /// </para>
    /// </summary>
    public interface ITransactionManager
    {
        /// <summary>Begins a transaction, or joins the one already open in the flow.</summary>
        ITransaction Begin();
    }
}
