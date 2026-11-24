# Transactions

UniSpring ships the advice, not the store. The application provides an
`ITransactionManager`; the container runs advised methods inside it.

`[Transactional]` relies on run-time proxies (`DispatchProxy`) and works on the
Mono scripting backend only. On IL2CPP use the explicit form below.

## Explicit use

`Run` and `RunAsync` apply the same rules as the attribute without a proxy.
They work on every scripting backend and anywhere the attribute cannot reach
(MonoBehaviours, private methods, classes resolved by their own type):

```csharp
public sealed class Shop
{
    private readonly ITransactionManager m_Transactions;

    public Shop(ITransactionManager transactions)
    {
        m_Transactions = transactions;
    }

    public void Buy(ItemId item)
        => m_Transactions.Run(() => { /* writes land as one step */ });

    public Task<Receipt> BuyAsync(ItemId item)
        => m_Transactions.RunAsync(() => ChargeAsync(item), rollbackOnError: true);
}
```

`Run` refuses work that returns a task: the transaction would commit before
the task completes. Use `RunAsync` there.

## Declarative use

```csharp
using UniSpring.Transactions;

[Transactional]
public void Buy(CarId car)
{
    // Every write made here, in any module, lands as one step.
}
```

Rules, as with the other AOP advice:

- Put `[Transactional]` on a method that implements an interface method.
- Calls made through that interface are advised; `this.Method()` is not.
- A class resolved by its own type is never proxied.
- Async methods (`Task`, `Task<T>`) keep the transaction until their task
  completes. `ValueTask` and iterators are refused when the bean is created.
- For a narrower unit of work call `ITransactionManager.Begin()` yourself.

## Commit and rollback

The default is **commit when the call ends**, including when the method throws.
Modules usually change their in-memory state as they go, so there is nothing
left to roll back.

For a store that can discard its changes:

```csharp
[Transactional(RollbackOnError = true)]
public void Import(string path)
{
    // A throw rolls the transaction back; the exception still reaches the caller.
}
```

The transaction is disposed after commit or rollback, whether it succeeded or
not.

For an async method, commit, rollback and dispose run on the thread that
completes the method's task. Heavy work may therefore finish off the main
thread, and `ITransaction` must not assume Unity's main thread there.

## Implementing the store

```csharp
using System.Threading;
using UniSpring.DependencyInjection;
using UniSpring.Transactions;

[Service]
[Primary]
public sealed class SaveTransactionManager : ITransactionManager
{
    private readonly AsyncLocal<SaveTransaction> m_Current = new AsyncLocal<SaveTransaction>();

    public ITransaction Begin()
    {
        var open = m_Current.Value;
        if (open != null)
            return open.Join(); // REQUIRED: the outermost flow commits

        var transaction = new SaveTransaction(this);
        m_Current.Value = transaction;
        return transaction;
    }
}
```

`Begin()` must join the transaction already open in the calling flow
(Spring's `REQUIRED`). Because a flow includes every async call the caller
makes, keep the ambient transaction in an `AsyncLocal`.

Register the store in one of two ways:

```csharp
// 1. A bean marked [Service] and [Primary], as above.

// 2. An instance, before the container warms up:
RuntimeContext.Configure(context =>
    context.RegisterInstance<ITransactionManager>(new SaveTransactionManager()));
```

Without either, the container still builds: `UniSpring.Transactions` registers
a placeholder manager that refuses to begin and explains what to configure. An
advised call is the first thing that fails, not application startup.

`RuntimeContext` scans every loaded assembly that references
`UniSpring.DependencyInjection`, so the transactions assembly is included
automatically. If you build a container from an explicit assembly list, add
`typeof(TransactionInterceptor).Assembly` to it.

## Contracts

| Member | Meaning |
| --- | --- |
| `ITransactionManager.Begin()` | Start or join the flow's transaction |
| `ITransaction.Commit()` | Hand the held changes over as one step |
| `ITransaction.Rollback()` | Discard the held changes |
| `ITransaction.Dispose()` | Release the transaction; does not commit |
| `TransactionalAttribute.RollbackOnError` | Roll back instead of committing on a throw |
