# Persistent shop sample

The complete code of the [shop walkthrough](https://github.com/ialex86/UniSpring/blob/main/Documentation~/getting-started.md):
a catalog of items, a wallet document, an inventory repository, a transactional
purchase, an after-commit listener, a Unity adapter and EditMode tests.

- Requires Mono (the repository, catalog and `[Transactional]` proxies).
- `Runtime/ShopSetup.cs` installs a `FileDocumentStore` under
  `Application.persistentDataPath/UniSpringShopTutorial` with
  `RuntimeContext.Configure`. A game that already has a configure hook or its
  own `IDocumentSerializer` should merge this into its own setup instead of
  importing both.
- Add `ShopDemo` to an active GameObject in any scene and enter Play Mode:
  the wallet starts at 100, **Buy sword** takes it to 75, a second purchase
  reports "Already owned", **Check disk** confirms the save reached the disk.
- `Tests/` holds EditMode tests (`Shop.Tests`); they need the Unity Test
  Framework.

The package's CI compiles this sample and runs its tests on every change.
