using System;
using System.Collections.Generic;
using System.Threading;
using UniSpring.Transactions;

namespace UniSpring.Data
{
    /// <summary>
    /// A document store that keeps everything in memory: for tests and tools, and as the reference for the
    /// <see cref="IDocumentStore"/> contract. It is also its own transaction manager: writes made in a transaction
    /// are read by its flow at once and by everyone else after the outermost commit; a rollback drops them.
    /// Safe to use from any thread.
    /// </summary>
    public sealed class InMemoryDocumentStore : IDocumentStore, ITransactionManager
    {
        private sealed class Transaction
        {
            // A null value is a pending delete.
            public readonly Dictionary<(string, string), byte[]> Staged = new Dictionary<(string, string), byte[]>();
            public bool Closed;
        }

        private readonly object m_Gate = new object();
        private readonly Dictionary<(string, string), byte[]> m_Documents = new Dictionary<(string, string), byte[]>();
        private readonly AsyncLocal<Transaction> m_Current = new AsyncLocal<Transaction>();

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
                return new Lease(this, transaction, true);
            }
        }

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
                    foreach (var staged in m_Transaction.Staged)
                        m_Store.Apply(staged.Key, staged.Value);
                    m_Transaction.Staged.Clear();
                }
            }

            /// <summary>Drops what the whole transaction holds so far, whichever lease asks.</summary>
            public void Rollback()
            {
                lock (m_Store.m_Gate)
                    m_Transaction.Staged.Clear();
            }

            public void Dispose()
            {
                if (!m_Outermost)
                    return;

                lock (m_Store.m_Gate)
                {
                    m_Transaction.Closed = true;
                    m_Transaction.Staged.Clear();
                }

                if (ReferenceEquals(m_Store.m_Current.Value, m_Transaction))
                    m_Store.m_Current.Value = null;
            }
        }
    }
}
