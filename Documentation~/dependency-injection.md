# Dependency injection

## Beans

Mark a concrete class with `[Service]`, `[Component]` or `[Strategy]`:

```csharp
[Service]
public sealed class InventoryService : IInventoryService
{
    private readonly IItemCatalog m_Catalog;

    public InventoryService(IItemCatalog catalog)
    {
        m_Catalog = catalog;
    }
}
```

- One public constructor is resolved automatically.
- `[Autowired]` is only needed when a type has several constructors.
- Depend on interfaces, not concrete implementations.
- `[Primary]` marks the default implementation when several beans match.
- `[Order]` controls warm-up order.
- `[PostConstruct]` runs once after every dependency of the bean is injected.

`ScriptableObject`, prefabs and scene objects are not beans. Use
`AutowiredMonoBehaviour` for Unity components.

## Lifetimes and scopes

| Lifetime | Owner |
| --- | --- |
| `Application` (default) | Root container, lives for the play session |
| `Scene` | Scene scope, disposed when the scene unloads |
| `Transient` | Created per resolution, owned by the consumer |

`RuntimeContext.ScopeOf(scene)` returns the scene scope. Scene components are
autowired from it. `RuntimeContext.JoinSceneScope(path, host)` makes a streamed
scene share the host's scope.

When the application quits (or Play Mode ends in the editor) the scene scopes
and the root container are disposed, so `IDisposable` beans release their
resources. From then on `RuntimeContext.IsShutDown` is true and any access
throws; check it in `OnDestroy` code that may run during shutdown:

```csharp
private void OnDestroy()
{
    if (!RuntimeContext.IsShutDown)
        m_Player.Unregister(this);
}
```

Dependencies injected earlier stay usable as objects; only beans that
implement `IDisposable` have been disposed.

## MonoBehaviour

```csharp
public sealed class PlayerView : AutowiredMonoBehaviour
{
    [Autowired] private IPlayerApi m_Player;
    [Autowired] private IInputApi m_Input;

    protected override void OnDependenciesInjected()
    {
        // Dependencies are ready here.
    }
}
```

Do not declare `Awake` in a subclass: it hides the base implementation and the
component silently gets no dependencies.

For an object Unity owns and the container does not create, inject into it
explicitly:

```csharp
RuntimeContext.Current.Autowire(existingObject);
```

## Method interception (AOP)

A bean implementing `IMethodInterceptor` advises bean methods, in the spirit of
Spring AOP:

```csharp
[Component]
public sealed class AuditInterceptor : IMethodInterceptor
{
    public bool Matches(MethodInfo targetMethod)
        => targetMethod.IsDefined(typeof(AuditedAttribute), true);

    public object Invoke(IMethodInvocation invocation)
    {
        // ... before
        var result = invocation.Proceed();
        // ... after
        return result;
    }
}
```

`ScopeInterceptor` is the common case: it opens an `IDisposable` around the
call, or around the method's `Task` for an async method, and can commit or roll
back in its `Complete` hook. `[Transactional]` is built on it.

Rules:

- Only calls made through an interface the bean was resolved by are advised;
  `this.Method()` and private methods are not.
- A bean with advised methods cannot be resolved by its class.
- A generic or iterator method that would outlive the advice is refused when
  the bean is created.
- Interceptors are application beans, created before the other beans.
- An instance passed to `RegisterInstance` is advised like a bean once the
  container has warmed up. Inside the configure hook it is returned as it is.
- Proxies are generated at run time with `DispatchProxy`: Mono only. On
  IL2CPP resolving an advised bean fails with an explanatory error; see
  [IL2CPP](il2cpp.md). `AopProxies.Create` builds one by hand for an object made with
  `new`.

## Strategies

```csharp
public interface IImportStrategy : IKeyedStrategy<AssetKind>
{
    void Import(string path);
}

[Strategy]
public sealed class TextureImportStrategy : IImportStrategy
{
    public IEnumerable<AssetKind> Keys
    {
        get { yield return AssetKind.Texture; }
    }

    public void Import(string path) { /* ... */ }
}

[Service]
public sealed class ImportService
{
    private readonly StrategyRegistry<AssetKind, IImportStrategy> m_Strategies;

    public ImportService(StrategyRegistry<AssetKind, IImportStrategy> strategies)
    {
        m_Strategies = strategies;
    }

    public void Import(AssetKind kind, string path)
    {
        m_Strategies.Require(kind).Import(path);
    }
}
```

`StrategyRegistry.Require` throws when no strategy has the key; `TryGet`
returns false instead.

## Composition

`RuntimeContext.Configure` is called before the first container access:

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
private static void Configure()
{
    RuntimeContext.Configure(context =>
    {
        // Register authored configuration or replace a default implementation.
        context.RegisterInstance(new GameSettings { Difficulty = 2 });
    });
}
```

For a fully controlled composition root use `ApplicationContext.Build`:

```csharp
var context = ApplicationContext.Build(new[] { typeof(GameRoot).Assembly });
```

## Module boundary policy

The optional `ModuleBoundaryOptions` rejects dependencies on another module's
non-`*.Api` assembly. It is disabled by default because it is a project
architecture convention, not a requirement of the container.

```csharp
var options = new ModuleBoundaryOptions
{
    Enabled = true,
    Strict = true,
    SharedAssemblies = { "MyGame.Bootstrap" }
};
var context = ApplicationContext.Build(assemblies, options, null);
```

When `Strict` is false, violations are collected in
`ApplicationContext.BoundaryViolations` instead of failing resolution.

## Scanned assemblies

`RuntimeContext` scans the DI assembly and every loaded assembly that depends
on it, directly or through another assembly. Editor and test assemblies are
left out: they are recognized by a whole segment of the assembly name
(`MyGame.Editor`, `Assembly-CSharp-Editor`, `MyGame.Inventory.Tests`), so
`MyGame.LevelEditor` is scanned. `RuntimeContext.AssemblyFilter` replaces that
rule:

```csharp
RuntimeContext.AssemblyFilter = assembly =>
    RuntimeContext.IsScannedByDefault(assembly) &&
    assembly.GetName().Name != "MyGame.Tools";
```

## Code stripping

Beans are created by reflection, so managed code stripping could remove them.
An editor build step (`IUnityLinkerProcessor`) generates a `link.xml` on every
player build. It preserves, in every assembly that depends on UniSpring:

- every bean: any class with `[Component]` or an attribute derived from it
  (`[Service]`, `[Strategy]`, `[LogSinkService]`, your own stereotypes) and
  every `[ComponentFamily]` member;
- every type that declares `[Autowired]` members or `[PostConstruct]` methods.

Nothing has to be configured in the consuming project.
