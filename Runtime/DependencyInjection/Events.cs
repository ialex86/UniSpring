using System;
using System.Collections.Concurrent;
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
    /// transaction commits (see <c>[AfterCommit]</c>). Return true when the delivery was taken over.
    /// </summary>
    public interface IEventDeferral
    {
        bool TryDefer(object listener, Action deliver);
    }

    /// <summary>
    /// The container's event publisher. It collects the listener beans when the container starts; events published
    /// before (from a constructor or <c>[PostConstruct]</c>) are kept and delivered then, in order. Safe to use from
    /// any thread once started.
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

        private readonly ApplicationContext m_Context;
        private readonly object m_Gate = new object();
        private readonly ConcurrentDictionary<Type, Target[]> m_Routes = new ConcurrentDictionary<Type, Target[]>();
        private Target[] m_Beans;
        private Target[] m_Subscribers = new Target[0];
        private IEventDeferral[] m_Deferrals = new IEventDeferral[0];
        private List<object> m_Early = new List<object>();

        public EventPublisher(ApplicationContext context)
        {
            m_Context = context;
        }

        public void Start()
        {
            var beans = new List<Target>();
            foreach (var listener in m_Context.GetAllInScope(typeof(IEventListener)))
                beans.AddRange(TargetsOf(listener));

            var deferrals = m_Context.GetAllInScope(typeof(IEventDeferral));
            var deferralArray = new IEventDeferral[deferrals.Count];
            for (var index = 0; index < deferrals.Count; index++)
                deferralArray[index] = (IEventDeferral)deferrals[index];

            List<object> early;
            lock (m_Gate)
            {
                m_Beans = beans.ToArray();
                m_Deferrals = deferralArray;
                m_Routes.Clear();
                early = m_Early;
                m_Early = null;
            }

            foreach (var evt in early)
                Deliver(evt);
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
                    m_Early.Add(evt);
                    return;
                }
            }

            Deliver(evt);
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

        private void Deliver(object evt)
        {
            var targets = m_Routes.GetOrAdd(evt.GetType(), Route);
            var deferrals = m_Deferrals;
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

        private Target[] Route(Type eventType)
        {
            Target[] beans;
            Target[] subscribers;
            lock (m_Gate)
            {
                beans = m_Beans ?? new Target[0];
                subscribers = m_Subscribers;
            }

            var result = new List<Target>();
            foreach (var target in beans)
            {
                if (target.EventType.IsAssignableFrom(eventType))
                    result.Add(target);
            }

            foreach (var target in subscribers)
            {
                if (target.EventType.IsAssignableFrom(eventType))
                    result.Add(target);
            }

            return result.ToArray();
        }

        private static IEnumerable<Target> TargetsOf(object listener)
        {
            foreach (var face in listener.GetType().GetInterfaces())
            {
                if (!face.IsGenericType || face.GetGenericTypeDefinition() != typeof(IEventListener<>))
                    continue;
                var eventType = face.GetGenericArguments()[0];
                var adapter = (IListenerAdapter)Activator.CreateInstance(
                    typeof(ListenerAdapter<>).MakeGenericType(eventType), listener);
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
