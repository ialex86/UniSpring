# Basic Setup

1. Import this sample from **Window > Package Manager > UniSpring > Samples**.
2. Copy `logging.properties` to `Assets/Settings/logging.properties` (create the
   folder if needed).
3. Create an empty GameObject in a scene and add the `BasicSetup` component.
4. Press Play.

Expected Console entry:

```text
[INFO][BasicSetup] Hello, UniSpring!
```

`GreetingService` is registered with `[Service]`, resolved into the
`AutowiredMonoBehaviour` field, and `GameLog.Info` writes through the Unity sink.

## Optional: dotted source names

By default the source is the file name (`BasicSetup`). To get module paths, call
this once at startup:

```csharp
using UniSpring.Logging;
using UniSpring.Logging.Domain;
using UnityEngine;

public static class SampleLoggingSetup
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Configure()
    {
        GameLog.ConfigureSource(new GameLogSourceOptions
        {
            SourceRoot = "/Assets/",
            IgnoredSegments = new[] { "Scripts" }
        });
    }
}
```
