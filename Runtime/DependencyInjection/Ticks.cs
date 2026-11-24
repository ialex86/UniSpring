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
        private object[] m_Beans = new object[0];
        private readonly List<Registration> m_Added = new List<Registration>();
        private ITickable[] m_Ticks = new ITickable[0];
        private ILateTickable[] m_LateTicks = new ILateTickable[0];
        private IFixedTickable[] m_FixedTicks = new IFixedTickable[0];

        public TickRunner(ApplicationContext context)
        {
            m_Context = context;
        }

        public void Start()
        {
            var beans = new List<object>();
            AddBeans(beans, typeof(ITickable));
            AddBeans(beans, typeof(ILateTickable));
            AddBeans(beans, typeof(IFixedTickable));
            lock (m_Gate)
            {
                m_Beans = beans.ToArray();
                Rebuild();
            }

            TickLoop.Attach(this);
        }

        private void AddBeans(List<object> beans, Type contract)
        {
            foreach (var bean in m_Context.GetAllInScope(contract))
            {
                if (IndexOf(beans, bean) < 0)
                    beans.Add(bean);
            }
        }

        public void Stop()
        {
            TickLoop.Detach(this);
            lock (m_Gate)
            {
                m_Beans = new object[0];
                m_Added.Clear();
                Rebuild();
            }
        }

        public IDisposable Add(object tickable)
        {
            if (tickable == null)
                throw new ArgumentNullException(nameof(tickable));
            if (!(tickable is ITickable) && !(tickable is ILateTickable) && !(tickable is IFixedTickable))
                throw new ArgumentException(tickable.GetType().FullName + " implements no tickable interface.", nameof(tickable));
            var registration = new Registration(this, tickable);
            lock (m_Gate)
            {
                m_Added.Add(registration);
                Rebuild();
            }

            return registration;
        }

        /// <summary>
        /// Under the gate: the beans in bean order, then the added objects in the order they were added. An object
        /// is ticked once however often it is there; each registration removes only itself, never a bean.
        /// </summary>
        private void Rebuild()
        {
            var all = new List<object>(m_Beans);
            foreach (var registration in m_Added)
            {
                if (IndexOf(all, registration.Target) < 0)
                    all.Add(registration.Target);
            }

            var ticks = new List<ITickable>();
            var lateTicks = new List<ILateTickable>();
            var fixedTicks = new List<IFixedTickable>();
            foreach (var item in all)
            {
                if (item is ITickable tick)
                    ticks.Add(tick);
                if (item is ILateTickable late)
                    lateTicks.Add(late);
                if (item is IFixedTickable fixedTick)
                    fixedTicks.Add(fixedTick);
            }

            m_Ticks = ticks.ToArray();
            m_LateTicks = lateTicks.ToArray();
            m_FixedTicks = fixedTicks.ToArray();
        }

        /// <summary>By reference: a tickable with its own Equals is still one object per registration.</summary>
        private static int IndexOf(IList<object> items, object item)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (ReferenceEquals(items[index], item))
                    return index;
            }

            return -1;
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

        private void Remove(Registration registration)
        {
            lock (m_Gate)
            {
                for (var index = 0; index < m_Added.Count; index++)
                {
                    if (!ReferenceEquals(m_Added[index], registration))
                        continue;
                    m_Added.RemoveAt(index);
                    Rebuild();
                    return;
                }
            }
        }

        private sealed class Registration : IDisposable
        {
            private TickRunner m_Owner;

            public Registration(TickRunner owner, object target)
            {
                m_Owner = owner;
                Target = target;
            }

            public object Target { get; }

            public void Dispose()
            {
                var owner = m_Owner;
                m_Owner = null;
                owner?.Remove(this);
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
