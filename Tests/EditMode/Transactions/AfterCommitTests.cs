using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniSpring.DependencyInjection;

namespace UniSpring.Transactions.Tests
{
    /// <summary>Events held back until the transaction they were published in commits.</summary>
    [TestFixture]
    public sealed class AfterCommitTests
    {
        public sealed class Paid
        {
            public long Amount;
        }

        public sealed class Received
        {
            public readonly List<string> Lines = new List<string>();
        }

        [Component]
        [AfterCommit]
        public sealed class Receipt : IEventListener<Paid>
        {
            private readonly Received m_Received;

            public Receipt(Received received)
            {
                m_Received = received;
            }

            public void On(Paid evt) => m_Received.Lines.Add("receipt " + evt.Amount);
        }

        [Component]
        public sealed class Meter : IEventListener<Paid>
        {
            private readonly Received m_Received;

            public Meter(Received received)
            {
                m_Received = received;
            }

            public void On(Paid evt) => m_Received.Lines.Add("meter " + evt.Amount);
        }

        private Received m_Received;
        private ApplicationContext m_Context;
        private IEventPublisher m_Events;
        private TransactionInterceptorTests.RecordingTransactionManager m_Transactions;

        [SetUp]
        public void SetUp()
        {
            m_Received = new Received();
            m_Transactions = new TransactionInterceptorTests.RecordingTransactionManager();
            m_Context = ApplicationContext.Build(
                new[] { typeof(EventPublisher), typeof(AfterCommitDeferral), typeof(Receipt), typeof(Meter) },
                scope => scope.RegisterInstance(m_Received));
            m_Events = m_Context.Get<IEventPublisher>();
        }

        [TearDown]
        public void TearDown() => m_Context.Dispose();

        [Test]
        public void An_After_Commit_Listener_Waits_For_The_Commit_And_The_Others_Do_Not()
        {
            m_Transactions.Run(() =>
            {
                m_Events.Publish(new Paid { Amount = 60 });
                Assert.AreEqual(new[] { "meter 60" }, m_Received.Lines, "held back inside the transaction");
            });

            Assert.AreEqual(new[] { "meter 60", "receipt 60" }, m_Received.Lines);
        }

        [Test]
        public void A_Rollback_Drops_The_Held_Events()
        {
            Assert.Throws<InvalidOperationException>(() => m_Transactions.Run(() =>
            {
                m_Events.Publish(new Paid { Amount = 60 });
                throw new InvalidOperationException("the purchase failed");
            }, true));

            Assert.AreEqual(new[] { "meter 60" }, m_Received.Lines);
        }

        [Test]
        public void Only_The_Outermost_Commit_Delivers()
        {
            m_Transactions.Run(() =>
            {
                m_Transactions.Run(() => m_Events.Publish(new Paid { Amount = 1 }));
                Assert.IsFalse(m_Received.Lines.Contains("receipt 1"), "the inner commit joins the outer one");
            });

            Assert.Contains("receipt 1", m_Received.Lines);
        }

        /// <summary>A transaction whose commit or dispose the disk refuses.</summary>
        private sealed class RefusingManager : ITransactionManager
        {
            public bool FailCommit;
            public bool FailDispose;

            public ITransaction Begin() => new Refusing(this);

            private sealed class Refusing : ITransaction
            {
                private readonly RefusingManager m_Owner;

                public Refusing(RefusingManager owner)
                {
                    m_Owner = owner;
                }

                public void Commit()
                {
                    if (m_Owner.FailCommit)
                        throw new InvalidOperationException("the commit failed");
                }

                public void Rollback()
                {
                }

                public void Dispose()
                {
                    if (m_Owner.FailDispose)
                        throw new InvalidOperationException("the dispose failed");
                }
            }
        }

        [Test]
        public void A_Commit_That_Fails_Drops_The_Held_Events()
        {
            var transactions = new RefusingManager { FailCommit = true };

            Assert.Throws<InvalidOperationException>(() => transactions.Run(() => m_Events.Publish(new Paid { Amount = 7 })));

            Assert.AreEqual(new[] { "meter 7" }, m_Received.Lines, "nothing was committed");
            Assert.IsFalse(TransactionSynchronization.IsActive);
        }

        [Test]
        public void A_Dispose_That_Throws_Still_Ends_The_Transaction_Boundary()
        {
            var transactions = new RefusingManager { FailDispose = true };

            Assert.Throws<InvalidOperationException>(() => transactions.Run(() => m_Events.Publish(new Paid { Amount = 8 })));

            Assert.IsFalse(TransactionSynchronization.IsActive, "the flow is not left inside a transaction");
            Assert.AreEqual(new[] { "meter 8", "receipt 8" }, m_Received.Lines, "the commit happened");
            m_Events.Publish(new Paid { Amount = 9 });
            Assert.Contains("receipt 9", m_Received.Lines, "later events are not held back");
        }

        [Test]
        public void Work_Registered_After_The_Transaction_Ended_Runs_At_Once()
        {
            var lines = new List<string>();
            System.Threading.Tasks.Task late = null;
            var gate = new System.Threading.ManualResetEventSlim();
            m_Transactions.Run(() =>
            {
                // A task started inside the transaction shares its flow and outlives it.
                late = System.Threading.Tasks.Task.Run(() =>
                {
                    gate.Wait();
                    TransactionSynchronization.AfterCommit(() => lines.Add("late"));
                });
            });

            gate.Set();
            late.GetAwaiter().GetResult();
            Assert.AreEqual(new[] { "late" }, lines, "run at once, not held by a transaction that is over");
        }

        [Test]
        public void A_Transaction_Begun_After_The_Outer_One_Ended_Is_Outermost()
        {
            var lines = new List<string>();
            System.Threading.Tasks.Task late = null;
            var gate = new System.Threading.ManualResetEventSlim();
            m_Transactions.Run(() =>
            {
                late = System.Threading.Tasks.Task.Run(() =>
                {
                    gate.Wait();
                    m_Transactions.Run(() =>
                    {
                        TransactionSynchronization.AfterCommit(() => lines.Add("committed"));
                        lock (lines)
                            lines.Add("inside");
                    });
                });
            });

            gate.Set();
            late.GetAwaiter().GetResult();
            Assert.AreEqual(new[] { "inside", "committed" }, lines, "its own commit delivers, not the ended outer one");
            Assert.IsFalse(TransactionSynchronization.IsActive);
        }

        [Component]
        public sealed class EarlyPayer
        {
            public EarlyPayer(IEventPublisher events)
            {
                events.Publish(new Paid { Amount = 3 });
            }
        }

        private ApplicationContext BuildWithEarlyPayer()
            => ApplicationContext.Build(
                new[] { typeof(EventPublisher), typeof(AfterCommitDeferral), typeof(Receipt), typeof(Meter), typeof(EarlyPayer) },
                scope => scope.RegisterInstance(m_Received));

        [Test]
        public void An_Event_Published_While_Building_Inside_A_Transaction_Reaches_After_Commit_Listeners_On_Commit()
        {
            ApplicationContext built = null;
            m_Transactions.Run(() =>
            {
                built = BuildWithEarlyPayer();
                Assert.AreEqual(new[] { "meter 3" }, m_Received.Lines, "the receipt waits for the commit");
            });

            Assert.AreEqual(new[] { "meter 3", "receipt 3" }, m_Received.Lines);
            built.Dispose();
        }

        [Test]
        public void An_Event_Published_While_Building_Inside_A_Rolled_Back_Transaction_Never_Reaches_After_Commit_Listeners()
        {
            ApplicationContext built = null;
            Assert.Throws<InvalidOperationException>(() => m_Transactions.Run(() =>
            {
                built = BuildWithEarlyPayer();
                throw new InvalidOperationException("the purchase failed");
            }, true));

            Assert.AreEqual(new[] { "meter 3" }, m_Received.Lines);
            built.Dispose();
        }

        [Test]
        public void Outside_A_Transaction_Events_Are_Delivered_At_Once()
        {
            m_Events.Publish(new Paid { Amount = 5 });

            Assert.AreEqual(new[] { "meter 5", "receipt 5" }, m_Received.Lines, "bean order: by name without [Order]");
        }
    }
}
