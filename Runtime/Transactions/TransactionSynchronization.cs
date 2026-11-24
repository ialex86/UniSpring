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
        private sealed class Frame
        {
            public int Depth = 1;
            public bool RolledBack;
            public List<Action> AfterCommit;
        }

        private static readonly AsyncLocal<Frame> s_Current = new AsyncLocal<Frame>();

        /// <summary>True inside a transaction boundary of the calling flow.</summary>
        public static bool IsActive => s_Current.Value != null;

        /// <summary>
        /// Runs <paramref name="action"/> after the outermost transaction of the flow commits; drops it on a
        /// rollback. Outside a transaction it runs at once. An action that throws is logged.
        /// </summary>
        public static void AfterCommit(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            var frame = s_Current.Value;
            if (frame == null)
            {
                Run(action);
                return;
            }

            (frame.AfterCommit ?? (frame.AfterCommit = new List<Action>())).Add(action);
        }

        /// <summary>Enters a transaction boundary; dispose the result after the transaction has ended.</summary>
        internal static Boundary Enter()
        {
            var frame = s_Current.Value;
            if (frame != null)
            {
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

            /// <summary>A rollback anywhere in the transaction drops the work held for its commit.</summary>
            public void RolledBack() => m_Frame.RolledBack = true;

            public void Dispose()
            {
                if (m_Done)
                    return;
                m_Done = true;
                m_Frame.Depth--;
                if (!m_Outermost)
                    return;

                if (ReferenceEquals(s_Current.Value, m_Frame))
                    s_Current.Value = null;
                var actions = m_Frame.AfterCommit;
                m_Frame.AfterCommit = null;
                if (m_Frame.RolledBack || actions == null)
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
            if (!TransactionSynchronization.IsActive || !listener.GetType().IsDefined(typeof(AfterCommitAttribute), true))
                return false;
            TransactionSynchronization.AfterCommit(deliver);
            return true;
        }
    }
}
