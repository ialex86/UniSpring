using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UniSpring.Data.Internal;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace UniSpring.Data.Tests
{
    /// <summary>Read-only catalogs and settings bound to resources.</summary>
    [TestFixture]
    public sealed class CatalogTests
    {
        // ---------------------------------------------------------------- fixtures

        public sealed class ShopItem
        {
            public ShopItem(string category, long price)
            {
                Category = category;
                Price = price;
            }

            public string Category { get; }
            public long Price { get; }
        }

        [Serializable]
        public sealed class ShopItemRecord
        {
            public string category;
            public long price;
        }

        [Component]
        public sealed class ShopItemMapper : IRecordMapper<ShopItem, ShopItemRecord>
        {
            public ShopItemRecord ToRecord(ShopItem item) => new ShopItemRecord { category = item.Category, price = item.Price };

            public ShopItem ToDomain(ShopItemRecord record) => new ShopItem(record.category, record.price);
        }

        [Component]
        public sealed class ShopItemValidator : IDocumentValidator<ShopItem>
        {
            public void Validate(string id, ShopItem item, ValidationErrors errors)
            {
                if (item.Price <= 0)
                    errors.Add("The price must be positive.");
            }
        }

        [Catalog("Shop/Items")]
        public interface IShopCatalog : ICatalog<ShopItem>
        {
            IReadOnlyList<ShopItem> FindByCategory(string category);
            int CountByCategory(string category);
        }

        [Catalog("Shop/Items")]
        public interface IWritableCatalog : ICatalog<ShopItem>
        {
            void DeleteByCategory(string category);
        }

        [Serializable]
        [ConfigurationProperties("Config/Driving")]
        public sealed class DrivingSettings
        {
            public float grip = 1f;
            public int gears = 5;
        }

        [Serializable]
        [ConfigurationProperties("Config/Audio", Required = true)]
        public sealed class AudioSettings
        {
            public float volume = 1f;
        }

        [ConfigurationProperties("Config/Feel")]
        public sealed class FeelAsset : ScriptableObject
        {
            public float steering = 0.5f;
        }

        [Component]
        public sealed class DrivingValidator : IDocumentValidator<DrivingSettings>
        {
            public void Validate(string id, DrivingSettings settings, ValidationErrors errors)
            {
                if (settings.gears < 1)
                    errors.Add("A car needs a gear.");
            }
        }

        public sealed class FakeResources : IResourceSource
        {
            public readonly Dictionary<string, string> Texts = new Dictionary<string, string>();
            public readonly Dictionary<string, UnityEngine.Object> Assets = new Dictionary<string, UnityEngine.Object>();

            public IReadOnlyList<KeyValuePair<string, byte[]>> LoadAll(string folder)
            {
                var result = new List<KeyValuePair<string, byte[]>>();
                foreach (var text in Texts)
                {
                    var slash = text.Key.LastIndexOf('/');
                    if (slash > 0 && text.Key.Substring(0, slash) == folder)
                        result.Add(new KeyValuePair<string, byte[]>(text.Key.Substring(slash + 1), Encoding.UTF8.GetBytes(text.Value)));
                }

                return result;
            }

            public byte[] Load(string path)
            {
                string text;
                return Texts.TryGetValue(path, out text) ? Encoding.UTF8.GetBytes(text) : null;
            }

            public UnityEngine.Object LoadAsset(string path, Type type)
            {
                UnityEngine.Object asset;
                return Assets.TryGetValue(path, out asset) ? asset : null;
            }
        }

        public interface IShop
        {
            int Tyres { get; }
            float Grip { get; }
        }

        [Service]
        public sealed class Shop : IShop
        {
            private readonly IShopCatalog m_Catalog;
            private readonly DrivingSettings m_Driving;

            public Shop(IShopCatalog catalog, DrivingSettings driving)
            {
                m_Catalog = catalog;
                m_Driving = driving;
            }

            public int Tyres => m_Catalog.CountByCategory("tyre");
            public float Grip => m_Driving.grip;
        }

        private FakeResources m_Resources;
        private DocumentData m_Data;

        [SetUp]
        public void SetUp()
        {
            m_Resources = new FakeResources();
            m_Resources.Texts["Shop/Items/slick"] = "{\"category\":\"tyre\",\"price\":120}";
            m_Resources.Texts["Shop/Items/turbo"] = "{\"category\":\"engine\",\"price\":900}";
            m_Resources.Texts["Shop/Items/rain"] = "{\"category\":\"tyre\",\"price\":80}";
            m_Data = DocumentData.InMemory(new JsonTestSerializer())
                .With(new ShopItemMapper())
                .With(new ShopItemValidator())
                .With(new DrivingValidator());
        }

        // ---------------------------------------------------------------- catalogs

        [Test]
        public void A_Catalog_Holds_Every_Item_Of_Its_Folder_By_Asset_Name()
        {
            var catalog = m_Data.Catalog<IShopCatalog>(m_Resources);

            Assert.AreEqual(new[] { "rain", "slick", "turbo" }, catalog.Ids());
            Assert.AreEqual(900, catalog.Find("turbo").Price);
            Assert.IsNull(catalog.Find("nitro"));
            Assert.IsTrue(catalog.Exists("slick"));
            Assert.AreEqual(3, catalog.All().Count);
            Assert.AreEqual(2, catalog.FindByCategory("tyre").Count);
            Assert.AreEqual(1, catalog.CountByCategory("engine"));
        }

        [Test]
        public void A_Broken_Item_Stops_The_Start_And_Names_The_Asset()
        {
            m_Resources.Texts["Shop/Items/broken"] = "not json";
            StringAssert.Contains("Shop/Items/broken", Assert.Throws<InvalidOperationException>(
                () => m_Data.Catalog<IShopCatalog>(m_Resources)).Message);
        }

        [Test]
        public void An_Invalid_Item_Stops_The_Start()
        {
            m_Resources.Texts["Shop/Items/free"] = "{\"category\":\"tyre\",\"price\":0}";
            StringAssert.Contains("price must be positive", Assert.Throws<InvalidOperationException>(
                () => m_Data.Catalog<IShopCatalog>(m_Resources)).Message);
        }

        [Test]
        public void A_Catalog_Is_Read_Only()
        {
            StringAssert.Contains("read-only", Assert.Throws<InvalidOperationException>(
                () => m_Data.Catalog<IWritableCatalog>(m_Resources)).Message);
        }

        // ---------------------------------------------------------------- settings

        [Test]
        public void Settings_Are_Read_From_Their_Resource()
        {
            m_Resources.Texts["Config/Driving"] = "{\"grip\":1.5,\"gears\":6}";

            var settings = m_Data.Catalog<DrivingSettings>(m_Resources);

            Assert.AreEqual(1.5f, settings.grip);
            Assert.AreEqual(6, settings.gears);
        }

        [Test]
        public void Missing_Settings_Give_Defaults_Unless_They_Are_Required()
        {
            Assert.AreEqual(5, m_Data.Catalog<DrivingSettings>(m_Resources).gears);
            StringAssert.Contains("Config/Audio", Assert.Throws<InvalidOperationException>(
                () => m_Data.Catalog<AudioSettings>(m_Resources)).Message);
        }

        [Test]
        public void Invalid_Settings_Stop_The_Start()
        {
            m_Resources.Texts["Config/Driving"] = "{\"gears\":0}";

            StringAssert.Contains("needs a gear", Assert.Throws<InvalidOperationException>(
                () => m_Data.Catalog<DrivingSettings>(m_Resources)).Message);
        }

        [Test]
        public void A_ScriptableObject_Setting_Is_Loaded_As_An_Asset()
        {
            var asset = ScriptableObject.CreateInstance<FeelAsset>();
            asset.steering = 0.9f;
            m_Resources.Assets["Config/Feel"] = asset;

            Assert.AreEqual(0.9f, m_Data.Catalog<FeelAsset>(m_Resources).steering);
            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void The_Container_Hands_Out_Catalogs_And_Settings_And_A_Registered_Instance_Wins()
        {
            m_Resources.Texts["Config/Driving"] = "{\"grip\":2.0,\"gears\":6}";
            var types = new[]
            {
                typeof(CatalogProvider), typeof(JsonTestSerializer), typeof(ShopItemMapper), typeof(ShopItemValidator),
                typeof(Shop)
            };

            var context = ApplicationContext.Build(types, scope => scope.RegisterInstance<IResourceSource>(m_Resources));
            Assert.AreEqual(2, context.Get<IShop>().Tyres);
            Assert.AreEqual(2f, context.Get<IShop>().Grip);
            context.Dispose();

            var overridden = ApplicationContext.Build(types, scope =>
            {
                scope.RegisterInstance<IResourceSource>(m_Resources);
                scope.RegisterInstance(new DrivingSettings { grip = 3f });
            });
            Assert.AreEqual(3f, overridden.Get<IShop>().Grip);
            overridden.Dispose();
        }
    }
}
