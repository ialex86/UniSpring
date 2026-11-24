# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- Events: the listener route cache is thread-safe; events published while the
  container builds are delivered in order, and `[AfterCommit]` listeners get
  them only if their transaction commits; advised listeners are called
  through their proxies.
- Transactions: a commit that throws drops the after-commit work; a dispose
  that throws still ends the boundary; tasks that outlive a transaction no
  longer register work into it.
- A transaction dropped by `DeleteAll` throws on `Commit` (file and in-memory
  stores).
- File store: journals are published atomically; a journal the disk cannot
  read is kept, a damaged one is renamed to `.corrupt`; opening the store on a
  disk that refuses to complete a journal no longer throws; closing drains on
  one thread within one overall timeout; a failure of an earlier journal no
  longer marks later steps as saved; `HasDocuments` no longer misses a change
  landing while it looks.
- Lifecycle: transient beans are not created to be started; disposing the
  container while it disposes does nothing more.
- Ticks: removing a registration never removes a bean or another equal object.
- Strategy registries: one built for an interceptor's dependencies (before AOP
  is ready) is not cached, so later consumers get proxied strategies; the
  scope that builds a registry owns and disposes its transient strategies.
- Catalogs: `All` and `Ids` are read-only; two mappers for one type are an
  error; default `ScriptableObject` settings are destroyed with the container.

## [0.3.0] - 2026-11-24

### Added

- `ILifecycle`: start and stop hooks for application beans; the container can
  be injected as `ApplicationContext`.
- `IEventPublisher` and `IEventListener<T>`: application events between beans,
  with subscriptions for objects the container does not own, and
  `[AfterCommit]` / `TransactionSynchronization.AfterCommit` for delivery after
  the transaction commits.
- `ITickable`, `ILateTickable`, `IFixedTickable` and `ITickRegistry`: beans
  ticked by Unity's player loop in bean order, without MonoBehaviour hosts.
- `[Profile]` and `ContextOptions`: beans chosen by active profiles
  (`Editor`/`Player`, `Development`, `UNISPRING_PROFILES`).
- `[Catalog]` interfaces over `ICatalog<T>`: read-only game data from
  `Resources`, checked at start; `[ConfigurationProperties]` settings classes
  from text assets or ScriptableObjects; `IResourceSource`.
- Strict reads: `IDocument<T>.Read()` and `IRepository<T>.Read(id)` return
  `LoadResult<T>` (Missing, Found, Damaged, Unreadable, Invalid); a save over a
  document found broken throws `DocumentDamagedException`.
- `FileDocumentStore.LostChanges`.

### Changed

- A rollback inside a joined transaction marks the whole transaction
  rollback-only: the outermost commit saves nothing and throws.
- `ExistsBy` and single-result `FindBy` stop at the first match.

### Fixed

- A retry after a refused journaled step could rewrite or delete the committed
  journal; committed journals are now completed before anything new is
  prepared, under step ids that are never reused.
- `WaitForDisk` and `IsIdle` reported success after changes were left out on
  close.
- A new-game clear could delete a document written by another thread after the
  clear began.
- A migration record read from its backup now logs that migrations may run
  again.

## [0.2.0] - 2026-11-24

### Added

- `UniSpring.Data`: Spring Data-style repositories for game saves. `[Repository]`
  interfaces deriving from `IRepository<T>` or `IDocument<T>` are implemented by
  the container, with query methods by name (`FindBy`, `CountBy`, `ExistsBy`,
  `DeleteBy`). Pluggable `IDocumentSerializer`, `IRecordMapper<TDomain, TRecord>`,
  `IDocumentValidator<T>`, numbered `IMigration`s with a separate migration
  record, the `IDocumentStore` port, `InMemoryDocumentStore` and `DocumentData`
  for tests. Repositories need Mono.
- `FileDocumentStore`: documents in files, written on a background thread,
  with atomic replaces, backups and checksums, journaled transactions with
  crash recovery, ordered retries on a refusing disk and synchronous writes
  after `Close`, which saves what the disk takes when one step keeps failing.
  `FileDocumentStore.Install` registers it in a container.
- `IDocumentReset`: clear the saves for a new game (`DeleteAll`,
  `HasDocuments`, `Subscribe`), implemented by both stores.
- `IContractProvider`: a DI extension point for beans made on demand.

## [0.1.0] - 2026-11-24

### Added

- Spring-like dependency injection: `[Service]`, `[Component]`, `[Strategy]`,
  `[Autowired]`, `[Primary]`, `[Order]`, `[PostConstruct]`, lifetimes and scopes.
- `ApplicationContext` and `RuntimeContext` with application and scene scopes.
- `ApplicationContext.Build(IEnumerable<Type>)` for an explicit component list,
  in addition to assembly scanning.
- `AutowiredMonoBehaviour` for Unity components.
- `StrategyRegistry<TKey, TStrategy>` for keyed strategies.
- `UniSpring.Transactions`: `[Transactional]` advice over `ITransactionManager`
  and `ITransaction`, with async support and an optional rollback on error.
- `ScopeInterceptor.Complete` hook, so an interceptor can commit or roll back
  when the advised call ends.
- Optional `ModuleBoundaryOptions` for the `*.Api` module-boundary rule.
- `GameLog` facade with `Debug`, `Info`, `Warn`, `Error`, lazy messages,
  exception entries, per-source levels and `logging.properties` configuration.
- Multiple `ILogSink` implementations with per-source routing through `sinks`
  and `sinks.<source>`, `[LogSink]` metadata and the combined
  `[LogSinkService]` stereotype.
- Sink failure isolation: a throwing sink is reported and skipped without
  breaking the other sinks.
- Unity log sink and editor logging bootstrap.
- `logging.properties` applies from the first log entry, before or without a
  container; lookup in `persistentDataPath`, `StreamingAssets` and a
  `Resources` text asset (for Android and WebGL).
- Generated `link.xml` (`IUnityLinkerProcessor`) that keeps beans and
  autowired types through managed code stripping.
- `RuntimeContext.IsScannedByDefault`: editor and test assemblies are
  recognized by a whole name segment.
- The container and its scene scopes are disposed when the application quits;
  `RuntimeContext.IsShutDown`.
- `ITransactionManager.Run` / `RunAsync`: the transaction advice as a plain
  call, for IL2CPP and code the attribute cannot reach.
- Hardening: a bean whose `[PostConstruct]` fails is disposed; `async void`
  is refused in `[PostConstruct]`, advice and `Run`; a commit or dispose
  failure no longer hides the original exception; `RuntimeContext` refuses
  re-entrant access while it builds; log backends can be removed in any
  order; a warning when an advised bean is handed out before AOP is ready.
- EditMode tests and a `Basic Setup` sample.
- `AGENTS.md` with the architecture conventions for AI-assisted development.
