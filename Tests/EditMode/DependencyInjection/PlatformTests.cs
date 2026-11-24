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
        [Lifetime(BeanLifetime.Transient)]
        public sealed class TransientStarter : ILifecycle
        {
            public static int Starts;

            public void Start() => Starts++;

            public void Stop()
            {
            }
        }

        [Test]
        public void A_Transient_Lifecycle_Bean_Is_Not_Made_To_Be_Started()
        {
            TransientStarter.Starts = 0;
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(TransientStarter) });

            Assert.AreEqual(0, TransientStarter.Starts, "a transient belongs to whoever asks for it");
            context.Dispose();
        }

        [Component]
        public sealed class ClosesTheGame : IDisposable
        {
            public static int Disposed;
            private readonly ApplicationContext m_Context;

            public ClosesTheGame(ApplicationContext context)
            {
                m_Context = context;
            }

            public void Dispose()
            {
                Disposed++;
                m_Context.Dispose();
            }
        }

        [Test]
        public void Disposing_The_Container_From_A_Bean_Being_Disposed_Does_Nothing_More()
        {
            ClosesTheGame.Disposed = 0;
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(ClosesTheGame) });
            context.Get<ClosesTheGame>();

            context.Dispose();

            Assert.AreEqual(1, ClosesTheGame.Disposed);
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

        [Component]
        [Order(5)]
        public sealed class LaterEarlyPublisher
        {
            public LaterEarlyPublisher(IEventPublisher events, EarlyPublisher first)
            {
                events.Publish(new Purchase { Item = "second" });
            }
        }

        [Component]
        [Order(9)]
        public sealed class EchoListener : IEventListener<Purchase>
        {
            private readonly Journal m_Journal;
            private readonly IEventPublisher m_Events;

            public EchoListener(Journal journal, IEventPublisher events)
            {
                m_Journal = journal;
                m_Events = events;
            }

            public void On(Purchase evt)
            {
                m_Journal.Lines.Add("echo " + evt.Item);
                if (evt.Item == "early")
                    m_Events.Publish(new Purchase { Item = "echoed" });
            }
        }

        [Test]
        public void Early_Events_Are_Delivered_In_The_Order_They_Were_Published()
        {
            var journal = new Journal();
            var context = Events(journal, typeof(LaterEarlyPublisher), typeof(EarlyPublisher), typeof(EchoListener));

            Assert.AreEqual(new[] { "echo early", "echo second", "echo echoed" }, journal.Lines,
                            "an event published while the early ones are delivered waits for them");
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
        public void Removing_A_Registration_Of_A_Bean_Keeps_The_Bean_Ticking()
        {
            var journal = new Journal();
            var context = Ticks(journal, typeof(Race));
            var runner = (TickRunner)context.Get<ITickRegistry>();
            var race = context.Get<Race>();

            var registration = runner.Add(race);
            runner.RunTick(1f);
            registration.Dispose();
            runner.RunTick(1f);

            Assert.AreEqual(new[] { "race 1", "race 1" }, journal.Lines, "ticked once a frame, and still after");
            context.Dispose();
        }

        /// <summary>Equal to every other meter: the registry must still tell them apart.</summary>
        private sealed class EqualMeter : ITickable
        {
            private readonly Journal m_Journal;
            private readonly string m_Name;

            public EqualMeter(Journal journal, string name)
            {
                m_Journal = journal;
                m_Name = name;
            }

            public void Tick(float deltaTime) => m_Journal.Lines.Add(m_Name);

            public override bool Equals(object obj) => obj is EqualMeter;

            public override int GetHashCode() => 1;
        }

        [Test]
        public void Each_Registration_Removes_Only_Its_Own_Object()
        {
            var journal = new Journal();
            var context = Ticks(journal);
            var runner = (TickRunner)context.Get<ITickRegistry>();
            var first = runner.Add(new EqualMeter(journal, "first"));
            runner.Add(new EqualMeter(journal, "second"));

            first.Dispose();
            runner.RunTick(1f);

            Assert.AreEqual(new[] { "second" }, journal.Lines);
            context.Dispose();
        }

        [Test]
        public void Installing_The_Loop_Entries_Again_Never_Duplicates_Them()
        {
            // A second Play session without a domain reload installs into the loop the first one left.
            var once = TickLoop.WithEntries(UnityEngine.LowLevel.PlayerLoop.GetDefaultPlayerLoop());
            var twice = TickLoop.WithEntries(TickLoop.WithEntries(once));

            foreach (var marker in TickLoop.Markers)
                Assert.AreEqual(1, Count(twice, marker), marker.Name);
        }

        private static int Count(UnityEngine.LowLevel.PlayerLoopSystem system, Type marker)
        {
            var count = system.type == marker ? 1 : 0;
            if (system.subSystemList != null)
            {
                foreach (var child in system.subSystemList)
                    count += Count(child, marker);
            }

            return count;
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
