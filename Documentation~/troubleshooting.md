# Troubleshooting

[Documentation index](index.md)

Start with the full exception including inner exceptions and the DI resolution path. Confirm the Unity version, pinned package revision, backend, active profiles and assembly references. Avoid catching startup errors and continuing with partially initialized game state.

| Symptom | Check first |
| --- | --- |
| Bean not found | Eligible loaded asmdef references DI, concrete class has a bean attribute, active profile matches |
| Multiple candidates | Select a single `[Primary]` or inject the collection intentionally; explicit instance overrides do not remove scanned beans from warm-up |
| Scene lifetime error | Application dependency/collection includes a Scene bean; separate contracts or move the consumer to Scene |
| Circular resolution | Inspect path and redesign the dependency; do not hide it behind `RuntimeContext.Get` |
| Null injected MonoBehaviour field | Inherit AutowiredMonoBehaviour, do not hide Awake, use OnDependenciesInjected |
| Strategy key missing/duplicate | Verify each strategy's Keys, profile, assembly and registration before registry construction |
| Transaction not applied | Resolve through an interface, avoid self-invocation, verify backend and manager |
| Unexpected commit after throw | Set RollbackOnError or rollbackOnError: true; default commits |
| Outer commit throws after handled inner error | Joined inner rollback made the transaction rollback-only |
| Event received after logical commit but not yet on disk | Background writer is separate; inspect WaitForDisk and LostChanges |
| Scene listener still called / tick duplicated | Dispose subscription/Add tokens when the owner goes |
| No ticks | Verify Application lifetime or manual registration, PlayerLoop installation and Play Mode; check other packages replacing the loop |
| Catalog/settings missing | Verify Resources-relative path, TextAsset import, required flag, serializer and validator |
| Repository query rejected | Exact member names, equality parameters in order and supported return type; inspect first-resolution exception |
| Save appears absent | Use strict Read to distinguish Missing from broken; never automatically delete or replace damaged data |
| Disk not caught up | Check WaitForDisk return, LostChanges, free disk space, permissions, writer logs and root path |
| Migration runs again | Metadata backup may be older; verify idempotence and version discriminator; preserve original data |
| Shutdown hangs | Check pending writes and blocked filesystem operations; timeout is not an absolute deadline |
| AOT proxy error | Repository/Catalog/Transactional need Mono; use explicit implementations and transactions, then verify player build |

## Safe diagnostic workflow for save problems

Stop game writers, retain the entire save directory including backups and journals, and copy it for diagnosis. Record `LostChanges` and the writer/recovery log. Never delete a journal or overwrite a damaged document merely to make startup pass. A successful logical transaction is not proof of disk durability, and an absent `Load()` result is not proof of missing data. Use a separate test root to reproduce failures, and require an explicit recovery or reset decision before replacing original data.

## When reporting an issue

Provide the exact commit and Unity version/backend, minimal assembly layout and registrations, active profiles, a minimal reproducer or test, full exception and logs, and observed versus expected behavior. For save issues, use synthetic data rather than distributing real player files. For timing issues, describe the operation order and thread/context involved. See [testing](testing.md) for what counts as verified.
