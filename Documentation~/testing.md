# Testing and verification

[Documentation index](index.md) · [Shop walkthrough](getting-started.md)

A passing EditMode suite is useful evidence, not evidence that a Unity player build or a disk failure path works. The package's EditMode suite runs on Unity 6000.6.6f1. Play Mode shutdown and IL2CPP player behavior are not covered by it; the macOS IL2CPP smoke test covers the latter for one target. The shop walkthrough's code is the Persistent Shop sample, which the package's CI compiles and tests.

## Test at the appropriate boundary

| Layer | Setup | What to prove |
| --- | --- | --- |
| Domain unit | Construct plain C# objects and fakes | Rules, branches, invariants, missing data |
| Strategy unit | `new StrategyRegistry<...>(implementations)` | Correct routing, duplicate/missing keys |
| Data integration | `DocumentData.InMemory(serializer).With(...)` | Actual mapping, query signatures, validation, rollback |
| Container integration | Explicit component types / assemblies, `using var context` | Discovery, primary, profiles, scopes, proxies, lifecycle |
| Filesystem integration | Unique temporary save root, real file store | Flush, reopen, recovery, fault injection, cleanup |
| Play Mode | Real Unity scene / PlayerLoop | Awake, scene scopes, ticks, shutdown, domain reload disabled |
| Player build | Real target and stripping configuration | AOT, resource assets, threads, disk support, linker behavior |

A service built with `new` has no advice; use `data.Transactions.Run(..., rollbackOnError: true)` to test storage behavior, and an injected interface separately to test interception. An event publisher fake records calls, not actual `[AfterCommit]` delivery. Verify the real event publisher and transaction manager together in an integration test.

## Regression scenarios

- Reject ambiguous/missing dependencies and conflicting keys. Verify strict lifetime collections and cleanup of started beans after a later Start fails. Test Stop order and disposal order.
- Use deterministic barriers for subscribe/unsubscribe racing publication, writer/clear and concurrent shutdown. Avoid sleep-based assertions. Test early event delivery, callback cleanup after failed commit/dispose, nested rollback and direct Begin semantics.
- With the file store, force failures at temporary file write, journal publication, journal read, replay and cleanup. Reopen the same root and verify every document; do not equate a successfully opened store with a consistent transaction. Test `WaitForDisk` false and `LostChanges` on failure.
- Test strict `Read` for Missing, Found, Damaged, Unreadable and Invalid. Never delete a player's save as a test workaround. Test fresh versus stale migration metadata, and replay migrations twice against serialized data.
- Verify catalog loading at first resolution, validation, duplicate mapper configuration and settings defaults/required values. Use a test `IResourceSource` rather than importing game assets for every unit test.
- In Play Mode run at least two sessions with domain reload enabled and disabled. Verify one tick per expected phase, subscriptions disposed, scene scope lifetime, no duplicate runner after reinstall, and store shutdown. Verify fixed ticks using actual physics cadence, not a frame-count assumption.

Use a fresh test root per test. When fault injection is needed, use the package's test seams or controlled filesystem fixtures. Do not run destructive tests against a real save directory. Ensure all threads and contexts are disposed in cleanup even when assertions fail.

## Running the package tests

From the package repository:

```sh
Tests~/run-tests.sh /path/to/Unity
```

The script uses a temporary host project and writes `TestResults/editmode.xml`. In a consuming project, use Unity Test Runner for its own EditMode and PlayMode assemblies. Do not assume the package script runs PlayMode or builds a player. Report the commit, Unity version, target, scripting backend, stripping level, exact test command and pass/fail/skip counts separately.

The [IL2CPP guide](il2cpp.md) lists proxy restrictions. EditMode tests compile and run the generated proxies in the editor, and `Tests~/il2cpp-smoke.sh` builds and runs a macOS IL2CPP player of the shop sample; other targets still need their own build.
