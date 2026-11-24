using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Integration;
using UniSpring.Logging.Services;
using UnityEngine.TestTools;

namespace UniSpring.Logging.Tests
{
    /// <summary>
    /// Sink selection: all-by-default, explicit lists, per-source routing,
    /// ordering and failure isolation.
    /// </summary>
    [TestFixture]
    public sealed class LogSinkRouterTests
    {
        private class TestSink : ILogSink
        {
            public readonly List<LogEntry> Entries = new List<LogEntry>();

            public virtual void Write(LogEntry entry) => Entries.Add(entry);
        }

        [LogSink("a")]
        private sealed class SinkA : TestSink
        {
        }

        [LogSink("b")]
        private sealed class SinkB : TestSink
        {
        }

        [LogSink("heavy", Default = false)]
        private sealed class HeavySink : TestSink
        {
        }

        [LogSinkService("combined", Default = false)]
        private sealed class CombinedSink : TestSink
        {
        }

        [LogSink("metadata-only")]
        private sealed class MetadataOnlySink : TestSink
        {
        }

        private sealed class ThrowingSink : ILogSink
        {
            public void Write(LogEntry entry)
                => throw new InvalidOperationException("sink failed");
        }

        private class OrderedSink : ILogSink
        {
            private readonly string m_Name;
            private readonly List<string> m_Log;

            protected OrderedSink(string name, List<string> log)
            {
                m_Name = name;
                m_Log = log;
            }

            public void Write(LogEntry entry) => m_Log.Add(m_Name);
        }

        [LogSink("first")]
        private sealed class FirstOrderedSink : OrderedSink
        {
            public FirstOrderedSink(List<string> log) : base("first", log)
            {
            }
        }

        [LogSink("second")]
        private sealed class SecondOrderedSink : OrderedSink
        {
            public SecondOrderedSink(List<string> log) : base("second", log)
            {
            }
        }

        private static LogEntry Entry(string source)
            => new LogEntry
            {
                Level = GameLogLevel.Info,
                Source = source,
                Message = "message"
            };

        [Test]
        public void All_Default_Enabled_Sinks_Are_Used_When_Nothing_Is_Configured()
        {
            var a = new SinkA();
            var b = new SinkB();
            var heavy = new HeavySink();
            var router = new LogSinkRouter(new ILogSink[] { a, b, heavy });
            router.Reload(new LoggingSettings());

            router.Write(Entry("Src"));

            Assert.AreEqual(1, a.Entries.Count);
            Assert.AreEqual(1, b.Entries.Count);
            Assert.AreEqual(0, heavy.Entries.Count, "Default = false sinks are opt-in");
        }

        [Test]
        public void Star_Selects_Every_Sink_Including_Default_Off()
        {
            var a = new SinkA();
            var heavy = new HeavySink();
            var router = new LogSinkRouter(new ILogSink[] { a, heavy });
            router.Reload(new LoggingSettings { SinkSelection = "*" });

            router.Write(Entry("Src"));

            Assert.AreEqual(1, a.Entries.Count);
            Assert.AreEqual(1, heavy.Entries.Count);
        }

        [Test]
        public void An_Explicit_List_Selects_Only_The_Listed_Sinks()
        {
            var a = new SinkA();
            var b = new SinkB();
            var router = new LogSinkRouter(new ILogSink[] { a, b });
            router.Reload(new LoggingSettings { SinkSelection = "b" });

            router.Write(Entry("Src"));

            Assert.AreEqual(0, a.Entries.Count);
            Assert.AreEqual(1, b.Entries.Count);
        }

        [Test]
        public void All_Minus_A_Name_Excludes_It()
        {
            var a = new SinkA();
            var b = new SinkB();
            var router = new LogSinkRouter(new ILogSink[] { a, b });
            router.Reload(new LoggingSettings { SinkSelection = "all,-b" });

            router.Write(Entry("Src"));

            Assert.AreEqual(1, a.Entries.Count);
            Assert.AreEqual(0, b.Entries.Count);
        }

        [Test]
        public void None_Silences_Every_Sink()
        {
            var a = new SinkA();
            var b = new SinkB();
            var router = new LogSinkRouter(new ILogSink[] { a, b });
            router.Reload(new LoggingSettings { SinkSelection = "none" });

            router.Write(Entry("Src"));

            Assert.AreEqual(0, a.Entries.Count);
            Assert.AreEqual(0, b.Entries.Count);
        }

        [Test]
        public void A_Per_Source_Selection_Overrides_The_Global_One()
        {
            var a = new SinkA();
            var b = new SinkB();
            var settings = new LoggingSettings { SinkSelection = "a" };
            settings.SourceSinkSelections["Module"] = "b";
            var router = new LogSinkRouter(new ILogSink[] { a, b });
            router.Reload(settings);

            router.Write(Entry("Module.File"));
            router.Write(Entry("Other.File"));

            Assert.AreEqual(1, a.Entries.Count, "unmatched sources use the global selection");
            Assert.AreEqual(1, b.Entries.Count, "the configured scope overrides the global selection");
        }

        [Test]
        public void File_Name_And_Scope_Keys_Are_Resolved_In_Order()
        {
            var a = new SinkA();
            var b = new SinkB();
            var heavy = new HeavySink();
            var settings = new LoggingSettings { SinkSelection = "a" };
            settings.SourceSinkSelections["Module"] = "b";
            settings.SourceSinkSelections["Special"] = "heavy";
            var router = new LogSinkRouter(new ILogSink[] { a, b, heavy });
            router.Reload(settings);

            router.Write(Entry("Module.Special"));
            router.Write(Entry("Module.Other"));
            router.Write(Entry("Unrelated"));

            Assert.AreEqual(1, heavy.Entries.Count, "the file-name key wins over the scope");
            Assert.AreEqual(1, b.Entries.Count, "the enclosing scope applies to nested sources");
            Assert.AreEqual(1, a.Entries.Count, "unmatched sources use the global selection");
        }

        [Test]
        public void An_Unknown_Name_Is_Ignored_And_Falls_Back_To_The_Global_Selection()
        {
            var a = new SinkA();
            var settings = new LoggingSettings { SinkSelection = "a" };
            settings.SourceSinkSelections["Module"] = "missing";
            var router = new LogSinkRouter(new ILogSink[] { a });
            router.Reload(settings);

            router.Write(Entry("Module.File"));

            Assert.AreEqual(1, a.Entries.Count);
        }

        [Test]
        public void Configuration_Order_Is_Preserved()
        {
            var log = new List<string>();
            var first = new FirstOrderedSink(log);
            var second = new SecondOrderedSink(log);
            var router = new LogSinkRouter(new ILogSink[] { first, second });
            router.Reload(new LoggingSettings { SinkSelection = "second,first" });

            router.Write(Entry("Src"));

            CollectionAssert.AreEqual(new[] { "second", "first" }, log);
        }

        [Test]
        public void A_Failing_Sink_Does_Not_Stop_The_Others()
        {
            var previous = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var a = new SinkA();
                var router = new LogSinkRouter(new ILogSink[] { new ThrowingSink(), a });
                router.Reload(new LoggingSettings { SinkSelection = "all" });

                Assert.DoesNotThrow(() => router.Write(Entry("Src")));
                Assert.AreEqual(1, a.Entries.Count);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previous;
            }
        }

        [Test]
        public void Reload_Swaps_The_Selection()
        {
            var a = new SinkA();
            var b = new SinkB();
            var settings = new LoggingSettings { SinkSelection = "a" };
            var router = new LogSinkRouter(new ILogSink[] { a, b });
            router.Reload(settings);
            router.Write(Entry("Src"));

            settings.SinkSelection = "b";
            router.Reload(settings);
            router.Write(Entry("Src"));

            Assert.AreEqual(1, a.Entries.Count);
            Assert.AreEqual(1, b.Entries.Count);
        }

        [Test]
        public void LogSinkService_Provides_The_Name_And_Default()
        {
            var combined = new CombinedSink();
            var a = new SinkA();
            var router = new LogSinkRouter(new ILogSink[] { combined, a });

            router.Reload(new LoggingSettings());
            router.Write(Entry("Src"));
            Assert.AreEqual(0, combined.Entries.Count, "Default = false is respected");

            router.Reload(new LoggingSettings { SinkSelection = "combined" });
            router.Write(Entry("Src"));
            Assert.AreEqual(1, combined.Entries.Count, "the attribute name is used");
        }

        [Test]
        public void LogSinkService_Registers_The_Sink_As_A_Bean()
        {
            var context = ApplicationContext.Build(new[] { typeof(CombinedSink) });

            var sinks = context.GetAll<ILogSink>();

            Assert.AreEqual(1, sinks.Count);
            Assert.IsInstanceOf<CombinedSink>(sinks[0]);
            context.Dispose();
        }

        [Test]
        public void LogSink_Alone_Does_Not_Register_A_Bean()
        {
            var context = ApplicationContext.Build(new[] { typeof(MetadataOnlySink) });

            Assert.IsEmpty(context.GetAll<ILogSink>());
            context.Dispose();
        }

        [Test]
        public void GameLogService_Applies_The_Configured_Selection()
        {
            var a = new SinkA();
            var b = new SinkB();
            var service = new GameLogService(
                new FakeConfigSource(new LoggingConfiguration
                {
                    Path = "<memory>",
                    Exists = true,
                    Settings = new LoggingSettings { SinkSelection = "b" }
                }),
                new LogLevelResolver(),
                new ILogSink[] { a, b });

            service.Log(GameLogLevel.Info, "Src", "hello");

            Assert.AreEqual(0, a.Entries.Count);
            Assert.AreEqual(1, b.Entries.Count);
        }
    }
}
