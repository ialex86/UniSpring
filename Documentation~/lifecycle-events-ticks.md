# Lifecycle, events and ticks

Three container services that replace hand-written plumbing: start and stop
hooks, events between modules, and the frame loop. All of them work on Mono and
IL2CPP: no run-time proxies are involved.

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
- A Start that throws fails the build; the beans already started are stopped.
- A Stop that throws does not stop the others.
- Application beans and registered instances take part. A Scene bean does not;
  use `IDisposable` for its end.

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
  `[PostConstruct]`) are kept and delivered when it starts.
- Listener beans are application beans. A Scene bean, a MonoBehaviour or a
  tool subscribes instead and disposes the subscription when it goes:

```csharp
private IDisposable m_Subscription;

protected override void OnDependenciesInjected()
    => m_Subscription = m_Events.Subscribe<LoanTaken>(loan => Refresh());

private void OnDestroy() => m_Subscription?.Dispose();
```

### After the commit

A listener marked `[AfterCommit]` (from `UniSpring.Transactions`) receives an
event published inside a transaction only after that transaction commits, as
Spring's `@TransactionalEventListener`; on a rollback the event is dropped.
Outside a transaction it is delivered at once. Use it for anything that should
only react to saved state: a feed line, a sound, an achievement.

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
  when it goes; it ticks after the beans.

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
