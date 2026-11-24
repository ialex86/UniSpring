# UniSpring

Dependency Injection, declarative transactions and structured logging for Unity, inspired by the Spring Framework.

- Constructor injection with `[Service]`, `[Component]` and `[Strategy]` beans.
- Application, Scene and Transient lifetimes, plus child scopes.
- `AutowiredMonoBehaviour` for Unity components with `[Autowired]` members.
- Keyed strategies through `StrategyRegistry<TKey, TStrategy>`.
- Declarative transactions with `[Transactional]` and `ITransactionManager`.
- Optional module-boundary policy (`*.Api` assemblies only).
- `GameLog` facade with per-source levels, lazy messages, exception entries and
  pluggable sinks.
- Multiple `ILogSink` implementations with per-source routing.
- Optional `logging.properties` configuration.

Requires Unity 6 (`6000.0` or newer). No third-party dependencies.

**Scripting backends.** Method interception, and therefore `[Transactional]`,
generates its proxies at run time with `DispatchProxy`. That needs Mono: on
IL2CPP (iOS, consoles, WebGL) resolving a bean with advised methods fails with
an explanatory error; use `ITransactionManager.Run(...)` / `RunAsync(...)`
there, which apply the same rules without a proxy.
The rest of the package uses plain reflection; IL2CPP builds have not been
tested yet.

## Install

Add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.ialex86.unispring": "https://github.com/ialex86/UniSpring.git#v0.1.0"
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
(Mono only, see *Scripting backends* above):

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

The package has no store of its own. Implement `ITransactionManager` and
`ITransaction`, then mark the manager `[Service]` and `[Primary]`, or register
an instance before warm-up:

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
`game-ci/unity-test-runner` and requires the `UNITY_LICENSE`, `UNITY_EMAIL`
and `UNITY_PASSWORD` secrets.

## Code stripping

Beans are created by reflection. An editor build step generates a `link.xml`
for every player build that preserves all beans and all types with
`[Autowired]` members, so managed code stripping needs no setup.

## License

Apache License 2.0. See [LICENSE.md](LICENSE.md).

## Provenance

The initial implementation is based on the DI and logging subsystems of the
NoLegal project.
