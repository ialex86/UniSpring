# Logging

## Facade

```csharp
using UniSpring.Logging;

GameLog.Info("Application started.");
GameLog.Warn("Optional config is missing.", unityContext);
GameLog.Error("Save failed.", exception, unityContext);
GameLog.Debug(() => BuildExpensiveMessage());
```

The source comes from `[CallerFilePath]`. Never pass the `caller` argument
yourself; per-source levels would stop working. The compiler stores the full
source path of every call site in the build; pass `-pathmap` to the compiler
(a `csc.rsp` file) if build-machine paths must not appear in a player.

Use the message + exception overload for handled failures. It writes one console
entry and keeps the original stack trace.

When a message is expensive to build and not lazy, ask first:

```csharp
if (GameLog.IsDebugEnabled())
    GameLog.Debug(BuildDump());
```

## Levels

`Debug`, `Info`, `Warn`, `Error`, `None`.

```properties
root = Info
MyGame = Debug
MyGame.Vehicle = Warn
MyGame.Vehicle.Engine = Error
```

Lookup order: full source, file name, each enclosing dotted scope, then root.
A longer prefix wins.

## Configuration file

- Environment variable `UNISPRING_LOG_CONFIG` points at an explicit file.
- Editor: `Assets/Settings/logging.properties`.
- Player: `persistentDataPath/logging.properties` when it exists, then
  `StreamingAssets/logging.properties`. A file put into `persistentDataPath`
  on a device overrides the shipped one without a rebuild.
- When no file is found: the `Resources` text asset `logging.properties`
  (`Assets/Resources/logging.properties.txt`).
- A missing configuration is optional in a player and a warning in the editor.

On Android and WebGL StreamingAssets is not a folder on disk (it is inside the
APK or behind HTTP) and is not read; ship the config through `Resources`
there.

Override the paths, the resource name and the environment variable through
`LoggingPathOptions`.

The configuration is read when the game starts, on the main thread, with the
default `LoggingPathOptions` and the Unity sink; it applies from the first log
entry even if no container is ever built. When a container is built, its
logger takes over with the registered `LoggingPathOptions` and sink beans.

## Sinks

`ILogSink` is the output extension point. Every registered implementation is
active by default; the container injects all of them into `GameLogService`.

```csharp
[LogSinkService("file", Default = false)]
public sealed class FileLogSink : ILogSink
{
    public void Write(LogEntry entry) { /* ... */ }
}
```

`[LogSinkService]` is a DI service plus sink metadata in one attribute;
`[Service]` plus `[LogSink("file")]` is equivalent. `[LogSink]` alone does not
register a bean: it only names a sink that you create and pass to
`GameLogService` yourself.

- `[LogSink("name")]` / `[LogSinkService("name")]` sets the stable name used in
  configuration. The type name is the default.
- `[LogSink(Default = false)]` / `[LogSinkService(Default = false)]` keeps a
  heavy sink (network, files) out of the default set. It is used only when
  selected explicitly or with `all`/`*`.
- Sinks are ordinary beans: constructor injection works.

Narrow the set globally or per source:

```properties
# every sink with Default = true (the default when the key is absent)
# sinks = *
# or
sinks = UnityLogSink, FileLogSink
# all except one
# sinks = all, -FileLogSink
# silence a noisy source
sinks.Noisy.Service = none
# route one source to one sink
sinks.MyGame.Audit = FileLogSink
```

Lookup order is the same as for levels: full source, file name, enclosing
scope, then the global `sinks` value.

Rules:

- Sink names are case-insensitive; the short and full type name are accepted
  as aliases.
- An unknown name is reported and ignored. If no positive name remains, the
  global selection is used.
- `none` silences the source and must not be combined with other names.
- Output order is the order in the configuration, then `[Order]`, then name.
- There is no per-sink level: a sink that needs only errors filters in its own
  `Write`.
- One instance per sink type; there are no named instances.

A failing sink never breaks logging: the exception is reported to the Unity
console, the sink is skipped for that entry, and it is disabled after three
consecutive failures.

Business code never selects a sink. It calls `GameLog.Debug/Info/Warn/Error`;
the source comes from `[CallerFilePath]` and the configuration decides where
the entry goes.

The editor bootstrap registers the built-in Unity sink; edit-mode tools use
the same routing rules for that single sink.

## Source names

By default the source is the caller's file name. To get dotted module paths,
configure a source root:

```csharp
GameLog.ConfigureSource(new GameLogSourceOptions
{
    SourceRoot = "/Assets/Src/",
    IgnoredSegments = new[] { "Scripts", "Internal" }
});
```

A file `Assets/Src/Scripts/Inventory/Internal/Application/LocalInventoryApi.cs`
becomes `Inventory.Application.LocalInventoryApi`.

## Backend lifecycle

The container installs `GameLogService` as the active backend for its lifetime.
Disposing the container removes its backend; the newest backend that is still
installed stays active, in whatever order containers are disposed.

To replace the backend yourself:

```csharp
var previous = GameLog.InstallBackend(myLogger);
// ...
GameLog.RestoreBackend(myLogger, previous);
```

`GameLog.Reload()` re-reads the configuration.

## Editor

`UniSpring.Logging.Editor` installs a backend for edit-mode tools when no
runtime container is active. A running container's backend always wins and is
restored when the container is disposed.
