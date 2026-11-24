using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UniSpring.Logging;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;
using UniSpring.Logging.Services;

namespace UniSpring.Logging.Tests
{
    /// <summary>Hierarchical sources, prefix levels, exception entries and backend lifecycle.</summary>
    [TestFixture]
    public sealed class GameLogSourceAndLifecycleTests
    {
        [SetUp]
        public void ConfigureSourceForTheseTests()
        {
            GameLogSource.Configure(new GameLogSourceOptions
            {
                SourceRoot = "/Assets/Src/",
                IgnoredSegments = new[] { "Scripts", "Internal" }
            });
        }

        [TearDown]
        public void RestoreDefaultSourceOptions()
        {
            GameLogSource.Configure(null);
        }

        [TestCase("/Users/a/P/Assets/Src/Scripts/Vehicle/Presentation/DashboardRoot.cs",
            "Vehicle.Presentation.DashboardRoot")]
        [TestCase("/Users/a/P/Assets/Src/Scripts/Inventory/Internal/Application/LocalInventoryApi.cs",
            "Inventory.Application.LocalInventoryApi")]
        [TestCase(@"C:\P\Assets\Src\MapTools\Editor\MapWindow.cs", "MapTools.Editor.MapWindow")]
        [TestCase("Assets/Src/Logging/GameLog.cs", "Logging.GameLog")]
        [TestCase("/Users/a/P/Assets/Src/Scripts/Foo/Bar.Partial.cs", "Foo.Bar")]
        [TestCase("/Users/a/P/Packages/vendor/Runtime/Water.cs", "Water")]
        [TestCase("", GameLogSource.Unknown)]
        public void Source_Is_The_Module_Path_Under_Src(string path, string expected)
        {
            Assert.AreEqual(expected, GameLogSource.FromCallerPath(path));
        }

        [Test]
        public void Without_A_Configured_Root_Only_The_File_Name_Is_Used()
        {
            GameLogSource.Configure(null);

            Assert.AreEqual(
                "Water",
                GameLogSource.FromCallerPath("/Users/a/P/Assets/Src/Scripts/Water.cs"));
        }

        [Test]
        public void A_Custom_Root_And_Ignored_Segments_Are_Applied()
        {
            GameLogSource.Configure(new GameLogSourceOptions
            {
                SourceRoot = "Assets/Game",
                IgnoredSegments = new[] { "Runtime" }
            });

            Assert.AreEqual(
                "Features.Water",
                GameLogSource.FromCallerPath("/P/Assets/Game/Runtime/Features/Water.cs"));
        }

        [Test]
        public void Short_Name_And_Module_Are_The_Last_And_First_Segments()
        {
            Assert.AreEqual("DashboardRoot", GameLogSource.ShortName("Vehicle.Presentation.DashboardRoot"));
            Assert.AreEqual("Vehicle", GameLogSource.Module("Vehicle.Presentation.DashboardRoot"));
            Assert.AreEqual("Src", GameLogSource.ShortName("Src"));
        }

        [Test]
        public void The_Most_Specific_Key_Wins()
        {
            var settings = new LoggingSettings { RootLevel = GameLogLevel.Info };
            settings.ClassLevels["Vehicle"] = GameLogLevel.Debug;
            settings.ClassLevels["Vehicle.Presentation"] = GameLogLevel.Error;
            settings.ClassLevels["DashboardRoot"] = GameLogLevel.Debug;
            var resolver = new LogLevelResolver();
            resolver.SetSettings(settings);

            Assert.IsTrue(resolver.IsEnabled("Vehicle.Application.Planner", GameLogLevel.Debug),
                "module scope applies to nested sources");
            Assert.IsFalse(resolver.IsEnabled("Vehicle.Presentation.DashboardMenu", GameLogLevel.Warn),
                "deeper scope overrides the module");
            Assert.IsTrue(resolver.IsEnabled("Vehicle.Presentation.DashboardRoot", GameLogLevel.Debug),
                "file-name keys keep working and beat scopes");
            Assert.IsFalse(resolver.IsEnabled("Inventory.Application.LocalInventoryApi", GameLogLevel.Debug),
                "unrelated sources use root");
        }

        [Test]
        public void Message_And_Exception_Are_One_Entry_At_The_Requested_Level()
        {
            var sink = new RecordingSink();
            var service = new GameLogService(
                new FakeConfigSource(new LoggingConfiguration
                {
                    Path = "<memory>", Exists = true, Settings = new LoggingSettings()
                }),
                new LogLevelResolver(), sink);
            var failure = new InvalidOperationException("disk full");

            service.Log(GameLogLevel.Warn, "Inventory.Persistence.Store", "save failed", failure);

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Warn, sink.Entries[0].Level);
            Assert.AreEqual("save failed", sink.Entries[0].Message);
            Assert.AreSame(failure, sink.Entries[0].Exception);
        }

        [Test]
        public void Missing_Optional_Configuration_Is_Only_Info()
        {
            var sink = new RecordingSink();
            var service = new GameLogService(
                new FakeConfigSource(new LoggingConfiguration
                {
                    Path = "<absent>", Exists = false, Required = false, Settings = new LoggingSettings()
                }),
                new LogLevelResolver(), sink);

            service.Reload();

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Info, sink.Entries[0].Level);
        }

        [Test]
        public void Concurrent_Logging_From_Many_Threads_Is_Safe()
        {
            var service = new GameLogService(
                new FakeConfigSource(new LoggingConfiguration
                {
                    Path = "<memory>", Exists = true, Settings = new LoggingSettings { RootLevel = GameLogLevel.Error }
                }),
                new LogLevelResolver(), new RecordingSink());

            var tasks = new List<Task>();
            for (var thread = 0; thread < 8; thread++)
            {
                var id = thread;
                tasks.Add(Task.Run(() =>
                {
                    for (var i = 0; i < 2000; i++)
                        service.IsEnabled(GameLogLevel.Info, "Module" + id + ".Scope" + (i % 50) + ".File");
                }));
            }

            Assert.DoesNotThrow(() => Task.WaitAll(tasks.ToArray()));
        }

        [Test]
        public void Backends_Removed_Out_Of_Order_Never_Bring_A_Removed_One_Back()
        {
            var original = ActiveBackend();
            var first = new NullLogger();
            var second = new NullLogger();
            var firstHandle = GameLog.RegisterBackend(first);
            var secondHandle = GameLog.RegisterBackend(second);
            try
            {
                firstHandle.Dispose();
                Assert.AreSame(second, ActiveBackend(), "the newer backend stays active");

                secondHandle.Dispose();
                Assert.AreSame(original, ActiveBackend(), "the removed first backend does not come back");
            }
            finally
            {
                firstHandle.Dispose();
                secondHandle.Dispose();
            }
        }

        private static IGameLogger ActiveBackend()
        {
            var probe = new NullLogger();
            var active = GameLog.InstallBackend(probe);
            GameLog.RestoreBackend(probe, active);
            return active;
        }

        [Test]
        public void A_Second_Backend_Replaces_And_Restore_Brings_Back_The_Previous()
        {
            var first = new NullLogger();
            var second = new NullLogger();
            var original = GameLog.InstallBackend(first);
            try
            {
                var replaced = GameLog.InstallBackend(second);
                Assert.AreSame(first, replaced, "installing never throws, it returns the replaced backend");

                GameLog.RestoreBackend(second, first);
                Assert.AreSame(first, GameLog.InstallBackend(first), "restore puts the previous backend back");

                GameLog.RestoreBackend(second, null);
                Assert.IsTrue(GameLog.HasInstalledBackend, "a stale restore never removes a newer backend");
            }
            finally
            {
                GameLog.InstallBackend(original ?? first);
                GameLog.RestoreBackend(first, original);
            }
        }

        private sealed class NullLogger : IGameLogger
        {
            public void Reload() { }
            public bool IsEnabled(GameLogLevel level, string source) => false;
            public void Log(GameLogLevel level, string source, object message, object context = null) { }
            public void Log(GameLogLevel level, string source, Func<object> messageFactory, object context = null) { }
            public void Log(GameLogLevel level, string source, object message, Exception exception, object context = null) { }
            public void LogException(string source, Exception exception, object context = null) { }
        }
    }
}
