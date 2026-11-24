using System.Collections;
using NUnit.Framework;
using UniSpring.DependencyInjection;
using UniSpring.DependencyInjection.Editor;
using UnityEngine.TestTools;

namespace UniSpring.Data.Tests
{
    /// <summary>Repositories and catalogs implemented by the code generated for IL2CPP, run in the editor.</summary>
    [TestFixture]
    public sealed class GeneratedRepositoryTests
    {
        [TearDown]
        public void TearDown() => GeneratedProxies.Override(null);

        [Test]
        public void The_Link_Xml_Keeps_What_Repositories_Catalogs_And_Mappers_Read_By_Reflection()
        {
            var xml = LinkXmlGenerator.Generate(new[] { typeof(GeneratedRepositoryTests).Assembly });

            StringAssert.Contains("\"UniSpring.Data.Tests.ParkedCar\"", xml, "stored type of a repository: queries read its properties");
            StringAssert.Contains("\"UniSpring.Data.Tests.ParkedCarRecord\"", xml, "record type of a mapper bean: the serializer reads it");
            StringAssert.Contains("\"UniSpring.Data.Tests.CatalogTests/ShopItem\"", xml, "stored type of a catalog");
            StringAssert.Contains("\"UniSpring.Data.Tests.CatalogTests/DrivingSettings\"", xml, "[ConfigurationProperties]");
        }

        [UnityTest]
        public IEnumerator A_Generated_Repository_And_Catalog_Behave_As_The_Run_Time_Ones()
        {
            var source = ProxySourceWriter.Write(
                "DataTests", new[] { typeof(ICarRepository), typeof(IWalletDocument), typeof(CatalogTests.IShopCatalog) }, null);
            var build = GeneratedProxyCompiler.Start(source, typeof(GeneratedRepositoryTests).Assembly, typeof(DocumentData).Assembly);
            while (!build.Done)
                yield return null;
            Assert.IsEmpty(build.Errors, string.Join("\n", build.Errors) + "\n" + source);
            GeneratedProxies.Override(GeneratedProxies.Declared(build.Assembly));

            var resources = new CatalogTests.FakeResources();
            resources.Texts["Shop/Items/slick"] = "{\"category\":\"tyre\",\"price\":120}";
            resources.Texts["Shop/Items/turbo"] = "{\"category\":\"engine\",\"price\":900}";
            var data = DocumentData.InMemory(new JsonTestSerializer())
                .With(new WalletMapper())
                .With(new ParkedCarMapper())
                .With(new ParkedCarValidator())
                .With(new CatalogTests.ShopItemMapper());
            var cars = data.Repository<ICarRepository>();
            var wallet = data.Repository<IWalletDocument>();
            var catalog = data.Catalog<CatalogTests.IShopCatalog>(resources);

            Assert.IsInstanceOf<GeneratedProxy>(cars);
            Assert.IsInstanceOf<GeneratedProxy>(catalog);
            cars.Save("car-1", new ParkedCar("alex", "Coupe", 10));
            cars.Save("car-2", new ParkedCar("sam", "Van", 20));
            Assert.AreEqual("Coupe", cars.Find("car-1").Model);
            Assert.AreEqual(1, cars.FindByOwner("sam").Count);
            Assert.AreEqual(LoadStatus.Found, cars.Read("car-2").Status);
            wallet.Save(new Wallet(40));
            Assert.AreEqual(40, wallet.Load().Balance);
            Assert.AreEqual(900, catalog.Find("turbo").Price);
            Assert.AreEqual(1, catalog.CountByCategory("tyre"));
        }
    }
}
