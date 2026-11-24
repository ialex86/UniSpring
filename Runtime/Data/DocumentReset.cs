using System;
using System.Collections.Generic;
using UniSpring.Logging;

namespace UniSpring.Data
{
    /// <summary>
    /// Clears the saves for a new game: every document of every area goes in one step. The record of applied
    /// migrations stays, because the documents written from now on already have the current shape.
    /// </summary>
    public interface IDocumentReset
    {
        /// <summary>True when the store holds at least one document (the migration record does not count).</summary>
        bool HasDocuments { get; }

        /// <summary>
        /// Deletes every document as one step and waits for the disk before it returns. A write another thread
        /// makes while it runs is kept. Throws <see cref="InvalidOperationException"/> inside an open transaction
        /// of the calling flow (its writes would land after the clear); open transactions of other flows are
        /// dropped, and what they write later belongs to the old game and is lost. Throws
        /// <see cref="System.IO.IOException"/> when the disk refuses; the deletes then wait and are tried again,
        /// and reads already see the store empty. The subscribers are told in every case.
        /// </summary>
        void DeleteAll();

        /// <summary>
        /// Calls <paramref name="cleared"/> after every <see cref="DeleteAll"/>, on the caller's thread: a service
        /// that keeps what it read drops it. Dispose the result to stop.
        /// </summary>
        IDisposable Subscribe(Action cleared);
    }

    /// <summary>The subscribers of <see cref="IDocumentReset.Subscribe"/>; shared by the stores.</summary>
    internal sealed class ClearListeners
    {
        private readonly List<Action> m_Listeners = new List<Action>();

        public IDisposable Add(Action cleared)
        {
            if (cleared == null)
                throw new ArgumentNullException(nameof(cleared));
            lock (m_Listeners)
                m_Listeners.Add(cleared);
            return new Subscription(this, cleared);
        }

        /// <summary>Tells everyone; a listener that fails is logged and does not stop the others.</summary>
        public void Tell()
        {
            Action[] listeners;
            lock (m_Listeners)
                listeners = m_Listeners.ToArray();
            foreach (var listener in listeners)
            {
                try
                {
                    listener();
                }
                catch (Exception exception)
                {
                    GameLog.Error("A service failed to drop its state after the saves were cleared.", exception);
                }
            }
        }

        private sealed class Subscription : IDisposable
        {
            private ClearListeners m_Owner;
            private readonly Action m_Listener;

            public Subscription(ClearListeners owner, Action listener)
            {
                m_Owner = owner;
                m_Listener = listener;
            }

            public void Dispose()
            {
                var owner = m_Owner;
                m_Owner = null;
                if (owner == null)
                    return;
                lock (owner.m_Listeners)
                    owner.m_Listeners.Remove(m_Listener);
            }
        }
    }
}
