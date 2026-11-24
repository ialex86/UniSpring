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

        [Test]
        public void Outside_A_Transaction_Events_Are_Delivered_At_Once()
        {
            m_Events.Publish(new Paid { Amount = 5 });

            Assert.AreEqual(new[] { "meter 5", "receipt 5" }, m_Received.Lines, "bean order: by name without [Order]");
        }
    }
}
