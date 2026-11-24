using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UniSpring.Data.Internal;
using UniSpring.DependencyInjection;
using UniSpring.Logging;
using UniSpring.Transactions;
using UnityEngine;

namespace UniSpring.Data
{
    /// <summary>
    /// Documents in files under a root folder, written on the store's own thread so the game never waits for the
    /// disk.
    /// <list type="bullet">
    /// <item>A write or delete returns at once; every read made after it, from any thread, sees the new content.
    /// The store's thread puts the waiting changes on disk shortly after, all of them as one step.</item>
    /// <item>On disk a change is atomic: a crash leaves the previous or the new content, never a mix. The previous
    /// copy stays as a backup and every copy carries a checksum; a read that finds a document damaged returns the
    /// backup.</item>
    /// <item>A transaction (<see cref="ITransactionManager"/>) puts all its documents on disk together: after a
    /// crash either all of them are there or none. Its writes are read by its own flow at once and by everyone else
    /// after the outermost commit; a rollback drops them.</item>
    /// <item>The disk always holds a state the game was in: changes land in the order they were made. When the
    /// disk refuses, the changes stay readable and are tried again; nothing later lands before them.</item>
    /// <item>After <see cref="Close"/> (the game is quitting), writes go to disk on the caller's thread before they
    /// return. Only then, when the disk keeps refusing a step, is that step left out and the later ones saved:
    /// losing every save for one refused file is worse than breaking the order once.</item>
    /// </list>
    /// Safe to use from any thread. Use <see cref="Install"/> to register it in a container.
    /// </summary>
    public sealed class FileDocumentStore : IDocumentStore, ITransactionManager, IDocumentReset, IDisposable
    {
        public static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

        private sealed class Transaction
        {
            // A null value is a pending delete.
            public readonly Dictionary<(string, string), byte[]> Staged = new Dictionary<(string, string), byte[]>();
            public bool Closed;

            /// <summary>Dropped by a new-game clear: what its flow writes until it ends is lost.</summary>
            public bool Dropped;
        }

        private sealed class Step
        {
            public long Sequence;
            public List<DocumentChange> Changes;
        }

        private readonly DocumentFiles m_Files;
        private readonly StoreTiming m_Timing;

        // Guards the queue, the waiting changes and the transactions; waiters for the disk sleep on it.
        private readonly object m_Gate = new object();

        // Held while files change: by the writer thread, or by the caller once the store is closed.
        private readonly object m_DiskGate = new object();

        private readonly List<Step> m_Queue = new List<Step>();
        private readonly Dictionary<(string, string), DocumentChange> m_Waiting = new Dictionary<(string, string), DocumentChange>();
        private readonly AsyncLocal<Transaction> m_Current = new AsyncLocal<Transaction>();
        private readonly HashSet<Transaction> m_OpenTransactions = new HashSet<Transaction>();
        private readonly ClearListeners m_ClearListeners = new ClearListeners();
        private readonly Thread m_Thread;
        private long m_LastSequence;

        // A committed step the disk refused to finish: its journal waits on disk, through this sequence.
        private bool m_Stranded;
        private long m_StrandedThrough;
        private long m_Landed;
        private bool m_Closed;
        private bool m_QuitHooked;

        /// <summary>Opens the store under <paramref name="root"/>, completing what a crash left unfinished.</summary>
        public FileDocumentStore(string root)
            : this(root, SystemFileSystem.Instance, StoreTiming.Default)
        {
        }

        internal FileDocumentStore(string root, IFileSystem files, StoreTiming timing)
        {
            if (string.IsNullOrEmpty(root))
                throw new ArgumentException("The store needs a root folder.", nameof(root));
            Root = Path.GetFullPath(root);
            m_Timing = timing ?? throw new ArgumentNullException(nameof(timing));
            m_Files = new DocumentFiles(Root, files ?? throw new ArgumentNullException(nameof(files)));
            files.CreateDirectory(Root);
            m_Files.Recover();
            m_Thread = new Thread(Run) { IsBackground = true, Name = "UniSpring document writer" };
            m_Thread.Start();
        }

        public string Root { get; }

        /// <summary>
        /// Opens a store and registers it in the container as its <see cref="IDocumentStore"/> and
        /// <see cref="ITransactionManager"/>; it is closed when the application quits (in the editor, when Play
        /// stops). Call it from <c>RuntimeContext.Configure</c>, on the main thread. The default root is
        /// <c>Application.persistentDataPath/Saves</c>.
        /// </summary>
        public static FileDocumentStore Install(ApplicationContext context, string root = null)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            var store = new FileDocumentStore(string.IsNullOrEmpty(root)
                ? Path.Combine(Application.persistentDataPath, "Saves")
                : root);
            context.RegisterInstance<IDocumentStore>(store);
            context.RegisterInstance<ITransactionManager>(store);
            context.RegisterInstance<IDocumentReset>(store);
            store.m_QuitHooked = true;
            Application.quitting += store.Dispose;
            return store;
        }

        // ---------------------------------------------------------------- IDocumentStore

        public DocumentRead Read(string area, string name)
        {
            var key = Key(area, name);
            lock (m_Gate)
            {
                var open = Open();
                byte[] staged;
                if (open != null && open.Staged.TryGetValue(key, out staged))
                    return staged == null ? DocumentRead.Missing : DocumentRead.Found((byte[])staged.Clone());
                DocumentChange waiting;
                if (m_Waiting.TryGetValue(key, out waiting))
                    return waiting.IsDelete ? DocumentRead.Missing : DocumentRead.Found((byte[])waiting.Bytes.Clone());
            }

            // Nothing waits for this document, so the writer is not touching its files.
            return m_Files.Read(area, name);
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
            DocumentFiles.RequireArea(area);

            // The waiting changes are taken before the files are listed: a change that lands in between is then
            // still applied on top, never lost.
            var changes = new List<KeyValuePair<string, bool>>();
            lock (m_Gate)
            {
                foreach (var waiting in m_Waiting)
                {
                    if (string.Equals(waiting.Key.Item1, area, StringComparison.Ordinal))
                        changes.Add(new KeyValuePair<string, bool>(waiting.Key.Item2, !waiting.Value.IsDelete));
                }

                var open = Open();
                if (open != null)
                {
                    foreach (var staged in open.Staged)
                    {
                        if (string.Equals(staged.Key.Item1, area, StringComparison.Ordinal))
                            changes.Add(new KeyValuePair<string, bool>(staged.Key.Item2, staged.Value != null));
                    }
                }
            }

            var names = new SortedSet<string>(m_Files.Names(area), StringComparer.Ordinal);
            foreach (var change in changes)
            {
                names.Remove(change.Key);
                if (change.Value)
                    names.Add(change.Key);
            }

            return new List<string>(names);
        }

        // ---------------------------------------------------------------- the disk

        /// <summary>True when every change made so far is on disk (open transactions do not count).</summary>
        public bool IsIdle
        {
            get
            {
                lock (m_Gate)
                    return m_Landed >= m_LastSequence;
            }
        }

        /// <summary>
        /// Waits until every change made before the call is on disk. False when <paramref name="timeout"/> passed
        /// first (the disk refuses or is slow; the changes still wait and are tried again).
        /// </summary>
        public bool WaitForDisk(TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            lock (m_Gate)
            {
                var target = m_LastSequence;
                while (m_Landed < target)
                {
                    if (timeout == Timeout.InfiniteTimeSpan)
                    {
                        Monitor.Wait(m_Gate);
                        continue;
                    }

                    var left = timeout - clock.Elapsed;
                    if (left <= TimeSpan.Zero)
                        return false;
                    Monitor.Wait(m_Gate, left);
                }

                return true;
            }
        }

        /// <summary>
        /// The game is closing: waits up to <paramref name="timeout"/> for the waiting changes to reach the disk and
        /// stops the store's thread. From then on every change goes to disk on its caller's thread before it
        /// returns, so nothing is left to a thread that ends with the process. Changes the disk still refuses are
        /// logged. Closing twice does nothing.
        /// </summary>
        public void Close(TimeSpan timeout)
        {
            lock (m_Gate)
            {
                if (m_Closed)
                    return;
            }

            if (m_QuitHooked)
            {
                m_QuitHooked = false;
                Application.quitting -= Dispose;
            }

            WaitForDisk(timeout);
            lock (m_Gate)
            {
                m_Closed = true;
                Monitor.PulseAll(m_Gate);
            }

            if (!m_Thread.Join(timeout))
                GameLog.Warn("UniSpring Data: the document writer did not stop in " + timeout.TotalSeconds + " s.");
            DrainNow();
        }

        public void Dispose() => Close(m_Timing.CloseTimeout);

        // ---------------------------------------------------------------- new game

        public bool HasDocuments
        {
            get
            {
                foreach (var area in AllAreas())
                {
                    if (Names(area).Count > 0)
                        return true;
                }

                return false;
            }
        }

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
                    DropOtherTransactions();

                var deletes = new List<KeyValuePair<(string, string), byte[]>>();
                foreach (var area in AllAreas())
                {
                    foreach (var name in Names(area))
                        deletes.Add(new KeyValuePair<(string, string), byte[]>((area, name), null));
                }

                bool closed;
                lock (m_Gate)
                {
                    if (deletes.Count > 0)
                        Enqueue(deletes);
                    closed = m_Closed;
                }

                if (closed)
                    DrainNow();
                if (!WaitForDisk(m_Timing.CloseTimeout))
                    throw new IOException("The disk refused to clear the saves; the deletes wait and are tried again.");
                GameLog.Info("Saves cleared for a new game: " + deletes.Count + " documents deleted.");
            }
            finally
            {
                m_ClearListeners.Tell();
            }
        }

        public IDisposable Subscribe(Action cleared) => m_ClearListeners.Add(cleared);

        /// <summary>Under the gate: the open transactions of other flows hold the old game and never commit.</summary>
        private void DropOtherTransactions()
        {
            if (m_OpenTransactions.Count == 0)
                return;
            foreach (var transaction in m_OpenTransactions)
            {
                transaction.Closed = true;
                transaction.Dropped = true;
                transaction.Staged.Clear();
            }

            GameLog.Warn("Saves cleared for a new game while " + m_OpenTransactions.Count + " transaction(s) were open; " +
                         "what they write belongs to the old game and is dropped.");
            m_OpenTransactions.Clear();
        }

        /// <summary>The areas on disk and those only waiting changes know of yet; the migration record is not one.</summary>
        private IReadOnlyList<string> AllAreas()
        {
            var areas = new SortedSet<string>(m_Files.Areas(), StringComparer.Ordinal);
            lock (m_Gate)
            {
                foreach (var key in m_Waiting.Keys)
                    areas.Add(key.Item1);
            }

            areas.Remove(MigrationEngine.Area);
            return new List<string>(areas);
        }

        // ---------------------------------------------------------------- transactions

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

        private Transaction Open()
        {
            var current = m_Current.Value;
            return current != null && !current.Closed ? current : null;
        }

        private void Change((string, string) key, byte[] bytes)
        {
            bool closed;
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

                Enqueue(new List<KeyValuePair<(string, string), byte[]>> { new KeyValuePair<(string, string), byte[]>(key, bytes) });
                closed = m_Closed;
            }

            if (closed)
                DrainNow();
        }

        /// <summary>Under the gate: the changes become one step, readable at once and queued for the disk.</summary>
        private void Enqueue(IEnumerable<KeyValuePair<(string, string), byte[]>> changes)
        {
            var step = new Step { Sequence = ++m_LastSequence, Changes = new List<DocumentChange>() };
            foreach (var change in changes)
            {
                var document = new DocumentChange(change.Key.Item1, change.Key.Item2, change.Value, step.Sequence);
                step.Changes.Add(document);
                m_Waiting[change.Key] = document;
            }

            m_Queue.Add(step);
            Monitor.PulseAll(m_Gate);
        }

        private void CommitTransaction(Transaction transaction)
        {
            bool closed;
            lock (m_Gate)
            {
                if (transaction.Closed || transaction.Staged.Count == 0)
                    return;
                Enqueue(transaction.Staged);
                transaction.Staged.Clear();
                closed = m_Closed;
            }

            if (closed)
                DrainNow();
        }

        // ---------------------------------------------------------------- the writer

        private void Run()
        {
            var refusals = 0;
            var pause = m_Timing.FirstRetryPause;
            while (true)
            {
                int count;
                lock (m_Gate)
                {
                    while (m_Queue.Count == 0 && !m_Closed)
                        Monitor.Wait(m_Gate);
                    if (m_Closed)
                        return;
                    count = m_Queue.Count;
                }

                Exception refusal;
                if (TryLand(count, out refusal))
                {
                    if (refusals > 0)
                        GameLog.Info("UniSpring Data: the disk took the waiting documents after " + refusals + " refusal(s).");
                    refusals = 0;
                    pause = m_Timing.FirstRetryPause;
                    continue;
                }

                if (refusals++ == 0)
                    GameLog.Warn("UniSpring Data: the disk refused the waiting documents; they stay readable and are tried again.", refusal);

                lock (m_Gate)
                {
                    if (m_Closed)
                        return;
                    Monitor.Wait(m_Gate, pause);
                }

                pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, m_Timing.LastRetryPause.Ticks));
            }
        }

        /// <summary>
        /// The store is closed: the caller's thread puts what waits on disk, step by step. A step the disk refuses
        /// before it is committed changed nothing: it is left out (logged) and the later ones are still saved. A
        /// committed step the disk refused to finish is completed when the store opens next; the changes after it
        /// would land under it then, so they are left out (logged) too.
        /// </summary>
        private void DrainNow()
        {
            while (true)
            {
                lock (m_Gate)
                {
                    if (m_Queue.Count == 0)
                        return;
                    if (m_Stranded)
                    {
                        DropAllStranded();
                        return;
                    }
                }

                Exception refusal;
                if (TryLand(1, out refusal))
                    continue;
                if (refusal is StepCommittedException)
                    continue; // stranded now: the next round drops the rest
                DropFirstStep(refusal);
            }
        }

        private void DropFirstStep(Exception refusal)
        {
            var documents = new List<string>();
            lock (m_Gate)
            {
                if (m_Queue.Count == 0)
                    return;
                Forget(m_Queue[0], documents);
                m_Queue.RemoveAt(0);
            }

            GameLog.Error("UniSpring Data: the disk refused documents while the game closes; they are not saved: " +
                          string.Join(", ", documents), refusal);
        }

        /// <summary>Under the gate: every waiting step goes; those the stranded journal holds are saved next start.</summary>
        private void DropAllStranded()
        {
            var later = new List<string>();
            var covered = 0;
            foreach (var step in m_Queue)
            {
                if (step.Sequence <= m_StrandedThrough)
                {
                    covered += step.Changes.Count;
                    Forget(step, null);
                    continue;
                }

                Forget(step, later);
            }

            m_Queue.Clear();
            GameLog.Warn("UniSpring Data: " + covered + " document change(s) the disk refused to finish are completed " +
                         "the next time the store opens.");
            if (later.Count > 0)
                GameLog.Error("UniSpring Data: documents written after a step the disk refused are not saved: " +
                              string.Join(", ", later));
        }

        /// <summary>Under the gate: the step no longer waits; its documents read from the disk again.</summary>
        private void Forget(Step step, List<string> documents)
        {
            foreach (var change in step.Changes)
            {
                documents?.Add(change.Area + "/" + change.Name);
                var key = (change.Area, change.Name);
                DocumentChange waiting;
                if (m_Waiting.TryGetValue(key, out waiting) && waiting.Version == step.Sequence)
                    m_Waiting.Remove(key);
            }

            if (step.Sequence > m_Landed)
                m_Landed = step.Sequence;
            Monitor.PulseAll(m_Gate);
        }

        /// <summary>
        /// Puts the first <paramref name="count"/> queued steps on disk as one step (a later change of a document
        /// replaces an earlier one), then lets the store forget them.
        /// </summary>
        private bool TryLand(int count, out Exception refusal)
        {
            refusal = null;
            lock (m_DiskGate)
            {
                List<Step> steps;
                lock (m_Gate)
                {
                    count = Math.Min(count, m_Queue.Count);
                    if (count == 0)
                        return true;
                    steps = m_Queue.GetRange(0, count);
                }

                var merged = new List<DocumentChange>();
                var index = new Dictionary<(string, string), int>();
                foreach (var step in steps)
                {
                    foreach (var change in step.Changes)
                    {
                        var key = (change.Area, change.Name);
                        int at;
                        if (index.TryGetValue(key, out at))
                        {
                            merged[at] = change;
                            continue;
                        }

                        index.Add(key, merged.Count);
                        merged.Add(change);
                    }
                }

                var last = steps[steps.Count - 1].Sequence;
                try
                {
                    m_Files.Apply(merged, last);
                }
                catch (Exception exception)
                {
                    refusal = exception;
                    if (exception is StepCommittedException)
                    {
                        lock (m_Gate)
                        {
                            m_Stranded = true;
                            m_StrandedThrough = last;
                        }
                    }

                    return false;
                }

                lock (m_Gate)
                {
                    m_Stranded = false;
                    m_Queue.RemoveRange(0, count);
                    foreach (var change in merged)
                    {
                        var key = (change.Area, change.Name);
                        DocumentChange waiting;
                        if (m_Waiting.TryGetValue(key, out waiting) && waiting.Version <= last)
                            m_Waiting.Remove(key);
                    }

                    m_Landed = last;
                    Monitor.PulseAll(m_Gate);
                }

                return true;
            }
        }

        private static (string, string) Key(string area, string name)
        {
            DocumentFiles.RequireArea(area);
            DocumentFiles.RequireName(name);
            return (area, name);
        }

        /// <summary>One <see cref="Begin"/>: the outermost lease commits, a joined one only takes part.</summary>
        private sealed class Lease : ITransaction
        {
            private readonly FileDocumentStore m_Store;
            private readonly Transaction m_Transaction;
            private readonly bool m_Outermost;

            public Lease(FileDocumentStore store, Transaction transaction, bool outermost)
            {
                m_Store = store;
                m_Transaction = transaction;
                m_Outermost = outermost;
            }

            public void Commit()
            {
                if (m_Outermost)
                    m_Store.CommitTransaction(m_Transaction);
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
                    m_Store.m_OpenTransactions.Remove(m_Transaction);
                }

                if (ReferenceEquals(m_Store.m_Current.Value, m_Transaction))
                    m_Store.m_Current.Value = null;
            }
        }
    }

    /// <summary>How long the file store waits for a refusing disk; tests shorten it.</summary>
    internal sealed class StoreTiming
    {
        public static readonly StoreTiming Default = new StoreTiming(
            TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(10), FileDocumentStore.DefaultCloseTimeout);

        public StoreTiming(TimeSpan firstRetryPause, TimeSpan lastRetryPause, TimeSpan closeTimeout)
        {
            FirstRetryPause = firstRetryPause;
            LastRetryPause = lastRetryPause < firstRetryPause ? firstRetryPause : lastRetryPause;
            CloseTimeout = closeTimeout;
        }

        public TimeSpan FirstRetryPause { get; }
        public TimeSpan LastRetryPause { get; }
        public TimeSpan CloseTimeout { get; }
    }
}
