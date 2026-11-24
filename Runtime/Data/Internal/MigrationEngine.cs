using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UniSpring.DependencyInjection;
using UniSpring.Logging;
using UniSpring.Transactions;

namespace UniSpring.Data.Internal
{
    /// <summary>
    /// Runs the migrations the store has not seen yet. The number of the last one applied is kept in its own
    /// document (<c>UniSpring/migrations</c>), apart from the business documents, and is written in the same
    /// transaction as the migration's changes when the store has transactions.
    /// </summary>
    internal static class MigrationEngine
    {
        public const string Area = "UniSpring";
        public const string Name = "migrations";

        public static void Apply(IDocumentStore store, IDocumentSerializer serializer, IEnumerable<IMigration> migrations)
        {
            var ordered = Ordered(migrations);
            if (ordered.Count == 0)
                return;

            var applied = ReadVersion(store);
            var migrationStore = new MigrationStore(store, serializer);
            var transactions = store as ITransactionManager;
            foreach (var entry in ordered)
            {
                if (entry.Key <= applied)
                    continue;

                var version = entry.Key;
                var migration = entry.Value;
                Action step = () =>
                {
                    migration.Run(migrationStore);
                    store.Write(Area, Name, Encoding.ASCII.GetBytes(version.ToString(CultureInfo.InvariantCulture)));
                };
                if (transactions != null)
                    transactions.Run(step, true);
                else
                    step();
                GameLog.Info("Migration " + version + " (" + migration.GetType().Name + ") applied.");
            }
        }

        private static SortedList<int, IMigration> Ordered(IEnumerable<IMigration> migrations)
        {
            var ordered = new SortedList<int, IMigration>();
            foreach (var migration in migrations)
            {
                var attribute = migration.GetType().GetCustomAttribute<MigrationAttribute>(false);
                if (attribute == null || attribute.Version < 1)
                    throw new InvalidOperationException(
                        "UniSpring Data: migration " + migration.GetType().FullName +
                        " needs [Migration(n)] with a positive number.");
                if (ordered.ContainsKey(attribute.Version))
                    throw new InvalidOperationException(
                        "UniSpring Data: migrations " + ordered[attribute.Version].GetType().FullName + " and " +
                        migration.GetType().FullName + " share the number " + attribute.Version + ".");
                ordered.Add(attribute.Version, migration);
            }

            return ordered;
        }

        private static int ReadVersion(IDocumentStore store)
        {
            var read = store.Read(Area, Name);
            if (read.Status == DocumentStatus.Missing)
                return 0;

            int version;
            if (read.Status == DocumentStatus.Found &&
                int.TryParse(Encoding.ASCII.GetString(read.Bytes), NumberStyles.None, CultureInfo.InvariantCulture, out version))
            {
                if (read.FromBackup)
                    GameLog.Warn("UniSpring Data: the migration record was damaged and its backup says " + version +
                                 "; the migrations after it run again. Migrations must be safe to run twice.");
                return version;
            }

            // Guessing would run migrations twice or skip them; neither is safe for the player's data.
            throw new InvalidOperationException(
                "UniSpring Data: the migration record " + Area + "/" + Name + " is unreadable (" + read.Error +
                "); migrations were not run.");
        }

        private sealed class MigrationStore : IMigrationStore
        {
            private readonly IDocumentStore m_Store;
            private readonly IDocumentSerializer m_Serializer;

            public MigrationStore(IDocumentStore store, IDocumentSerializer serializer)
            {
                m_Store = store;
                m_Serializer = serializer;
            }

            public T Load<T>(string document) where T : class => Load<T>(DocumentAccess.SingleArea, document);

            public void Save(string document, object record) => Save(DocumentAccess.SingleArea, document, record);

            public void Delete(string document) => m_Store.Delete(DocumentAccess.SingleArea, document);

            public IReadOnlyList<string> Ids(string collection) => m_Store.Names(collection);

            public T Load<T>(string collection, string id) where T : class
            {
                var read = m_Store.Read(collection, id);
                if (read.Status == DocumentStatus.Missing)
                    return null;
                if (read.Status == DocumentStatus.Damaged)
                    throw new InvalidOperationException(
                        "Document " + collection + "/" + id + " is damaged and cannot be migrated: " + read.Error);
                return (T)m_Serializer.Read(typeof(T), read.Bytes);
            }

            public void Save(string collection, string id, object record)
            {
                if (record == null)
                    throw new ArgumentNullException(nameof(record));
                m_Store.Write(collection, id, m_Serializer.Write(record));
            }

            public void Delete(string collection, string id) => m_Store.Delete(collection, id);
        }
    }

    /// <summary>
    /// Applies the migration beans while the container warms up, before the ordinary beans are created
    /// (<c>[Order(-500)]</c>). The store and the serializer are taken as lists, so an application without
    /// migrations needs neither.
    /// </summary>
    [Service]
    [Order(-500)]
    internal sealed class MigrationRunner
    {
        private readonly IReadOnlyList<IMigration> m_Migrations;
        private readonly IReadOnlyList<IDocumentStore> m_Stores;
        private readonly IReadOnlyList<IDocumentSerializer> m_Serializers;

        public MigrationRunner(
            IReadOnlyList<IMigration> migrations,
            IReadOnlyList<IDocumentStore> stores,
            IReadOnlyList<IDocumentSerializer> serializers)
        {
            m_Migrations = migrations;
            m_Stores = stores;
            m_Serializers = serializers;
        }

        [PostConstruct]
        internal void Run()
        {
            if (m_Migrations.Count == 0)
                return;
            if (m_Stores.Count != 1 || m_Serializers.Count != 1)
                throw new InvalidOperationException(
                    "UniSpring Data: migrations need exactly one IDocumentStore and one IDocumentSerializer bean; " +
                    "found " + m_Stores.Count + " store(s) and " + m_Serializers.Count + " serializer(s).");
            MigrationEngine.Apply(m_Stores[0], m_Serializers[0], m_Migrations);
        }
    }
}
