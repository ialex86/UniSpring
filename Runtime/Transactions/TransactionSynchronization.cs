using System;
using System.Collections.Generic;
using System.Threading;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace UniSpring.Transactions
{
    /// <summary>
    /// Work to do once the current transaction has committed, as Spring's <c>TransactionSynchronization</c>.
    /// The package's transaction boundaries (<see cref="TransactionalAttribute"/>,
    /// <see cref="TransactionManagerExtensions.Run"/>, <c>RunAsync</c>) keep track of the transaction of the calling
    /// flow; a transaction begun with <see cref="ITransactionManager.Begin"/> directly is not seen here.
    /// </summary>
    public static class TransactionSynchronization
    {
        /// <summary>
        /// The transaction of a flow. Tasks started inside it share it (they inherit the flow), so its state is
        /// guarded: they may register work concurrently. Once the outermost boundary ends it is closed; work a task
        /// registers after that runs at once, as outside a transaction.
        /// </summary>
        private sealed class Frame
        {
            public readonly object Gate = new object();
            public int Depth = 1;
            public bool RolledBack;
            public bool Closed;
            public List<Action> AfterCommit;
            public List<Action> AfterRollback;
        }

        private static readonly AsyncLocal<Frame> s_Current = new AsyncLocal<Frame>();

        /// <summary>True inside a transaction boundary of the calling flow.</summary>
        public static bool IsActive => Open() != null;

        /// <summary>
        /// Runs <paramref name="action"/> after the outermost transaction of the flow commits; drops it on a
        /// rollback. Outside a transaction it runs at once. An action that throws is logged.
        /// </summary>
        public static void AfterCommit(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            if (!TryHold(action, false))
                Run(action);
        }

        /// <summary>Runs <paramref name="action"/> if the transaction of the flow rolls back; outside one, never.</summary>
        internal static void AfterRollback(Action action)
        {
            TryHold(action, true);
        }

        private static bool TryHold(Action action, bool onRollback)
        {
            var frame = Open();
            if (frame == null)
                return false;
            lock (frame.Gate)
            {
                if (frame.Closed)
                    return false;
                if (onRollback)
                    (frame.AfterRollback ?? (frame.AfterRollback = new List<Action>())).Add(action);
                else
                    (frame.AfterCommit ?? (frame.AfterCommit = new List<Action>())).Add(action);
                return true;
            }
        }

        private static Frame Open()
        {
            var frame = s_Current.Value;
            if (frame == null)
                return null;
            lock (frame.Gate)
                return frame.Closed ? null : frame;
        }

        /// <summary>Enters a transaction boundary; dispose the result after the transaction has ended.</summary>
        internal static Boundary Enter()
        {
            var frame = Open();
            if (frame != null)
            {
                lock (frame.Gate)
                    frame.Depth++;
                return new Boundary(frame, false);
            }

            frame = new Frame();
            s_Current.Value = frame;
            return new Boundary(frame, true);
        }

        private static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        internal sealed class Boundary : IDisposable
        {
            private readonly Frame m_Frame;
            private readonly bool m_Outermost;
            private bool m_Done;

            public Boundary(object frame, bool outermost)
            {
                m_Frame = (Frame)frame;
                m_Outermost = outermost;
            }

            /// <summary>
            /// The transaction did not commit (a rollback, or a commit that failed): the work held for its commit
            /// is dropped.
            /// </summary>
            public void RolledBack()
            {
                lock (m_Frame.Gate)
                    m_Frame.RolledBack = true;
            }

            public void Dispose()
            {
                if (m_Done)
                    return;
                m_Done = true;

                List<Action> actions;
                lock (m_Frame.Gate)
                {
                    m_Frame.Depth--;
                    if (!m_Outermost)
                        return;
                    m_Frame.Closed = true;
                    actions = m_Frame.RolledBack ? m_Frame.AfterRollback : m_Frame.AfterCommit;
                    m_Frame.AfterCommit = null;
                    m_Frame.AfterRollback = null;
                }

                if (ReferenceEquals(s_Current.Value, m_Frame))
                    s_Current.Value = null;
                if (actions == null)
                    return;
                foreach (var action in actions)
                    Run(action);
            }
        }
    }

    /// <summary>
    /// Delivers events to this listener only after the transaction they were published in commits, as Spring's
    /// <c>@TransactionalEventListener</c>; dropped on a rollback. Outside a transaction they are delivered at once.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class AfterCommitAttribute : Attribute
    {
    }

    [Component]
    internal sealed class AfterCommitDeferral : IEventDeferral
    {
        public bool TryDefer(object listener, Action deliver)
        {
            if (!IsAfterCommit(listener) || !TransactionSynchronization.IsActive)
                return false;
            TransactionSynchronization.AfterCommit(deliver);
            return true;
        }

        /// <summary>
        /// An event published before the container started, inside a transaction: its after-commit listeners get it
        /// only if that transaction commits (at the start when it already has, at its commit when it is still open).
        /// </summary>
        public IEventDeferral Capture()
        {
            if (!TransactionSynchronization.IsActive)
                return null;
            var captured = new Captured();
            TransactionSynchronization.AfterCommit(captured.Committed);
            TransactionSynchronization.AfterRollback(captured.RolledBack);
            return captured;
        }

        internal static bool IsAfterCommit(object listener)
            => listener != null && listener.GetType().IsDefined(typeof(AfterCommitAttribute), true);

        private sealed class Captured : IEventDeferral
        {
            private readonly object m_Gate = new object();
            private readonly List<Action> m_Waiting = new List<Action>();
            private bool m_Committed;
            private bool m_RolledBack;

            public bool TryDefer(object listener, Action deliver)
            {
                if (!IsAfterCommit(listener))
                    return false;
                lock (m_Gate)
                {
                    if (m_Committed)
                        return false;
                    if (!m_RolledBack)
                        m_Waiting.Add(deliver);
                    return true;
                }
            }

            public IEventDeferral Capture() => null;

            public void Committed()
            {
                Action[] waiting;
                lock (m_Gate)
                {
                    m_Committed = true;
                    waiting = m_Waiting.ToArray();
                    m_Waiting.Clear();
                }

                foreach (var deliver in waiting)
                    deliver();
            }

            public void RolledBack()
            {
                lock (m_Gate)
                {
                    m_RolledBack = true;
                    m_Waiting.Clear();
                }
            }
        }
    }
}
