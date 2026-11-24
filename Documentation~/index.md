# UniSpring documentation

UniSpring is a small dependency injection container with declarative
transactions and a structured logging facade for Unity, inspired by the Spring
Framework.

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
    "com.ialex86.unispring": "https://github.com/ialex86/UniSpring.git#v0.3.0"
  }
}
```

## Assemblies

| Assembly | Purpose |
| --- | --- |
| `UniSpring.DependencyInjection` | Container, attributes, scopes, strategies, Unity composition |
| `UniSpring.Transactions` | `[Transactional]` advice and the `ITransactionManager` port |
| `UniSpring.Data` | Repositories, mappers, validators, migrations and the document store port |
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
