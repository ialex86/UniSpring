using System;
using System.Collections.Generic;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// The Scene scopes of the loaded scenes. A scene gets its own scope the
    /// first time one of its components is autowired. A scene that joins
    /// another one (a chunk streamed into the world) shares the host's scope
    /// instead: its components see the same Scene beans. A scope is disposed
    /// with its Scene beans when the last scene that uses it unloads, so a
    /// joined scene leaving never ends it, whichever of them is the host. The scene type is a parameter (Unity's
    /// Scene at runtime, plain ids in tests), so this class needs no Unity
    /// runtime.
    /// </summary>
    internal sealed class SceneScopes<TScene> : IDisposable
    {
        private sealed class Scope
        {
            public ApplicationContext Context;
            public int Scenes;
        }

        private readonly Func<ApplicationContext> m_Create;
        private readonly Dictionary<TScene, Scope> m_ByScene = new Dictionary<TScene, Scope>(EqualityComparer<TScene>.Default);

        public SceneScopes(Func<ApplicationContext> create)
        {
            m_Create = create ?? throw new ArgumentNullException(nameof(create));
        }

        public int Count => m_ByScene.Count;

        /// <summary>The scope of <paramref name="scene"/>, created on first use.</summary>
        public ApplicationContext For(TScene scene) => For(scene, scene);

        /// <summary>
        /// The scope of <paramref name="scene"/>; a scene seen for the first
        /// time joins the scope of <paramref name="host"/> (its own when the
        /// two are the same). A scene keeps the scope it got first.
        /// </summary>
        public ApplicationContext For(TScene scene, TScene host)
        {
            if (m_ByScene.TryGetValue(scene, out var known))
                return known.Context;
            var scope = HostScope(host);
            if (m_ByScene.ContainsKey(scene))
                return scope.Context;
            m_ByScene[scene] = scope;
            scope.Scenes++;
            return scope.Context;
        }

        public bool Has(TScene scene) => m_ByScene.ContainsKey(scene);

        /// <summary>
        /// The scene is gone: it leaves its scope, and the last scene to leave
        /// disposes it.
        /// </summary>
        public void Unloaded(TScene scene)
        {
            if (!m_ByScene.TryGetValue(scene, out var scope))
                return;
            m_ByScene.Remove(scene);
            scope.Scenes--;
            if (scope.Scenes > 0)
                return;
            scope.Context.Dispose();
        }

        /// <summary>Disposes every scope (end of the play session).</summary>
        public void Dispose()
        {
            var scopes = new List<Scope>();
            foreach (var scope in m_ByScene.Values)
            {
                if (!scopes.Contains(scope))
                    scopes.Add(scope);
            }
            m_ByScene.Clear();
            Exception failure = null;
            for (var i = scopes.Count - 1; i >= 0; i--)
                failure = DisposeQuietly(scopes[i].Context) ?? failure;
            if (failure != null)
                throw new InvalidOperationException("UniSpring DI failed while disposing scene scopes.", failure);
        }

        private Scope HostScope(TScene host)
        {
            if (m_ByScene.TryGetValue(host, out var existing))
                return existing;
            var scope = new Scope { Context = m_Create() };
            if (scope.Context == null || scope.Context.Lifetime != BeanLifetime.Scene)
                throw new InvalidOperationException("UniSpring DI scene scopes must be created with BeanLifetime.Scene.");
            m_ByScene[host] = scope;
            scope.Scenes++;
            return scope;
        }

        private static Exception DisposeQuietly(ApplicationContext context)
        {
            try
            {
                context.Dispose();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
