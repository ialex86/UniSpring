# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project follows [Semantic Versioning](https://semver.org/).

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
