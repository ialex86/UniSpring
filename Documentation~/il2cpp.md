# IL2CPP

IL2CPP is the only scripting backend on iOS, consoles and WebGL, and the usual
choice for Android release builds. It cannot create types at run time, which is
how the editor and Mono make the proxies behind `[Transactional]`, your own
interceptors, `[Repository]` and `[Catalog]`. For IL2CPP the editor writes those
proxies as ordinary C# instead; your code does not change.

**Status:** EditMode tests compile the generated code and run it in the editor.
`Tests~/il2cpp-smoke.sh` builds a macOS IL2CPP player with high stripping and
checks DI, transactions, repositories, a catalog, events, ticks, lifecycle, an
enum-keyed strategy registry and the file store in it. Other targets (iOS,
Android, consoles, WebGL) have not been built yet.

## What you have to do

| Area | Action |
| --- | --- |
| `[Transactional]`, your interceptors, `[Repository]`, `[Catalog]` | Nothing in code. Commit the generated `UniSpring.Generated.cs` files |
| Code stripping | Nothing: `link.xml` is generated and the generated proxies carry `[Preserve]` |
| `logging.properties` on Android / WebGL | Ship it as a `Resources` text asset (below) |
| `FileDocumentStore` | Verify threads, file access, shutdown and recovery on each target; WebGL has no writable file system for it |

## Generated proxies

- **Where.** Every player assembly under `Assets` that needs proxies gets one
  `UniSpring.Generated.cs` next to its asmdef (`Assets/` for
  Assembly-CSharp). Code in packages is not written to; an interface there
  that needs a proxy is reported in the console.
- **When.** After every compilation, while the active build target uses IL2CPP
  (or once the files exist). **Tools > UniSpring > Generate IL2CPP Proxies**
  does it by hand. Commit the files with the code, as you would a generated
  `link.xml`.
- **Only in IL2CPP players.** The file is wrapped in
  `#if ENABLE_IL2CPP && !UNITY_EDITOR`: the editor and Mono builds keep using
  run-time proxies, so the files never break a compilation in the editor.
- **Checked before the build.** An IL2CPP build whose files are missing or out
  of date stops before it starts and says to regenerate. (Writing them during
  the build would recompile the scripts and abort it.)
- **Which interfaces.** Every `[Repository]` and `[Catalog]` interface, and
  every interface of a bean that has a method with an attribute (other than
  the compiler's, the platform's, `[Autowired]` and `[PostConstruct]`): that is
  how advice is declared. An interceptor that matches methods without an
  attribute is not seen; put an attribute on the methods it advises.
- **Not generated** (reported in the console): interfaces not visible from the
  bean's assembly, members with by-reference returns, pointers or `ref struct`
  parameters (`Span<T>`). Resolving a bean through one of them on IL2CPP fails
  at startup with a message naming it.
- **How they work.** Each proxy implements the interface explicitly and passes
  every call, with a delegate that calls the bean directly, to the same advice
  chain and the same repository handlers as on Mono. No reflection call is left
  in the player.

## Generic types made by reflection

The container creates a few generic types by reflection. With reference-type
arguments IL2CPP shares their code; with a value-type argument it needs the
instantiation compiled ahead of time. The generator adds a hint for strategy
registries keyed by a value type (`StrategyRegistry<GearKind, IGearbox>`) that a
bean takes in its constructor or an `[Autowired]` member. Not covered: events of
a value type (`IEventListener<SomeStruct>`) and repositories or catalogs of
value types; use classes for those.

## Explicit transactions still work

`ITransactionManager.Run` / `RunAsync` remain available on every backend; see
[transactions](transactions.md). They are no longer required for IL2CPP.

## Logging configuration

This is about the platform, not IL2CPP itself, but it meets you in the same
builds. On Android and WebGL `StreamingAssets` is not a folder on disk, so
`StreamingAssets/logging.properties` is not read. Put the file at
`Assets/Resources/logging.properties.txt` instead. A file placed in
`persistentDataPath` on the device still overrides it.

## Checklist for the first IL2CPP build

1. Switch the target to IL2CPP and let the editor compile: the console reports
   interfaces that got no proxy. Commit the `UniSpring.Generated.cs` files.
2. Build with **Managed Stripping Level: High** and run on the device.
3. The first scene starts without a "UniSpring" exception in the log: the
   container warmed up, every bean was created, every proxy was found.
4. A `GameLog.Debug` line from a source you enabled in `logging.properties`
   appears: the configuration was found.
5. One transactional use case and one repository read and write work.

To check the package itself on a Mac with the IL2CPP module:
`Tests~/il2cpp-smoke.sh [/path/to/Unity]`.
