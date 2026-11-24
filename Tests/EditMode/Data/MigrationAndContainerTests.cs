using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniSpring.Data.Internal;
using UniSpring.DependencyInjection;

namespace UniSpring.Data.Tests
{
    /// <summary>Migrations with their separate record, and repositories handed out by the container.</summary>
    [TestFixture]
    public sealed class MigrationAndContainerTests
    {
        [Migration(1)]
        public sealed class WalletCoinsToBalance : IMigration
        {
            public static int Runs;

            public void Run(IMigrationStore store)
            {
                Runs++;
                var old = store.Load<WalletRecordV1>("Wallet");
                if (old == null)
                    return;
                store.Save("Wallet", new WalletRecord { balance = old.coins });
            }
        }

        [Migration(2)]
        public sealed class DoubleMileage : IMigration
        {
            public void Run(IMigrationStore store)
            {
                foreach (var id in store.Ids("ParkedCar"))
                {
                    var car = store.Load<ParkedCarRecord>("ParkedCar", id);
                    car.mileage *= 2;
                    store.Save("ParkedCar", id, car);
                }
            }
        }

        [Migration(3)]
        public sealed class Failing : IMigration
        {
            public void Run(IMigrationStore store)
            {
                store.Save("Wallet", new WalletRecord { balance = -1 });
                throw new InvalidOperationException("the migration failed");
            }
        }

        public interface IGarage
        {
            int CarsOf(string owner);
            void Park(string id, string owner);
        }

        [Service]
        public sealed class Garage : IGarage
        {
            private readonly ICarRepository m_Cars;

            public Garage(ICarRepository cars)
            {
                m_Cars = cars;
            }

            public int CarsOf(string owner) => m_Cars.CountByOwner(owner);

            public void Park(string id, string owner) => m_Cars.Save(id, new ParkedCar(owner, "Coupe", 0));
        }

        [SetUp]
        public void SetUp()
        {
            WalletCoinsToBalance.Runs = 0;
        }

        [Test]
        public void A_Migration_Rewrites_The_Stored_Shape_Once()
        {
            var serializer = new JsonTestSerializer();
            var data = DocumentData.InMemory(serializer).With(new WalletMapper());
            data.Store.Write("Documents", "Wallet", serializer.Write(new WalletRecordV1 { coins = 7 }));

            data.Migrate(new WalletCoinsToBalance());
            data.Migrate(new WalletCoinsToBalance());

            Assert.AreEqual(1, WalletCoinsToBalance.Runs, "the store remembers what it has applied");
            Assert.AreEqual(7, data.Repository<IWalletDocument>().Load().Balance);
        }

        [Test]
        public void Migrations_Run_In_Order_And_Only_Those_Not_Applied_Yet()
        {
            var serializer = new JsonTestSerializer();
            var data = DocumentData.InMemory(serializer).With(new ParkedCarMapper());
            data.Repository<ICarRepository>().Save("car-1", new ParkedCar("alex", "Coupe", 5));

            data.Migrate(new DoubleMileage(), new WalletCoinsToBalance());
            data.Migrate(new WalletCoinsToBalance(), new DoubleMileage());

            Assert.AreEqual(10, data.Repository<ICarRepository>().Find("car-1").Mileage, "applied once");
            Assert.AreEqual(1, WalletCoinsToBalance.Runs);
        }

        [Test]
        public void A_Failed_Migration_Leaves_Nothing_Behind()
        {
            var data = DocumentData.InMemory(new JsonTestSerializer()).With(new WalletMapper());

            Assert.Throws<InvalidOperationException>(() => data.Migrate(new WalletCoinsToBalance(), new Failing()));

            Assert.IsNull(data.Repository<IWalletDocument>().Load(), "its write was rolled back");
            data.Migrate(new WalletCoinsToBalance());
            Assert.AreEqual(1, WalletCoinsToBalance.Runs, "the earlier migration stays applied");
        }

        [Test]
        public void The_Record_Of_Applied_Migrations_Is_Its_Own_Document()
        {
            var data = DocumentData.InMemory(new JsonTestSerializer());

            data.Migrate(new WalletCoinsToBalance(), new DoubleMileage());

            Assert.AreEqual("2", System.Text.Encoding.ASCII.GetString(data.Store.Read("UniSpring", "migrations").Bytes));
        }

        [Test]
        public void The_Container_Implements_A_Repository_A_Bean_Asks_For()
        {
            var store = new InMemoryDocumentStore();
            var context = ApplicationContext.Build(
                new[]
                {
                    typeof(RepositoryProvider), typeof(JsonTestSerializer), typeof(ParkedCarMapper),
                    typeof(ParkedCarValidator), typeof(Garage)
                },
                scope => scope.RegisterInstance<IDocumentStore>(store));
            var garage = context.Get<IGarage>();

            garage.Park("car-1", "alex");

            Assert.AreEqual(1, garage.CarsOf("alex"));
            Assert.AreSame(context.Get<ICarRepository>(), context.Get<ICarRepository>(), "one repository per container");
            Assert.AreEqual(DocumentStatus.Found, store.Read("ParkedCar", "car-1").Status);
            context.Dispose();
        }

        [Test]
        public void The_Container_Runs_Migrations_Before_The_Beans_Are_Created()
        {
            var serializer = new JsonTestSerializer();
            var store = new InMemoryDocumentStore();
            store.Write("Documents", "Wallet", serializer.Write(new WalletRecordV1 { coins = 3 }));

            var context = ApplicationContext.Build(
                new[]
                {
                    typeof(RepositoryProvider), typeof(MigrationRunner), typeof(JsonTestSerializer),
                    typeof(WalletMapper), typeof(WalletCoinsToBalance)
                },
                scope => scope.RegisterInstance<IDocumentStore>(store));

            Assert.AreEqual(3, context.Get<IWalletDocument>().Load().Balance);
            context.Dispose();
        }

        [Test]
        public void A_Container_Without_Repositories_Needs_No_Store()
        {
            var context = ApplicationContext.Build(new[] { typeof(RepositoryProvider), typeof(MigrationRunner) });

            Assert.Throws<InvalidOperationException>(() => context.Get<IList<string>>(), "an unknown contract is still an error");
            context.Dispose();
        }
    }
}
