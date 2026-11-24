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
internal sealed class WalletRecord { public long balance; }

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
        var wallet = m_Wallet.Load() ?? new Wallet(1000);
        if (wallet.Balance < price)
            throw new InvalidOperationException("Not enough money.");

        m_Wallet.Save(new Wallet(wallet.Balance - price));
        m_Cars.Save(carId, new ParkedCar(owner, model, 0));
    }

    public IReadOnlyList<ParkedCar> CarsOf(string owner) => m_Cars.FindByOwner(owner);
}
```

## Repositories

`IRepository<T>` keeps many objects, each under a string id:
`Find`, `Exists`, `Save`, `Delete`, `All` and `Ids`. `IDocument<T>` keeps the
one object of its type: `Load`, `Exists`, `Save`, `Delete`. Declare an
interface for each, even without extra methods: the repository is then explicit
in every constructor.

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

After the prefix come public fields or properties of the stored type, joined by
`And`, one parameter per member, in the same order. Members are compared for
equality. Every stored object is loaded and compared in memory: there are no
indexes, which suits game saves of tens or hundreds of documents.

A method whose name or parameters do not fit is refused when the repository is
created, that is when the container builds, with the interface, the method and
the reason in the message.

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
- on load, a stored object with errors is logged through `GameLog` and read as
  missing, so one bad document never stops the game.

The id is passed to the validator, so a rule such as "the owner inside the
document matches the id it is stored under" belongs here too.

## Serializer

The module does not pick a format. Register exactly one `IDocumentSerializer`
bean. It receives the record (or the object, without a mapper) and the type to
read back. A document the serializer cannot read is logged and read as missing.

## Migrations

Stored documents carry no version. The number of the last migration applied is
kept in its own document, `UniSpring/migrations`, as in Flyway.

```csharp
[Serializable]
internal sealed class WalletRecordV1 { public int coins; }

[Migration(2)]
internal sealed class WalletCoinsToBalance : IMigration
{
    public void Run(IMigrationStore store)
    {
        var old = store.Load<WalletRecordV1>("Wallet");
        if (old == null)
            return;
        store.Save("Wallet", new WalletRecord { balance = old.coins });
    }
}
```

- Migrations are beans; they run while the container warms up, before the
  ordinary beans, in ascending order, each only once.
- A migration sees records (old and new shapes), not domain objects: no mappers
  or validators run.
- When the store has transactions, a migration and the new number are written
  in one transaction; a migration that throws leaves nothing behind and stops
  the start.
- Numbers are positive and unique. An unreadable migration record stops the
  start rather than run migrations twice.

Keep the old record class in the code for as long as saves of that shape can
exist.

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

- a write may return before the disk has it (a store may write on its own
  thread, so the game never waits for the disk), but every later read sees the
  new content;
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

- **No freezes.** A write or delete returns at once and is readable at once;
  the store's own thread puts the waiting changes on disk shortly after, all of
  them as one step. Repeated writes of a document collapse into the last one.
- **Atomic files.** A write goes to a temporary file and then replaces the
  document; the previous copy stays as a `.bak`, and every copy carries a
  SHA-256. A read that finds a document damaged returns the backup.
- **Transactions.** The documents of one transaction land together: their new
  contents are written first, then a journal (the commit point), then they are
  put in place. A journal a crash left behind is completed the next time the
  store opens; an incomplete one is dropped.
- **Order.** Changes land in the order they were made, so the disk always holds
  a state the game was in. When the disk refuses (full, file held by another
  program), the changes stay readable and are tried again with a growing pause;
  nothing later lands before them.
- **Quitting.** `Close(timeout)` waits for the waiting changes and stops the
  thread; after it, writes go to disk on the caller's thread before they return,
  so saves made while objects are destroyed are not lost. Changes the disk
  still refuses are logged.
- `WaitForDisk(timeout)` and `IsIdle` tell whether the changes made so far are
  on disk, for tests and tools.

Files: `<root>/<area>/<hex of the name>.doc`, with `.bak` next to it and
journals in `<root>/.journal`. Areas are segments of letters, digits, `-` and
`_` joined by `/`; names are any text of at most 100 UTF-8 bytes.

Transactions of different flows do not merge: when two flows write the same
document, the later commit wins.

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

    garage.BuyCar("alex", "car-7", "Coupe", 60);

    Assert.AreEqual(1, cars.CountByOwner("alex"));
}
```

`data.Migrate(...)` runs migrations the same way the container does, and
`data.Transactions` is the store's transaction manager. A service made with
`new` runs without `[Transactional]` advice; use `data.Transactions.Run(...)`
to test transactional behaviour.

## Extending the container

Repositories are handed out through `IContractProvider`, a DI extension point:
a bean that implements it supplies beans for contracts no scanned class
implements. The container asks providers only when a contract has no bean and
no registered instance, creates each provided bean once, and disposes it with
the root container.
