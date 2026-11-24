using System;
using UniSpring.DependencyInjection;
using NUnit.Framework;

namespace UniSpring.DependencyInjection.Tests
{
    public sealed class SceneScopesTests
    {
        [Component]
        [Lifetime(BeanLifetime.Scene)]
        public sealed class SceneProbe : IDisposable
        {
            public bool Disposed;

            public void Dispose() => Disposed = true;
        }

        [Component]
        public sealed class AppProbe
        {
        }

        private ApplicationContext m_Root;
        private SceneScopes<int> m_Scopes;

        [SetUp]
        public void SetUp()
        {
            m_Root = ApplicationContext.BuildFromTypes(new[] { typeof(SceneProbe), typeof(AppProbe) });
            m_Scopes = new SceneScopes<int>(() => m_Root.CreateScope(BeanLifetime.Scene));
        }

        [TearDown]
        public void TearDown()
        {
            m_Scopes.Dispose();
            m_Root.Dispose();
        }

        [Test]
        public void A_Scene_Keeps_One_Scope_And_Each_Scene_Gets_Its_Own()
        {
            var first = m_Scopes.For(1);
            Assert.AreSame(first, m_Scopes.For(1));
            Assert.AreEqual(BeanLifetime.Scene, first.Lifetime);
            Assert.AreNotSame(first, m_Scopes.For(2));
            Assert.AreNotSame(first.Get<SceneProbe>(), m_Scopes.For(2).Get<SceneProbe>(),
                "every scene has its own Scene beans");
            Assert.AreSame(first.Get<AppProbe>(), m_Scopes.For(2).Get<AppProbe>(),
                "Application beans are shared by every scene");
        }

        [Test]
        public void Unloading_A_Scene_Disposes_Its_Scene_Beans_Only()
        {
            var gone = m_Scopes.For(1).Get<SceneProbe>();
            var kept = m_Scopes.For(2).Get<SceneProbe>();

            m_Scopes.Unloaded(1);

            Assert.IsTrue(gone.Disposed);
            Assert.IsFalse(kept.Disposed);
            Assert.IsFalse(m_Scopes.Has(1));
            Assert.AreNotSame(gone, m_Scopes.For(1).Get<SceneProbe>(),
                "a scene loaded again under the same handle starts fresh");
        }

        [Test]
        public void A_Joined_Scene_Shares_The_Host_Scope_And_Leaving_Keeps_It()
        {
            var host = m_Scopes.For(1);
            var probe = host.Get<SceneProbe>();

            Assert.AreSame(host, m_Scopes.For(2, 1));
            Assert.AreSame(probe, m_Scopes.For(2).Get<SceneProbe>());

            m_Scopes.Unloaded(2);
            Assert.IsFalse(probe.Disposed, "a streamed part leaving does not end the host's scope");
            Assert.IsTrue(m_Scopes.Has(1));
        }

        [Test]
        public void A_Scene_Waking_Before_Its_Host_Creates_The_Host_Scope()
        {
            var member = m_Scopes.For(2, 1);
            Assert.AreSame(member, m_Scopes.For(1), "the host finds the scope its part created");
        }

        [Test]
        public void A_Shared_Scope_Ends_With_The_Last_Of_Its_Scenes()
        {
            var probe = m_Scopes.For(1).Get<SceneProbe>();
            m_Scopes.For(2, 1);
            m_Scopes.For(3, 1);

            m_Scopes.Unloaded(1);
            Assert.IsFalse(probe.Disposed, "the host leaving first keeps the scope for the scenes still loaded");
            Assert.AreSame(probe, m_Scopes.For(2).Get<SceneProbe>());
            m_Scopes.Unloaded(2);
            Assert.IsFalse(probe.Disposed);

            m_Scopes.Unloaded(3);
            Assert.IsTrue(probe.Disposed);
            Assert.AreEqual(0, m_Scopes.Count);
        }

        [Test]
        public void A_Scene_Unloaded_Twice_Or_Never_Seen_Changes_Nothing()
        {
            var probe = m_Scopes.For(1).Get<SceneProbe>();
            m_Scopes.For(2, 1);
            m_Scopes.Unloaded(2);
            m_Scopes.Unloaded(2);
            m_Scopes.Unloaded(42);
            Assert.IsFalse(probe.Disposed);
        }

        [Test]
        public void Disposing_Ends_Every_Scope_Once()
        {
            var a = m_Scopes.For(1).Get<SceneProbe>();
            m_Scopes.For(2, 1);
            var b = m_Scopes.For(3).Get<SceneProbe>();

            m_Scopes.Dispose();

            Assert.IsTrue(a.Disposed);
            Assert.IsTrue(b.Disposed);
            Assert.AreEqual(0, m_Scopes.Count);
        }

        [Test]
        public void A_Scope_Of_Another_Lifetime_Is_Refused()
        {
            var wrong = new SceneScopes<int>(() => m_Root);
            Assert.Throws<InvalidOperationException>(() => wrong.For(1));
        }

        [Test]
        public void The_Root_Cannot_Resolve_A_Scene_Bean()
        {
            Assert.Throws<InvalidOperationException>(() => m_Root.Get<SceneProbe>(),
                "Scene beans exist only inside a scene scope");
        }
    }
}
