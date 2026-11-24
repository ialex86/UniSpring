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
  The method must be synchronous, `void` and parameterless. If it throws, the
  bean is disposed and the error propagates.

`ScriptableObject`, prefabs and scene objects are not beans. Use
`AutowiredMonoBehaviour` for Unity components.

## Selecting and injecting implementations

A single dependency such as `IItemCatalog` must have one matching bean, or exactly one `[Primary]` among multiple scanned candidates. Missing or ambiguous single dependencies fail resolution. An explicitly registered instance under the requested contract takes precedence for single resolution; it does not remove scanned beans from collection resolution or warm-up.

Inject `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>` or a one-dimensional `T[]` to receive all matching implementations. An empty collection is valid. Mutable collection contracts (`List<T>`, `IList<T>`, `ICollection<T>`) are not automatically populated. `[Primary]` does not filter collections. The container returns registered instances first, followed by discovered beans in its definition order.

```csharp
using System.Collections.Generic;
using UniSpring.DependencyInjection;

public interface IStartupCheck { void Check(); }

[Component]
public sealed class StorageCheck : IStartupCheck
{
    public void Check() { /* validate storage */ }
}

[Service]
public sealed class StartupChecks
{
    private readonly IReadOnlyList<IStartupCheck> m_Checks;

    public StartupChecks(IReadOnlyList<IStartupCheck> checks)
        => m_Checks = checks;

    public void Run()
    {
        foreach (var check in m_Checks)
            check.Check();
    }
}
```

Collection resolution is strict: every matching bean must be resolvable from the requesting scope. For example, do not inject a collection containing scene-scoped beans into an application-scoped service. Use a scene-scoped consumer or separate the contracts.

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

### Ownership and disposal

Application and Scene beans created by the container are disposed with their owning scope if they implement `IDisposable`. Transient instances are owned by their consumers; the consumer must arrange disposal. Instances supplied with `RegisterInstance` and objects passed to `Autowire` remain externally owned. Register external configuration before warm-up, and do not assume registration suppresses construction of a scanned bean.

An application bean is created in the root even if requested from a scene: it cannot capture scene-scoped dependencies. Container operations are thread-affine; use `RuntimeContext` on the Unity main thread.

For automatic lifecycle, listener and tick roles, use Application beans only. A Scene bean manages a subscription or tick registration token and releases it on disposal; Transient beans are not collected as lifecycle, listener or tick participants. See [lifecycle, events and ticks](lifecycle-events-ticks.md). `ApplicationContext` itself may be injected into infrastructure but ordinary services should declare their dependencies, not resolve them dynamically.

Use `JoinSceneScope` before loading the joined scene. Its path-based API should not be relied on to distinguish multiple concurrently loaded instances of the same scene asset.

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
- Interceptors are application beans, created before the other beans. What an
  interceptor depends on is handed to it without a proxy.
- An instance passed to `RegisterInstance` is advised like a bean once the
  container has warmed up. Inside the configure hook it is returned as it is.
- In both cases, if the bean handed out early has advised methods, the
  container logs a warning: that one reference skips the advice and is not
  proxied retroactively, while everything resolved later gets the proxy.
  A strategy registry resolved while the interceptors are created is not
  cached: the one that interceptor got keeps the unproxied strategies, while
  the registry handed out later holds the proxies.
- A failure in `ScopeInterceptor.Complete` or in disposing the scope never
  replaces the exception of the method itself; it is attached to it in
  `Exception.Data`.
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
returns false instead. A registry is a snapshot taken when it is first
resolved; register strategy instances before that.

### Registry contract and testing

A strategy may advertise several domain keys; all must be unique across the registry. A null key or null key collection is invalid. Duplicate keys fail when the registry is built (during warm-up if an application bean requires it). A missing key fails at `Require`, while `TryGet` is appropriate for optional behavior.

A registry is a snapshot of instances cached in the resolving scope, not a factory. Repeated `Require` calls return the same instance even for Transient strategies. Register matching external instances before the registry is requested; late registration that would affect an existing registry is rejected. Prefer stateless strategies. Transient strategies kept by a registry belong to the scope that built it and are disposed with that scope.

Keep AOP interceptors and pre-warm-up configuration independent of advised strategy registries. A registry resolved that early holds unproxied references (a warning is logged); it is not cached, so beans resolved later get a registry of proxies.

The registry is a plain C# object and can be constructed without a container. This self-contained NUnit example tests dispatch and its missing-key contract. Place it in an EditMode test assembly referencing `UniSpring.DependencyInjection`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniSpring.DependencyInjection;

public sealed class RegistryTests
{
    private enum Kind { Double, Missing }

    private interface IRule : IKeyedStrategy<Kind>
    {
        int Apply(int value);
    }

    private sealed class DoubleRule : IRule
    {
        public IEnumerable<Kind> Keys => new[] { Kind.Double };
        public int Apply(int value) => value * 2;
    }

    private sealed class Calculator
    {
        private readonly StrategyRegistry<Kind, IRule> m_Rules;
        public Calculator(StrategyRegistry<Kind, IRule> rules) => m_Rules = rules;
        public int Apply(Kind kind, int value) => m_Rules.Require(kind).Apply(value);
    }

    [Test]
    public void DispatchesWithoutAContainer()
    {
        var registry = new StrategyRegistry<Kind, IRule>(new IRule[] { new DoubleRule() });
        var calculator = new Calculator(registry);
        Assert.That(calculator.Apply(Kind.Double, 10), Is.EqualTo(20));
        Assert.That(registry.TryGet(Kind.Missing, out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => calculator.Apply(Kind.Missing, 10));
    }

    [Test]
    public void RejectsDuplicateKeys()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new StrategyRegistry<Kind, IRule>(new IRule[] { new DoubleRule(), new DoubleRule() }));
    }
}
```

Use separate container integration tests to check scanning, injection, module boundaries and scene lifetimes.

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

var tests = ApplicationContext.Build(new[] { typeof(GameRoot).Assembly }, new ContextOptions
{
    Profiles = new HashSet<string> { "Test" },
    Configure = scope => scope.RegisterInstance<IClock>(new FixedClock())
});
```

Start and stop hooks, events between beans, the frame loop and profiles are
described in [Lifecycle, events and ticks](lifecycle-events-ticks.md).

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

No extra linker configuration is normally expected for these types. This does
not establish IL2CPP/AOT compatibility; see [IL2CPP](il2cpp.md) and verify a
real player build.
