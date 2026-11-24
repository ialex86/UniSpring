# Data: repositories for saves

`UniSpring.Data` stores the game's objects as documents, in the spirit of
Spring Data: you declare a repository interface, the container implements it.
The stored classes know nothing about storage; the format, the shape on disk,
the checks and the migrations are separate beans.

**Status:** repository interfaces are implemented by run-time proxies, so they
work on the Mono scripting backend only (see [IL2CPP](il2cpp.md)). The package
ships a file store that writes on its own thread and an in-memory store for
tests.

## The pieces

| Piece | Required | What it is |
| --- | --- | --- |
| Repository interface | yes | `[Repository]` interface deriving from `IRepository<T>` or `IDocument<T>` |
| `IDocumentSerializer` | yes, one per application | Turns records into bytes and back (JSON, protobuf, ...) |
| `IDocumentStore` | yes, one per application | Where the bytes live |
| `IRecordMapper<TDomain, TRecord>` | when the stored shape differs from the domain | Domain object to record and back |
| `IDocumentValidator<T>` | when objects need checks | Refuses invalid objects on save, hides invalid ones on load |
| `IMigration` + `[Migration(n)]` | when the stored shape changes | One-time rewrite of stored data |

The path of an object:

```
Save:  object -> validators -> mapper -> serializer -> store
Load:  store -> serializer -> mapper -> validators -> object
```

## A complete example

Domain classes, with no attributes and no storage concerns:

```csharp
public sealed class Wallet
{
    public Wallet(long balance) { Balance = balance; }
    public long Balance { get; }
}

public sealed class ParkedCar
{
    public ParkedCar(string owner, string model, int mileage)
    {
        Owner = owner;
        Model = model;
        Mileage = mileage;
    }

    public string Owner { get; }
    public string Model { get; }
    public int Mileage { get; }
}
```

Repositories: interfaces only, no classes.

```csharp
using System.Collections.Generic;
using UniSpring.Data;

[Repository]
public interface IWalletDocument : IDocument<Wallet>
{
}

[Repository]
public interface ICarRepository : IRepository<ParkedCar>
{
    IReadOnlyList<ParkedCar> FindByOwner(string owner);
    IReadOnlyList<ParkedCar> FindByOwnerAndModel(string owner, string model);
    ParkedCar FindByModel(string model);
    int CountByOwner(string owner);
    bool ExistsByModel(string model);
    int DeleteByOwner(string owner);
}
```

Records and mappers: only the records satisfy the serializer.

```csharp
using System;
using UniSpring.Data;
using UniSpring.DependencyInjection;

[Serializable]
internal sealed class WalletRecord { public int schemaVersion = 2; public long balance; }

[Serializable]
internal sealed class ParkedCarRecord
{
    public string owner;
    public string model;
    public int mileage;
}

[Component]
internal sealed class WalletMapper : IRecordMapper<Wallet, WalletRecord>
{
    public WalletRecord ToRecord(Wallet wallet) => new WalletRecord { balance = wallet.Balance };
    public Wallet ToDomain(WalletRecord record) => new Wallet(record.balance);
}

[Component]
internal sealed class ParkedCarMapper : IRecordMapper<ParkedCar, ParkedCarRecord>
{
    public ParkedCarRecord ToRecord(ParkedCar car)
        => new ParkedCarRecord { owner = car.Owner, model = car.Model, mileage = car.Mileage };

    public ParkedCar ToDomain(ParkedCarRecord record)
        => new ParkedCar(record.owner, record.model, record.mileage);
}
```

A validator:

```csharp
[Component]
internal sealed class ParkedCarValidator : IDocumentValidator<ParkedCar>
{
    public void Validate(string id, ParkedCar car, ValidationErrors errors)
    {
        if (string.IsNullOrEmpty(car.Owner))
            errors.Add("The car has no owner.");
        if (car.Mileage < 0)
            errors.Add("The mileage is negative.");
    }
}
```

The serializer and the store, once per application:

```csharp
using System;
using System.Text;
using UniSpring.Data;
using UniSpring.DependencyInjection;
using UnityEngine;

[Service]
internal sealed class JsonDocumentSerializer : IDocumentSerializer
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public byte[] Write(object record) => Utf8.GetBytes(JsonUtility.ToJson(record));

    public object Read(Type recordType, byte[] bytes)
        => JsonUtility.FromJson(Utf8.GetString(bytes), recordType);
}

public static class DataSetup
{
    // Saves under Application.persistentDataPath/Saves, written on a background thread.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Configure()
        => RuntimeContext.Configure(context => FileDocumentStore.Install(context));
}
```

A service uses the repositories:

```csharp
using System;
using System.Collections.Generic;
using UniSpring.DependencyInjection;
using UniSpring.Transactions;

public interface IGarage
{
    void BuyCar(string owner, string carId, string model, long price);
    IReadOnlyList<ParkedCar> CarsOf(string owner);
}

[Service]
internal sealed class GarageService : IGarage
{
    private readonly IWalletDocument m_Wallet;
    private readonly ICarRepository m_Cars;

    public GarageService(IWalletDocument wallet, ICarRepository cars)
    {
        m_Wallet = wallet;
        m_Cars = cars;
    }

    [Transactional(RollbackOnError = true)]
    public void BuyCar(string owner, string carId, string model, long price)
    {
        var read = m_Wallet.Read();
        if (read.IsBroken)
            throw new InvalidOperationException("The wallet save is broken: " + read.Error);
        var wallet = read.Value ?? new Wallet(1000);
        if (price <= 0 || wallet.Balance < price)
            throw new InvalidOperationException("Not enough money.");

        m_Wallet.Save(new Wallet(wallet.Balance - price));
        m_Cars.Save(carId, new ParkedCar(owner, model, 0));
    }

    public IReadOnlyList<ParkedCar> CarsOf(string owner) => m_Cars.FindByOwner(owner);
}
```

## Repositories

`IRepository<T>` keeps many objects, each under a string id:
`Find`, `Read`, `Exists`, `Save`, `Delete`, `All` and `Ids`. `IDocument<T>`
keeps the one object of its type: `Load`, `Read`, `Exists`, `Save`, `Delete`.

### Missing or broken

`Load` and `Find` are lenient: a document that is damaged, unreadable or
invalid is logged and returned as null, like a missing one. That suits data the
game can rebuild. For data the player must not lose, use `Read`: it returns a
`LoadResult<T>` with a status.

| Status | Meaning |
| --- | --- |
| `Missing` | Nothing is stored under the id |
| `Found` | Read, mapped and validated; `Value` holds the object |
| `Damaged` | The store has no intact copy of the bytes |
| `Unreadable` | The serializer or the mapper failed |
| `Invalid` | A validator refused the object |

After a repository detects broken data on read, saving the same id through that repository is refused with `DocumentDamagedException`. This is a session-local, per-repository-interface guard, not a durable quarantine: it does not cover unread documents, other interfaces, direct store writes or a restart. A later successful read can clear it. Use strict `Read()` and initialize defaults only for `Missing`; keep damaged data for recovery and ask for explicit consent before `Delete` and replacement. Declare a separate interface for each repository so its role is clear in constructors.

The container creates one implementation per interface and shares it. Ask for
the interface in a constructor; nothing has to be registered.

### Query methods

A method added to a repository interface is implemented from its name:

| Prefix | Returns | Meaning |
| --- | --- | --- |
| `FindBy` | `T` | The first match, or null |
| `FindBy` | `IReadOnlyList<T>` (or `T[]`) | Every match |
| `CountBy` | `int` | How many match |
| `ExistsBy` | `bool` | Whether any matches |
| `DeleteBy` | `void` or `int` | Deletes the matches (and returns how many) |

After the prefix come exact, case-sensitive public field or property names of the stored domain type, joined by `And`, with one parameter per member in the same order. For a field named `itemId`, expose a public `ItemId` property if you want the idiomatic `FindByItemId` spelling. Members are compared for equality. There are no indexes: full-result queries scan readable documents.

`ExistsBy` and a `FindBy` that returns one object stop at the first match; the
others read every document.

Invalid query signatures are rejected when the repository is first created (during warm-up if an Application bean needs it, otherwise at first resolution). The exception identifies the contract and method.

Reading, deserializing, mapping and validating run on the caller's thread,
which is usually the main thread: only writing to disk is moved off it. Keep
queries over large repositories out of the frame loop.

For anything else (ranges, sorting, joins) write an ordinary service that takes
the repository.

## Mappers

Without a mapper, the object itself goes to the serializer. With an
`IRecordMapper<TDomain, TRecord>` bean, the repositories of `TDomain` store
`TRecord`: the domain class stays free of serializer requirements
(`[Serializable]`, public fields, parameterless constructors), and the record is
the only class that changes with the format. One mapper per domain type.

## Validators

Every `IDocumentValidator<T>` bean for the type runs:

- on save, an object with errors is not stored; the caller gets
  `DocumentValidationException` with the list of errors;
- on load, validation errors are logged through `GameLog`; lenient reads return null and strict reads report `Invalid`. The caller decides whether the game can continue.

The id is passed to the validator, so a rule such as "the owner inside the
document matches the id it is stored under" belongs here too.

## Serializer

The module does not pick a format. Register exactly one `IDocumentSerializer`
bean. It receives the record (or the object, without a mapper) and the type to
read back. A document the serializer cannot read is logged and read as missing.

## Migrations

The framework tracks the last applied migration in `UniSpring/migrations`, rather than injecting a schema version into every record. Your record format should include an explicit version when a transformation cannot safely distinguish old and new shapes. The example below assumes that the released v1 record already had `schemaVersion: 1`; the v2 writer shown above emits `schemaVersion: 2`. It intentionally refuses ambiguous unversioned records instead of guessing.

```csharp
[Serializable]
internal sealed class WalletMigrationProbe
{
    public int schemaVersion;
    public long coins;
}

[Migration(2)]
internal sealed class WalletCoinsToBalance : IMigration
{
    public void Run(IMigrationStore store)
    {
        var old = store.Load<WalletMigrationProbe>("Wallet");
        if (old == null || old.schemaVersion == 2)
            return;
        if (old.schemaVersion != 1 || old.coins < 0)
            throw new InvalidOperationException("Unknown or invalid wallet format; preserve the save for recovery.");
        store.Save("Wallet", new WalletRecord { schemaVersion = 2, balance = old.coins });
    }
}
```

- Migrations are beans; they run during warm-up in ascending version order, for versions newer than the recorded version. Recovery from an older metadata backup can run a migration again. Keep storage-dependent initialization after migration.
- A migration sees records (old and new shapes), not domain objects: no mappers
  or validators run.
- When the store has transactions, a migration and the new number are written
  in one transaction; a migration that throws leaves nothing behind and stops
  the start.
- Numbers are positive and unique. An unreadable migration record stops the
  start rather than run migrations twice.

Keep the old record class in the code for as long as saves of that shape can
exist.

Write replay-safe migrations and test them on old, current, missing, malformed and unknown-version records. An old DTO deserializing successfully does not prove the stored shape is old: serializers may substitute default values for absent fields. Never infer that a zero balance identifies a legacy record. For already shipped unversioned formats, inspect a reliable discriminator or use an explicit recovery/import tool before adopting this example. If migration metadata falls back to an older backup, the package logs a warning and may rerun migrations.

## Where documents live

| Repository | Area | Name |
| --- | --- | --- |
| `IDocument<Wallet>` | `Documents` | `Wallet` |
| `IRepository<ParkedCar>` | `ParkedCar` | the id |
| Migration record | `UniSpring` | `migrations` |

The names come from the domain type's name, so two stored types must not share
a name; the container refuses it. `Documents` and `UniSpring` are reserved.

## The store

`IDocumentStore` is the port to the disk: `Read`, `Write`, `Delete` and
`Names`, on bytes. Its contract:

- a write may return before reaching disk. Outside a transaction, subsequent reads see the accepted change; inside a transaction the change is visible to its own flow until the outer commit. This is not a disk-durability guarantee;
- a store that supports transactions also implements `ITransactionManager`:
  writes in a transaction are read by its own flow at once and by everyone else
  after the outermost commit; a rollback drops them.

Validation, mapping and serialization run on the caller's thread; only the
bytes reach the store.

### FileDocumentStore

The store for the game. `FileDocumentStore.Install(context, root)` opens it,
registers it as the container's `IDocumentStore` and `ITransactionManager`, and
closes it when the application quits (in the editor, when Play stops). The
default root is `Application.persistentDataPath/Saves`.

- **Background writes.** Accepted changes are visible before the writer lands them. Repeated writes may be coalesced. Reads, serialization, mapping, validation, lock contention, reset and shutdown can still block; profile realistic workloads and keep large scans out of ticks.
- **Atomic files.** A write goes to a temporary file and then replaces the
  document; the previous copy stays as a `.bak`, and every copy carries a
  SHA-256. A read that finds a document damaged returns the backup.
- **Transactions.** The documents of one transaction land together: their new
  contents are written first, then a journal (the commit point), then they are
  put in place. The journal is written under a temporary name and moved in
  place, so it is either whole or absent. A journal a crash left behind is
  completed the next time the store opens. One the disk cannot read stays and
  is tried again; a damaged one is renamed to `.corrupt` and logged as an
  error, never deleted. This is the recovery protocol, not a guarantee against
  every filesystem fault or power loss: test the target platform and keep the
  backups.
- **Order.** Changes land in the order they were made, so the disk always holds
  a state the game was in. When the disk refuses (full, file held by another
  program), the changes stay readable and are tried again with a growing pause;
  nothing later lands before them.
- **Quitting.** `Close(timeout)` attempts to drain pending work and stop the writer; later writes are synchronous. Timeout is not a strict wall-clock bound when filesystem I/O is blocked. Do not rely on shutdown as the only save checkpoint.
- **Failure reporting.** During shutdown, refused steps may be discarded to permit later steps to land. `LostChanges` counts discarded changes; `WaitForDisk` and `IsIdle` remain false after a recorded loss in that session. Recovery of a stranded journal is best effort. Preserve the save directory and logs on failure.
- `WaitForDisk(timeout)` and `IsIdle` tell whether the changes made so far are
  on disk, for tests and tools.

  `WaitForDisk` is a synchronous check of the store's queued changes, not a power-loss durability guarantee. `AfterCommit` does not wait for it. Check the return value, `LostChanges` and logs; avoid blocking the frame loop on this method.

Files: `<root>/<area>/<hex of the name>.doc`, with `.bak` next to it and
journals in `<root>/.journal`. Areas are segments of letters, digits, `-` and
`_` joined by `/`; names are any text of at most 100 UTF-8 bytes.

Transactions of different flows do not merge: when two flows write the same
document, the later commit wins.

Nested transactions join the outer one, as Spring's `REQUIRED`. A rollback
inside marks the whole transaction rollback-only: the outermost commit then
saves nothing and throws, so a failure caught half-way cannot leave half the
writes saved.

### New game

Both stores implement `IDocumentReset`, which `Install` registers too:

```csharp
[Service]
internal sealed class NewGame : INewGame
{
    private readonly IDocumentReset m_Saves;

    public NewGame(IDocumentReset saves) { m_Saves = saves; }

    public bool CanContinue => m_Saves.HasDocuments;

    public void Start() => m_Saves.DeleteAll();
}
```

- `DeleteAll` deletes every document of every area as one step and waits for
  the disk. The record of applied migrations stays: documents written from now
  on already have the current shape.
- A service that keeps what it read subscribes with `Subscribe(() => ...)` and
  drops its state when the saves are cleared.
- Inside an open transaction of the calling flow `DeleteAll` throws and changes
  nothing. Open transactions of other flows are dropped: they belong to the old
  game, what their flows write until they end is lost, and their `Commit`
  throws, so the old game's code learns that nothing was saved.
- A write another thread makes after the clear began is kept. Still, prefer to
  start a new game from a quiet point (no gameplay transactions in flight): a
  dropped transaction is consistent, but the use case that owned it fails.

### InMemoryDocumentStore

Keeps everything in memory with the same transaction rules; for unit tests and
tools.

## Testing

`DocumentData` composes the module by hand, without a container or a scene:

```csharp
[Test]
public void A_Bought_Car_Belongs_To_The_Buyer()
{
    var data = DocumentData.InMemory(new JsonDocumentSerializer())
        .With(new WalletMapper())
        .With(new ParkedCarMapper())
        .With(new ParkedCarValidator());
    var cars = data.Repository<ICarRepository>();
    var garage = new GarageService(data.Repository<IWalletDocument>(), cars);

    data.Transactions.Run(() => garage.BuyCar("alex", "car-7", "Coupe", 60), rollbackOnError: true);

    Assert.AreEqual(1, cars.CountByOwner("alex"));
}
```

`data.Migrate(...)` runs migrations the same way the container does, and
`data.Transactions` is the store's transaction manager. A service made with
`new` runs without `[Transactional]` advice; use `data.Transactions.Run(...)`
to test transactional behaviour.

## Catalogs

Game data that ships with the game and is never written (shop items, part
tables, liveries) is a catalog: one text asset per item in a `Resources`
folder, the asset's name its id.

```csharp
[Catalog("Shop/Items")]
public interface IShopCatalog : ICatalog<ShopItem>
{
    IReadOnlyList<ShopItem> FindByCategory(string category);
    int CountByCategory(string category);
}
```

`ICatalog<T>` gives `Find`, `Exists`, `All` and `Ids`; query methods work as in
a repository, but there is no `DeleteBy`. Items go through the serializer, the
type's mapper and its validators once, when the catalog is loaded. A broken item
is a bug in the game's data: it stops the start and the error names the asset.
A catalog is loaded on its first resolution: one injected into an application
bean is checked while the application starts, one used only by a Scene bean
when that scene's scope is built. `All` and `Ids` are read-only. Two
mappers for one type stop the start. Catalogs are implemented by run-time
proxies: Mono only.

## Settings

A settings class is bound to a resource, as Spring's
`@ConfigurationProperties`:

```csharp
[Serializable]
[ConfigurationProperties("Config/Driving")]
public sealed class DrivingSettings
{
    public float grip = 1f;
    public int gears = 5;
}

[Service]
internal sealed class Steering : ISteering
{
    public Steering(DrivingSettings settings) { ... }
}
```

- A plain class is read from the text asset at the path through the serializer.
  A class that derives from `ScriptableObject` is loaded as that asset, so it
  can be edited in the inspector.
- A missing resource gives the class's defaults; `Required = true` makes it
  stop the start (fail the resolution) instead. A `ScriptableObject` made for
  the defaults is destroyed with the container.
- `IDocumentValidator<T>` checks the settings; invalid settings stop the start.
- An instance registered with `RegisterInstance` wins over the resource (tests,
  overrides); it is not validated, its owner is responsible for that.

Catalogs and settings read Unity's `Resources` unless an `IResourceSource`
bean is registered (Addressables, files, a test double).
`DocumentData.Catalog<T>(source)` builds either without a container, for tests.

## Extending the container

Repositories, catalogs and settings are handed out through `IContractProvider`, a DI extension point:
a bean that implements it supplies beans for contracts no scanned class
implements. The container asks providers only when a contract has no bean and
no registered instance, creates each provided bean once, and disposes it with
the root container.
