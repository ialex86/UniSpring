using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UniSpring.DependencyInjection;
using NUnit.Framework;

namespace UniSpring.DependencyInjection.Tests
{
    /// <summary>
    /// The container's AOP: a bean with advised methods is handed out through a proxy per interface, its advice runs
    /// around calls made through that interface only, and a scope spans an async method until its task completes.
    /// </summary>
    [TestFixture]
    public sealed class AopProxyTests
    {
        private static readonly Type[] Beans = { typeof(Trace), typeof(TraceInterceptor), typeof(TracedService) };

        [Test]
        public void An_Advised_Method_Runs_In_The_Scope_And_Another_Does_Not()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var service = context.Get<ITracedService>();
            var trace = context.Get<Trace>();

            Assert.IsTrue(service.InScope(), "advised");
            Assert.IsFalse(service.Plain(), "not advised");
            Assert.AreEqual(new[] { "open", "close" }, trace.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void A_Call_The_Bean_Makes_To_Itself_Is_Not_Advised()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);

            Assert.IsFalse(context.Get<ITracedService>().CallsItself(), "this.InScope() bypasses the proxy, as in Spring");
            context.Dispose();
        }

        [Test]
        public void An_Async_Method_Keeps_Its_Scope_Until_Its_Task_Completes_And_The_Caller_Is_Outside_It()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var service = context.Get<ITracedService>();
            var trace = context.Get<Trace>();
            var gate = new TaskCompletionSource<bool>();

            var running = service.InScopeAfterAsync(gate.Task);
            Assert.AreEqual(0, trace.Depth.Value, "the caller's flow does not see the method's scope");
            Assert.AreEqual(new[] { "open" }, trace.Events.ToArray(), "open while the method awaits");

            gate.SetResult(true);
            Assert.IsTrue(running.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, running.Result, "the scope was open after the await");
            Assert.AreEqual(new[] { "open", "close" }, trace.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void A_Plain_Task_Method_Returns_A_Task_That_Ends_With_It()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var gate = new TaskCompletionSource<bool>();

            var running = context.Get<ITracedService>().WaitAsync(gate.Task);
            Assert.IsFalse(running.IsCompleted);
            gate.SetResult(true);

            Assert.IsTrue(running.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(new[] { "open", "close" }, context.Get<Trace>().Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void An_Exception_Comes_Through_Unwrapped_And_The_Scope_Closes()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);

            Assert.Throws<ArgumentException>(() => context.Get<ITracedService>().Throws());
            Assert.AreEqual(new[] { "open", "close" }, context.Get<Trace>().Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void Out_Values_And_Generic_Methods_Pass_Through_The_Proxy()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);
            var service = context.Get<ITracedService>();

            Assert.IsTrue(service.TryGet(out var value));
            Assert.AreEqual(42, value);
            Assert.AreEqual("x", service.Echo("x"), "a generic method passes through unadvised");
            Assert.AreEqual(new[] { "open", "close" }, context.Get<Trace>().Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void The_Same_Proxy_Is_Handed_Out_For_A_Contract_And_Consumers_Get_It()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(Trace), typeof(TraceInterceptor), typeof(TracedService), typeof(Consumer) });
            var service = context.Get<ITracedService>();

            Assert.AreSame(service, context.Get<ITracedService>());
            Assert.AreSame(service, context.Get<Consumer>().Service);
            Assert.IsInstanceOf<AopProxy>(service);
            context.Dispose();
        }

        [Test]
        public void An_Advised_Bean_Is_Not_Handed_Out_By_Its_Class()
        {
            var context = ApplicationContext.BuildFromTypes(Beans);

            var error = Assert.Throws<InvalidOperationException>(() => context.Get<TracedService>());
            StringAssert.Contains("interfaces only", error.Message);
            context.Dispose();
        }

        [Test]
        public void Advice_On_A_Method_No_Interface_Calls_Is_Refused_At_Warm_Up()
        {
            var error = Assert.Throws<InvalidOperationException>(() => ApplicationContext.BuildFromTypes(
                new[] { typeof(Trace), typeof(TraceInterceptor), typeof(PrivateAdvice) }));

            StringAssert.Contains("Hidden", error.Message);
        }

        [Test]
        public void Advice_On_A_ValueTask_Method_Is_Refused()
        {
            Assert.Throws<NotSupportedException>(() => ApplicationContext.BuildFromTypes(
                new[] { typeof(Trace), typeof(TraceInterceptor), typeof(ValueTaskAdvice) }));
        }

        [Test]
        public void Advice_On_A_Generic_Or_Iterator_Method_Is_Refused()
        {
            Assert.Throws<NotSupportedException>(() => ApplicationContext.BuildFromTypes(
                new[] { typeof(Trace), typeof(TraceInterceptor), typeof(GenericAdvice) }));
            Assert.Throws<NotSupportedException>(() => ApplicationContext.BuildFromTypes(
                new[] { typeof(Trace), typeof(TraceInterceptor), typeof(IteratorAdvice) }));
        }

        [Test]
        public void A_Bean_Behind_An_Internal_Interface_Is_Proxied_Too()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(Trace), typeof(TraceInterceptor), typeof(InternalService) });

            Assert.IsTrue(context.Get<IInternalService>().InScope());
            context.Dispose();
        }

        [Test]
        public void Without_Interceptors_Beans_Are_Handed_Out_As_They_Are()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(Trace), typeof(TracedService) });

            Assert.IsInstanceOf<TracedService>(context.Get<ITracedService>());
            context.Dispose();
        }

        [Test]
        public void An_Advised_Bean_An_Interceptor_Depends_On_Is_Reported()
        {
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("TracedService has advised methods but was handed out before"));

            var context = ApplicationContext.BuildFromTypes(
                new[] { typeof(Trace), typeof(TraceInterceptor), typeof(TracedService), typeof(DependentInterceptor) });

            Assert.IsInstanceOf<AopProxy>(context.Get<ITracedService>(), "beans resolved later still get the proxy");
            context.Dispose();
        }

        [Component]
        public sealed class DependentInterceptor : IMethodInterceptor
        {
            public DependentInterceptor(ITracedService service)
            {
            }

            public bool Matches(MethodInfo targetMethod) => false;

            public object Invoke(IMethodInvocation invocation) => invocation.Proceed();
        }

        [Test]
        public void A_Registered_Instance_Is_Advised_Like_A_Bean()
        {
            var trace = new Trace();
            var context = ApplicationContext.BuildFromTypes(
                new[] { typeof(TraceInterceptor) },
                scope =>
                {
                    scope.RegisterInstance(trace);
                    scope.RegisterInstance<ITracedService>(new TracedService(trace));
                });
            var service = context.Get<ITracedService>();

            Assert.IsInstanceOf<AopProxy>(service);
            Assert.IsTrue(service.InScope(), "advised");
            Assert.AreEqual(new[] { "open", "close" }, trace.Events.ToArray());
            context.Dispose();
        }

        [Test]
        public void A_Proxy_Made_By_Hand_Advises_A_Service_Made_With_New()
        {
            var trace = new Trace();
            var service = AopProxies.Create<ITracedService>(new TracedService(trace), new TraceInterceptor(trace));

            Assert.IsTrue(service.InScope());
            Assert.AreEqual(new[] { "open", "close" }, trace.Events.ToArray());
        }

        // ------------------------------------------------------------------ fixtures

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class TracedAttribute : Attribute
        {
        }

        [Component]
        public sealed class Trace
        {
            public readonly List<string> Events = new List<string>();
            public readonly AsyncLocal<int> Depth = new AsyncLocal<int>();
        }

        [Component]
        public sealed class TraceInterceptor : ScopeInterceptor
        {
            private readonly Trace m_Trace;

            public TraceInterceptor(Trace trace)
            {
                m_Trace = trace;
            }

            protected override bool Applies(MethodInfo targetMethod) => targetMethod.IsDefined(typeof(TracedAttribute), true);

            protected override IDisposable Open(IMethodInvocation invocation)
            {
                m_Trace.Events.Add("open");
                m_Trace.Depth.Value++;
                return new Closing(m_Trace);
            }

            private sealed class Closing : IDisposable
            {
                private readonly Trace m_Trace;

                public Closing(Trace trace)
                {
                    m_Trace = trace;
                }

                public void Dispose()
                {
                    m_Trace.Depth.Value--;
                    m_Trace.Events.Add("close");
                }
            }
        }

        public interface ITracedService
        {
            bool InScope();
            bool Plain();
            bool CallsItself();
            Task<int> InScopeAfterAsync(Task gate);
            Task WaitAsync(Task gate);
            void Throws();
            bool TryGet(out int value);
            T Echo<T>(T value);
        }

        [Service]
        public sealed class TracedService : ITracedService
        {
            private readonly Trace m_Trace;

            public TracedService(Trace trace)
            {
                m_Trace = trace;
            }

            [Traced]
            public bool InScope() => m_Trace.Depth.Value == 1;

            public bool Plain() => m_Trace.Depth.Value == 1;

            public bool CallsItself() => InScope();

            [Traced]
            public async Task<int> InScopeAfterAsync(Task gate)
            {
                await gate.ConfigureAwait(false);
                return m_Trace.Depth.Value;
            }

            [Traced]
            public async Task WaitAsync(Task gate) => await gate.ConfigureAwait(false);

            [Traced]
            public void Throws() => throw new ArgumentException("thrown by the bean");

            [Traced]
            public bool TryGet(out int value)
            {
                value = 42;
                return true;
            }

            public T Echo<T>(T value) => value;
        }

        [Service]
        public sealed class Consumer
        {
            public readonly ITracedService Service;

            public Consumer(ITracedService service)
            {
                Service = service;
            }
        }

        public interface IPrivateAdvice
        {
            void Visible();
        }

        [Service]
        public sealed class PrivateAdvice : IPrivateAdvice
        {
            public void Visible() => Hidden();

            [Traced]
            private void Hidden()
            {
            }
        }

        public interface IValueTaskAdvice
        {
            ValueTask Run();
        }

        [Service]
        public sealed class ValueTaskAdvice : IValueTaskAdvice
        {
            [Traced]
            public ValueTask Run() => default;
        }

        public interface IGenericAdvice
        {
            T Run<T>(T value);
        }

        [Service]
        public sealed class GenericAdvice : IGenericAdvice
        {
            [Traced]
            public T Run<T>(T value) => value;
        }

        public interface IIteratorAdvice
        {
            IEnumerable<int> Run();
        }

        [Service]
        public sealed class IteratorAdvice : IIteratorAdvice
        {
            [Traced]
            public IEnumerable<int> Run()
            {
                yield return 1;
            }
        }

        internal interface IInternalService
        {
            bool InScope();
        }

        [Service]
        internal sealed class InternalService : IInternalService
        {
            private readonly Trace m_Trace;

            public InternalService(Trace trace)
            {
                m_Trace = trace;
            }

            [Traced]
            public bool InScope() => m_Trace.Depth.Value == 1;
        }
    }
}
