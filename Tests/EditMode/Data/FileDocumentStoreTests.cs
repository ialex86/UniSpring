using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UniSpring.Data.Internal;
using UniSpring.Transactions;
using UnityEngine.TestTools;

namespace UniSpring.Data.Tests
{
    /// <summary>The file store on a real temporary folder: background writes, crash safety and recovery.</summary>
    [TestFixture]
    public sealed class FileDocumentStoreTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
        private static readonly StoreTiming Quick = new StoreTiming(
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(300));

        private readonly List<FileDocumentStore> m_Stores = new List<FileDocumentStore>();
        private string m_Root;
        private bool m_IgnoredLogs;

        [SetUp]
        public void SetUp()
        {
            m_Root = Path.Combine(Path.GetTempPath(), "UniSpringTests", Guid.NewGuid().ToString("N"));
            m_IgnoredLogs = LogAssert.ignoreFailingMessages;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var store in m_Stores)
                store.Close(TimeSpan.FromSeconds(1));
            m_Stores.Clear();
            LogAssert.ignoreFailingMessages = m_IgnoredLogs;
            if (Directory.Exists(m_Root))
                Directory.Delete(m_Root, true);
        }

        private FileDocumentStore Open(IFileSystem files = null)
        {
            var store = new FileDocumentStore(m_Root, files ?? SystemFileSystem.Instance, Quick);
            m_Stores.Add(store);
            return store;
        }

        /// <summary>Closes every open store and opens a fresh one: what the next run of the game would find.</summary>
        private FileDocumentStore Reopen()
        {
            foreach (var store in m_Stores)
                store.Close(TimeSpan.FromSeconds(1));
            return Open();
        }

        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        private static string Text(DocumentRead read) => Encoding.UTF8.GetString(read.Bytes);

        [Test]
        public void A_Write_Is_Read_At_Once_And_Found_After_A_Restart()
        {
            var store = Open();

            store.Write("Garage", "car-1", Bytes("coupe"));

            Assert.AreEqual("coupe", Text(store.Read("Garage", "car-1")), "readable before it reaches the disk");
            Assert.IsTrue(store.WaitForDisk(Wait));
            Assert.IsTrue(store.IsIdle);
            Assert.AreEqual("coupe", Text(Reopen().Read("Garage", "car-1")));
        }

        [Test]
        public void A_Delete_Removes_The_Document_And_Its_Backup()
        {
            var store = Open();
            store.Write("Garage", "car-1", Bytes("v1"));
            store.Write("Garage", "car-1", Bytes("v2"));
            store.WaitForDisk(Wait);

            store.Delete("Garage", "car-1");

            Assert.AreEqual(DocumentStatus.Missing, store.Read("Garage", "car-1").Status);
            store.WaitForDisk(Wait);
            Assert.AreEqual(DocumentStatus.Missing, Reopen().Read("Garage", "car-1").Status, "the backup does not bring it back");
        }

        [Test]
        public void Names_Are_Listed_Sorted_With_Any_Text_And_Survive_A_Restart()
        {
            var store = Open();
            store.Write("VehicleAssembly/Cars", "sam|car-2", Bytes("a"));
            store.Write("VehicleAssembly/Cars", "alex|car-7", Bytes("b"));
            store.Write("VehicleAssembly/Cars", "Ünïcode name", Bytes("c"));
            store.Write("Garage", "other", Bytes("d"));

            var expected = new[] { "alex|car-7", "sam|car-2", "Ünïcode name" };
            Assert.AreEqual(expected, store.Names("VehicleAssembly/Cars"), "before the disk has them");
            store.WaitForDisk(Wait);
            Assert.AreEqual(expected, Reopen().Names("VehicleAssembly/Cars"));
        }

        [Test]
        public void A_Damaged_Document_Is_Read_From_Its_Backup()
        {
            var store = Open();
            store.Write("Garage", "car-1", Bytes("v1"));
            store.WaitForDisk(Wait);
            store.Write("Garage", "car-1", Bytes("v2"));
            store.WaitForDisk(Wait);
            store.Close(Wait);

            var path = new DocumentFiles(m_Root, SystemFileSystem.Instance).DocumentPath("Garage", "car-1");
            File.WriteAllBytes(path, Bytes("garbage"));
            LogAssert.ignoreFailingMessages = true;

            Assert.AreEqual("v1", Text(Open().Read("Garage", "car-1")));
        }

        [Test]
        public void A_Document_With_No_Intact_Copy_Is_Damaged()
        {
            var store = Open();
            store.Write("Garage", "car-1", Bytes("v1"));
            store.WaitForDisk(Wait);
            store.Close(Wait);

            var path = new DocumentFiles(m_Root, SystemFileSystem.Instance).DocumentPath("Garage", "car-1");
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            var read = Open().Read("Garage", "car-1");
            Assert.AreEqual(DocumentStatus.Damaged, read.Status);
            StringAssert.Contains("checksum", read.Error);
        }

        [Test]
        public void A_Transaction_Lands_Whole_And_A_Rollback_Leaves_Nothing()
        {
            var store = Open();

            store.Run(() =>
            {
                store.Write("Economy", "wallet", Bytes("40"));
                store.Write("Garage", "car-1", Bytes("coupe"));
            });
            Assert.Throws<InvalidOperationException>(() => store.Run(() =>
            {
                store.Write("Economy", "wallet", Bytes("0"));
                store.Write("Garage", "car-2", Bytes("truck"));
                throw new InvalidOperationException("the purchase failed");
            }, true));
            store.WaitForDisk(Wait);

            var reopened = Reopen();
            Assert.AreEqual("40", Text(reopened.Read("Economy", "wallet")));
            Assert.AreEqual("coupe", Text(reopened.Read("Garage", "car-1")));
            Assert.AreEqual(DocumentStatus.Missing, reopened.Read("Garage", "car-2").Status);
        }

        [Test]
        public void Other_Flows_See_A_Transaction_Only_After_It_Commits()
        {
            var store = Open();
            using (var transaction = store.Begin())
            {
                store.Write("Economy", "wallet", Bytes("40"));
                Assert.AreEqual(DocumentStatus.Found, store.Read("Economy", "wallet").Status, "its own flow reads it");
                Assert.AreEqual(DocumentStatus.Missing, ReadInAnotherFlow(store), "another flow does not");

                transaction.Commit();
            }

            Assert.AreEqual(DocumentStatus.Found, ReadInAnotherFlow(store));
        }

        private static DocumentStatus ReadInAnotherFlow(FileDocumentStore store)
        {
            Task<DocumentStatus> read;
            using (ExecutionContext.SuppressFlow())
                read = Task.Run(() => store.Read("Economy", "wallet").Status);
            return read.GetAwaiter().GetResult();
        }

        [Test]
        public void A_Refusing_Disk_Keeps_The_Change_Readable_And_Retries()
        {
            var files = new FlakyFileSystem { FailWrites = 3 };
            var store = Open(files);

            store.Write("Garage", "car-1", Bytes("coupe"));

            Assert.AreEqual("coupe", Text(store.Read("Garage", "car-1")), "readable while the disk refuses");
            Assert.IsTrue(store.WaitForDisk(Wait), "lands once the disk takes it");
            Assert.AreEqual(0, files.FailWrites);
            Assert.AreEqual("coupe", Text(Reopen().Read("Garage", "car-1")));
        }

        [Test]
        public void A_Committed_Step_Interrupted_On_Disk_Is_Completed_When_The_Store_Opens()
        {
            LogAssert.ignoreFailingMessages = true;
            var files = new FlakyFileSystem { FailReplaceOf = "Garage" };
            var store = Open(files);

            store.Run(() =>
            {
                store.Write("Economy", "wallet", Bytes("40"));
                store.Write("Garage", "car-1", Bytes("coupe"));
            });
            Assert.IsFalse(store.WaitForDisk(TimeSpan.FromMilliseconds(200)), "the disk keeps refusing the car");
            store.Close(TimeSpan.FromMilliseconds(100));

            var reopened = Open();
            Assert.AreEqual("40", Text(reopened.Read("Economy", "wallet")));
            Assert.AreEqual("coupe", Text(reopened.Read("Garage", "car-1")), "completed from the journal");
            Assert.IsEmpty(Directory.GetFiles(m_Root, "*.tmp", SearchOption.AllDirectories), "no temporary file is left");
        }

        [Test]
        public void A_Journal_Left_By_A_Refused_Step_Never_Overwrites_Newer_Content()
        {
            LogAssert.ignoreFailingMessages = true;
            var files = new FlakyFileSystem { FailReplaceOf = "Garage" };
            var store = Open(files);
            store.Run(() =>
            {
                store.Write("Economy", "wallet", Bytes("old"));
                store.Write("Garage", "car-1", Bytes("coupe"));
            });
            Assert.IsFalse(store.WaitForDisk(TimeSpan.FromMilliseconds(100)));

            store.Write("Economy", "wallet", Bytes("new"));
            files.FailReplaceOf = null;
            Assert.IsTrue(store.WaitForDisk(Wait), "the retry lands the old step and the new write together");

            Assert.AreEqual("new", Text(Reopen().Read("Economy", "wallet")), "the old journal is gone, not completed");
        }

        [Test]
        public void An_Incomplete_Journal_Is_Dropped_And_The_Documents_Stay_As_They_Were()
        {
            var store = Open();
            store.Write("Garage", "car-1", Bytes("coupe"));
            store.WaitForDisk(Wait);
            store.Close(Wait);

            var journal = Path.Combine(m_Root, ".journal");
            Directory.CreateDirectory(journal);
            File.WriteAllText(Path.Combine(journal, "9.journal"), "UNISPRING-TX 1\nW Garage 6361722d31\n");
            var stray = new DocumentFiles(m_Root, SystemFileSystem.Instance).DocumentPath("Garage", "car-1") + ".9.tmp";
            File.WriteAllText(stray, "half written");

            var reopened = Open();

            Assert.AreEqual("coupe", Text(reopened.Read("Garage", "car-1")));
            Assert.IsEmpty(Directory.GetFiles(journal));
            Assert.IsFalse(File.Exists(stray));
        }

        [Test]
        public void After_Close_A_Write_Is_On_Disk_When_It_Returns()
        {
            var store = Open();
            store.Close(Wait);

            store.Write("Garage", "car-1", Bytes("coupe"));

            Assert.IsTrue(store.IsIdle);
            Assert.AreEqual("coupe", Text(Open().Read("Garage", "car-1")));
        }

        [Test]
        public void Invalid_Areas_And_Names_Are_Refused_On_The_Callers_Thread()
        {
            var store = Open();

            Assert.Throws<ArgumentException>(() => store.Write("../escape", "x", Bytes("a")));
            Assert.Throws<ArgumentException>(() => store.Write("List`1", "x", Bytes("a")));
            Assert.Throws<ArgumentException>(() => store.Write("Garage", string.Empty, Bytes("a")));
            Assert.Throws<ArgumentException>(() => store.Write("Garage", new string('x', 101), Bytes("a")));
        }

        [Test]
        public void Repositories_Keep_Their_Objects_Across_A_Restart()
        {
            var serializer = new JsonTestSerializer();
            var first = Open();
            var cars = new DocumentData(first, serializer).With(new ParkedCarMapper()).Repository<ICarRepository>();
            cars.Save("alex|car-7", new ParkedCar("alex", "Coupe", 12));
            first.WaitForDisk(Wait);

            var reopened = new DocumentData(Reopen(), serializer).With(new ParkedCarMapper()).Repository<ICarRepository>();

            Assert.AreEqual(12, reopened.Find("alex|car-7").Mileage);
            Assert.AreEqual(1, reopened.CountByOwner("alex"));
        }

        [Test]
        public void A_New_Game_Clears_Every_Document_But_Keeps_The_Migration_Record()
        {
            var store = Open();
            store.Write("Garage", "car-1", Bytes("coupe"));
            store.Write("VehicleAssembly/Cars", "alex|car-7", Bytes("truck"));
            store.Write("UniSpring", "migrations", Bytes("3"));
            store.WaitForDisk(Wait);
            store.Write("Economy", "wallet", Bytes("40")); // still waiting for the disk
            var told = 0;
            using (store.Subscribe(() => told++))
            {
                Assert.IsTrue(store.HasDocuments);

                store.DeleteAll();

                Assert.AreEqual(1, told, "the subscribers drop their state");
                Assert.IsFalse(store.HasDocuments);
            }

            var reopened = Reopen();
            Assert.IsFalse(reopened.HasDocuments, "nothing comes back after a restart");
            Assert.IsEmpty(reopened.Names("VehicleAssembly/Cars"));
            Assert.AreEqual("3", Text(reopened.Read("UniSpring", "migrations")));
        }

        [Test]
        public void A_New_Game_Is_Refused_Inside_A_Transaction_And_Drops_Those_Of_Other_Flows()
        {
            LogAssert.ignoreFailingMessages = true;
            var store = Open();
            store.Write("Garage", "car-1", Bytes("coupe"));

            using (store.Begin())
                Assert.Throws<InvalidOperationException>(() => store.DeleteAll());
            Assert.AreEqual(DocumentStatus.Found, store.Read("Garage", "car-1").Status, "the refused clear changed nothing");

            var opened = new ManualResetEventSlim();
            var cleared = new ManualResetEventSlim();
            Task oldGame;
            using (ExecutionContext.SuppressFlow())
            {
                oldGame = Task.Run(() =>
                {
                    using (var transaction = store.Begin())
                    {
                        store.Write("Economy", "wallet", Bytes("old"));
                        opened.Set();
                        cleared.Wait();
                        store.Write("Economy", "loan", Bytes("old"));
                        transaction.Commit();
                    }
                });
            }

            opened.Wait();
            store.DeleteAll();
            cleared.Set();
            oldGame.GetAwaiter().GetResult();
            store.WaitForDisk(Wait);

            Assert.IsFalse(store.HasDocuments, "the old game's transaction never lands");
            Assert.IsFalse(Reopen().HasDocuments);
        }

        [Test]
        public void Closing_On_A_Refusing_Disk_Saves_What_The_Disk_Takes()
        {
            LogAssert.ignoreFailingMessages = true;
            var store = Open(new FlakyFileSystem { FailWritesOf = "Garage" });
            store.Write("Garage", "car-1", Bytes("coupe"));
            store.Write("Economy", "wallet", Bytes("40"));
            Assert.IsFalse(store.WaitForDisk(TimeSpan.FromMilliseconds(100)), "the car blocks everything after it");

            store.Close(TimeSpan.FromMilliseconds(100));

            Assert.IsFalse(store.IsIdle, "a change left out is not on disk");
            Assert.AreEqual(1, store.LostChanges);
            Assert.IsFalse(store.WaitForDisk(TimeSpan.Zero));
            var reopened = Open();
            Assert.AreEqual("40", Text(reopened.Read("Economy", "wallet")), "the wallet is saved without the car");
            Assert.AreEqual(DocumentStatus.Missing, reopened.Read("Garage", "car-1").Status);
        }

        [Test]
        public void The_In_Memory_Store_Clears_The_Same_Way()
        {
            var store = new InMemoryDocumentStore();
            store.Write("Garage", "car-1", Bytes("coupe"));
            store.Write("UniSpring", "migrations", Bytes("3"));
            var told = 0;
            store.Subscribe(() => told++);

            store.DeleteAll();

            Assert.IsFalse(store.HasDocuments);
            Assert.AreEqual(1, told);
            Assert.AreEqual(DocumentStatus.Found, store.Read("UniSpring", "migrations").Status);
        }

        [Test]
        public void A_Retry_Completes_The_Committed_Journal_Before_Anything_New_Is_Prepared()
        {
            LogAssert.ignoreFailingMessages = true;
            var files = new FlakyFileSystem { FailReplaceOf = "Garage" };
            var store = Open(files);
            store.Run(() =>
            {
                store.Write("Economy", "wallet", Bytes("40"));
                store.Write("Garage", "car-1", Bytes("coupe"));
            });
            Assert.IsFalse(store.WaitForDisk(TimeSpan.FromMilliseconds(100)), "committed, stuck on the car");

            // The next attempt fails while preparing: the committed journal must survive it.
            files.FailReplaceOf = null;
            files.FailWritesOf = "Economy";
            store.Write("Economy", "wallet", Bytes("50"));
            Assert.IsFalse(store.WaitForDisk(TimeSpan.FromMilliseconds(100)));
            store.Close(TimeSpan.FromMilliseconds(100));

            var reopened = Open();
            Assert.AreEqual("coupe", Text(reopened.Read("Garage", "car-1")), "the transaction is whole");
            Assert.AreEqual("40", Text(reopened.Read("Economy", "wallet")), "the later write could not be prepared");
        }

        [Test]
        public void A_Clear_Keeps_A_Document_Written_After_It_Began()
        {
            var files = new FlakyFileSystem();
            var store = Open(files);
            store.Write("Garage", "car-1", Bytes("old game"));
            store.WaitForDisk(Wait);

            // Another thread writes while the clear lists what to delete.
            files.OnListing = () => store.Write("Garage", "car-1", Bytes("new game"));
            store.DeleteAll();

            Assert.AreEqual("new game", Text(store.Read("Garage", "car-1")));
            Assert.IsTrue(store.WaitForDisk(Wait));
            Assert.AreEqual("new game", Text(Reopen().Read("Garage", "car-1")));
        }

        [Test]
        public void A_Nested_Rollback_Makes_The_Whole_Transaction_Save_Nothing()
        {
            var store = Open();

            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                using (var outer = store.Begin())
                {
                    store.Write("Game", "A", Bytes("a"));
                    using (var inner = store.Begin())
                    {
                        store.Write("Game", "B", Bytes("b"));
                        inner.Rollback();
                    }

                    store.Write("Game", "C", Bytes("c"));
                    outer.Commit();
                }
            });

            StringAssert.Contains("rolled back inside", error.Message);
            Assert.IsEmpty(store.Names("Game"), "neither A, B nor C");
        }

        /// <summary>A disk that refuses some operations.</summary>
        private sealed class FlakyFileSystem : IFileSystem
        {
            private readonly IFileSystem m_Inner = SystemFileSystem.Instance;

            /// <summary>How many durable writes still fail.</summary>
            public int FailWrites;

            /// <summary>Replacing a document under a folder with this name always fails.</summary>
            public string FailReplaceOf;

            /// <summary>Writing a file under a folder with this name always fails.</summary>
            public string FailWritesOf;

            public bool Exists(string path) => m_Inner.Exists(path);

            public byte[] ReadAllBytes(string path) => m_Inner.ReadAllBytes(path);

            public void WriteDurable(string path, byte[] bytes)
            {
                if (FailWritesOf != null && path.Contains(Path.DirectorySeparatorChar + FailWritesOf + Path.DirectorySeparatorChar))
                    throw new IOException("the disk is full");
                if (Interlocked.Decrement(ref FailWrites) >= 0)
                    throw new IOException("the disk is full");
                Interlocked.Exchange(ref FailWrites, 0);
                m_Inner.WriteDurable(path, bytes);
            }

            public void Replace(string source, string destination, string backup)
            {
                if (FailReplaceOf != null && destination.Contains(Path.DirectorySeparatorChar + FailReplaceOf + Path.DirectorySeparatorChar))
                    throw new IOException("the file is held by another program");
                m_Inner.Replace(source, destination, backup);
            }

            public void Delete(string path) => m_Inner.Delete(path);

            public void CreateDirectory(string path) => m_Inner.CreateDirectory(path);

            public IReadOnlyList<string> Files(string directory, string pattern) => m_Inner.Files(directory, pattern);

            /// <summary>Runs once, the next time a folder tree is listed.</summary>
            public Action OnListing;

            public IReadOnlyList<string> FilesBelow(string directory, string pattern)
            {
                var listing = Interlocked.Exchange(ref OnListing, null);
                listing?.Invoke();
                return m_Inner.FilesBelow(directory, pattern);
            }
        }
    }
}
