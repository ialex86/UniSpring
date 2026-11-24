# UniSpring documentation

UniSpring is a Spring-inspired application framework for Unity. Start with the tutorial, then use the subsystem guides as a reference. It runs on Mono and IL2CPP; for IL2CPP the editor generates the proxies behind advice, repositories and catalogs, and a macOS IL2CPP player passes the package's smoke test.

- [Getting started: a complete shop](getting-started.md)
- [Architecture and module boundaries](architecture.md)
- [Spring-to-UniSpring cheat sheet](spring-cheatsheet.md)
- [Catalogs and typed settings](catalogs-and-settings.md)
- [Testing and platform verification](testing.md)
- [Troubleshooting](troubleshooting.md)
- [Agent architecture rules](../AGENTS.md)

- [Dependency injection](dependency-injection.md)
- [Lifecycle, events and ticks](lifecycle-events-ticks.md): start and stop hooks, events, the frame loop, profiles
- [Transactions](transactions.md)
- [Data](data.md): repositories, catalogs, settings, mappers, validators and migrations
- [Worked example: a minimal transaction store](transaction-store-example.md)
- [Logging](logging.md)
- [Configuration](configuration.md)
- [IL2CPP](il2cpp.md): what to change for iOS, consoles, WebGL and Android release builds

## Installation

Add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.ialex86.unispring": "https://github.com/ialex86/UniSpring.git#v0.4.0"
  }
}
```

## Assemblies

| Assembly | Purpose |
| --- | --- |
| `UniSpring.DependencyInjection` | Container, attributes, scopes, strategies, Unity composition |
| `UniSpring.Transactions` | `[Transactional]` advice and the `ITransactionManager` port |
| `UniSpring.Data` | Repositories, catalogs, typed settings, mapping, validation, migrations, file and memory stores |
| `UniSpring.Logging` | `GameLog` facade, configuration, Unity sink |
| `UniSpring.Logging.Editor` | Editor bootstrap for edit-mode tools |

`UniSpring.Transactions` references `UniSpring.DependencyInjection`, and
`UniSpring.Logging` references `UniSpring.DependencyInjection`; the dependency
direction is one-way.

## Minimal setup

```csharp
using UniSpring.DependencyInjection;
using UnityEngine;

[Service]
public sealed class Clock
{
    public System.DateTime Now => System.DateTime.UtcNow;
}

public sealed class GameRoot : AutowiredMonoBehaviour
{
    [Autowired] private Clock m_Clock;

    protected override void OnDependenciesInjected()
    {
        UnityEngine.Debug.Log(m_Clock.Now);
    }
}
```

`RuntimeContext` builds the application container lazily on first access. If
you prefer to own the composition root, use `ApplicationContext.Build`.
