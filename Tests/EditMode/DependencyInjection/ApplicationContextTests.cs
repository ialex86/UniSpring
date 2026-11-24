using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UniSpring.DependencyInjection;
using NUnit.Framework;

namespace UniSpring.DependencyInjection.Tests
{
    [TestFixture]
    public sealed class ApplicationContextTests
    {
        [SetUp]
        public void ResetProbes()
        {
            DisposableProbe.Disposed = 0;
            TransientProbe.Disposed = 0;
        }

        [Test]
        public void A_Failed_Warm_Up_Disposes_Created_Beans_And_Keeps_The_First_Error()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ApplicationContext.BuildFromTypes(new[] { typeof(DisposableProbe), typeof(FailingBean) }));

            Assert.AreEqual("first failure", error.InnerException?.Message, "the original cause is kept");
            Assert.AreEqual(1, DisposableProbe.Disposed, "beans created before the failure are disposed");
        }

        [Test]
        public void A_Missing_Dependency_Reports_The_Resolution_Path()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ApplicationContext.BuildFromTypes(new[] { typeof(TopBean), typeof(MiddleBean) }));

            StringAssert.Contains("IMissingContract", error.Message);
            StringAssert.Contains("MiddleBean -> IMissingContract", error.Message);
        }

        [Test]
        public void Transients_Are_Owned_By_Their_Consumer()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(TransientProbe) });
            var first = context.Get<TransientProbe>();
            var second = context.Get<TransientProbe>();
            context.Dispose();

            Assert.AreNotSame(first, second);
            Assert.AreEqual(0, TransientProbe.Disposed, "the scope must not keep or dispose transients");
        }

        [Test]
        public void A_Registered_Instance_Is_Found_By_Its_Interface()
        {
            var config = new ConfigImplementation();
            var context = ApplicationContext.BuildFromTypes(
                Type.EmptyTypes, c => c.RegisterInstance(config));

            Assert.AreSame(config, context.Get<IConfigContract>());
            CollectionAssert.Contains(context.GetAll<IConfigContract>(), config);
            context.Dispose();
        }

        [Test]
        public void Strategy_Registries_Are_Built_Once_Per_Scope()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(AlphaStrategy) });
            var first = context.Get<StrategyRegistry<string, ITestStrategy>>();
            var second = context.Get<StrategyRegistry<string, ITestStrategy>>();
            Assert.AreSame(first, second);
            Assert.IsTrue(first.TryGet("alpha", out _));
            context.Dispose();
        }

        [Test]
        public void Describe_Lists_Definitions()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(DisposableProbe) });
            StringAssert.Contains(typeof(DisposableProbe).FullName, context.Describe());
            context.Dispose();
        }

        [Test]
        public void A_Container_Can_Be_Built_From_An_Explicit_Type_List()
        {
            var context = ApplicationContext.Build(new[] { typeof(DisposableProbe) });

            Assert.IsNotNull(context.Get<DisposableProbe>());
            context.Dispose();
        }

        [Test]
        public void Module_Boundary_Policy_Flags_Foreign_Non_Api_Types_Including_Generic_Arguments()
        {
            var options = new ModuleBoundaryOptions
            {
                Enabled = true,
                Strict = true
            };

            Assert.IsNull(ModuleBoundaryPolicy.Check(typeof(TopBean), typeof(ApplicationContext), options),
                "the DI core is shared infrastructure");
            Assert.IsNull(ModuleBoundaryPolicy.Check(typeof(TopBean), typeof(string), options));
            Assert.IsNotNull(ModuleBoundaryPolicy.Check(typeof(ApplicationContext), typeof(TopBean), options),
                "UniSpring.DependencyInjection must not reach into a test module's internals");
            Assert.IsNotNull(ModuleBoundaryPolicy.Check(
                typeof(ApplicationContext), typeof(StrategyRegistry<string, ITestStrategy>), options));
            Assert.IsNull(ModuleBoundaryPolicy.Check(typeof(TopBean), typeof(NUnit.Framework.Assert), options),
                "assemblies outside the module layout are not module internals");
        }

        [Test]
        public void No_Autowired_Component_Hides_The_Base_Awake()
        {
            var offenders = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = Array.FindAll(exception.Types, type => type != null);
                }

                foreach (var type in types)
                {
                    if (!typeof(AutowiredMonoBehaviour).IsAssignableFrom(type) || type == typeof(AutowiredMonoBehaviour))
                        continue;
                    var awake = type.GetMethod("Awake",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                    if (awake != null)
                        offenders.Add(type.FullName);
                }
            }

            Assert.IsEmpty(offenders,
                "these components declare Awake and therefore never get their dependencies:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void Autowiring_Reuses_Reflection_Metadata()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(DisposableProbe), typeof(MiddleDependency) });
            var target = new AutowireTarget();
            context.Autowire(target); // warm the plan

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var index = 0; index < 1000; index++)
                context.Autowire(new AutowireTarget());
            watch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.IsNotNull(target.Probe);
            Assert.Less(watch.ElapsedMilliseconds, 250, "1000 autowires took " + watch.ElapsedMilliseconds + " ms");
            TestContext.WriteLine("1000 autowires: " + watch.ElapsedMilliseconds + " ms, " +
                                  allocated + " bytes allocated (targets included)");
            context.Dispose();
        }

        [Test]
        public void An_Overridden_PostConstruct_Runs_Once()
        {
            var context = ApplicationContext.BuildFromTypes(new[] { typeof(DerivedInit) });

            Assert.AreEqual(1, context.Get<DerivedInit>().Calls);
            context.Dispose();
        }

        [Test]
        public void Editor_And_Test_Assemblies_Are_Recognized_By_A_Whole_Name_Segment()
        {
            Assert.IsTrue(RuntimeContext.IsScannedByDefault(typeof(ApplicationContext).Assembly));
            Assert.IsFalse(RuntimeContext.IsScannedByDefault(typeof(ApplicationContextTests).Assembly),
                "UniSpring.DependencyInjection.Tests has a Tests segment");
            Assert.IsFalse(RuntimeContext.IsScannedByDefault(null));
        }

        public abstract class BaseInit
        {
            public int Calls;

            [PostConstruct]
            protected virtual void Init() => Calls++;
        }

        [Component]
        public sealed class DerivedInit : BaseInit
        {
            protected override void Init() => Calls++;
        }

        [Test]
        public void Mutable_Collection_Contracts_Are_Not_Injected()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ApplicationContext.BuildFromTypes(new[] { typeof(DisposableProbe), typeof(MutableListConsumer) }));
            var context = ApplicationContext.BuildFromTypes(
                new[] { typeof(DisposableProbe), typeof(ReadOnlyListConsumer) });
            Assert.AreEqual(1, context.Get<ReadOnlyListConsumer>().Probes.Count);
            context.Dispose();
        }

        // ---- test types (only ever scanned explicitly through BuildFromTypes) ----

        [Service]
        public sealed class MutableListConsumer
        {
            public MutableListConsumer(IList<DisposableProbe> probes)
            {
            }
        }

        [Service]
        public sealed class ReadOnlyListConsumer
        {
            public readonly IReadOnlyList<DisposableProbe> Probes;

            public ReadOnlyListConsumer(IReadOnlyList<DisposableProbe> probes)
            {
                Probes = probes;
            }
        }

        public interface IMissingContract
        {
        }

        public interface IConfigContract
        {
        }

        public interface ITestStrategy : IKeyedStrategy<string>
        {
        }

        public sealed class ConfigImplementation : IConfigContract
        {
        }

        [Service]
        [UniSpring.DependencyInjection.Order(-10)]
        public sealed class DisposableProbe : IDisposable
        {
            public static int Disposed;
            public void Dispose() => Disposed++;
        }

        [Service]
        public sealed class FailingBean
        {
            public FailingBean(DisposableProbe probe)
            {
                throw new InvalidOperationException("first failure");
            }
        }

        [Service]
        public sealed class TopBean
        {
            public TopBean(MiddleBean middle)
            {
            }
        }

        [Service]
        public sealed class MiddleBean
        {
            public MiddleBean(IMissingContract missing)
            {
            }
        }

        [Service]
        public sealed class MiddleDependency
        {
        }

        [Service]
        [Lifetime(BeanLifetime.Transient)]
        public sealed class TransientProbe : IDisposable
        {
            public static int Disposed;
            public void Dispose() => Disposed++;
        }

        [Strategy]
        public sealed class AlphaStrategy : ITestStrategy
        {
            public IEnumerable<string> Keys
            {
                get { yield return "alpha"; }
            }
        }

        public sealed class AutowireTarget
        {
            [Autowired] public DisposableProbe Probe;
            [Autowired] private MiddleDependency m_A;
            [Autowired] private MiddleDependency m_B;
            [Autowired] private DisposableProbe m_C;
            [Autowired] private MiddleDependency m_D;
        }
    }
}
