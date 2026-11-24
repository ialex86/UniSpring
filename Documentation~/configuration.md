# Configuration

## Composition and profiles

Configure once, before the first `RuntimeContext.Current` access. Calls to `RuntimeContext.Configure` set the hook rather than accumulate hooks; combine store installation, explicit instances and boundary configuration in a single composition root.

| `ContextOptions` member | Default when null | Purpose |
| --- | --- | --- |
| `Profiles` | `ContextOptions.DefaultProfiles()` | Active profiles used by the component scanner |
| `Boundaries` | Disabled boundary policy | Optional module-boundary enforcement |
| `Configure` | No hook | Register instances before warm-up |

```csharp
using System.Collections.Generic;
using UniSpring.DependencyInjection;

// In a test or manually owned composition root:
using var context = ApplicationContext.Build(
    new[] { typeof(MyService) }, // replace with the explicit bean types
    new ContextOptions { Profiles = new HashSet<string> { "Test" } });
```

For Unity's automatic context, set `RuntimeContext.Profiles` before first access. Its default profiles include `Editor` or `Player`, `Development` for development builds, and names from the comma-separated `UNISPRING_PROFILES` environment variable. Assigning a set replaces the defaults; include any default names you still want. `[Profile("!Editor")]` matches an inactive profile. Profiles are chosen at build time, not a live switch. `ApplicationContext.Profiles` reports the chosen set.

See [catalogs and settings](catalogs-and-settings.md) for typed application settings and resource overrides, and [architecture](architecture.md) for a composition root layout.

## `GameLogSourceOptions`

| Property | Default | Meaning |
| --- | --- | --- |
| `SourceRoot` | `null` | Path fragment that marks the source tree; `null` uses only the file name |
| `IgnoredSegments` | empty | Folder names skipped while building the dotted source |

## `LoggingPathOptions`

| Property | Default | Meaning |
| --- | --- | --- |
| `EnvironmentVariable` | `UNISPRING_LOG_CONFIG` | Explicit config path |
| `FileName` | `logging.properties` | Player config file name |
| `ResourceName` | `logging.properties` | `Resources` text asset read when no file is found |
| `EditorRelativePath` | `Settings/logging.properties` | Path relative to `Application.dataPath` |
| `RequiredInEditor` | `true` | Missing config in the editor is a warning |

`LoggingPathOptions` is a DI component. Replace it before warm-up:

```csharp
RuntimeContext.Configure(context =>
    context.RegisterInstance(new LoggingPathOptions
    {
        EditorRelativePath = "Src/Settings/logging.properties"
    }));
```

## `LoggingSettings`

| Member | Meaning |
| --- | --- |
| `RootLevel` | Fallback level for sources without an override |
| `ClassLevels` | Per-source level overrides |
| `SinkSelection` | Global sink expression: `all`, `none`, names, `all,-Name` |
| `SourceSinkSelections` | Per-source sink expressions |

## `ModuleBoundaryOptions`

| Property | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Turn the module-boundary check on |
| `Strict` | `true` | Fail resolution on a violation |
| `ApiSuffix` | `.Api` | Public contract assembly suffix |
| `InternalLayerSuffixes` | `.Runtime`, `.Presentation`, ... | Non-public layer suffixes |
| `SharedAssemblies` | UniSpring assemblies | Assemblies every module may use |

## `RuntimeContext`

| Member | Purpose |
| --- | --- |
| `Configure(action, boundaryOptions)` | Hook applied before the container warms up |
| `AssemblyFilter` | Optional predicate that replaces the default choice of scanned assemblies |
| `IsScannedByDefault(assembly)` | The default rule: editor and test assemblies are left out |
| `ClearConfiguration()` | Drops the configuration (tests and tooling) |
| `Current` | Application container, built on first access |
| `IsShutDown` | True once the application quits and the container is disposed |
| `ScopeOf(scene)` | Scene scope |
| `JoinSceneScope(path, host)` | Share the host's scope with a streamed scene |

## `ApplicationContext`

| Member | Purpose |
| --- | --- |
| `Build(assemblies)` | Build a root container |
| `Build(assemblies, configureBeforeWarmUp)` | Two-phase build with instance registration |
| `Build(assemblies, boundaryOptions, configureBeforeWarmUp)` | Build with an optional boundary policy |
| `Build(componentTypes)` | Build from an explicit list of component types |
| `Build(assemblies, ContextOptions)` / `Build(componentTypes, ContextOptions)` | Build with profiles, boundaries and a pre-warm-up hook |
| `Profiles` | Read the active profile names |
| `Get<T>()`, `GetAll<T>()` | Resolve beans |
| `RegisterInstance<T>(instance)` | Register an authored object before warm-up |
| `CreateScope(lifetime)` | Child scope |
| `Autowire(target)` | Inject into an existing Unity object |
| `Describe()` | Human-readable list of bean definitions |
| `BoundaryViolations` | Collected violations in non-strict mode |
