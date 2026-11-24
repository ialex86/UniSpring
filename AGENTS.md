# AGENTS.md — UniSpring architecture guide

These instructions apply to work on this repository. Projects consuming UniSpring can copy the application rules into their own agent instructions and adapt them to their domain.

## Application architecture

- Prefer plain C# services with constructor injection. Use `[Service]`, `[Component]` and `[Strategy]` for discoverable application beans.
- Prefer explicit interfaces at module boundaries and independent strategies for genuinely extensible, domain-keyed behavior. Use `StrategyRegistry<TKey, TStrategy>` instead of growing type-dispatch switches when the cases have independent behavior. A small, stable conditional is fine; do not create abstractions solely to remove it.
- Keep MonoBehaviours as thin Unity adapters. In `AutowiredMonoBehaviour` subclasses never hide `Awake`; use `OnDependenciesInjected` instead. Unity-owned objects are not ordinary beans.
- Do not use `RuntimeContext.Get` or `ApplicationContext.Get` as a service locator in ordinary business logic. Keep manual resolution in composition roots or infrastructure adapters.
- Keep dependencies explicit; avoid global mutable singletons and cross-module references to internal implementations. For projects using the API/runtime assembly convention, depend on other modules' `*.Api` assemblies and explicitly enable strict boundary checks.
- Respect Application, Scene and Transient lifetimes. Application beans cannot depend on scene beans. Transient consumers and external instances own their disposal. Registry entries are cached instances, not factories; prefer stateless, non-disposable strategies and register external strategies before resolution.
- Inject collections only when all matching implementations are valid in the consumer's scope. Do not use `[Primary]` to filter collections.
- Use synchronous, parameterless `void` methods for `[PostConstruct]`. Keep AOP interceptors independent of advised beans/registries during bootstrap.
- On Mono, advised calls must go through interfaces; self-invocation is not intercepted. For IL2CPP, avoid runtime advice and use explicit `ITransactionManager.Run` / `RunAsync`. The default transaction policy commits on exceptions; request rollback only if the store supports it. Never use `async void` for transaction work.
- Keep container calls on their owning thread. Async transaction completion may occur away from Unity's main thread.

## Data, events and lifecycle rules

- Use `[Repository]` / `IRepository<T>` for collections of saved objects and `IDocument<T>` for singleton saves. Use `[Catalog]` for shipped immutable content; use `[ConfigurationProperties]` for typed settings. Do not invent a new save manager, resource loader or event bus when the existing contracts fit.
- Install one store and serializer at the composition root. Keep domain models free of persistence details where useful; map to serializer-friendly records. Do not register competing managers, serializers or mappers.
- Use `Read()` for critical state, initialize only for `Missing`, and never silently overwrite broken data. The damaged-save guard is per interface and session; explicit deletion is a recovery decision, not an automatic fallback.
- Use versioned, replay-safe migrations. Test migration twice against actual serialized records and preserve unknown formats. Do not assume deserialization into an old DTO proves the old schema.
- Use explicit rollback-on-error boundaries for multi-document changes. No parallel child tasks inside an ambient transaction. Independent writers use last-commit-wins, not isolation. Do not assume rollback reverses in-memory or Unity side effects.
- Use application events for independent reactions, not as a substitute for direct calls when a dependency or return value is required. Use `[AfterCommit]` for ordinary post-commit reactions, not as a durable delivery guarantee. A transaction dropped by a save reset throws on commit; handle that as a cancelled use case.
- Use Application beans for automatic lifecycle/listeners/ticks; Scene objects own disposable Subscribe/Add tokens. Infrastructure callbacks are called through AOP proxies like any other call. A failed Start must clean up its own partial work.
- Use `[Profile]` and explicit context options for environment differences. Do not bypass strict collection scope or module boundary rules.
- Keep Unity API calls on the main thread. Events run on the publisher/completion thread; marshal UI, audio and scene work when needed. Keep full scans, serialization-heavy work and disk waits out of frame ticks.
- File writes are asynchronous during normal operation. Check `WaitForDisk`, `LostChanges` and logs at persistence checkpoints; do not promise durability on logical commit. Coordinate reset, shutdown and active work.
- `[Repository]`, `[Catalog]` and runtime advice require Mono. Do not propose IL2CPP support based on EditMode tests or generated link.xml alone; use ordinary explicit implementations and verify the target build.
- Read [architecture](Documentation~/architecture.md), [shop walkthrough](Documentation~/getting-started.md), [testing](Documentation~/testing.md) and the subsystem contract before introducing a new pattern. Test plain domain behavior, actual repository integration and container/Unity integration separately.

## Working on UniSpring itself

- Read the relevant guide and implementation before changing API behavior. Do not silently change lifecycle, ownership, selection, proxy or transaction semantics.
- Keep changes local to the relevant subsystem. Prefer adding a focused implementation or test over changing unrelated infrastructure.
- Preserve the public API unless the task explicitly requests a breaking change. Update documentation when observable behavior changes.
- Add focused NUnit EditMode regression tests for changes and failure paths. Test business logic with ordinary objects/fakes; test container integration separately.
- Run `Tests~/run-tests.sh /path/to/Unity` when a Unity installation is available. State exactly which tests and player builds ran; do not claim IL2CPP verification without a real build.
- Keep the package dependency-free unless a dependency is explicitly approved. Do not add generated binaries, local Unity caches, credentials or test artifacts.
