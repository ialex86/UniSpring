# UniSpring

**Spring-inspired application framework for Unity — for Java/Spring developers and AI-assisted game development.**

## Why UniSpring?

This project starts from an opinionated premise: when it comes to declarative application composition, architecture conventions and the developer experience of building modular applications, the Java Spring Framework is far ahead of the built-in .NET DI container and the DI tooling commonly used in Unity.

Spring is much more than a DI container. It gives developers a consistent way to structure applications, declare components, compose dependencies, extend behavior and handle cross-cutting concerns. UniSpring brings a small, deliberately selected part of that experience to Unity. It is designed first and foremost for developers who are already comfortable writing Java applications with Spring and want a familiar programming model in C#.

UniSpring is not a port of Spring, a replacement for the Unity engine, or a claim of performance superiority over other DI containers. Its focus is application architecture and developer experience, not rendering, ECS or hot-path optimization.

## AI slop, with architectural guardrails

This project is itself a piece of AI slop: AI-assisted code intended to help AI write less tangled code.

Coding agents are good at implementing small, well-defined contracts. They are less reliable when they have to infer a large, undocumented graph of scene objects, global singletons, hidden dependencies and duplicated dispatch logic.

UniSpring aims to make both human and AI-assisted development more predictable by providing a small set of reusable architectural rules:

- Declare services and inject dependencies through constructors instead of searching for them globally.
- Extend behavior by adding strategies rather than editing a growing conditional dispatcher.
- Keep game rules in plain C# objects; use MonoBehaviour as the Unity-facing adapter.
- Make ownership and lifetime explicit; avoid accidental cross-scene dependencies.
- Keep modules isolated behind API assemblies and enable strict boundary checks where appropriate.
- Test behavior using ordinary objects and fakes, without loading a Unity scene or starting the DI container.

The goal is to reduce integration mistakes, limit the scope of changes and improve testability. These are design goals, not measured reductions in AI-generated defects. A container cannot make bad code good, and generated code still needs review and tests.

## Features

Application architecture for Unity: dependency injection, lifecycle, events, game-loop integration, transactions, persistence, read-only content, configuration and logging.

- Constructor injection with `[Service]`, `[Component]` and `[Strategy]` beans.
- Application, Scene and Transient lifetimes, plus child scopes.
- `AutowiredMonoBehaviour` for Unity components with `[Autowired]` members.
- Keyed strategies through `StrategyRegistry<TKey, TStrategy>`.
- Lifecycle hooks, application events (with after-commit delivery), beans
  ticked by the player loop without MonoBehaviour hosts, and profiles.
- Declarative transactions with `[Transactional]` and `ITransactionManager`.
- Spring Data-style repositories with derived equality queries, mappers, validators and migrations; file and in-memory stores.
- Read-only content catalogs and typed settings bound to `Resources` (or a custom resource source).
- Background file writes, checksums, backups and journal-based recovery. Logical commit is not disk durability; see the [storage guarantees](Documentation~/data.md#filedocumentstore).
- Optional module-boundary policy (`*.Api` assemblies only).
- `GameLog` facade with per-source levels, lazy messages, exception entries and
  pluggable sinks.
- Multiple `ILogSink` implementations with per-source routing.
- Optional `logging.properties` configuration.

Requires Unity 6 (`6000.0` or newer). No third-party dependencies.

**Platform status.** Mono and IL2CPP. On Mono the proxies behind
`[Transactional]`, interceptors, `[Repository]` and `[Catalog]` are made at run
time; for IL2CPP the editor generates them as C# (`UniSpring.Generated.cs`,
commit it) and `link.xml` is generated too. A macOS IL2CPP player with high
stripping passes the package's smoke test; other IL2CPP targets have not been
built yet. See [Documentation~/il2cpp.md](Documentation~/il2cpp.md).

## Install

Add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.ialex86.unispring": "https://github.com/ialex86/UniSpring.git#v0.3.1"
  }
}
```

Pin a tag or commit; do not track a moving branch in production. For private forks use an SSH URL or a configured git credential helper.

## Quick start

```csharp
using UniSpring.DependencyInjection;
using UniSpring.Logging;
using UnityEngine;

public interface IGreetingService
{
    string Greet(string name);
}

[Service]
public sealed class GreetingService : IGreetingService
{
    public string Greet(string name) => "Hello, " + name + "!";
}

[DefaultExecutionOrder(-10000)]
public sealed class GameBootstrap : AutowiredMonoBehaviour
{
    [Autowired] private IGreetingService m_Greeting;

    protected override void OnDependenciesInjected()
    {
        GameLog.Info(m_Greeting.Greet("UniSpring"));
    }
}
```

`RuntimeContext` builds the application container on first use and scans loaded
assemblies that reference `UniSpring.DependencyInjection`. Scene scopes are
created per loaded scene. Use `ApplicationContext.Build(assemblies)` directly
when you want to own the composition root yourself.

For a custom asmdef, reference `UniSpring.DependencyInjection` (and `UniSpring.Logging` if using `GameLog`). Put the `GameBootstrap` MonoBehaviour in `GameBootstrap.cs`, attach it to an active GameObject and enter Play Mode. The Console should show `Hello, UniSpring!`. Do not declare `Awake` in an `AutowiredMonoBehaviour` subclass; initialize injected dependencies in `OnDependenciesInjected()` instead.

**Next:** follow the [shop walkthrough](Documentation~/getting-started.md) to combine services, repositories, a catalog, a transaction, events, Unity UI and tests. For migration from Java, use the [Spring cheat sheet](Documentation~/spring-cheatsheet.md). For agents, copy the application rules in [AGENTS.md](AGENTS.md).

## How does it compare?

This comparison concerns the standard APIs, without custom scanners, third-party .NET extensions or additional architecture packages.

| Capability | Plain Unity | .NET built-in DI | Unity + VContainer | Unity + UniSpring |
| --- | --- | --- | --- | --- |
| Constructor injection into plain C# classes | Manual composition | Yes | Yes | Yes |
| Component discovery | Manual | Explicit registration | Explicit registration | Attribute-based discovery |
| Inject multiple implementations | Manual | `IEnumerable<T>` | `IEnumerable<T>` / `IReadOnlyList<T>` | Collections and arrays |
| Business-key strategy registry | Write your own | Write your own or use keyed services | Write your own or use keyed registrations | `StrategyRegistry<TKey, TStrategy>` |
| Keys declared by each strategy | Custom | Custom | Custom | `IKeyedStrategy<TKey>.Keys` |
| Application and scene lifetimes | Manual | General-purpose scopes; Unity integration is custom | Configure `LifetimeScope` hierarchy | Automatic scene scopes with `RuntimeContext` |
| Injection into Unity components | Manual / Inspector | Custom Unity integration | Component registration or explicit injection | `AutowiredMonoBehaviour` |
| Post-injection initialization | Unity lifecycle / manual | Custom | Entry points / callbacks | `[PostConstruct]` / `OnDependenciesInjected()` |
| Module-boundary enforcement | Custom / asmdef | Custom | Custom | Optional `ModuleBoundaryOptions` |
| Start/stop lifecycle | Unity callbacks / custom | Generic Host (separate from DI) | Entry points | `ILifecycle` |
| Game-loop callbacks | MonoBehaviour | Not part of DI | Tick entry points | `ITickable`, `ILateTickable`, `IFixedTickable` |
| Application events | Custom / UnityEvent | Not part of DI | Custom / separate package | `IEventPublisher`, `IEventListener<T>` |
| After-commit events | Custom | Not part of DI | Custom | `[AfterCommit]` (logical commit) |
| Component profiles | Custom | Conditional registrations | Conditional registrations | `[Profile]`, `ContextOptions` |
| Declarative repositories and derived queries | Custom | Not part of DI | Not part of DI | `[Repository]` |
| Save storage and migrations | Custom / PlayerPrefs | Not part of DI | Not part of DI | File / memory stores, `[Migration]` |
| Read-only game content | Resources / custom | Not part of DI | Custom | `[Catalog]` |
| Typed settings | ScriptableObject / custom | Options (separate infrastructure) | Explicit registration | `[ConfigurationProperties]` |
| Method interception / declarative transactions | Custom | Not in the DI container | Not in the DI container | Built in (IL2CPP through generated proxies); use the Data store or a custom manager |
| Logging facade and routing | Unity logging API | Separate .NET logging infrastructure | Separate logging solution | `GameLog` and configurable sinks |
| IL2CPP | Native Unity backend | Not a Unity-specific container | Supported; see its AOT guidance | Supported through editor-generated proxies; macOS player smoke-tested |

.NET and VContainer already support keyed resolution. UniSpring's distinction is the automatically discovered, injectable registry keyed by the application's own domain values, without requiring consumers to resolve services from the container. The trade-off is more convention and a younger implementation.

This is a comparison with the built-in DI container, not the entire .NET ecosystem: EF Core, Options, Hosting and other packages solve many related problems. Choose UniSpring when you want a coherent Spring-like application model rather than a minimal container; choose a mature Unity DI container if verified AOT support is a requirement.

References: [Spring component scanning](https://docs.spring.io/spring/reference/core/beans/classpath-scanning.html), [.NET DI](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection), [VContainer](https://vcontainer.hadashikick.jp/).

## Same game rule, three approaches

Imagine a reward system: coins use the supplied base amount; gems receive twice that amount. In each example, the scene prints the result for 10 gems (20) during initialization. The arithmetic is intentionally trivial so the composition differences are visible.

The three versions are alternatives, not files to install together. Place each public MonoBehaviour in its own matching `.cs` file, and attach `RewardDemo` to an active GameObject.

Shared by all three versions:

```csharp
public enum RewardKind { Coins, Gems }
```

### 1. Plain Unity — direct implementation

```csharp
using System;
using UnityEngine;

public sealed class RewardDemo : MonoBehaviour
{
    public int Calculate(RewardKind kind, int amount)
    {
        switch (kind)
        {
            case RewardKind.Coins: return amount;
            case RewardKind.Gems: return amount * 2;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private void Start()
        => Debug.Log(Calculate(RewardKind.Gems, 10));
}
```

No framework setup. For a small, stable set of cases, this is perfectly reasonable. As independent reward behaviors and their dependencies grow, the dispatcher becomes a shared modification point. Plain Unity can also use constructor injection and strategies; the composition is simply your responsibility.

### 2. Unity + VContainer — dependency injection and a custom strategy map

```csharp
using System.Collections.Generic;
using System.Linq;

public interface IRewardRule
{
    RewardKind Kind { get; }
    int Calculate(int amount);
}

public sealed class CoinsRule : IRewardRule
{
    public RewardKind Kind => RewardKind.Coins;
    public int Calculate(int amount) => amount;
}

public sealed class GemsRule : IRewardRule
{
    public RewardKind Kind => RewardKind.Gems;
    public int Calculate(int amount) => amount * 2;
}

public sealed class RewardService
{
    private readonly Dictionary<RewardKind, IRewardRule> m_Rules;

    public RewardService(IEnumerable<IRewardRule> rules)
        => m_Rules = rules.ToDictionary(rule => rule.Kind);

    public int Calculate(RewardKind kind, int amount)
        => m_Rules[kind].Calculate(amount);
}
```

Register the rules, the service and the view:

```csharp
using UnityEngine;
using VContainer;
using VContainer.Unity;

public sealed class GameScope : LifetimeScope
{
    [SerializeField] private RewardDemo m_Demo;

    protected override void Configure(IContainerBuilder builder)
    {
        builder.Register<IRewardRule, CoinsRule>(Lifetime.Singleton);
        builder.Register<IRewardRule, GemsRule>(Lifetime.Singleton);
        builder.Register<RewardService>(Lifetime.Singleton);
        builder.RegisterComponent(m_Demo);
    }
}
```

```csharp
using UnityEngine;
using VContainer;

public sealed class RewardDemo : MonoBehaviour
{
    private RewardService m_Rewards;

    [Inject]
    public void Construct(RewardService rewards) => m_Rewards = rewards;

    private void Start()
        => Debug.Log(m_Rewards.Calculate(RewardKind.Gems, 10));
}
```

Install VContainer, add `GameScope` to an active scene object with its normal automatic build enabled, and assign the demo component in the Inspector. This is ordinary, explicit DI: strategies can be tested independently. VContainer also supports keyed registrations if you prefer that approach to a custom map.

For comparison, plain Unity with the same `IRewardRule` and `RewardService` from above can replace the entire container configuration with manual constructor injection in `Start`:

```csharp
var rewards = new RewardService(new IRewardRule[]
{
    new CoinsRule(), new GemsRule()
});
Debug.Log(rewards.Calculate(RewardKind.Gems, 10));
```

### 3. Unity + UniSpring — declarative strategies

```csharp
using System.Collections.Generic;
using UniSpring.DependencyInjection;

public interface IRewardRule : IKeyedStrategy<RewardKind>
{
    int Calculate(int amount);
}

[Strategy]
public sealed class CoinsRule : IRewardRule
{
    public IEnumerable<RewardKind> Keys => new[] { RewardKind.Coins };
    public int Calculate(int amount) => amount;
}

[Strategy]
public sealed class GemsRule : IRewardRule
{
    public IEnumerable<RewardKind> Keys => new[] { RewardKind.Gems };
    public int Calculate(int amount) => amount * 2;
}

[Service]
public sealed class RewardService
{
    private readonly StrategyRegistry<RewardKind, IRewardRule> m_Rules;

    public RewardService(StrategyRegistry<RewardKind, IRewardRule> rules)
        => m_Rules = rules;

    public int Calculate(RewardKind kind, int amount)
        => m_Rules.Require(kind).Calculate(amount);
}
```

```csharp
using UniSpring.DependencyInjection;
using UnityEngine;

public sealed class RewardDemo : AutowiredMonoBehaviour
{
    [Autowired] private RewardService m_Rewards;

    protected override void OnDependenciesInjected()
        => Debug.Log(m_Rewards.Calculate(RewardKind.Gems, 10));
}
```

Install UniSpring and attach `RewardDemo`. No custom scope component, registration list, dictionary-building code or manually resolved service is needed for this example. The runtime discovers the attributed classes in eligible loaded assemblies and injects a registry with their declared keys.

Do not declare `Awake` in an `AutowiredMonoBehaviour` subclass: it hides the base injection hook. Use `OnDependenciesInjected` for initialization that needs injected dependencies.

`Require` rejects missing keys, `TryGet` supports optional behavior, and duplicate keys are rejected when the registry is built. One strategy may declare more than one key. Keep registry strategies stateless by default; see the [DI guide](Documentation~/dependency-injection.md#strategies) for ownership and lifetime rules.

### What changes when a new reward type is added?

| Change | Plain Unity (switch) | Plain Unity (manual DI) | VContainer (collection DI) | UniSpring |
| --- | --- | --- | --- | --- |
| Add a domain key | Yes | Yes | Yes | Yes |
| Add independent implementation | No, add a case instead | Yes | Yes | Yes, `[Strategy]` |
| Modify dispatcher | Yes | No | No | No |
| Modify composition / registrations | No | Yes | Yes | No |
| Write a business-key map | No | Yes | Yes | No |

The benefit is not necessarily fewer lines than a tiny switch. It is a smaller and more predictable change surface as behavior grows.

### Testing without Unity lifecycle or a container

The UniSpring registry can be constructed directly, so the same service can be tested with a hand-picked set of strategies. In an NUnit test assembly referencing the example types:

```csharp
using NUnit.Framework;
using UniSpring.DependencyInjection;

public sealed class RewardServiceTests
{
    [Test]
    public void GemsDoubleTheBaseAmount()
    {
        var registry = new StrategyRegistry<RewardKind, IRewardRule>(
            new IRewardRule[] { new GemsRule() });
        var service = new RewardService(registry);

        Assert.That(service.Calculate(RewardKind.Gems, 10), Is.EqualTo(20));
    }
}
```

This is a unit test of the rule and service, not a test of assembly scanning or injection. Test the container and scene integration separately.

## Spring concepts in Unity

| Spring concept | UniSpring equivalent |
| --- | --- |
| `@Component`, `@Service` | `[Component]`, `[Service]` |
| Constructor injection | Constructor injection |
| `@Autowired`, `@Primary` | `[Autowired]`, `[Primary]` |
| `@PostConstruct` | `[PostConstruct]` (synchronous initialization) |
| Singleton / prototype | Application / Transient (different ownership rules) |
| Context and scoped beans | `ApplicationContext`, Application / Scene / child scopes |
| Inject a collection and build a strategy map | `[Strategy]`, `IKeyedStrategy<TKey>`, `StrategyRegistry<TKey, TStrategy>` |
| Spring AOP | `IMethodInterceptor` (generated proxies on IL2CPP) |
| `@Transactional` | `[Transactional]` (different default rollback policy) |
| `Lifecycle` / `SmartLifecycle` (conceptual) | `ILifecycle` |
| `@Profile` | `[Profile]` |
| `ApplicationEventPublisher` / `@EventListener` | `IEventPublisher` / `IEventListener<T>` |
| `@TransactionalEventListener` | `[AfterCommit]` (outside a transaction, immediate) |
| Spring Data repositories and query derivation | `[Repository]`, `IRepository<T>`, `IDocument<T>`, `FindBy...` |
| `@ConfigurationProperties` | `[ConfigurationProperties]` (resource binding) |
| Flyway / Liquibase (conceptual) | `[Migration]`, `IMigration` |
| Bean Validation (conceptual) | `IDocumentValidator<T>` |
| No direct Spring counterpart | `[Catalog]`, `ICatalog<T>`, Unity tick interfaces |
| Package / module architecture conventions | Optional API-assembly boundary policy |

These are conceptual counterparts, not a promise of identical Spring semantics.

## Beyond DI: a game application

Here is a small excerpt from the [complete shop](Documentation~/getting-started.md) (model types and configuration are defined there):

```csharp
[Repository]
public interface IInventoryRepository : IRepository<OwnedItem>
{
    IReadOnlyList<OwnedItem> FindByItemId(string itemId);
}

[Catalog("Shop/Items")]
public interface IShopCatalog : ICatalog<ShopItem> { }

[Component]
[AfterCommit]
public sealed class PurchaseLog : IEventListener<ItemPurchased>
{
    public void On(ItemPurchased evt) => UnityEngine.Debug.Log("Purchased " + evt.Id);
}
```

The use case injects the repository, catalog and event publisher. It changes player data inside a transaction; independent listeners react after logical commit. A separate Unity adapter handles input and presentation. The full walkthrough includes the serializer, store setup, settings, domain mapper and validators, a purchase transaction, resources, and NUnit tests. This is the architectural workflow UniSpring aims to make predictable for developers and coding agents.

## Documentation

| Guide | Start here if you want to… |
| --- | --- |
| [Dependency injection](Documentation~/dependency-injection.md) | Discover beans, inject collections, manage lifetimes, use strategies or configure module boundaries |
| [Transactions](Documentation~/transactions.md) | Understand commit/rollback and implement a transaction manager |
| [Lifecycle, events and ticks](Documentation~/lifecycle-events-ticks.md) | Start and stop beans, publish events between modules, tick beans every frame, choose beans by profile |
| [Data](Documentation~/data.md) | Store saves through repository interfaces, mappers, validators and migrations |
| [Getting started](Documentation~/getting-started.md) | Build and test a complete shop with persistent state and events |
| [Architecture](Documentation~/architecture.md) | Choose modules, lifetimes, explicit contracts and communication patterns |
| [Spring cheat sheet](Documentation~/spring-cheatsheet.md) | Translate familiar Spring patterns and understand semantic differences |
| [Catalogs and settings](Documentation~/catalogs-and-settings.md) | Load static content and typed configuration |
| [Testing](Documentation~/testing.md) | Test rules, repository integration, transactions and Unity lifecycle separately |
| [Configuration](Documentation~/configuration.md) | Configure profiles, scanning, logging and module boundaries |
| [Troubleshooting](Documentation~/troubleshooting.md) | Diagnose injection, persistence, lifecycle and AOT failures |
| [Logging](Documentation~/logging.md) | Configure levels, paths, sources, sinks and routing |
| [IL2CPP](Documentation~/il2cpp.md) | Prepare and verify an AOT player build |
| [AGENTS.md](AGENTS.md) | Apply the architecture conventions in AI-assisted development |

## Logging configuration

The editor reads `Assets/Settings/logging.properties`. A player build looks
for:

1. `persistentDataPath/logging.properties` — put a file there to change
   logging on a device without rebuilding;
2. `StreamingAssets/logging.properties`;
3. the `Resources` text asset `logging.properties`
   (`Assets/Resources/logging.properties.txt`).

On Android and WebGL StreamingAssets is not a folder on disk, so ship the
config through `Resources` there. The `UNISPRING_LOG_CONFIG` environment
variable overrides the location.

The file is read when the game starts, before any container exists, so it
applies from the first log entry. Custom `LoggingPathOptions` and sinks
registered as beans take effect when the container is built (the first
`RuntimeContext` access).

```properties
root = Info
MyGame = Debug
MyGame.Vehicle = Warn
```

Levels are inherited by dotted source prefix. The source is derived from
`[CallerFilePath]`; do not pass the `caller` argument yourself.

Configure source naming when your code lives outside the default layout:

```csharp
GameLog.ConfigureSource(new GameLogSourceOptions
{
    SourceRoot = "/Assets/Src/",
    IgnoredSegments = new[] { "Scripts", "Internal" }
});
```

### Multiple sinks

Every registered `ILogSink` is active by default. Mark a sink with
`[LogSinkService("file", Default = false)]` to register it as a bean and keep
it out of the default set. `[LogSink("file")]` alone only names a sink that
you create and pass to `GameLogService` yourself.

```properties
# default: every sink with Default = true
# sinks = UnityLogSink, FileLogSink
# sinks = all, -FileLogSink
sinks.MyGame.Audit = FileLogSink
sinks.Noisy.Service = none
```

The source comes from `[CallerFilePath]`, so business code never chooses a
sink; the configuration routes by source. A failing sink is reported and
skipped without breaking the others.

## Configuration hooks

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
private static void ConfigureUniSpring()
{
    RuntimeContext.Configure(
        context => context.RegisterInstance(new LoggingPathOptions
        {
            EditorRelativePath = "Src/Settings/logging.properties"
        }),
        new ModuleBoundaryOptions
        {
            Enabled = true,
            SharedAssemblies = { "MyGame.Bootstrap" }
        });
}
```

`ModuleBoundaryOptions` is disabled by default. Enable it only when your
assemblies follow the `<Module>.Api` / `<Module>.Runtime` layout.

## Transactions

`[Transactional]` runs a bean method inside one `ITransactionManager.Begin`
(on IL2CPP through a generated proxy, see *IL2CPP* above):

```csharp
[Transactional]
public void Buy(CarId car)
{
    // Every write made here, in any module, lands as one step.
}
```

The method must implement an interface method; the container advises calls
made through that interface. An async method keeps its transaction until its
task completes. By default the transaction commits when the call ends, also
when the method throws; set `RollbackOnError = true` when the store can discard
its changes.

For game saves, install `FileDocumentStore` from `UniSpring.Data` before warm-up; it also supplies the transaction manager. `InMemoryDocumentStore` is available for tests. See the [shop walkthrough](Documentation~/getting-started.md) for the complete setup.

Only if you need a different storage backend, implement `ITransactionManager` and `ITransaction` and register your manager (for example, with the following composition hook). Do not install competing managers unintentionally. A successful logical commit can precede disk persistence.

```csharp
RuntimeContext.Configure(context =>
    context.RegisterInstance<ITransactionManager>(new SaveTransactionManager()));
```

`Begin()` is expected to join the transaction already open in the flow
(Spring's `REQUIRED`), usually through an `AsyncLocal`. Without a configured
manager the container still builds, and the first advised call fails with a
message that explains what to register.

## Samples

Import **Basic Setup** from the Package Manager to see DI, an autowired
MonoBehaviour and `GameLog` in action.

Import **Persistent Shop** for the complete code of the
[shop walkthrough](Documentation~/getting-started.md): a catalog, saves, a
transactional purchase, events, a Unity adapter and EditMode tests (Mono). CI
compiles it and runs its tests, and checks that it matches the walkthrough.

## Tests

The package tests live in `Tests/EditMode`. `Tests~/UnityProject` is a host
project template; Unity cannot open a path containing `~`, so the template is
copied outside the package and its manifest is pointed at the package:

```bash
Tests~/run-tests.sh /path/to/Unity
```

The script creates a temporary host project, writes an absolute
`file:` dependency, runs the EditMode tests and stores the results in
`TestResults/editmode.xml`.

On CI the same steps are performed by `.github/workflows/tests.yml`, which uses
`game-ci/unity-test-runner` and requires the `UNITY_EMAIL` and
`UNITY_PASSWORD` secrets (a Personal license needs nothing else).

## Code stripping

Beans are created by reflection. An editor build step generates a `link.xml`
for every player build that preserves all beans and all types with
`[Autowired]` members. This reduces stripping risks but does not guarantee
all AOT generic instantiations or cover unrelated reflection in user code.

## License

Apache License 2.0. See [LICENSE.md](LICENSE.md).

## Provenance

The initial implementation is based on the DI and logging subsystems of the
NoLegal project.
