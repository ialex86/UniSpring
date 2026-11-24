using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniSpring.DependencyInjection.Tests
{
    /// <summary>Lifecycle, profiles, events and ticks.</summary>
    [TestFixture]
    public sealed class PlatformTests
    {
        public sealed class Journal
        {
            public readonly List<string> Lines = new List<string>();
        }

        // ---------------------------------------------------------------- lifecycle

        [Component]
        [Order(2)]
        public sealed class SecondStarter : ILifecycle
        {
            private readonly Journal m_Journal;

            public SecondStarter(Journal journal, FirstStarter first)
            {
                m_Journal = journal;
                m_Journal.Lines.Add("create second");
            }

            public void Start() => m_Journal.Lines.Add("start second");

            public void Stop() => m_Journal.Lines.Add("stop second");
        }

        [Component]
        [Order(1)]
        public sealed class FirstStarter : ILifecycle
        {
            private readonly Journal m_Journal;

            public FirstStarter(Journal journal)
            {
                m_Journal = journal;
                m_Journal.Lines.Add("create first");
            }

            public void Start() => m_Journal.Lines.Add("start first");

            public void Stop() => m_Journal.Lines.Add("stop first");
        }

        [Component]
        [Order(3)]
        public sealed class FailingStarter : ILifecycle
        {
            public void Start() => throw new InvalidOperationException("cannot start");

            public void Stop()
            {
            }
        }

        [Test]
        public void Lifecycle_Beans_Start_In_Order_After_Every_Bean_Exists_And_Stop_In_Reverse()
        {
            var journal = new Journal();
            var context = ApplicationContext.BuildFromTypes(
                new[] { typeof(SecondStarter), typeof(FirstStarter) }, scope => scope.RegisterInstance(journal));

            Assert.AreEqual(new[] { "create first", "create second", "start first", "start second" }, journal.Lines);

            context.Dispose();

            Assert.AreEqual(new[] { "stop second", "stop first" }, journal.Lines.GetRange(4, 2));
        }

        [Test]
        public void A_Start_That_Throws_Fails_The_Build_And_Stops_What_Started()
        {
            var journal = new Journal();

            Assert.Throws<InvalidOperationException>(() => ApplicationContext.BuildFromTypes(
                new[] { typeof(FirstStarter), typeof(FailingStarter) }, scope => scope.RegisterInstance(journal)));

            Assert.Contains("stop first", journal.Lines);
        }

        [Component]
        public sealed class NeedsContext
        {
            public readonly ApplicationContext Context;

            public NeedsContext(ApplicationContext context)
            {
                Context = context;
            }
        }

        [Test]
        public void The_Container_Can_Be_Injected()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(NeedsContext) });

            Assert.AreSame(context, context.Get<NeedsContext>().Context);
            context.Dispose();
        }

        // ---------------------------------------------------------------- profiles

        public interface IClock
        {
            string Name { get; }
        }

        [Component]
        [Profile("Test")]
        public sealed class TestClock : IClock
        {
            public string Name => "test";
        }

        [Component]
        [Profile("!Test")]
        public sealed class RealClock : IClock
        {
            public string Name => "real";
        }

        [Test]
        public void Profiles_Choose_Which_Beans_Exist()
        {
            var plain = ApplicationContext.Build(new[] { typeof(TestClock), typeof(RealClock) }, new ContextOptions());
            var test = ApplicationContext.Build(
                new[] { typeof(TestClock), typeof(RealClock) },
                new ContextOptions { Profiles = new HashSet<string> { "Test" } });

            Assert.AreEqual("real", plain.Get<IClock>().Name);
            Assert.AreEqual("test", test.Get<IClock>().Name);
            CollectionAssert.Contains(plain.Profiles, Application.isEditor ? "Editor" : "Player");
            CollectionAssert.AreEquivalent(new[] { "Test" }, test.Profiles);
            plain.Dispose();
            test.Dispose();
        }

        // ---------------------------------------------------------------- events

        public class Purchase
        {
            public string Item;
        }

        public sealed class CarPurchase : Purchase
        {
        }

        [Component]
        [Order(2)]
        public sealed class PurchaseFeed : IEventListener<Purchase>
        {
            private readonly Journal m_Journal;

            public PurchaseFeed(Journal journal)
            {
                m_Journal = journal;
            }

            public void On(Purchase evt) => m_Journal.Lines.Add("feed " + evt.Item);
        }

        [Component]
        [Order(1)]
        public sealed class CarGarage : IEventListener<CarPurchase>
        {
            private readonly Journal m_Journal;

            public CarGarage(Journal journal)
            {
                m_Journal = journal;
            }

            public void On(CarPurchase evt) => m_Journal.Lines.Add("garage " + evt.Item);
        }

        [Component]
        [Order(0)]
        public sealed class FailingListener : IEventListener<Purchase>
        {
            public void On(Purchase evt) => throw new InvalidOperationException("listener failed");
        }

        [Component]
        public sealed class EarlyPublisher
        {
            public EarlyPublisher(IEventPublisher events)
            {
                events.Publish(new Purchase { Item = "early" });
            }
        }

        private static ApplicationContext Events(Journal journal, params Type[] beans)
        {
            var types = new List<Type>(beans) { typeof(EventPublisher) };
            return ApplicationContext.BuildFromTypes(types, scope => scope.RegisterInstance(journal));
        }

        [Test]
        public void An_Event_Reaches_Every_Listener_Of_Its_Type_In_Bean_Order()
        {
            var journal = new Journal();
            var context = Events(journal, typeof(PurchaseFeed), typeof(CarGarage));
            var events = context.Get<IEventPublisher>();

            events.Publish(new CarPurchase { Item = "coupe" });
            events.Publish(new Purchase { Item = "wheel" });

            Assert.AreEqual(new[] { "garage coupe", "feed coupe", "feed wheel" }, journal.Lines);
            context.Dispose();
        }

        [Test]
        public void A_Failing_Listener_Is_Logged_And_The_Others_Still_Run()
        {
            var journal = new Journal();
            var context = Events(journal, typeof(PurchaseFeed), typeof(FailingListener));
            LogAssert.Expect(LogType.Exception, new Regex("listener failed"));

            context.Get<IEventPublisher>().Publish(new Purchase { Item = "wheel" });

            Assert.AreEqual(new[] { "feed wheel" }, journal.Lines);
            context.Dispose();
        }

        [Test]
        public void Subscribers_Receive_Events_Until_They_Unsubscribe()
        {
            var journal = new Journal();
            var context = Events(journal);
            var events = context.Get<IEventPublisher>();
            var subscription = events.Subscribe<Purchase>(purchase => journal.Lines.Add("view " + purchase.Item));

            events.Publish(new CarPurchase { Item = "coupe" });
            subscription.Dispose();
            events.Publish(new Purchase { Item = "wheel" });

            Assert.AreEqual(new[] { "view coupe" }, journal.Lines);
            context.Dispose();
        }

        [Test]
        public void An_Event_Published_While_The_Container_Builds_Is_Delivered_When_It_Starts()
        {
            var journal = new Journal();
            var context = Events(journal, typeof(EarlyPublisher), typeof(PurchaseFeed));

            Assert.AreEqual(new[] { "feed early" }, journal.Lines);
            context.Dispose();
        }

        // ---------------------------------------------------------------- ticks

        [Component]
        [Order(2)]
        public sealed class Traffic : ITickable, ILateTickable
        {
            private readonly Journal m_Journal;

            public Traffic(Journal journal)
            {
                m_Journal = journal;
            }

            public void Tick(float deltaTime) => m_Journal.Lines.Add("traffic " + deltaTime);

            public void LateTick(float deltaTime) => m_Journal.Lines.Add("traffic late");
        }

        [Component]
        [Order(1)]
        public sealed class Race : ITickable, IFixedTickable
        {
            private readonly Journal m_Journal;

            public Race(Journal journal)
            {
                m_Journal = journal;
            }

            public void Tick(float deltaTime) => m_Journal.Lines.Add("race " + deltaTime);

            public void FixedTick(float deltaTime) => m_Journal.Lines.Add("race fixed");
        }

        [Component]
        [Order(0)]
        public sealed class FailingTick : ITickable
        {
            public void Tick(float deltaTime) => throw new InvalidOperationException("tick failed");
        }

        private sealed class Meter : ITickable
        {
            private readonly Journal m_Journal;

            public Meter(Journal journal)
            {
                m_Journal = journal;
            }

            public void Tick(float deltaTime) => m_Journal.Lines.Add("meter");
        }

        private static ApplicationContext Ticks(Journal journal, params Type[] beans)
        {
            var types = new List<Type>(beans) { typeof(TickRunner) };
            return ApplicationContext.BuildFromTypes(types, scope => scope.RegisterInstance(journal));
        }

        [Test]
        public void Tickable_Beans_Run_In_Bean_Order_In_Each_Phase()
        {
            var journal = new Journal();
            var context = Ticks(journal, typeof(Traffic), typeof(Race));
            var runner = (TickRunner)context.Get<ITickRegistry>();

            runner.RunTick(0.5f);
            runner.RunLateTick(0.5f);
            runner.RunFixedTick(0.02f);

            Assert.AreEqual(new[] { "race 0.5", "traffic 0.5", "traffic late", "race fixed" }, journal.Lines);
            context.Dispose();
        }

        [Test]
        public void An_Added_Tickable_Runs_After_The_Beans_Until_It_Is_Removed()
        {
            var journal = new Journal();
            var context = Ticks(journal, typeof(Race));
            var runner = (TickRunner)context.Get<ITickRegistry>();
            var registration = runner.Add(new Meter(journal));

            runner.RunTick(1f);
            registration.Dispose();
            runner.RunTick(1f);

            Assert.AreEqual(new[] { "race 1", "meter", "race 1" }, journal.Lines);
            Assert.Throws<ArgumentException>(() => runner.Add(new object()));
            context.Dispose();
        }

        [Test]
        public void A_Failing_Tickable_Is_Logged_And_The_Others_Still_Run_Until_The_Container_Ends()
        {
            var journal = new Journal();
            var context = Ticks(journal, typeof(FailingTick), typeof(Race));
            var runner = (TickRunner)context.Get<ITickRegistry>();
            LogAssert.Expect(LogType.Exception, new Regex("tick failed"));

            runner.RunTick(1f);
            context.Dispose();
            runner.RunTick(1f);

            Assert.AreEqual(new[] { "race 1" }, journal.Lines, "nothing ticks after the container ends");
        }
    }
}
