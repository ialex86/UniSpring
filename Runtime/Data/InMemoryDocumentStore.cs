using System;
using System.Collections.Generic;
using System.Threading;
using UniSpring.Data.Internal;
using UniSpring.Transactions;

namespace UniSpring.Data
{
    /// <summary>
    /// A document store that keeps everything in memory: for tests and tools, and as the reference for the
    /// <see cref="IDocumentStore"/> contract. It is also its own transaction manager: writes made in a transaction
    /// are read by its flow at once and by everyone else after the outermost commit; a rollback drops them.
    /// Safe to use from any thread.
    /// </summary>
    public sealed class InMemoryDocumentStore : IDocumentStore, ITransactionManager, IDocumentReset
    {
        private sealed class Transaction
        {
            // A null value is a pending delete.
            public readonly Dictionary<(string, string), byte[]> Staged = new Dictionary<(string, string), byte[]>();
            public bool Closed;

            /// <summary>Dropped by a new-game clear: what its flow writes until it ends is lost.</summary>
            public bool Dropped;

            /// <summary>A rollback happened inside: the outermost commit saves nothing and throws.</summary>
            public bool RollbackOnly;
        }

        private readonly object m_Gate = new object();
        private readonly Dictionary<(string, string), byte[]> m_Documents = new Dictionary<(string, string), byte[]>();
        private readonly AsyncLocal<Transaction> m_Current = new AsyncLocal<Transaction>();
        private readonly HashSet<Transaction> m_OpenTransactions = new HashSet<Transaction>();
        private readonly ClearListeners m_ClearListeners = new ClearListeners();

        public DocumentRead Read(string area, string name)
        {
            var key = Key(area, name);
            lock (m_Gate)
            {
                var open = Open();
                byte[] bytes;
                if (open != null && open.Staged.TryGetValue(key, out bytes))
                    return bytes == null ? DocumentRead.Missing : DocumentRead.Found((byte[])bytes.Clone());
                return m_Documents.TryGetValue(key, out bytes)
                    ? DocumentRead.Found((byte[])bytes.Clone())
                    : DocumentRead.Missing;
            }
        }

        public void Write(string area, string name, byte[] bytes)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            Change(Key(area, name), (byte[])bytes.Clone());
        }

        public void Delete(string area, string name) => Change(Key(area, name), null);

        public IReadOnlyList<string> Names(string area)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            lock (m_Gate)
            {
                foreach (var key in m_Documents.Keys)
                {
                    if (string.Equals(key.Item1, area, StringComparison.Ordinal))
                        names.Add(key.Item2);
                }

                var open = Open();
                if (open != null)
                {
                    foreach (var staged in open.Staged)
                    {
                        if (!string.Equals(staged.Key.Item1, area, StringComparison.Ordinal))
                            continue;
                        names.Remove(staged.Key.Item2);
                        if (staged.Value != null)
                            names.Add(staged.Key.Item2);
                    }
                }
            }

            return new List<string>(names);
        }

        public ITransaction Begin()
        {
            lock (m_Gate)
            {
                var open = Open();
                if (open != null)
                    return new Lease(this, open, false);

                var transaction = new Transaction();
                m_Current.Value = transaction;
                m_OpenTransactions.Add(transaction);
                return new Lease(this, transaction, true);
            }
        }

        public bool HasDocuments
        {
            get
            {
                lock (m_Gate)
                {
                    foreach (var key in m_Documents.Keys)
                    {
                        if (key.Item1 != MigrationEngine.Area)
                            return true;
                    }

                    return false;
                }
            }
        }

        /// <summary>See <see cref="IDocumentReset.DeleteAll"/>; nothing waits for a disk here.</summary>
        public void DeleteAll()
        {
            lock (m_Gate)
            {
                if (Open() != null)
                    throw new InvalidOperationException(
                        "The saves cannot be cleared inside a transaction: its writes would land after the clear. " +
                        "Commit it first.");
            }

            try
            {
                lock (m_Gate)
                {
                    foreach (var transaction in m_OpenTransactions)
                    {
                        transaction.Closed = true;
                        transaction.Dropped = true;
                        transaction.Staged.Clear();
                    }

                    m_OpenTransactions.Clear();
                    var keys = new List<(string, string)>(m_Documents.Keys);
                    foreach (var key in keys)
                    {
                        if (key.Item1 != MigrationEngine.Area)
                            m_Documents.Remove(key);
                    }
                }
            }
            finally
            {
                m_ClearListeners.Tell();
            }
        }

        public IDisposable Subscribe(Action cleared) => m_ClearListeners.Add(cleared);

        private void Change((string, string) key, byte[] bytes)
        {
            lock (m_Gate)
            {
                var open = Open();
                if (open != null)
                {
                    open.Staged[key] = bytes;
                    return;
                }

                var current = m_Current.Value;
                if (current != null && current.Dropped)
                    return;

                Apply(key, bytes);
            }
        }

        private void Apply((string, string) key, byte[] bytes)
        {
            if (bytes == null)
            {
                m_Documents.Remove(key);
                return;
            }

            m_Documents[key] = bytes;
        }

        private Transaction Open()
        {
            var current = m_Current.Value;
            return current != null && !current.Closed ? current : null;
        }

        private static (string, string) Key(string area, string name)
        {
            if (string.IsNullOrEmpty(area))
                throw new ArgumentException("The document area is empty.", nameof(area));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("The document name is empty.", nameof(name));
            return (area, name);
        }

        /// <summary>One <see cref="Begin"/>: the outermost lease commits, a joined one only takes part.</summary>
        private sealed class Lease : ITransaction
        {
            private readonly InMemoryDocumentStore m_Store;
            private readonly Transaction m_Transaction;
            private readonly bool m_Outermost;

            public Lease(InMemoryDocumentStore store, Transaction transaction, bool outermost)
            {
                m_Store = store;
                m_Transaction = transaction;
                m_Outermost = outermost;
            }

            public void Commit()
            {
                if (!m_Outermost)
                    return;

                lock (m_Store.m_Gate)
                {
                    if (m_Transaction.Closed)
                        return;
                    if (m_Transaction.RollbackOnly)
                    {
                        m_Transaction.Staged.Clear();
                        throw new InvalidOperationException(FileDocumentStore.RollbackOnlyMessage);
                    }

                    foreach (var staged in m_Transaction.Staged)
                        m_Store.Apply(staged.Key, staged.Value);
                    m_Transaction.Staged.Clear();
                }
            }

            /// <summary>Drops what the whole transaction holds and marks it rollback-only, as Spring's REQUIRED.</summary>
            public void Rollback()
            {
                lock (m_Store.m_Gate)
                {
                    m_Transaction.Staged.Clear();
                    m_Transaction.RollbackOnly = true;
                }
            }

            public void Dispose()
            {
                if (!m_Outermost)
                    return;

                lock (m_Store.m_Gate)
                {
                    m_Transaction.Closed = true;
                    m_Transaction.Staged.Clear();
                    m_Store.m_OpenTransactions.Remove(m_Transaction);
                }

                if (ReferenceEquals(m_Store.m_Current.Value, m_Transaction))
                    m_Store.m_Current.Value = null;
            }
        }
    }
}
