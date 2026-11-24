// Kept in sync with Documentation~/getting-started.md, which shows this code step by step.
using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using ShopExample;
using UniSpring.Data;
using UniSpring.DependencyInjection;
using UniSpring.Transactions;

public sealed class ShopTests
{
    private sealed class TestResources : IResourceSource
    {
        public IReadOnlyList<KeyValuePair<string, byte[]>> LoadAll(string folder)
            => new[] { new KeyValuePair<string, byte[]>("sword", Encoding.UTF8.GetBytes("{\"label\":\"Sword\",\"price\":25}")) };
        public byte[] Load(string path) => null;
        public UnityEngine.Object LoadAsset(string path, Type type) => null;
    }

    private sealed class RecordingEvents : IEventPublisher
    {
        public readonly List<object> Published = new List<object>();
        public void Publish(object evt) => Published.Add(evt);
        public IDisposable Subscribe<T>(Action<T> listener) => throw new NotSupportedException();
    }

    [TestCase(100, true)]
    [TestCase(10, false)]
    public void PurchaseChangesBothDocumentsOrNeither(long startingBalance, bool succeeds)
    {
        var data = DocumentData.InMemory(new ShopJsonSerializer())
            .With(new WalletMapper()).With(new WalletValidator()).With(new ShopItemValidator())
            .With(new OwnedItemValidator());
        var wallet = data.Repository<IWalletDocument>();
        var inventory = data.Repository<IInventoryRepository>();
        var catalog = data.Catalog<IShopCatalog>(new TestResources());
        var events = new RecordingEvents();
        var shop = new ShopService(wallet, inventory, catalog,
            new ShopSettings { startingBalance = startingBalance }, events);

        if (succeeds)
        {
            data.Transactions.Run(() => shop.Buy("sword"), rollbackOnError: true);
            Assert.That(wallet.Read().Value.Balance, Is.EqualTo(75));
            Assert.That(inventory.FindByItemId("sword").Count, Is.EqualTo(1));
            Assert.That(events.Published.Count, Is.EqualTo(1));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                data.Transactions.Run(() => shop.Buy("sword"), rollbackOnError: true));
            Assert.That(wallet.Read().Status, Is.EqualTo(LoadStatus.Missing));
            Assert.That(inventory.Ids(), Is.Empty);
            Assert.That(events.Published, Is.Empty);
        }
    }

    [Test]
    public void FailureAfterFirstWriteRollsBack()
    {
        var data = DocumentData.InMemory(new ShopJsonSerializer())
            .With(new WalletMapper()).With(new WalletValidator());
        var wallet = data.Repository<IWalletDocument>();
        wallet.Save(new Wallet(100));
        Assert.Throws<InvalidOperationException>(() => data.Transactions.Run(() =>
        {
            wallet.Save(new Wallet(75));
            throw new InvalidOperationException("Simulated failure before inventory write");
        }, rollbackOnError: true));
        Assert.That(wallet.Read().Value.Balance, Is.EqualTo(100));
    }
}
