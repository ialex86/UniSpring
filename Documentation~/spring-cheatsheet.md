# Spring to UniSpring

[Documentation index](index.md)

UniSpring borrows application composition conventions from Spring, not its entire ecosystem or exact semantics. C# attributes use brackets, and Unity manages scene object lifetimes.

| Spring | UniSpring | Important difference |
| --- | --- | --- |
| `@Component`, `@Service` | `[Component]`, `[Service]` | Scans eligible loaded assemblies, not Java packages |
| Constructor injection | Constructor injection | One public constructor works without an attribute |
| `@Autowired`, `@Primary` | `[Autowired]`, `[Primary]` | Primary affects single resolution, not collections |
| `List<T>` injection | `IEnumerable<T>`, `IReadOnlyList<T>` | Strict lifetime compatibility; mutable lists not populated |
| `@PostConstruct` | `[PostConstruct]` | Synchronous void, no arguments |
| Singleton, prototype | Application, Transient | Transient disposal is the consumer's job |
| Context scopes | Application, Scene, child scopes | Unity scene integration and path-based joins |
| Lifecycle | `ILifecycle` | Application start after warm-up, stop in reverse order |
| `@Profile` | `[Profile]` | Selected at context build |
| `ApplicationEventPublisher` | `IEventPublisher` | Synchronous on publishing thread |
| `@EventListener` | `IEventListener<T>` | Application listener beans; scene subscribers own tokens |
| `@TransactionalEventListener` | `[AfterCommit]` | Immediate outside recognized boundaries; no disk acknowledgement |
| `@Transactional` | `[Transactional]` | Mono, interface proxy, commits on exception by default |
| `TransactionTemplate` (conceptually) | `ITransactionManager.Run` / `RunAsync` | Explicit boundaries, no runtime advice needed |
| Spring Data repositories | `[Repository]`, `IRepository<T>`, `IDocument<T>` | Mono-only proxies, in-memory equality scans, no SQL/ORM |
| `@ConfigurationProperties` | `[ConfigurationProperties]` | Binds a Unity resource rather than a property-source chain |
| Flyway / Liquibase | `[Migration]`, `IMigration` | Document rewrites; must be replay-safe |
| Bean Validation (conceptually) | `IDocumentValidator<T>` | Explicit validators, not Jakarta constraint annotations |
| Spring Modulith (partly) | `ModuleBoundaryOptions` | API-assembly resolution checks, not full Modulith |
| SLF4J / Logback (conceptually) | `GameLog` | Caller-file source and configurable sinks |
| No direct equivalent | `StrategyRegistry<TKey,TStrategy>` | Discover keyed implementations and inject the registry |
| No direct equivalent | `[Catalog]` | Read-only shipped game content |
| No direct equivalent | `ITickable`, `ILateTickable`, `IFixedTickable` | Unity PlayerLoop integration |

## Common surprises

- Unity creates MonoBehaviours. Inject them through `AutowiredMonoBehaviour` and use `OnDependenciesInjected`, not a normal constructor or a hidden Awake.
- A transaction annotation on a concrete/self-invoked method is not intercepted. Runtime proxies (including repository and catalog proxies) require Mono. Explicit transactions only replace advice, not generated repositories.
- Commit-on-error is the default; opt into rollback for multi-document changes. A nested rollback marks the bundled store's root transaction rollback-only.
- The file writer runs after logical commit. A deferred listener is not proof of disk persistence, and async completion may be off the main thread.
- Use `Read()` rather than treating an absent result from `Load()`/`Find()` as proof of a new save. Do not overwrite damaged player data.
- Profiles, scope selection and module boundaries are not identical to their Spring counterparts. Verify the [DI](dependency-injection.md), [events](lifecycle-events-ticks.md), [Data](data.md) and [transactions](transactions.md) guides.

Start with the [shop walkthrough](getting-started.md) to see the patterns combined rather than learning each attribute separately.
