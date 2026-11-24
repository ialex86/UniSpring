using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UniSpring.DependencyInjection.Editor;
using UnityEngine.TestTools;

namespace UniSpring.DependencyInjection.Tests
{
    /// <summary>
    /// The proxies generated for IL2CPP, compiled with Unity's compiler and run in the editor: every kind of
    /// interface member, advice through the chain, and the generator's choice of interfaces.
    /// </summary>
    [TestFixture]
    public sealed class GeneratedProxyTests
    {
        public interface IProbeBase
        {
            string Base();
        }

        public interface IProbe : IProbeBase
        {
            int Add(int left, int right);
            bool TryParse(string text, out int value);
            void Twice(ref int value);
            string Name { get; set; }
            int this[int index] { get; }
            event Action Changed;
            void RaiseChanged();
            T Echo<T>(T value);
            bool InScope();
        }

        [Service]
        public sealed class Probe : IProbe
        {
            private readonly AopProxyTests.Trace m_Trace;

            public Probe(AopProxyTests.Trace trace)
            {
                m_Trace = trace;
            }

            public string Base() => "base";

            [AopProxyTests.Traced]
            public int Add(int left, int right) => left + right;

            public bool TryParse(string text, out int value) => int.TryParse(text, out value);

            public void Twice(ref int value) => value *= 2;

            public string Name { get; set; }

            public int this[int index] => index * 10;

            public event Action Changed;

            public void RaiseChanged() => Changed?.Invoke();

            // ScopeInterceptor refuses generic methods: this one goes straight to the bean.
            public T Echo<T>(T value) => value;

            [AopProxyTests.Traced]
            public bool InScope() => m_Trace.Depth.Value == 1;
        }

        private GeneratedProxyCompiler m_Build;

        [TearDown]
        public void TearDown() => GeneratedProxies.Override(null);

        private IEnumerator Compile(params Type[] contracts)
        {
            var source = ProxySourceWriter.Write("Tests", contracts, null);
            m_Build = GeneratedProxyCompiler.Start(source, typeof(GeneratedProxyTests).Assembly, typeof(ComponentAttribute).Assembly);
            while (!m_Build.Done)
                yield return null;
            Assert.IsEmpty(m_Build.Errors, "the generated code compiles:\n" + string.Join("\n", m_Build.Errors) + "\n" + source);
            GeneratedProxies.Override(GeneratedProxies.Declared(m_Build.Assembly));
        }

        [UnityTest]
        public IEnumerator Every_Kind_Of_Member_Goes_Through_The_Generated_Proxy()
        {
            yield return Compile(typeof(IProbe));
            var context = ApplicationContext.BuildFromTypes(
                new[] { typeof(AopProxyTests.Trace), typeof(AopProxyTests.TraceInterceptor), typeof(Probe) });
            var probe = context.Get<IProbe>();
            var trace = context.Get<AopProxyTests.Trace>();

            Assert.IsInstanceOf<GeneratedProxy>(probe);
            Assert.AreEqual(5, probe.Add(2, 3));
            Assert.AreEqual(new[] { "open", "close" }, trace.Events.ToArray(), "advice ran around Add");
            Assert.IsTrue(probe.InScope(), "the bean runs inside the advice");
            Assert.AreEqual("base", probe.Base());
            Assert.IsTrue(probe.TryParse("42", out var parsed));
            Assert.AreEqual(42, parsed);
            var value = 4;
            probe.Twice(ref value);
            Assert.AreEqual(8, value);
            probe.Name = "coupe";
            Assert.AreEqual("coupe", probe.Name);
            Assert.AreEqual(30, probe[3]);
            var raised = 0;
            Action handler = () => raised++;
            probe.Changed += handler;
            probe.RaiseChanged();
            probe.Changed -= handler;
            probe.RaiseChanged();
            Assert.AreEqual(1, raised);
            trace.Events.Clear();
            Assert.AreEqual(7, probe.Echo(7));
            Assert.AreEqual("seven", probe.Echo("seven"));
            Assert.IsEmpty(trace.Events, "not advised: called directly");
            context.Dispose();
        }

        [UnityTest]
        public IEnumerator A_Bean_Without_Advice_Is_Handed_Out_As_It_Is()
        {
            yield return Compile(typeof(IProbe));
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(AopProxyTests.Trace), typeof(Probe) });

            Assert.IsInstanceOf<Probe>(context.Get<IProbe>(), "no interceptor matches: no proxy");
            context.Dispose();
        }

        [Test]
        public void The_Generator_Picks_The_Interfaces_Of_Advised_Beans()
        {
            var skipped = new System.Collections.Generic.List<string>();
            var contracts = ProxyGenerator.Contracts(typeof(GeneratedProxyTests).Assembly, skipped);

            CollectionAssert.Contains(contracts, typeof(IProbe));
            CollectionAssert.Contains(contracts, typeof(IProbeBase));
            CollectionAssert.Contains(contracts, typeof(AopProxyTests.ITracedService), "TracedService has [Traced] methods");
            CollectionAssert.DoesNotContain(contracts, typeof(PlatformTests.IClock), "no clock has an advised method");
        }

        public enum Gear
        {
            Low,
            High
        }

        public interface IGearbox : IKeyedStrategy<Gear>
        {
        }

        [Service]
        public sealed class NeedsGearboxes
        {
            public NeedsGearboxes(StrategyRegistry<Gear, IGearbox> gearboxes)
            {
            }
        }

        [Test]
        public void Enum_Keyed_Registries_Beans_Take_Get_An_Aot_Hint()
        {
            var hints = ProxyGenerator.AotTypes(typeof(GeneratedProxyTests).Assembly);
            CollectionAssert.Contains(hints, typeof(StrategyRegistry<Gear, IGearbox>));

            var source = ProxySourceWriter.Write("Game", new Type[0], hints, ProxySourceWriter.PlayerGuard);
            StringAssert.Contains(
                "new global::UniSpring.DependencyInjection.StrategyRegistry<global::UniSpring.DependencyInjection.Tests.GeneratedProxyTests.Gear, " +
                "global::UniSpring.DependencyInjection.Tests.GeneratedProxyTests.IGearbox>(null);", source);
        }

        [Test]
        public void Generated_Source_Is_Guarded_For_Il2Cpp_Players()
        {
            var source = ProxySourceWriter.Write("Game", new[] { typeof(IProbe) }, ProxySourceWriter.PlayerGuard);

            StringAssert.StartsWith(ProxySourceWriter.Header + "#if ENABLE_IL2CPP && !UNITY_EDITOR\n", source);
            StringAssert.EndsWith("#endif\n", source);
            StringAssert.Contains("[global::UnityEngine.Scripting.Preserve]", source);
        }
    }
}
