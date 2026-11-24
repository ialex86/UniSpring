namespace UniSpring.Data
{
    /// <summary>Implemented through <see cref="IRecordMapper{TDomain,TRecord}"/>; lets the module find the mappers.</summary>
    public interface IRecordMapper
    {
    }

    /// <summary>
    /// Keeps the domain object apart from the shape it is stored in. Register one as a bean and the repositories
    /// of <typeparamref name="TDomain"/> store <typeparamref name="TRecord"/> instead: only the record has to
    /// satisfy the serializer (attributes, public fields), and only the record changes with the format.
    /// </summary>
    public interface IRecordMapper<TDomain, TRecord> : IRecordMapper
        where TDomain : class
        where TRecord : class
    {
        TRecord ToRecord(TDomain domain);

        TDomain ToDomain(TRecord record);
    }
}
