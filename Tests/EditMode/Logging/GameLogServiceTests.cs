using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Services;

namespace UniSpring.Logging.Tests
{
    /// <summary>
    /// Regression tests for the logging configuration parser and level policy
    ///: root/per-source levels, comments and error cases.
    /// </summary>
    [TestFixture]
    public sealed class LoggingPropertiesParserTests
    {
        private readonly LoggingPropertiesParser m_Parser = new LoggingPropertiesParser();

        [Test]
        public void Parses_Root_And_Per_Source_Levels()
        {
            LoggingSettings settings = m_Parser.Parse(new[]
            {
                "# comment",
                "; other comment",
                "",
                "root = Warn",
                "GameLog = Debug",
            });

            Assert.AreEqual(GameLogLevel.Warn, settings.RootLevel);
            Assert.AreEqual(GameLogLevel.Debug,
                settings.ClassLevels["GameLog"]);
        }

        [Test]
        public void Level_Names_Are_Case_Insensitive()
        {
            LoggingSettings settings = m_Parser.Parse(new[] { "root=error" });
            Assert.AreEqual(GameLogLevel.Error, settings.RootLevel);
        }

        [Test]
        public void Parses_Sink_Selection()
        {
            LoggingSettings settings = m_Parser.Parse(new[]
            {
                "root = Info",
                "sinks = UnityLogSink, FileLogSink",
                "sinks.MyGame.Audit = FileLogSink",
                "sinks.Noisy = none",
            });

            Assert.AreEqual("UnityLogSink, FileLogSink", settings.SinkSelection);
            Assert.AreEqual("FileLogSink", settings.SourceSinkSelections["MyGame.Audit"]);
            Assert.AreEqual("none", settings.SourceSinkSelections["Noisy"]);
            Assert.IsFalse(settings.ClassLevels.ContainsKey("sinks"));
        }

        [Test]
        public void An_Empty_Sink_Source_Is_Rejected()
        {
            Assert.Throws<FormatException>(
                () => m_Parser.Parse(new[] { "sinks. = FileLogSink" }));
        }

        [Test]
        public void Malformed_Lines_Are_Rejected()
        {
            Assert.Throws<FormatException>(
                () => m_Parser.Parse(new[] { "no-separator" }));
            Assert.Throws<FormatException>(
                () => m_Parser.Parse(new[] { "=Info" }));
            Assert.Throws<FormatException>(
                () => m_Parser.Parse(new[] { "root=" }));
            Assert.Throws<FormatException>(
                () => m_Parser.Parse(new[] { "root=Loud" }));
            Assert.Throws<FormatException>(
                () => m_Parser.Parse(new[] { "root=2" }), "a number is not a level name");
        }

        [Test]
        public void Null_Input_Is_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => m_Parser.Parse(null));
        }
    }

    /// <summary>Regression tests for the root/per-source level resolver.</summary>
    [TestFixture]
    public sealed class LogLevelResolverTests
    {
        [Test]
        public void Root_Level_Filters_Below_Threshold()
        {
            var resolver = new LogLevelResolver();
            resolver.SetSettings(new LoggingSettings
            {
                RootLevel = GameLogLevel.Warn,
            });

            Assert.IsFalse(resolver.IsEnabled("Anywhere", GameLogLevel.Debug));
            Assert.IsFalse(resolver.IsEnabled("Anywhere", GameLogLevel.Info));
            Assert.IsTrue(resolver.IsEnabled("Anywhere", GameLogLevel.Warn));
            Assert.IsTrue(resolver.IsEnabled("Anywhere", GameLogLevel.Error));
        }

        [Test]
        public void Per_Source_Level_Overrides_The_Root()
        {
            var settings = new LoggingSettings { RootLevel = GameLogLevel.Info };
            settings.ClassLevels["Quiet"] = GameLogLevel.Error;
            settings.ClassLevels["Loud"] = GameLogLevel.Debug;
            var resolver = new LogLevelResolver();
            resolver.SetSettings(settings);

            Assert.IsFalse(resolver.IsEnabled("Quiet", GameLogLevel.Info));
            Assert.IsTrue(resolver.IsEnabled("Loud", GameLogLevel.Debug));
            Assert.IsTrue(resolver.IsEnabled("Other", GameLogLevel.Info));
        }

        [Test]
        public void None_Disables_A_Source_And_Setting_Changes_Invalidate_The_Cache()
        {
            var settings = new LoggingSettings { RootLevel = GameLogLevel.None };
            var resolver = new LogLevelResolver();
            resolver.SetSettings(settings);
            Assert.IsFalse(resolver.IsEnabled("Any", GameLogLevel.Error));

            resolver.SetSettings(new LoggingSettings { RootLevel = GameLogLevel.Debug });
            Assert.IsTrue(resolver.IsEnabled("Any", GameLogLevel.Debug));
        }
    }

    /// <summary>Fake sink that records every written entry.</summary>
    internal sealed class RecordingSink : ILogSink
    {
        public readonly List<LogEntry> Entries = new List<LogEntry>();

        public void Write(LogEntry entry) => Entries.Add(entry);
    }

    /// <summary>Fake configuration source.</summary>
    internal sealed class FakeConfigSource : ILogConfigurationSource
    {
        private readonly LoggingConfiguration m_Configuration;
        private readonly Exception m_Throw;

        public FakeConfigSource(LoggingConfiguration configuration)
        {
            m_Configuration = configuration;
        }

        public FakeConfigSource(Exception toThrow)
        {
            m_Throw = toThrow;
        }

        public LoggingConfiguration Load()
        {
            if (m_Throw != null)
                throw m_Throw;

            return m_Configuration;
        }
    }

    /// <summary>
    /// Regression tests for GameLogService routing: levels,
    /// lazy factories, exception stack, per-source policy and startup fallback.
    /// </summary>
    [TestFixture]
    public sealed class GameLogServiceTests
    {
        private static GameLogService Create(
            LoggingSettings settings, RecordingSink sink)
        {
            var configuration = new LoggingConfiguration
            {
                Path = "<memory>",
                Exists = true,
                Settings = settings ?? new LoggingSettings(),
            };
            return new GameLogService(
                new FakeConfigSource(configuration), new LogLevelResolver(), sink);
        }

        [Test]
        public void Below_Threshold_Is_Not_Written()
        {
            var sink = new RecordingSink();
            var service = Create(new LoggingSettings { RootLevel = GameLogLevel.Warn }, sink);

            service.Log(GameLogLevel.Info, "Src", "hidden");
            service.Log(GameLogLevel.Warn, "Src", "shown");

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Warn, sink.Entries[0].Level);
            Assert.AreEqual("Src", sink.Entries[0].Source);
            Assert.AreEqual("shown", sink.Entries[0].Message);
        }

        [Test]
        public void Lazy_Factory_Is_Not_Evaluated_When_Disabled()
        {
            var sink = new RecordingSink();
            var service = Create(new LoggingSettings { RootLevel = GameLogLevel.Error }, sink);

            var called = false;
            service.Log(GameLogLevel.Debug, "Src", (Func<object>)(() =>
            {
                called = true;
                return "expensive";
            }));

            Assert.IsFalse(called);
            Assert.IsEmpty(sink.Entries);
        }

        [Test]
        public void Lazy_Factory_Exception_Is_Reported_As_An_Error()
        {
            var sink = new RecordingSink();
            var service = Create(new LoggingSettings { RootLevel = GameLogLevel.Debug }, sink);

            var failure = new InvalidOperationException("boom");
            service.Log(GameLogLevel.Info, "Src", (Func<object>)(() => throw failure));

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Error, sink.Entries[0].Level);
            Assert.AreSame(failure, sink.Entries[0].Exception);
        }

        [Test]
        public void Exception_Logging_Keeps_The_Exception_Object()
        {
            var sink = new RecordingSink();
            var service = Create(new LoggingSettings { RootLevel = GameLogLevel.Info }, sink);

            var failure = new InvalidOperationException("boom");
            service.LogException("Src", failure);

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Error, sink.Entries[0].Level);
            Assert.AreSame(failure, sink.Entries[0].Exception);
        }

        [Test]
        public void Per_Source_Policy_Is_Applied()
        {
            var settings = new LoggingSettings { RootLevel = GameLogLevel.Info };
            settings.ClassLevels["Quiet"] = GameLogLevel.Error;
            var sink = new RecordingSink();
            var service = Create(settings, sink);

            service.Log(GameLogLevel.Info, "Quiet", "hidden");
            service.Log(GameLogLevel.Info, "Loud", "shown");

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual("Loud", sink.Entries[0].Source);
        }

        [Test]
        public void All_Injected_Sinks_Receive_The_Entry()
        {
            var first = new RecordingSink();
            var second = new RecordingSink();
            var service = new GameLogService(
                new FakeConfigSource(new LoggingConfiguration
                {
                    Path = "<memory>", Exists = true, Settings = new LoggingSettings()
                }),
                new LogLevelResolver(), new ILogSink[] { first, second });

            service.Log(GameLogLevel.Info, "Src", "hello");

            Assert.AreEqual(1, first.Entries.Count);
            Assert.AreEqual(1, second.Entries.Count);
        }

        [Test]
        public void Missing_Configuration_Is_Reported_As_A_Warning()
        {
            var sink = new RecordingSink();
            var service = new GameLogService(
                new FakeConfigSource(new LoggingConfiguration
                {
                    Path = "<absent>",
                    Exists = false,
                    Settings = new LoggingSettings(),
                }),
                new LogLevelResolver(), sink);

            service.Reload();

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Warn, sink.Entries[0].Level);
        }

        [Test]
        public void A_Configuration_Load_Failure_Falls_Back_To_Defaults_And_Is_Logged()
        {
            var sink = new RecordingSink();
            var failure = new InvalidOperationException("bad config");
            var service = new GameLogService(
                new FakeConfigSource(failure), new LogLevelResolver(), sink);

            service.Reload();

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Error, sink.Entries[0].Level);
            Assert.AreSame(failure, sink.Entries[0].Exception);

            // Defaults applied: Info is enabled, Debug is not.
            Assert.IsTrue(service.IsEnabled(GameLogLevel.Info, "Src"));
            Assert.IsFalse(service.IsEnabled(GameLogLevel.Debug, "Src"));
        }
    }
}
