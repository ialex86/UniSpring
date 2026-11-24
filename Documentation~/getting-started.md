# Getting started: a small persistent shop

[Documentation index](index.md) · [Architecture](architecture.md) · [Testing](testing.md)

Build a shop that reads item definitions from shipped content, maintains a wallet and owned items, and announces successful purchases. The first purchase costs 25 coins from a starting balance of 100. The example uses Unity 6 (Mono or IL2CPP), does not require a custom DI container configuration component, and does not depend on scene objects in business logic.

This is a teaching project, not a production commerce or crash-proof persistence system. Use a separate save root, keep purchases and reset on one thread, and avoid parallel work inside transactions. [Known platform and persistence limits](data.md#filedocumentstore) apply.

The finished code is the **Persistent Shop** sample: import it from the Package Manager (UniSpring → Samples) instead of copying the blocks below. The package's CI compiles it, runs its tests and checks that it matches this page.

## 1. Install and create an assembly

In a fresh Unity project install the package as described in the [README](../README.md#install), pinning the latest release tag. Do not overwrite the other dependencies in your manifest.

Create `Assets/Shop/Shop.Runtime.asmdef` with these assembly references (add them using the Inspector):

- `UniSpring.DependencyInjection`
- `UniSpring.Data`
- `UniSpring.Transactions`

Keep the C# runtime files below in that directory, and put the JSON files under `Assets/Resources/Shop`. For a larger project, split the contracts into API assemblies; this tutorial deliberately uses one runtime assembly. Do not mix it with other sample classes or another application store installation. All code blocks in steps 2–5 form one example; the filename of the MonoBehaviour must match its class.

## 2. Define models and storage contracts

`ShopModels.cs`:

```csharp
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
```

A document stores one object; a repository stores many objects by string ID. The catalog is separate because shipped content is not player state. Treat its returned records as immutable. The wallet mapper is optional infrastructure for the immutable domain model; the other models use simple serializable fields to keep the example small.

Create `Assets/Resources/Shop/Items/sword.json`:

```json
{"label":"Sword","price":25}
```

Create `Assets/Resources/Shop/Settings.json`:

```json
{"startingBalance":100}
```

The catalog ID is `sword`, from the asset name (without extension). `Resources` paths are relative to the Resources directory and omit extensions. A required settings asset must exist; catalog validation occurs when the catalog is first resolved.

## 3. Compose the infrastructure once

`ShopSetup.cs`:

```csharp
using System;
using System.IO;
using System.Text;
using UniSpring.Data;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace ShopExample
{
    [Service]
    public sealed class ShopJsonSerializer : IDocumentSerializer
    {
        public byte[] Write(object record) => Encoding.UTF8.GetBytes(JsonUtility.ToJson(record));
        public object Read(Type type, byte[] bytes) => JsonUtility.FromJson(Encoding.UTF8.GetString(bytes), type);
    }

    public static class ShopSetup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Configure()
        {
            RuntimeContext.Configure(context => FileDocumentStore.Install(context,
                Path.Combine(Application.persistentDataPath, "UniSpringShopTutorial")));
        }
    }
}
```

This installs the store and transaction manager before container warm-up. The serializer is discovered as a bean. Other services, repositories, listeners and the catalog require no per-class registration. Keep only one `RuntimeContext.Configure` hook: if your game already has one, integrate store installation into that hook.

## 4. Implement the purchase

`ShopService.cs`:

```csharp
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
```

Use `IShop` (not `ShopService`) for the runtime call to receive transaction advice. A failed purchase does not persist staged writes under the normal rollback contract. Defaults are used only when the wallet is missing. The two writes share a logical transaction; the event is not an acknowledgement of a durable disk write. Do not call `Buy` from a constructor or `[PostConstruct]`, or concurrently with save reset. The listener is independent of the shop. This tutorial calls the shop synchronously from Unity's main thread; a real UI/audio listener must marshal to that thread when publication or completion can happen elsewhere.

## 5. Add a Unity adapter

`ShopDemo.cs` (attach it to an active GameObject):

```csharp
using System;
using UniSpring.Data;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace ShopExample
{
    public sealed class ShopDemo : AutowiredMonoBehaviour
    {
        [Autowired] private IShop m_Shop;
        [Autowired] private IDocumentStore m_Store;

        protected override void OnDependenciesInjected()
        {
            Debug.Log("Wallet: " + m_Shop.Balance());
        }

        // Connect to a UI Button, or use this temporary OnGUI control.
        public void BuySword()
        {
            try
            {
                m_Shop.Buy("sword");
                Debug.Log("Wallet after purchase: " + m_Shop.Balance());
            }
            catch (Exception error) { Debug.LogWarning(error.Message); }
        }

        // Diagnostic only: this waits synchronously, so do not use it every frame.
        public void Checkpoint()
        {
            if (!(m_Store is FileDocumentStore store)) return;
            bool saved = store.WaitForDisk(TimeSpan.FromSeconds(2));
            Debug.Log($"Checkpoint: {saved}; lost changes: {store.LostChanges}");
        }

        private void OnGUI()
        {
            if (m_Shop == null) return;
            if (GUILayout.Button("Buy sword")) BuySword();
            if (GUILayout.Button("Check disk")) Checkpoint();
        }
    }
}
```

Do not hide `Awake`; the base class performs injection. Run the scene and click Buy sword: the balance should become 75. A second click should report `Already owned`. For a fresh tutorial run use a new root directory, not an unrelated game's save directory.

## 6. Test without a scene

Create an EditMode test assembly referencing `Shop.Runtime`, `UniSpring.Data`, `UniSpring.Transactions` and `UniSpring.DependencyInjection`. Add the Unity Test Framework if it is not already installed. The tests use the same repository and catalog proxy implementation as runtime but an isolated in-memory store. Directly constructed services are not advised, so explicitly apply the transaction boundary.

```csharp
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
```

These tests cover domain rules and repository/transaction integration, not the real event deferral, DI proxy, disk writer or Unity adapter. Test those separately; see [testing](testing.md). The fake resource source is deliberately limited to this one catalog.

## 7. Verify persistence and the Unity integration

Click **Check disk** after buying the sword. The diagnostic adapter calls `FileDocumentStore.WaitForDisk(TimeSpan)` on the installed store via its injected `IDocumentStore`. Expect `Checkpoint: True; lost changes: 0` when the queued changes land successfully. A false result means the checkpoint did not confirm persistence; check the logs and `LostChanges`. Do not poll from `Update`, and do not interpret a logical commit as a durable acknowledgement. Stop new transactions before reset or shutdown.

After a successful checkpoint, exit and re-enter Play Mode. The same tutorial root should retain the wallet (75) and sword. Also run a clean session with domain reload disabled. Verify no duplicate registrations, that shutdown releases the store, and that broken saves trigger recovery rather than initialization. See [troubleshooting](troubleshooting.md) if the checks fail.

## Where to go next

- [Architecture](architecture.md): split Shop, Inventory and Achievements into modules.
- [Lifecycle, events and ticks](lifecycle-events-ticks.md): make a separate simulation or listener.
- [Data](data.md): add record migrations, strict reads and recovery tests.
- [Catalogs and settings](catalogs-and-settings.md): supply resources from a test double or a custom backend.
- [IL2CPP](il2cpp.md): build this shop for IL2CPP (the editor generates the proxies; the package's smoke test builds exactly this sample).
