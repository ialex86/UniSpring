# IL2CPP

IL2CPP is the only scripting backend on iOS, consoles and WebGL, and the usual
choice for Android release builds. This page lists everything you have to do
for it.

**Status:** the package is tested on Mono (editor, EditMode tests). IL2CPP
builds have not been tested yet; the checklist at the end is the way to verify
your own build.

## What you have to do

| Area | Action |
| --- | --- |
| `[Transactional]` | Replace it with `ITransactionManager.Run` / `RunAsync` |
| Your own `IMethodInterceptor` advice | Do not rely on it; call the logic explicitly |
| Code stripping | `link.xml` is generated; verify stripping and AOT behavior in a player build |
| `logging.properties` on Android / WebGL | Ship it as a `Resources` text asset |
| Everything else (DI, scopes, strategies, logging) | No additional configuration currently expected; IL2CPP compatibility is unverified |

## 1. Replace `[Transactional]` with `Run` / `RunAsync`

Method interception builds its proxies at run time with `DispatchProxy`, which
needs a JIT. IL2CPP has none, so a bean that has an advised method fails with
an `InvalidOperationException` ("could not make a proxy ...") as soon as it is
resolved, usually during container warm-up. The game does not silently run
without transactions; it stops at startup.

Remove the attribute and wrap the body instead. The rules are the same: commit
when the work ends (also on a throw), roll back only with `rollbackOnError`,
always dispose.

Before (Mono only):

```csharp
[Service]
public sealed class Shop : IShop
{
    [Transactional]
    public void Buy(ItemId item)
    {
        // writes
    }

    [Transactional(RollbackOnError = true)]
    public async Task<Receipt> BuyAsync(ItemId item)
    {
        return await ChargeAsync(item);
    }
}
```

After (Mono and IL2CPP):

```csharp
[Service]
public sealed class Shop : IShop
{
    private readonly ITransactionManager m_Transactions;

    public Shop(ITransactionManager transactions)
    {
        m_Transactions = transactions;
    }

    public void Buy(ItemId item)
        => m_Transactions.Run(() =>
        {
            // writes
        });

    public Task<Receipt> BuyAsync(ItemId item)
        => m_Transactions.RunAsync(() => ChargeAsync(item), rollbackOnError: true);
}
```

Your `ITransactionManager` implementation does not change. If the project
targets both backends, use `Run` / `RunAsync` everywhere: one code path, no
platform differences.

To find what is left, search the project for `[Transactional` and for classes
that implement `IMethodInterceptor` or derive from `ScopeInterceptor`.

## 2. Your own interceptors

An `IMethodInterceptor` bean is harmless while it matches no method. Once its
`Matches` returns true for a bean method, that bean needs a proxy and fails on
IL2CPP exactly like `[Transactional]`. Move the logic into an explicit call
(a helper like `Run`, or a decorator class you write by hand).

`TransactionInterceptor` ships with the package and is always registered; it
matches nothing when no method carries `[Transactional]`, so it needs no
action.

## 3. Code stripping

Nothing to configure. An editor build step generates a `link.xml` on every
player build that preserves every bean (any attribute derived from
`[Component]`) and every type with `[Autowired]` members or `[PostConstruct]`
methods, in all assemblies that depend on UniSpring.

Types that only *your* code reaches by reflection (not through UniSpring
attributes) are not covered; keep them with your own `link.xml` or
`[Preserve]` as in any IL2CPP project.

Linker preservation is not proof of AOT compatibility: reflected closed generic
instantiations (for example, strategy registries) may still require AOT-specific
work. Test representative registries, scene lifetimes and explicit transactions
in a real player build on each target backend.

## 4. Logging configuration

This is about the platform, not IL2CPP itself, but it meets you in the same
builds. On Android and WebGL `StreamingAssets` is not a folder on disk, so
`StreamingAssets/logging.properties` is not read. Put the file at
`Assets/Resources/logging.properties.txt` instead. A file placed in
`persistentDataPath` on the device still overrides it.

## Checklist for the first IL2CPP build

1. Search for `[Transactional` and custom interceptors; none should advise a
   bean (steps 1 and 2).
2. Build with **Managed Stripping Level: High** and run on the device.
3. The first scene starts without a "UniSpring DI" exception in the log: the
   container warmed up and every bean was created.
4. A `GameLog.Debug` line from a source you enabled in `logging.properties`
   appears in the log: the configuration was found.
5. One code path that goes through `Run` / `RunAsync` works: the transaction
   store is wired.

If step 3 fails with "has no bean assignable to ..." for a class that exists,
stripping removed it; please report it with the class and its attributes.
