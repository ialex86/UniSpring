// Kept in sync with Documentation~/getting-started.md, which shows this code step by step.
using System;
using UniSpring.DependencyInjection;
using UniSpring.Transactions;

namespace ShopExample
{
    public interface IShop
    {
        long Balance();
        bool Owns(string id);
        void Buy(string id);
    }

    public sealed class ItemPurchased
    {
        public ItemPurchased(string id) => Id = id;
        public string Id { get; }
    }

    [Service]
    public sealed class ShopService : IShop
    {
        private readonly IWalletDocument m_Wallet;
        private readonly IInventoryRepository m_Inventory;
        private readonly IShopCatalog m_Catalog;
        private readonly ShopSettings m_Settings;
        private readonly IEventPublisher m_Events;

        public ShopService(IWalletDocument wallet, IInventoryRepository inventory,
            IShopCatalog catalog, ShopSettings settings, IEventPublisher events)
        {
            m_Wallet = wallet;
            m_Inventory = inventory;
            m_Catalog = catalog;
            m_Settings = settings;
            m_Events = events;
        }

        public long Balance()
        {
            var read = m_Wallet.Read();
            if (read.IsBroken) throw new InvalidOperationException("Wallet needs recovery: " + read.Error);
            return read.Value?.Balance ?? m_Settings.startingBalance;
        }

        public bool Owns(string id)
        {
            var read = m_Inventory.Read(id);
            if (read.IsBroken) throw new InvalidOperationException("Inventory needs recovery: " + read.Error);
            return read.Found;
        }

        [Transactional(RollbackOnError = true)]
        public void Buy(string id)
        {
            var item = m_Catalog.Find(id);
            if (item == null) throw new ArgumentException("Unknown item.", nameof(id));
            if (Owns(id)) throw new InvalidOperationException("Already owned.");
            var balance = Balance();
            if (item.price <= 0 || balance < item.price)
                throw new InvalidOperationException("Invalid price or insufficient funds.");
            m_Wallet.Save(new Wallet(balance - item.price));
            m_Inventory.Save(id, new OwnedItem { itemId = id });
            m_Events.Publish(new ItemPurchased(id));
        }
    }

    [Component]
    [AfterCommit]
    public sealed class PurchaseLog : IEventListener<ItemPurchased>
    {
        public void On(ItemPurchased evt) => UnityEngine.Debug.Log("Purchased " + evt.Id);
    }
}
