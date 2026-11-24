using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UniSpring.DependencyInjection;

namespace UniSpring.Transactions.Tests
{
    /// <summary>
    /// The transaction advice: a transaction around an advised call (and
    /// around its task for an async method), committed when the call ends and
    /// rolled back only when the method asks for it.
    /// </summary>
    [TestFixture]
    public sealed class TransactionInterceptorTests
    {
        private static readonly Type[] Beans =
        {
            typeof(RecordingTransactionManager),
            typeof(TransactionInterceptor),
            typeof(OrderService)
        };

        [Test]
        public void An_Advised_Method_Runs_In_A_Transaction_And_Commits_When_It_Ends()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var manager = context.Get<RecordingTransactionManager>();

            Assert.IsTrue(context.Get<IOrderService>().RunsInTransaction());
            Assert.AreEqual(new[] { "begin", "commit", "dispose" }, manager.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void A_Method_Without_The_Attribute_Is_Not_Advised()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var manager = context.Get<RecordingTransactionManager>();

            Assert.IsFalse(context.Get<IOrderService>().Plain());
            CollectionAssert.IsEmpty(manager.Events);
            context.Dispose();
        }

        [Test]
        public void An_Exception_Still_Commits_By_Default_And_Reaches_The_Caller()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var manager = context.Get<RecordingTransactionManager>();

            var error = Assert.Throws<InvalidOperationException>(
                () => context.Get<IOrderService>().Throws());
            StringAssert.Contains("thrown by the bean", error.Message);
            Assert.AreEqual(new[] { "begin", "commit", "dispose" }, manager.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void RollbackOnError_Discards_The_Transaction()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var manager = context.Get<RecordingTransactionManager>();

            Assert.Throws<ArgumentException>(
                () => context.Get<IOrderService>().ThrowsAndRollsBack());
            Assert.AreEqual(new[] { "begin", "rollback", "dispose" }, manager.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void An_Async_Method_Keeps_Its_Transaction_Until_The_Task_Completes()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var service = context.Get<IOrderService>();
            var manager = context.Get<RecordingTransactionManager>();
            var gate = new TaskCompletionSource<bool>();

            var running = service.RunsInTransactionAfterAsync(gate.Task);
            Assert.AreEqual(new[] { "begin" }, manager.Events.ToArray(), "open while the method awaits");

            gate.SetResult(true);
            Assert.IsTrue(running.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(running.Result, "the method saw the transaction after the await");
            Assert.AreEqual(new[] { "begin", "commit", "dispose" }, manager.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void A_Call_The_Bean_Makes_To_Itself_Is_Not_Advised()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var manager = context.Get<RecordingTransactionManager>();

            Assert.IsFalse(context.Get<IOrderService>().CallsItself(),
                "this.RunsInTransaction() bypasses the proxy, as in Spring");
            CollectionAssert.IsEmpty(manager.Events);
            context.Dispose();
        }

        [Test]
        public void An_Unconfigured_Store_Explains_How_To_Configure_It()
        {
            var context = ApplicationContext.BuildFromTypes(
                new[] { typeof(UnconfiguredTransactionManager) });

            var error = Assert.Throws<InvalidOperationException>(
                () => context.Get<ITransactionManager>().Begin());
            StringAssert.Contains("ITransactionManager", error.Message);
            StringAssert.Contains("[Primary]", error.Message);
            context.Dispose();
        }

        [Test]
        public void The_Transactions_Assembly_Builds_Without_An_Application_Store()
        {
            var context = ApplicationContext.Build(new[] { typeof(TransactionInterceptor).Assembly });

            Assert.IsInstanceOf<UnconfiguredTransactionManager>(context.Get<ITransactionManager>());
            context.Dispose();
        }

        [Test]
        public void A_Registered_Instance_Replaces_The_Placeholder()
        {
            var manager = new RecordingTransactionManager();
            var context = ApplicationContext.Build(
                new[] { typeof(TransactionInterceptor).Assembly },
                c => c.RegisterInstance<ITransactionManager>(manager));

            Assert.AreSame(manager, context.Get<ITransactionManager>());
            context.Dispose();
        }

        [Test]
        public void An_Async_Exception_Still_Commits_By_Default()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var manager = context.Get<RecordingTransactionManager>();

            var running = context.Get<IOrderService>().ThrowsAfterAsync(Task.CompletedTask);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await running);
            Assert.AreEqual(new[] { "begin", "commit", "dispose" }, manager.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void RollbackOnError_Discards_An_Async_Transaction()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var manager = context.Get<RecordingTransactionManager>();

            var running = context.Get<IOrderService>().ThrowsAfterAsyncWithRollback(Task.CompletedTask);
            Assert.ThrowsAsync<ArgumentException>(async () => await running);
            Assert.AreEqual(new[] { "begin", "rollback", "dispose" }, manager.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void Run_Commits_When_The_Work_Ends_And_Returns_Its_Result()
        {
            var manager = new RecordingTransactionManager();

            var result = manager.Run(() => manager.Current.Value != null ? 42 : 0);

            Assert.AreEqual(42, result, "the work runs inside the transaction");
            Assert.AreEqual(new[] { "begin", "commit", "dispose" }, manager.Events.ToArray());
        }

        [Test]
        public void Run_Commits_On_A_Throw_By_Default_And_Rolls_Back_On_Request()
        {
            var committing = new RecordingTransactionManager();
            Assert.Throws<InvalidOperationException>(
                () => committing.Run(() => throw new InvalidOperationException("thrown by the work")));
            Assert.AreEqual(new[] { "begin", "commit", "dispose" }, committing.Events.ToArray());

            var rollingBack = new RecordingTransactionManager();
            Assert.Throws<InvalidOperationException>(
                () => rollingBack.Run(() => throw new InvalidOperationException("thrown by the work"), true));
            Assert.AreEqual(new[] { "begin", "rollback", "dispose" }, rollingBack.Events.ToArray());
        }

        [Test]
        public void RunAsync_Keeps_The_Transaction_Until_The_Task_Completes()
        {
            var manager = new RecordingTransactionManager();
            var gate = new TaskCompletionSource<bool>();

            var running = manager.RunAsync(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return 7;
            });

            Assert.AreEqual(new[] { "begin" }, manager.Events.ToArray(), "still open while the work waits");
            gate.SetResult(true);
            Assert.AreEqual(7, running.GetAwaiter().GetResult());
            Assert.AreEqual(new[] { "begin", "commit", "dispose" }, manager.Events.ToArray());
        }

        [Test]
        public void Run_Refuses_Work_That_Returns_A_Task()
        {
            var manager = new RecordingTransactionManager();

            Assert.Throws<InvalidOperationException>(() => manager.Run(() => Task.CompletedTask));
            CollectionAssert.IsEmpty(manager.Events);
        }

        // ------------------------------------------------------------------ fixtures

        [Component]
        public sealed class RecordingTransactionManager : ITransactionManager
        {
            public readonly List<string> Events = new List<string>();
            public readonly AsyncLocal<RecordingTransaction> Current =
                new AsyncLocal<RecordingTransaction>();

            public ITransaction Begin()
            {
                Events.Add("begin");
                var transaction = new RecordingTransaction(this);
                Current.Value = transaction;
                return transaction;
            }
        }

        public sealed class RecordingTransaction : ITransaction
        {
            private readonly RecordingTransactionManager m_Owner;
            private bool m_Finished;

            public RecordingTransaction(RecordingTransactionManager owner)
            {
                m_Owner = owner;
            }

            public void Commit()
            {
                if (m_Finished)
                    return;
                m_Finished = true;
                m_Owner.Events.Add("commit");
            }

            public void Rollback()
            {
                if (m_Finished)
                    return;
                m_Finished = true;
                m_Owner.Events.Add("rollback");
            }

            public void Dispose()
            {
                m_Owner.Events.Add("dispose");
                m_Owner.Current.Value = null;
            }
        }

        public interface IOrderService
        {
            bool RunsInTransaction();
            bool Plain();
            bool CallsItself();
            Task<bool> RunsInTransactionAfterAsync(Task gate);
            void Throws();
            void ThrowsAndRollsBack();
            Task ThrowsAfterAsync(Task gate);
            Task ThrowsAfterAsyncWithRollback(Task gate);
        }

        [Service]
        public sealed class OrderService : IOrderService
        {
            private readonly RecordingTransactionManager m_Transactions;

            public OrderService(RecordingTransactionManager transactions)
            {
                m_Transactions = transactions;
            }

            [Transactional]
            public bool RunsInTransaction() => m_Transactions.Current.Value != null;

            public bool Plain() => m_Transactions.Current.Value != null;

            public bool CallsItself() => RunsInTransaction();

            [Transactional]
            public async Task<bool> RunsInTransactionAfterAsync(Task gate)
            {
                await gate.ConfigureAwait(false);
                return m_Transactions.Current.Value != null;
            }

            [Transactional]
            public void Throws() => throw new InvalidOperationException("thrown by the bean");

            [Transactional(RollbackOnError = true)]
            public void ThrowsAndRollsBack() => throw new ArgumentException("rolled back");

            [Transactional]
            public async Task ThrowsAfterAsync(Task gate)
            {
                await gate.ConfigureAwait(false);
                throw new InvalidOperationException("thrown after await");
            }

            [Transactional(RollbackOnError = true)]
            public async Task ThrowsAfterAsyncWithRollback(Task gate)
            {
                await gate.ConfigureAwait(false);
                throw new ArgumentException("rolled back after await");
            }
        }
    }
}
