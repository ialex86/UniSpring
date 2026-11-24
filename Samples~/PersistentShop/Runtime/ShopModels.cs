// Kept in sync with Documentation~/getting-started.md, which shows this code step by step.
using System;
using System.Collections.Generic;
using UniSpring.Data;
using UniSpring.DependencyInjection;

namespace ShopExample
{
    public sealed class Wallet
    {
        public Wallet(long balance) => Balance = balance;
        public long Balance { get; }
    }

    [Serializable]
    public sealed class WalletRecord
    {
        public long balance;
    }

    [Component]
    public sealed class WalletMapper : IRecordMapper<Wallet, WalletRecord>
    {
        public WalletRecord ToRecord(Wallet wallet) => new WalletRecord { balance = wallet.Balance };
        public Wallet ToDomain(WalletRecord record) => new Wallet(record.balance);
    }

    [Component]
    public sealed class WalletValidator : IDocumentValidator<Wallet>
    {
        public void Validate(string id, Wallet wallet, ValidationErrors errors)
        {
            if (wallet.Balance < 0) errors.Add("Negative wallet balance.");
        }
    }

    [Serializable]
    public sealed class ShopItem
    {
        public string label;
        public int price;
        public int Price => price; // exact public member used by derived queries
    }

    [Component]
    public sealed class ShopItemValidator : IDocumentValidator<ShopItem>
    {
        public void Validate(string id, ShopItem item, ValidationErrors errors)
        {
            if (string.IsNullOrEmpty(item.label)) errors.Add("Missing label.");
            if (item.price <= 0) errors.Add("Price must be positive.");
        }
    }

    [Serializable]
    public sealed class OwnedItem
    {
        public string itemId;
        public string ItemId => itemId;
    }

    [Component]
    public sealed class OwnedItemValidator : IDocumentValidator<OwnedItem>
    {
        public void Validate(string id, OwnedItem item, ValidationErrors errors)
        {
            if (string.IsNullOrEmpty(item.itemId) || item.itemId != id)
                errors.Add("Invalid inventory item ID.");
        }
    }

    [Serializable]
    [ConfigurationProperties("Shop/Settings", Required = true)]
    public sealed class ShopSettings
    {
        public long startingBalance;
    }

    [Component]
    public sealed class ShopSettingsValidator : IDocumentValidator<ShopSettings>
    {
        public void Validate(string id, ShopSettings settings, ValidationErrors errors)
        {
            if (settings.startingBalance < 0) errors.Add("Negative starting balance.");
        }
    }

    [Repository]
    public interface IWalletDocument : IDocument<Wallet> { }

    [Repository]
    public interface IInventoryRepository : IRepository<OwnedItem>
    {
        IReadOnlyList<OwnedItem> FindByItemId(string itemId);
    }

    [Catalog("Shop/Items")]
    public interface IShopCatalog : ICatalog<ShopItem> { }
}
