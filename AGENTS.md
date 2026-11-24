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

## Working on UniSpring itself

- Read the relevant guide and implementation before changing API behavior. Do not silently change lifecycle, ownership, selection, proxy or transaction semantics.
- Keep changes local to the relevant subsystem. Prefer adding a focused implementation or test over changing unrelated infrastructure.
- Preserve the public API unless the task explicitly requests a breaking change. Update documentation when observable behavior changes.
- Add focused NUnit EditMode regression tests for changes and failure paths. Test business logic with ordinary objects/fakes; test container integration separately.
- Run `Tests~/run-tests.sh /path/to/Unity` when a Unity installation is available. State exactly which tests and player builds ran; do not claim IL2CPP verification without a real build.
- Keep the package dependency-free unless a dependency is explicitly approved. Do not add generated binaries, local Unity caches, credentials or test artifacts.
