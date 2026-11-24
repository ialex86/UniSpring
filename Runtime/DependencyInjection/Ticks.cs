using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;

namespace UniSpring.DependencyInjection
{
    /// <summary>Called every frame during Unity's Update, with <c>Time.deltaTime</c> (0 while the game is paused by time scale).</summary>
    public interface ITickable
    {
        void Tick(float deltaTime);
    }

    /// <summary>Called every frame after Update (Unity's PreLateUpdate), with <c>Time.deltaTime</c>.</summary>
    public interface ILateTickable
    {
        void LateTick(float deltaTime);
    }

    /// <summary>Called on every physics step (Unity's FixedUpdate), with <c>Time.fixedDeltaTime</c>.</summary>
    public interface IFixedTickable
    {
        void FixedTick(float deltaTime);
    }

    /// <summary>
    /// Adds objects the container does not own (Scene beans, plain objects) to the frame loop; application beans
    /// that implement a tickable interface are added on their own.
    /// </summary>
    public interface ITickRegistry
    {
        /// <summary>
        /// Ticks <paramref name="tickable"/> (any of <see cref="ITickable"/>, <see cref="ILateTickable"/>,
        /// <see cref="IFixedTickable"/>) after the beans, until the result is disposed.
        /// </summary>
        IDisposable Add(object tickable);
    }

    /// <summary>
    /// Runs the tickable beans in Unity's player loop, in bean order (<see cref="OrderAttribute"/>): no
    /// MonoBehaviour host is needed. Started with the container, stopped before its beans are disposed. A tickable
    /// that throws is logged and the others still run.
    /// </summary>
    [Component]
    [Order(-800)]
    internal sealed class TickRunner : ITickRegistry, ILifecycle
    {
        private readonly ApplicationContext m_Context;
        private readonly object m_Gate = new object();
        private ITickable[] m_Ticks = new ITickable[0];
        private ILateTickable[] m_LateTicks = new ILateTickable[0];
        private IFixedTickable[] m_FixedTicks = new IFixedTickable[0];

        public TickRunner(ApplicationContext context)
        {
            m_Context = context;
        }

        public void Start()
        {
            var ticks = Beans<ITickable>();
            var lateTicks = Beans<ILateTickable>();
            var fixedTicks = Beans<IFixedTickable>();
            lock (m_Gate)
            {
                // Objects added before the start (from a constructor) follow the beans.
                m_Ticks = Concat(ticks, m_Ticks);
                m_LateTicks = Concat(lateTicks, m_LateTicks);
                m_FixedTicks = Concat(fixedTicks, m_FixedTicks);
            }

            TickLoop.Attach(this);
        }

        private T[] Beans<T>()
        {
            var beans = m_Context.GetAllInScope(typeof(T));
            var result = new T[beans.Count];
            for (var index = 0; index < beans.Count; index++)
                result[index] = (T)beans[index];
            return result;
        }

        public void Stop()
        {
            TickLoop.Detach(this);
            lock (m_Gate)
            {
                m_Ticks = new ITickable[0];
                m_LateTicks = new ILateTickable[0];
                m_FixedTicks = new IFixedTickable[0];
            }
        }

        public IDisposable Add(object tickable)
        {
            if (tickable == null)
                throw new ArgumentNullException(nameof(tickable));
            if (!(tickable is ITickable) && !(tickable is ILateTickable) && !(tickable is IFixedTickable))
                throw new ArgumentException(tickable.GetType().FullName + " implements no tickable interface.", nameof(tickable));
            AddTo(tickable);
            return new Registration(this, tickable);
        }

        internal void RunTick(float deltaTime)
        {
            var ticks = m_Ticks;
            for (var index = 0; index < ticks.Length; index++)
            {
                try
                {
                    ticks[index].Tick(deltaTime);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        internal void RunLateTick(float deltaTime)
        {
            var ticks = m_LateTicks;
            for (var index = 0; index < ticks.Length; index++)
            {
                try
                {
                    ticks[index].LateTick(deltaTime);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        internal void RunFixedTick(float deltaTime)
        {
            var ticks = m_FixedTicks;
            for (var index = 0; index < ticks.Length; index++)
            {
                try
                {
                    ticks[index].FixedTick(deltaTime);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private void AddTo(object tickable)
        {
            lock (m_Gate)
            {
                if (tickable is ITickable tick)
                    m_Ticks = With(m_Ticks, tick);
                if (tickable is ILateTickable late)
                    m_LateTicks = With(m_LateTicks, late);
                if (tickable is IFixedTickable fixedTick)
                    m_FixedTicks = With(m_FixedTicks, fixedTick);
            }
        }

        private void Remove(object tickable)
        {
            lock (m_Gate)
            {
                if (tickable is ITickable tick)
                    m_Ticks = Without(m_Ticks, tick);
                if (tickable is ILateTickable late)
                    m_LateTicks = Without(m_LateTicks, late);
                if (tickable is IFixedTickable fixedTick)
                    m_FixedTicks = Without(m_FixedTicks, fixedTick);
            }
        }

        private static T[] Concat<T>(T[] first, T[] second)
        {
            var list = new List<T>(first);
            foreach (var item in second)
            {
                if (!list.Contains(item))
                    list.Add(item);
            }

            return list.ToArray();
        }

        private static T[] With<T>(T[] items, T item)
        {
            var list = new List<T>(items) { item };
            return list.ToArray();
        }

        private static T[] Without<T>(T[] items, T item)
        {
            var list = new List<T>(items);
            list.Remove(item);
            return list.ToArray();
        }

        private sealed class Registration : IDisposable
        {
            private TickRunner m_Owner;
            private readonly object m_Tickable;

            public Registration(TickRunner owner, object tickable)
            {
                m_Owner = owner;
                m_Tickable = tickable;
            }

            public void Dispose()
            {
                var owner = m_Owner;
                m_Owner = null;
                owner?.Remove(m_Tickable);
            }
        }
    }

    /// <summary>
    /// UniSpring's entries in Unity's player loop: one system each at the end of Update, PreLateUpdate and
    /// FixedUpdate, calling the attached runners. Installed while the game plays; an editor without domain reload
    /// keeps the loop between sessions, so entries are replaced, never added twice.
    /// </summary>
    internal static class TickLoop
    {
        private struct UniSpringUpdate { }
        private struct UniSpringLateUpdate { }
        private struct UniSpringFixedUpdate { }

        private static TickRunner[] s_Runners = new TickRunner[0];
        private static readonly object s_Gate = new object();

        public static void Attach(TickRunner runner)
        {
            lock (s_Gate)
                s_Runners = new List<TickRunner>(s_Runners) { runner }.ToArray();
            if (Application.isPlaying)
                Install();
        }

        public static void Detach(TickRunner runner)
        {
            lock (s_Gate)
            {
                var runners = new List<TickRunner>(s_Runners);
                runners.Remove(runner);
                s_Runners = runners.ToArray();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            lock (s_Gate)
                s_Runners = new TickRunner[0];
        }

        private static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null)
                return;
            var systems = loop.subSystemList;
            for (var index = 0; index < systems.Length; index++)
            {
                var type = systems[index].type;
                if (type == typeof(UnityEngine.PlayerLoop.Update))
                    systems[index] = WithEntry(systems[index], typeof(UniSpringUpdate), Update);
                else if (type == typeof(UnityEngine.PlayerLoop.PreLateUpdate))
                    systems[index] = WithEntry(systems[index], typeof(UniSpringLateUpdate), LateUpdate);
                else if (type == typeof(UnityEngine.PlayerLoop.FixedUpdate))
                    systems[index] = WithEntry(systems[index], typeof(UniSpringFixedUpdate), FixedUpdate);
            }

            loop.subSystemList = systems;
            PlayerLoop.SetPlayerLoop(loop);
        }

        private static PlayerLoopSystem WithEntry(PlayerLoopSystem phase, Type marker, PlayerLoopSystem.UpdateFunction update)
        {
            var entries = new List<PlayerLoopSystem>();
            if (phase.subSystemList != null)
            {
                foreach (var entry in phase.subSystemList)
                {
                    if (entry.type != marker)
                        entries.Add(entry);
                }
            }

            entries.Add(new PlayerLoopSystem { type = marker, updateDelegate = update });
            phase.subSystemList = entries.ToArray();
            return phase;
        }

        private static void Update()
        {
            var runners = s_Runners;
            var deltaTime = Time.deltaTime;
            for (var index = 0; index < runners.Length; index++)
                runners[index].RunTick(deltaTime);
        }

        private static void LateUpdate()
        {
            var runners = s_Runners;
            var deltaTime = Time.deltaTime;
            for (var index = 0; index < runners.Length; index++)
                runners[index].RunLateTick(deltaTime);
        }

        private static void FixedUpdate()
        {
            var runners = s_Runners;
            var deltaTime = Time.fixedDeltaTime;
            for (var index = 0; index < runners.Length; index++)
                runners[index].RunFixedTick(deltaTime);
        }
    }
}
