using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Global DI entry point. Runtime access is restricted to the Unity main thread.
    ///
    /// The root (Application) container lives for the whole play session. Each
    /// loaded scene has a Scene scope under it (<see cref="ScopeOf"/>): scene
    /// components are autowired from it, Scene beans live in it, and it is
    /// disposed when the scene unloads. A scene loaded into another one
    /// (<see cref="JoinSceneScope"/>) shares the host's scope; so do the
    /// scenes that are open together when Play starts in the editor.
    /// </summary>
    public static class RuntimeContext
    {
        private static int s_MainThreadId;
        private static ApplicationContext s_Context;
        private static Exception s_BuildFailure;
        private static bool s_Building;
        private static bool s_ShutDown;
        private static SceneScopes<Scene> s_Scenes;
        private static Action<ApplicationContext> s_Configure;
        private static ModuleBoundaryOptions s_BoundaryOptions;
        private static Func<Assembly, bool> s_AssemblyFilter;
        private static ISet<string> s_Profiles;
        // Scene path -> path of the scene whose scope it joins when it wakes.
        private static readonly Dictionary<string, string> s_Joins =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static ApplicationContext Current
        {
            get
            {
                AssertMainThread();
                EnsureInitialized();
                return s_Context;
            }
        }

        /// <summary>
        /// True once the container exists. It lives for the whole play
        /// session, so a scene loaded later finds it already built.
        /// </summary>
        public static bool IsBuilt
        {
            get
            {
                AssertMainThread();
                return s_Context != null;
            }
        }

        /// <summary>
        /// True from the moment the application quits (or Play Mode ends in
        /// the editor) until the next play session starts. The container and
        /// its scene scopes are disposed then, so disposable beans release
        /// files and connections; any later access throws instead of building
        /// a new container. Check this in <c>OnDestroy</c> code that runs
        /// during shutdown.
        /// </summary>
        public static bool IsShutDown => s_ShutDown;

        /// <summary>
        /// Configures the container that <see cref="Current"/> builds on first
        /// access. Call this before the first access, typically from a
        /// <c>[RuntimeInitializeOnLoadMethod]</c> in the application assembly.
        /// </summary>
        /// <param name="configureBeforeWarmUp">
        /// Composition hook: register configuration instances or replace
        /// defaults before any bean is created.
        /// </param>
        /// <param name="boundaryOptions">
        /// Optional module-boundary policy; <c>null</c> keeps it disabled.
        /// </param>
        public static void Configure(
            Action<ApplicationContext> configureBeforeWarmUp,
            ModuleBoundaryOptions boundaryOptions = null)
        {
            s_Configure = configureBeforeWarmUp;
            s_BoundaryOptions = boundaryOptions;
        }

        /// <summary>
        /// Optional predicate that decides which assemblies the container
        /// scans. When set it replaces <see cref="IsScannedByDefault"/>; call
        /// that method from your predicate to keep the default exclusion of
        /// editor and test assemblies.
        /// </summary>
        public static Func<Assembly, bool> AssemblyFilter
        {
            get => s_AssemblyFilter;
            set => s_AssemblyFilter = value;
        }

        /// <summary>
        /// The profiles the container is built with (see <see cref="ProfileAttribute"/>); null, the default, means
        /// <see cref="ContextOptions.DefaultProfiles"/>. Set it before the first access.
        /// </summary>
        public static ISet<string> Profiles
        {
            get => s_Profiles;
            set => s_Profiles = value;
        }

        /// <summary>Drops the configuration set by <see cref="Configure"/> (tests and tooling).</summary>
        public static void ClearConfiguration()
        {
            s_Configure = null;
            s_BoundaryOptions = null;
            s_AssemblyFilter = null;
            s_Profiles = null;
        }

        /// <summary>
        /// The Scene scope of <paramref name="scene"/>, created the first time
        /// it is asked for and disposed when the scene unloads. Scene
        /// components are autowired from it; composition roots may register
        /// scene configuration on it. Without a valid scene (an object outside
        /// any scene) the root container answers.
        /// </summary>
        public static ApplicationContext ScopeOf(Scene scene)
        {
            AssertMainThread();
            if (!scene.IsValid() || s_ShutDown)
                return Current;
            var scenes = Scenes();
            if (scenes.Has(scene))
                return scenes.For(scene);
            return scenes.For(scene, HostOf(scene));
        }

        /// <summary>
        /// The scene at <paramref name="scenePath"/>, when it loads next, joins
        /// the scope of <paramref name="host"/> instead of getting its own: a
        /// part of the host streamed in (a chunk of an open world) sees the host's
        /// Scene beans. Call it before starting the load.
        /// </summary>
        public static void JoinSceneScope(string scenePath, Scene host)
        {
            AssertMainThread();
            if (string.IsNullOrEmpty(scenePath) || !host.IsValid() || string.IsNullOrEmpty(host.path))
                return;
            if (string.Equals(scenePath, host.path, StringComparison.Ordinal))
                return;
            s_Joins[scenePath] = host.path;
        }

        /// <summary>Number of loaded scenes that have a scope (diagnostics and tests).</summary>
        public static int SceneScopeCount => s_Scenes != null ? s_Scenes.Count : 0;

        private static SceneScopes<Scene> Scenes()
        {
            if (s_Scenes != null)
                return s_Scenes;
            s_Scenes = new SceneScopes<Scene>(() => Current.CreateScope(BeanLifetime.Scene));
            return s_Scenes;
        }

        /// <summary>The scene whose scope <paramref name="scene"/> joins: a waiting join, else the scene itself.</summary>
        private static Scene HostOf(Scene scene)
        {
            var path = scene.path ?? string.Empty;
            if (!s_Joins.TryGetValue(path, out var hostPath))
                return scene;
            s_Joins.Remove(path);
            var host = SceneManager.GetSceneByPath(hostPath);
            if (!host.IsValid() || host == scene)
                return scene;
            return host;
        }

        /// <summary>
        /// A Scene bean failing to dispose must not stop the other listeners
        /// of sceneUnloaded: the error is rethrown on the next frame instead
        /// (Unity reports it there with its stack trace).
        /// </summary>
        private static void OnSceneUnloaded(Scene scene)
        {
            try
            {
                s_Scenes?.Unloaded(scene);
            }
            catch (Exception exception)
            {
                var context = SynchronizationContext.Current;
                if (context == null)
                    throw;
                var failure = ExceptionDispatchInfo.Capture(exception);
                context.Post(_ => failure.Throw(), null);
            }
        }

        public static T Get<T>()
        {
            return Current.Get<T>();
        }

        public static IReadOnlyList<T> GetAll<T>()
        {
            return Current.GetAll<T>();
        }

        public static void Autowire(object target)
        {
            Current.Autowire(target);
        }

        public static ApplicationContext CreateScope(BeanLifetime lifetime)
        {
            return Current.CreateScope(lifetime);
        }

        public static StrategyRegistry<TKey, TStrategy> Strategies<TKey, TStrategy>()
            where TStrategy : IKeyedStrategy<TKey>
        {
            return Current.Get<StrategyRegistry<TKey, TStrategy>>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Volatile.Write(
                ref s_MainThreadId,
                Thread.CurrentThread.ManagedThreadId);

            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;

            // While the previous session's beans are disposed, an access from their Dispose must not build a
            // new container.
            s_ShutDown = true;
            try
            {
                DisposeSession();
            }
            finally
            {
                s_ShutDown = false;
            }
        }

        /// <summary>
        /// The application quits, or Play Mode ends in the editor: scene
        /// scopes and the container are disposed, so their disposable beans
        /// are released while the process is still alive.
        /// </summary>
        private static void OnQuitting()
        {
            s_ShutDown = true;
            DisposeSession();
        }

        private static void DisposeSession()
        {
            var scenes = s_Scenes;
            var context = s_Context;
            s_Scenes = null;
            s_Context = null;
            s_BuildFailure = null;
            s_Joins.Clear();
            var failures = new List<Exception>();
            DisposeInto(scenes, failures);
            DisposeInto(context, failures);
            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException("UniSpring DI failed while disposing the play session.", failures);
        }

        private static void DisposeInto(IDisposable disposable, List<Exception> failures)
        {
            try
            {
                disposable?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeBeforeScene()
        {
            // The container is built on first access, not here. Scenes that
            // are open together when Play starts (the editor's multi-scene
            // setup: a world with its chunks) form one group: they share
            // the active scene's scope, as chunks streamed in later do.
            AssertMainThread();
            var active = SceneManager.GetActiveScene();
            if (!active.IsValid() || string.IsNullOrEmpty(active.path))
                return;
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (string.IsNullOrEmpty(scene.path) || string.Equals(scene.path, active.path, StringComparison.Ordinal))
                    continue;
                s_Joins[scene.path] = active.path;
            }
        }

        private static IReadOnlyList<Assembly> GetComponentAssemblies()
        {
            return ComponentAssemblies.Find(s_AssemblyFilter ?? (Func<Assembly, bool>)IsScannedByDefault);
        }

        private static readonly char[] s_NameSeparators = { '.', '-', '_' };

        private static readonly string[] s_ToolingSegments =
        {
            "Editor", "Tests", "Test", "EditorTests", "EditModeTests", "PlayModeTests", "PlayTests"
        };

        /// <summary>
        /// The filter used while <see cref="AssemblyFilter"/> is not set: editor
        /// and test assemblies are left out. They are recognized by a whole
        /// segment of the assembly name (<c>MyGame.Editor</c>,
        /// <c>Assembly-CSharp-Editor</c>, <c>MyGame.Inventory.Tests</c>), so
        /// <c>MyGame.LevelEditor</c> or <c>MyGame.Contests</c> are scanned.
        /// </summary>
        public static bool IsScannedByDefault(Assembly assembly)
        {
            if (assembly == null)
                return false;

            var name = assembly.GetName().Name ?? string.Empty;
            var segments = name.Split(s_NameSeparators, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                for (var tooling = 0; tooling < s_ToolingSegments.Length; tooling++)
                {
                    if (string.Equals(segments[index], s_ToolingSegments[tooling], StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds once. A failed build is remembered: every later access reports
        /// the same first error instead of rebuilding the whole container (and
        /// hiding the cause behind a follow-up failure) on each Awake.
        /// </summary>
        private static void EnsureInitialized()
        {
            if (s_Context != null)
                return;
            if (s_ShutDown)
            {
                throw new InvalidOperationException(
                    "UniSpring DI RuntimeContext has shut down: the application is quitting and the container " +
                    "is disposed. Check RuntimeContext.IsShutDown before using it from OnDestroy or OnDisable.");
            }

            if (s_BuildFailure != null)
            {
                throw new InvalidOperationException(
                    "UniSpring DI container failed to build earlier in this session; " +
                    "the inner exception is the first error.", s_BuildFailure);
            }

            if (s_Building)
            {
                throw new InvalidOperationException(
                    "UniSpring DI RuntimeContext is already building: a bean or the configure hook accessed " +
                    "RuntimeContext during the build. Take the dependency through the constructor instead.");
            }

            s_Building = true;
            try
            {
                s_Context = ApplicationContext.Build(
                    GetComponentAssemblies(),
                    new ContextOptions { Boundaries = s_BoundaryOptions, Configure = s_Configure, Profiles = s_Profiles });
            }
            catch (Exception exception)
            {
                s_BuildFailure = exception;
                throw;
            }
            finally
            {
                s_Building = false;
            }
        }

        private static void AssertMainThread()
        {
            var mainThreadId = Volatile.Read(ref s_MainThreadId);
            if (mainThreadId == 0)
            {
                throw new InvalidOperationException(
                    "UniSpring DI RuntimeContext is not initialized: it exists only while the game runs " +
                    "(Play Mode or a player, after SubsystemRegistration). In edit-mode tools and EditMode " +
                    "tests build a container with ApplicationContext.Build instead.");
            }

            if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
                return;

            throw new InvalidOperationException(
                "UniSpring DI RuntimeContext can only be accessed from the Unity main thread.");
        }
    }
}
