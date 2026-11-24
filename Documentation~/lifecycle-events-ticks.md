# Lifecycle, events and ticks

Three container services that replace hand-written plumbing: start and stop
hooks, events between modules, and the frame loop. They do not require runtime method proxies; IL2CPP compatibility and PlayerLoop installation across Play sessions are not yet verified.

## Lifecycle

A bean that implements `ILifecycle` is started once every application bean
exists, and stopped before the container disposes its beans:

```csharp
[Service]
[Order(10)]
internal sealed class Radio : IRadio, ILifecycle
{
    public void Start() { /* subscribe, open a stream */ }
    public void Stop() { /* undo it */ }
}
```

- Start runs in bean order (`[Order]`, then type name); Stop in reverse.
- A Start that throws fails the build; only successfully started beans are stopped. Clean up partial resources inside a failing Start.
- A Stop that throws does not stop the others.
- Application beans and registered instances take part. A Scene bean does not;
  use `IDisposable` for its end. A transient bean does not either: it belongs to
  whoever asked for it.
- Start is not a transaction: when a Start throws, the beans started before it
  are stopped, but what they did (events published, files written) stays.
- Lifecycle beans, listeners and tickables are called through the same AOP
  proxies as any other caller: `[Transactional]` and other advice apply.
- Disposing the container from a bean that is being disposed does nothing more.

The container itself can be injected (`ApplicationContext` in a constructor),
for infrastructure that collects beans by role. Business code should take its
dependencies directly instead.

## Events

`IEventPublisher` delivers application events, as Spring's
`ApplicationEventPublisher`. An event is any object:

```csharp
public sealed class LoanTaken
{
    public long Principal;
}

[Service]
internal sealed class LoanService : ILoans
{
    private readonly IEventPublisher m_Events;

    public LoanService(IEventPublisher events) { m_Events = events; }

    public void Take(long principal)
    {
        // ...
        m_Events.Publish(new LoanTaken { Principal = principal });
    }
}

[Component]
internal sealed class LoanFeed : IEventListener<LoanTaken>
{
    public void On(LoanTaken loan) { /* add a line to the feed */ }
}
```

- Delivery is synchronous, on the publisher's thread, to every
  `IEventListener<T>` bean whose `T` the event is assignable to (a listener of
  a base class receives derived events), in bean order, and then to the
  subscribers.
- A listener that throws is logged; the others still run.
- Events published while the container builds (from a constructor or
  `[PostConstruct]`) are kept and delivered when it starts, in the order they
  were published; an event published while they are delivered comes after them.
  An `[AfterCommit]` listener gets such an event only if the transaction it was
  published in commits.
- Listener beans are application beans. A Scene bean, a MonoBehaviour or a
  tool subscribes instead and disposes the subscription when it goes:

```csharp
private IDisposable m_Subscription;

protected override void OnDependenciesInjected()
    => m_Subscription = m_Events.Subscribe<LoanTaken>(loan => Refresh());

private void OnDestroy() => m_Subscription?.Dispose();
```

### After the commit

Mark an application listener `[AfterCommit]` (from `UniSpring.Transactions`) to defer it until the logical transaction boundary succeeds; normal rollback is intended to discard deferred delivery. Outside a recognized boundary the listener runs immediately, unlike Spring's default transactional listener fallback policy. With FileDocumentStore, logical commit only makes changes visible and queues disk writes. It is not proof of disk persistence or a reliable external event outbox.

Keep the work of one transaction sequential, and do not make an after-commit callback the only record of an irreversible side effect that must complete. See [transactions](transactions.md) and [testing](testing.md).

```csharp
[Component]
[AfterCommit]
internal sealed class PurchaseSound : IEventListener<CarBought>
{
    public void On(CarBought evt) { /* play the till */ }
}
```

The transaction is the one of the package's boundaries in the calling flow:
`[Transactional]`, `ITransactionManager.Run` and `RunAsync`. A transaction
begun with `ITransactionManager.Begin` directly is not seen.
`TransactionSynchronization.AfterCommit(action)` holds any other work the same
way.

Delivery and deferred callbacks use the executing thread, not a Unity main-thread dispatcher. For UI, audio or scene changes, dispatch explicitly to the Unity main thread if the publisher or completion path may be asynchronous. Keep subscription tokens and dispose them when the subscriber goes.

A commit that throws counts as a rollback: the held work is dropped. Tasks
started inside a transaction share it; work they register after the
transaction has ended runs at once.

## Ticks

A bean that implements `ITickable`, `ILateTickable` or `IFixedTickable` runs
every frame without a MonoBehaviour host:

```csharp
[Service]
[Order(20)] // after the races (Order 10)
internal sealed class GangService : IGangs, ITickable
{
    public void Tick(float deltaTime) { /* one frame of the gangs */ }
}
```

| Interface | When | Delta |
| --- | --- | --- |
| `ITickable` | end of Update | `Time.deltaTime` (0 while time scale is 0) |
| `ILateTickable` | end of PreLateUpdate | `Time.deltaTime` |
| `IFixedTickable` | end of FixedUpdate | `Time.fixedDeltaTime` |

- The container adds its entries to Unity's player loop while the game plays
  and starts ticking when it starts; it stops before the beans are disposed.
- Beans tick in bean order; set `[Order]` when one must run after another.
- A tickable that throws is logged; the others still run.
- Application beans are added on their own. A Scene bean or another object
  adds itself through `ITickRegistry.Add(tickable)` and disposes the result
  when it goes; it ticks after the beans. Each registration removes only
  itself (by reference, not `Equals`), and never a bean; an object is ticked
  once however often it is added. Use ticks for frame-level orchestration, not
  large synchronous repository queries.
- The entries are inserted into the current player loop at the end of each
  phase. A tool that calls `PlayerLoop.SetPlayerLoop` with a loop built before
  the container started removes them; build such a loop from
  `PlayerLoop.GetCurrentPlayerLoop()` after the start.

## Profiles

`[Profile]` registers a bean only while one of its profiles is active, as
Spring's `@Profile`; `!Name` matches when the profile is not active:

```csharp
[Component]
[Profile("Editor")]
internal sealed class EditorCheats : ICheats { }

[Component]
[Profile("!Editor")]
internal sealed class NoCheats : ICheats { }
```

The default profiles are `Editor` or `Player`, plus `Development` in a
development build, plus the comma-separated names of the `UNISPRING_PROFILES`
environment variable (for example `Test` on a test runner). Choose them in code
with `RuntimeContext.Profiles = new HashSet<string> { ... }` before the first
access, or with `ContextOptions.Profiles` when you build a container yourself.
`ApplicationContext.Profiles` tells which were used.
