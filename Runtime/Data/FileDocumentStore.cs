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

        internal const string RollbackOnlyMessage =
            "The transaction was rolled back inside (a joined transaction asked for it); nothing it wrote is saved.";

        internal const string DroppedMessage =
            "The transaction was cancelled: the saves were cleared for a new game while it was open; nothing it " +
            "wrote is saved.";

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

        // Step ids name journals and temporary files on disk: seeded from the clock, they never repeat one an
        // earlier run left there.
        private long m_StepIds = DateTime.UtcNow.Ticks;

        // Document changes left out of the disk in this session (refused while closing); never on disk.
        private int m_Lost;

        // The sequence of the last write of every document, so a clear never deletes a later write.
        private readonly Dictionary<(string, string), long> m_LastWrite = new Dictionary<(string, string), long>();

        // A committed journal waits on disk (the disk refused to finish it, now or in an earlier run). When it is
        // this run's step m_StrandedStep, it holds the queued steps through m_StrandedThrough.
        private bool m_Stranded;
        private long m_StrandedStep;
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
            try
            {
                m_Files.Recover();
            }
            catch (Exception exception)
            {
                // Retried by the writer until the disk takes it; until then the documents it names read their old
                // contents and the store does not report itself idle.
                m_Stranded = true;
                GameLog.Error("UniSpring Data: the disk refused to complete an interrupted save step; it is tried " +
                              "again before the next save.", exception);
            }
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

        /// <summary>
        /// True when every change made so far is on disk (open transactions do not count). False for the rest of the
        /// session once a change was left out (see <see cref="LostChanges"/>).
        /// </summary>
        public bool IsIdle
        {
            get
            {
                lock (m_Gate)
                    return m_Landed >= m_LastSequence && m_Lost == 0 && !m_Stranded;
            }
        }

        /// <summary>
        /// A committed save step waits on disk to be completed (the disk refused to finish it, now or when the store
        /// opened). The writer keeps trying; until it succeeds the store is not idle and the documents it names read
        /// their older contents.
        /// </summary>
        public bool RecoveryPending
        {
            get
            {
                lock (m_Gate)
                    return m_Stranded;
            }
        }

        /// <summary>
        /// Document changes left out of the disk in this session: refused while the store closed, or held by a
        /// journal that is only completed the next time the store opens.
        /// </summary>
        public int LostChanges
        {
            get
            {
                lock (m_Gate)
                    return m_Lost;
            }
        }

        /// <summary>
        /// Waits until every change made before the call is on disk. False when <paramref name="timeout"/> passed
        /// first (the disk refuses or is slow; the changes still wait and are tried again), and false when a change
        /// was left out (see <see cref="LostChanges"/>): it is not on disk.
        /// </summary>
        public bool WaitForDisk(TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            lock (m_Gate)
            {
                var target = m_LastSequence;
                while (m_Landed < target || m_Stranded)
                {
                    if (m_Closed && m_Stranded)
                        return false; // nothing retries it any more in this run
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

                return m_Lost == 0;
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

            var deadline = DateTime.UtcNow + timeout;
            WaitForDisk(Remaining(deadline));
            lock (m_Gate)
            {
                m_Closed = true;
                Monitor.PulseAll(m_Gate);
            }

            if (!m_Thread.Join(Remaining(deadline)))
                GameLog.Warn("UniSpring Data: the document writer did not stop in " + timeout.TotalSeconds + " s.");
            DrainNow(Remaining(deadline));
        }

        private static TimeSpan Remaining(DateTime deadline)
        {
            var left = deadline - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
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
                long began;
                lock (m_Gate)
                {
                    DropOtherTransactions();
                    began = m_LastSequence;
                }

                var candidates = new List<(string, string)>();
                foreach (var area in AllAreas())
                {
                    foreach (var name in Names(area))
                        candidates.Add((area, name));
                }

                bool closed;
                var deletes = new List<KeyValuePair<(string, string), byte[]>>();
                lock (m_Gate)
                {
                    // A document another thread wrote after the clear began belongs to the new game: it stays.
                    foreach (var key in candidates)
                    {
                        long written;
                        if (m_LastWrite.TryGetValue(key, out written) && written > began)
                            continue;
                        deletes.Add(new KeyValuePair<(string, string), byte[]>(key, null));
                    }

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
            // The waiting changes first: one that lands meanwhile is then on the disk when it is listed.
            var areas = new SortedSet<string>(StringComparer.Ordinal);
            lock (m_Gate)
            {
                foreach (var key in m_Waiting.Keys)
                    areas.Add(key.Item1);
            }

            areas.UnionWith(m_Files.Areas());

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
                if (change.Value != null)
                    m_LastWrite[change.Key] = step.Sequence;
            }

            m_Queue.Add(step);
            Monitor.PulseAll(m_Gate);
        }

        private void CommitTransaction(Transaction transaction)
        {
            bool closed;
            lock (m_Gate)
            {
                if (transaction.Dropped)
                    throw new InvalidOperationException(DroppedMessage);
                if (transaction.Closed)
                    return;
                if (transaction.RollbackOnly)
                {
                    transaction.Staged.Clear();
                    throw new InvalidOperationException(RollbackOnlyMessage);
                }

                if (transaction.Staged.Count == 0)
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
                    while (m_Queue.Count == 0 && !m_Stranded && !m_Closed)
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
        private void DrainNow() => DrainNow(Timeout.InfiniteTimeSpan);

        /// <summary>
        /// Holds the disk for the whole drain, so two callers never pick, land or drop the same step. A writer
        /// that still holds the disk past <paramref name="wait"/> leaves the steps where they are (logged).
        /// </summary>
        private void DrainNow(TimeSpan wait)
        {
            if (!Monitor.TryEnter(m_DiskGate, wait))
            {
                GameLog.Warn("UniSpring Data: the disk was still busy when the game closed; waiting documents may not be saved.");
                return;
            }

            try
            {
                while (true)
                {
                    lock (m_Gate)
                    {
                        if (m_Queue.Count == 0 && !m_Stranded)
                            return;
                    }

                    // A waiting journal is tried first; only if the disk still refuses it do the steps go.
                    Exception refusal;
                    if (TryLand(1, out refusal))
                        continue;
                    if (refusal is StepCommittedException)
                    {
                        lock (m_Gate)
                            DropAllStranded();
                        return;
                    }

                    DropFirstStep(refusal);
                }
            }
            finally
            {
                Monitor.Exit(m_DiskGate);
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
                m_Lost += m_Queue[0].Changes.Count;
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
                m_Lost += step.Changes.Count;
                if (step.Sequence <= m_StrandedThrough)
                {
                    covered += step.Changes.Count;
                    Forget(step, null);
                    continue;
                }

                Forget(step, later);
            }

            m_Queue.Clear();
            if (covered > 0)
                GameLog.Warn("UniSpring Data: " + covered + " document change(s) the disk refused to finish are " +
                             "completed the next time the store opens.");
            if (later.Count > 0)
                GameLog.Error("UniSpring Data: documents written after a step the disk refused are not saved: " +
                              string.Join(", ", later));
        }

        /// <summary>
        /// Under the gate: the step no longer waits and its documents read from the disk again. It is not on disk:
        /// the caller counts it in <see cref="LostChanges"/>, so waits for the disk report it.
        /// </summary>
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
                // The journals waiting on disk first, as their own operation: the store learns which completed even
                // when the step after them fails.
                IReadOnlyCollection<long> completed;
                try
                {
                    completed = m_Files.CompletePending();
                }
                catch (Exception exception)
                {
                    // Whatever the disk refused (reading, finishing or even listing the journals), a committed
                    // step may wait there: nothing after it may land first.
                    refusal = exception as StepCommittedException ?? new StepCommittedException(exception, true);
                    lock (m_Gate)
                        m_Stranded = true;
                    return false;
                }

                List<Step> steps;
                lock (m_Gate)
                {
                    if (m_Stranded)
                    {
                        // Nothing waits on disk any more. The queued steps this run's journal held are on disk now:
                        // applying them again would overwrite the backups the journal made.
                        if (m_StrandedStep != 0 && Contains(completed, m_StrandedStep))
                            count -= Landed(m_StrandedThrough);
                        m_Stranded = false;
                        m_StrandedStep = 0;
                        m_StrandedThrough = 0;
                        Monitor.PulseAll(m_Gate);
                    }

                    count = Math.Min(count, m_Queue.Count);
                    if (count <= 0)
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
                var id = Interlocked.Increment(ref m_StepIds);
                try
                {
                    m_Files.ApplyStep(merged, id);
                }
                catch (Exception exception)
                {
                    refusal = exception;
                    if (exception is StepCommittedException)
                    {
                        lock (m_Gate)
                        {
                            m_Stranded = true;
                            m_StrandedStep = id;
                            m_StrandedThrough = last;
                        }
                    }

                    return false;
                }

                lock (m_Gate)
                {
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

        private static bool Contains(IReadOnlyCollection<long> ids, long id)
        {
            foreach (var each in ids)
            {
                if (each == id)
                    return true;
            }

            return false;
        }

        /// <summary>Under the gate: the queued steps through <paramref name="through"/> are on disk; returns how many.</summary>
        private int Landed(long through)
        {
            var removed = 0;
            while (m_Queue.Count > 0 && m_Queue[0].Sequence <= through)
            {
                var step = m_Queue[0];
                m_Queue.RemoveAt(0);
                removed++;
                foreach (var change in step.Changes)
                {
                    var key = (change.Area, change.Name);
                    DocumentChange waiting;
                    if (m_Waiting.TryGetValue(key, out waiting) && waiting.Version <= step.Sequence)
                        m_Waiting.Remove(key);
                }

                if (step.Sequence > m_Landed)
                    m_Landed = step.Sequence;
            }

            return removed;
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

            /// <summary>
            /// Drops what the whole transaction holds and marks it rollback-only (Spring's REQUIRED): a rollback of a
            /// joined lease makes the outermost commit save nothing and throw.
            /// </summary>
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
