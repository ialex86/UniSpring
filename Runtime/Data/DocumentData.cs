using System;
using System.Collections.Generic;
using UniSpring.Data.Internal;
using UniSpring.Transactions;

namespace UniSpring.Data
{
    /// <summary>
    /// The data module composed by hand, without a container: for unit tests of a service made with <c>new</c>,
    /// and for tools. Game code gets its repositories from the container.
    /// </summary>
    public sealed class DocumentData
    {
        private readonly IDocumentSerializer m_Serializer;
        private readonly List<IRecordMapper> m_Mappers = new List<IRecordMapper>();
        private readonly List<IDocumentValidator> m_Validators = new List<IDocumentValidator>();
        private RepositoryFactory m_Factory;

        public DocumentData(IDocumentStore store, IDocumentSerializer serializer)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            m_Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        }

        /// <summary>A fresh in-memory store; nothing touches the disk.</summary>
        public static DocumentData InMemory(IDocumentSerializer serializer)
            => new DocumentData(new InMemoryDocumentStore(), serializer);

        public IDocumentStore Store { get; }

        /// <summary>The store's transactions, for services that take an <see cref="ITransactionManager"/>.</summary>
        public ITransactionManager Transactions
            => Store as ITransactionManager ?? throw new InvalidOperationException(
                Store.GetType().FullName + " has no transactions.");

        /// <summary>Adds a mapper. Add everything before the first repository is asked for.</summary>
        public DocumentData With(IRecordMapper mapper)
        {
            RequireNoRepositories();
            m_Mappers.Add(mapper ?? throw new ArgumentNullException(nameof(mapper)));
            return this;
        }

        /// <summary>Adds a validator. Add everything before the first repository is asked for.</summary>
        public DocumentData With(IDocumentValidator validator)
        {
            RequireNoRepositories();
            m_Validators.Add(validator ?? throw new ArgumentNullException(nameof(validator)));
            return this;
        }

        /// <summary>The object behind a <see cref="RepositoryAttribute"/> interface; the same one every time.</summary>
        public T Repository<T>()
            where T : class
        {
            if (m_Factory == null)
                m_Factory = new RepositoryFactory(Store, m_Serializer, m_Mappers, m_Validators);
            return (T)m_Factory.Create(typeof(T));
        }

        /// <summary>Runs the migrations the store has not seen yet, as the container does at start.</summary>
        public void Migrate(params IMigration[] migrations)
            => MigrationEngine.Apply(Store, m_Serializer, migrations ?? Array.Empty<IMigration>());

        private void RequireNoRepositories()
        {
            if (m_Factory != null)
                throw new InvalidOperationException(
                    "Add mappers and validators before the first repository is created.");
        }
    }
}
