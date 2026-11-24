# Worked example: a minimal transaction store

[Back to transactions](transactions.md)

UniSpring.Data already provides file and in-memory document stores. This optional example illustrates a custom application-owned transaction manager for a different kind of state: an in-memory counter. It stages a delta, joins sequential nested calls, commits at the root and discards changes on rollback. It is neither a durable save system nor a general concurrent transaction manager. Do not use parallel work within one transaction or Unity API in completion callbacks.

Put this class in an assembly referencing `UniSpring.DependencyInjection` and `UniSpring.Transactions`. `[Service]` and `[Primary]` make the manager available to the container; do not also register a separate instance unless you deliberately want an override.

```csharp
using System;
using System.Threading;
using UniSpring.DependencyInjection;
using UniSpring.Transactions;

[Service]
[Primary]
public sealed class CounterStore : ITransactionManager
{
    private sealed class State
    {
        public int Delta;
        public bool RollbackOnly;
        public bool Closed;
    }

    private readonly AsyncLocal<State> m_Current = new AsyncLocal<State>();
    private readonly object m_Gate = new object();
    private int m_Value;

    public int Value
    {
        get { lock (m_Gate) return m_Value; }
    }

    public void Add(int amount)
    {
        var state = m_Current.Value;
        if (state == null || state.Closed)
            throw new InvalidOperationException("No open transaction.");
        state.Delta = checked(state.Delta + amount);
    }

    public ITransaction Begin()
    {
        var state = m_Current.Value;
        if (state != null && !state.Closed)
            return new Lease(this, state, false);

        state = new State();
        m_Current.Value = state;
        return new Lease(this, state, true);
    }

    private sealed class Lease : ITransaction
    {
        private readonly CounterStore m_Store;
        private readonly State m_State;
        private readonly bool m_Root;
        private bool m_Completed;
        private bool m_Disposed;

        public Lease(CounterStore store, State state, bool root)
        {
            m_Store = store;
            m_State = state;
            m_Root = root;
        }

        private void EnsureOpen()
        {
            if (m_Disposed || m_Completed || m_State.Closed)
                throw new InvalidOperationException("Transaction is already completed.");
        }

        public void Commit()
        {
            EnsureOpen();
            if (m_Root)
            {
                if (m_State.RollbackOnly)
                    throw new InvalidOperationException("Nested work requested rollback.");
                lock (m_Store.m_Gate)
                    m_Store.m_Value = checked(m_Store.m_Value + m_State.Delta);
            }
            m_Completed = true;
        }

        public void Rollback()
        {
            EnsureOpen();
            m_State.RollbackOnly = true;
            m_Completed = true;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (!m_Completed) m_State.RollbackOnly = true;
            if (!m_Root) return;

            m_State.Closed = true;
            if (ReferenceEquals(m_Store.m_Current.Value, m_State))
                m_Store.m_Current.Value = null;
        }
    }
}
```

The root lease publishes one checked integer update under a lock. Nested leases only mark completion or rollback intent; disposing an incomplete lease never commits. A nested rollback makes the root rollback-only, even if the application catches the nested exception. That is this example store's policy, not a new rule imposed by UniSpring.

## Exercise the store without Unity or DI

```csharp
using System;
using NUnit.Framework;
using UniSpring.Transactions;

public sealed class CounterStoreTests
{
    [Test]
    public void NestedCallsCommitOnce()
    {
        var store = new CounterStore();
        store.Run(() =>
        {
            store.Add(10);
            store.Run(() => store.Add(5));
            Assert.That(store.Value, Is.Zero);
        });
        Assert.That(store.Value, Is.EqualTo(15));
    }

    [Test]
    public void RollbackDiscardsStagedChanges()
    {
        var store = new CounterStore();
        Assert.Throws<InvalidOperationException>(() => store.Run(() =>
        {
            store.Add(10);
            throw new InvalidOperationException("failed");
        }, rollbackOnError: true));
        Assert.That(store.Value, Is.Zero);
    }

    [Test]
    public void CaughtNestedRollbackStillPreventsRootCommit()
    {
        var store = new CounterStore();
        Assert.Throws<InvalidOperationException>(() => store.Run(() =>
        {
            store.Add(10);
            try
            {
                store.Run(() =>
                {
                    store.Add(5);
                    throw new InvalidOperationException("nested failure");
                }, rollbackOnError: true);
            }
            catch (InvalidOperationException) { }
        }));
        Assert.That(store.Value, Is.Zero);
    }

    [Test]
    public void DefaultPolicyCommitsEvenWhenWorkThrows()
    {
        var store = new CounterStore();
        Assert.Throws<InvalidOperationException>(() => store.Run(() =>
        {
            store.Add(10);
            throw new InvalidOperationException("failed");
        }));
        Assert.That(store.Value, Is.EqualTo(10));
    }
}
```

These examples are synchronous. A real async store must also test ambient flow across awaits, cancellation, nested rollback, concurrent independent transactions, completion failure and disposal. For Unity objects, explicitly marshal work requiring the main thread; async transaction completion is not guaranteed to run there. A production save or database store must provide its own durability, isolation and recovery guarantees.
