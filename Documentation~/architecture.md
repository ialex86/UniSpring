# Application architecture

[Documentation index](index.md) · [Shop walkthrough](getting-started.md)

UniSpring is designed for modular Unity applications and for agents that can implement bounded tasks using existing contracts. It does not require a framework abstraction for every class or every conditional. Prefer the smallest design that keeps dependencies explicit and behavior testable.

## Choose the right abstraction

| Need | Use | Avoid |
| --- | --- | --- |
| Game rules / orchestration | Plain C# service, constructor injection | Service locator in game logic |
| Independent behavior selected by domain key | `[Strategy]`, `StrategyRegistry` | Growing dispatchers, hand-maintained registration lists |
| Unity presentation | `AutowiredMonoBehaviour`, thin adapter | Business logic tied to scenes |
| Game-session startup/shutdown | `ILifecycle` on Application beans | Starting services manually in random Awake methods |
| Independent notification | `IEventPublisher`, `IEventListener<T>` | Event chains for required synchronous operations |
| Frame simulation | `ITickable` / `ILateTickable` / `IFixedTickable` | Disk I/O or broad queries each frame |
| Player state | Repository / document | Global mutable SaveManager |
| Shipped content / settings | Catalog / ConfigurationProperties | Mutable copies of assets used as saves |
| Multi-document update | Transaction with explicit rollback policy | Treating events as a transaction or durable outbox |
| Environment-specific components | `[Profile]` | Scattered environment branches |

## Module layout

```text
Assets/Game/
  Bootstrap/                 # composition root
  Shop/Api/                  # Shop.Api.asmdef: IShop, public event contracts
  Shop/Runtime/              # Shop.Runtime.asmdef: implementations, repositories
  Shop/Presentation/         # Shop.Presentation.asmdef: Unity adapters
  Inventory/Api/
  Inventory/Runtime/
  Achievements/Api/
  Achievements/Runtime/
  Tests/                     # EditMode and PlayMode assemblies
```

A module exposes its small public API to other modules. Its implementation can depend on another module's API, not its runtime/presentation assemblies. Keep public event DTOs in the API assembly of the module that owns the event. A module's presentation can reference its own API and engine-facing adapters, but should not orchestrate other modules' internals. Use asmdef references to enforce compile-time boundaries; the optional `ModuleBoundaryOptions` additionally checks DI resolution, not arbitrary C# calls.

Enable `Enabled = true, Strict = true` only after the assembly naming convention is in place. See [configuration](configuration.md#moduleboundaryoptions) for shared assemblies. Register the store and external instances in one root hook; do not call `RuntimeContext.Configure` in each feature module (later calls replace the hook).

## Lifetime and ownership

Application beans live for the context; they may not capture Scene beans. Scene beans clean up tokens and resources as their scene scope ends. Transient objects are created per resolution and are owned by their consumers. Registry entries are cached instances rather than factories: prefer stateless strategies; transient strategies a registry keeps are disposed with the scope that built it. Transient beans are not collected as listeners, tickables or lifecycle participants.

Constructor injection is for required dependencies. `PostConstruct` is synchronous and for initialization of an already injected bean; `ILifecycle.Start` is for starting application activity after warm-up. A failed Start must clean its partial work. Use explicit ownership for manually created Unity objects; resource assets are not application-owned disposable objects.

Keep external effects out of constructors. Events published during startup are delivered when the container starts. Automatic callbacks (lifecycle, listeners, ticks) go through the beans' AOP proxies, so their advice applies.

## Communication and consistency

Use a direct method call when a result, invariant or required operation is part of the current use case. Use events for independent reactions (a UI feed, statistics or an achievement), not as a way to hide the dependency graph. Events are synchronous by default, not a job queue; `[AfterCommit]` defers logical completion but is not disk durability or an outbox. Publish immutable event values and avoid sharing mutable state through them.

The transaction boundary belongs at a use case that changes related documents. Specify `RollbackOnError = true` where rollback is required; UniSpring's default is commit even on a thrown exception. Do not parallelize work inside one ambient transaction. Independent flows use last-commit-wins, not serializable isolation. Coordinate reset and shutdown with writers. See [transactions](transactions.md).

## A bounded task for a coding agent

Example: add a new reward kind. Give the agent the domain key, the strategy interface, the existing strategy examples and the expected tests. It should add the key, implementation and focused tests, not edit an unrelated dispatcher or create another service registry. For a new save field, give the agent the existing record schema, migration policy and recovery test cases. It should not replace the store or silently reset existing saves.

Review the resulting changes for explicit dependencies, correct lifetimes, missing/invalid data handling and runtime backend assumptions. The [AGENTS.md](../AGENTS.md) rules are intended to be copied into consuming projects and adapted to their domain. They are conventions, not a guarantee of generated-code correctness.
