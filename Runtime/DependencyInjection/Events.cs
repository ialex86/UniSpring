using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Publishes application events, as Spring's <c>ApplicationEventPublisher</c>. An event is any object; it is
    /// delivered synchronously, on the publisher's thread, to every <see cref="IEventListener{T}"/> bean whose type
    /// argument it is assignable to (in bean order), then to every subscriber added with <see cref="Subscribe{T}"/>.
    /// A listener that throws is logged and does not stop the others.
    /// </summary>
    public interface IEventPublisher
    {
        void Publish(object evt);

        /// <summary>
        /// Delivers events of <typeparamref name="T"/> (and derived types) to <paramref name="listener"/> until the
        /// result is disposed: for objects the container does not own (MonoBehaviours, Scene beans, tools).
        /// </summary>
        IDisposable Subscribe<T>(Action<T> listener);
    }

    /// <summary>Implemented through <see cref="IEventListener{T}"/>; lets the publisher find the listeners.</summary>
    public interface IEventListener
    {
    }

    /// <summary>
    /// A bean that receives events of <typeparamref name="T"/>, as Spring's <c>@EventListener</c>. Application
    /// beans are collected when the container starts; a Scene bean subscribes through
    /// <see cref="IEventPublisher.Subscribe{T}"/> instead.
    /// </summary>
    public interface IEventListener<in T> : IEventListener
    {
        void On(T evt);
    }

    /// <summary>
    /// Lets a module hold an event back for a listener: the transactions module delivers it after the current
    /// transaction commits (see <c>[AfterCommit]</c>).
    /// </summary>
    public interface IEventDeferral
    {
        /// <summary>Return true when the delivery to <paramref name="listener"/> was taken over.</summary>
        bool TryDefer(object listener, Action deliver);

        /// <summary>
        /// Called when an event is published before the container has started, on the publisher's flow: returns a
        /// deferral bound to the state of that moment (the transaction then open), used when the event is
        /// delivered at the start; null when nothing has to be remembered.
        /// </summary>
        IEventDeferral Capture();
    }

    /// <summary>
    /// The container's event publisher. It collects the listener beans when the container starts; events published
    /// before (from a constructor or <c>[PostConstruct]</c>) are kept and delivered then. Every event kept that way,
    /// and every event published while they are delivered, is delivered in publishing order before the publisher
    /// delivers directly. Safe to use from any thread once started.
    /// </summary>
    [Component]
    [Order(-900)]
    internal sealed class EventPublisher : IEventPublisher, ILifecycle
    {
        private sealed class Target
        {
            public object Listener;
            public Type EventType;
            public Action<object> Deliver;
        }

        private sealed class Early
        {
            public object Event;
            public IEventDeferral[] Deferrals;
        }

        private readonly ApplicationContext m_Context;
        private readonly IEventDeferral[] m_Deferrals;

        // Guards the beans, the subscribers, the routes and the early queue; delivery itself runs outside it.
        private readonly object m_Gate = new object();
        private readonly Dictionary<Type, Target[]> m_Routes = new Dictionary<Type, Target[]>();
        private Target[] m_Beans = new Target[0];
        private Target[] m_Subscribers = new Target[0];
        private Queue<Early> m_Early = new Queue<Early>();

        public EventPublisher(ApplicationContext context, IReadOnlyList<IEventDeferral> deferrals)
        {
            m_Context = context;
            m_Deferrals = new List<IEventDeferral>(deferrals).ToArray();
        }

        public void Start()
        {
            var beans = new List<Target>();
            foreach (var listener in m_Context.GetAllInScope(typeof(IEventListener)))
                beans.AddRange(TargetsOf(listener));

            lock (m_Gate)
            {
                m_Beans = beans.ToArray();
                m_Routes.Clear();
            }

            // Drained in order; what is published meanwhile (by a listener, or another thread) queues behind.
            while (true)
            {
                Early early;
                lock (m_Gate)
                {
                    if (m_Early.Count == 0)
                    {
                        m_Early = null;
                        return;
                    }

                    early = m_Early.Dequeue();
                }

                Deliver(early.Event, early.Deferrals);
            }
        }

        public void Stop()
        {
            lock (m_Gate)
            {
                m_Beans = new Target[0];
                m_Subscribers = new Target[0];
                m_Routes.Clear();
            }
        }

        public void Publish(object evt)
        {
            if (evt == null)
                throw new ArgumentNullException(nameof(evt));
            lock (m_Gate)
            {
                if (m_Early != null)
                {
                    m_Early.Enqueue(new Early { Event = evt, Deferrals = Capture() });
                    return;
                }
            }

            Deliver(evt, m_Deferrals);
        }

        /// <summary>The deferrals bound to the publisher's state now, for an event delivered later.</summary>
        private IEventDeferral[] Capture()
        {
            var captured = new List<IEventDeferral>();
            foreach (var deferral in m_Deferrals)
            {
                var bound = deferral.Capture();
                if (bound != null)
                    captured.Add(bound);
            }

            return captured.ToArray();
        }

        public IDisposable Subscribe<T>(Action<T> listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));
            var target = new Target { Listener = listener, EventType = typeof(T), Deliver = evt => listener((T)evt) };
            lock (m_Gate)
            {
                var subscribers = new List<Target>(m_Subscribers) { target };
                m_Subscribers = subscribers.ToArray();
                m_Routes.Clear();
            }

            return new Subscription(this, target);
        }

        private void Unsubscribe(Target target)
        {
            lock (m_Gate)
            {
                var subscribers = new List<Target>(m_Subscribers);
                if (!subscribers.Remove(target))
                    return;
                m_Subscribers = subscribers.ToArray();
                m_Routes.Clear();
            }
        }

        private void Deliver(object evt, IEventDeferral[] deferrals)
        {
            Target[] targets;
            lock (m_Gate)
            {
                var type = evt.GetType();
                if (!m_Routes.TryGetValue(type, out targets))
                {
                    targets = Route(type);
                    m_Routes.Add(type, targets);
                }
            }

            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                Action deliver = () => Invoke(target, evt);
                if (Defer(deferrals, target.Listener, deliver))
                    continue;
                deliver();
            }
        }

        private static bool Defer(IEventDeferral[] deferrals, object listener, Action deliver)
        {
            for (var index = 0; index < deferrals.Length; index++)
            {
                if (deferrals[index].TryDefer(listener, deliver))
                    return true;
            }

            return false;
        }

        private static void Invoke(Target target, object evt)
        {
            try
            {
                target.Deliver(evt);
            }
            catch (Exception exception)
            {
                Debug.LogException(new InvalidOperationException(
                    "UniSpring DI: listener " + target.Listener.GetType().FullName + " failed on " +
                    evt.GetType().FullName + ".", exception));
            }
        }

        /// <summary>Under the gate: the bean listeners, then the subscribers, that take events of the type.</summary>
        private Target[] Route(Type eventType)
        {
            var result = new List<Target>();
            foreach (var target in m_Beans)
            {
                if (target.EventType.IsAssignableFrom(eventType))
                    result.Add(target);
            }

            foreach (var target in m_Subscribers)
            {
                if (target.EventType.IsAssignableFrom(eventType))
                    result.Add(target);
            }

            return result.ToArray();
        }

        /// <summary>One target per listener interface, each called through its proxy when the bean is advised.</summary>
        private IEnumerable<Target> TargetsOf(object listener)
        {
            foreach (var face in listener.GetType().GetInterfaces())
            {
                if (!face.IsGenericType || face.GetGenericTypeDefinition() != typeof(IEventListener<>))
                    continue;
                var eventType = face.GetGenericArguments()[0];
                var adapter = (IListenerAdapter)Activator.CreateInstance(
                    typeof(ListenerAdapter<>).MakeGenericType(eventType), m_Context.AdviseAs(listener, face));
                yield return new Target { Listener = listener, EventType = eventType, Deliver = adapter.Deliver };
            }
        }

        private interface IListenerAdapter
        {
            void Deliver(object evt);
        }

        private sealed class ListenerAdapter<T> : IListenerAdapter
        {
            private readonly IEventListener<T> m_Listener;

            public ListenerAdapter(IEventListener<T> listener)
            {
                m_Listener = listener;
            }

            public void Deliver(object evt) => m_Listener.On((T)evt);
        }

        private sealed class Subscription : IDisposable
        {
            private EventPublisher m_Owner;
            private readonly Target m_Target;

            public Subscription(EventPublisher owner, Target target)
            {
                m_Owner = owner;
                m_Target = target;
            }

            public void Dispose()
            {
                var owner = m_Owner;
                m_Owner = null;
                owner?.Unsubscribe(m_Target);
            }
        }
    }
}
