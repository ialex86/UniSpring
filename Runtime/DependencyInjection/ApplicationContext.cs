using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Mutable DI scope bound to the thread that created the root context.
    /// </summary>
    public sealed class ApplicationContext : IDisposable
    {
        /// <summary>
        /// Definitions are immutable after the scan, so the candidate lists for
        /// a contract are computed once and shared by the root and its scopes.
        /// </summary>
        private sealed class BeanDefinitionIndex
        {
            public readonly List<BeanDefinition> Definitions;
            public readonly Dictionary<Type, List<BeanDefinition>> Candidates =
                new Dictionary<Type, List<BeanDefinition>>();
            public readonly List<string> BoundaryViolations = new List<string>();
            public readonly HashSet<string> ReportedBoundaries = new HashSet<string>(StringComparer.Ordinal);
            public readonly ModuleBoundaryOptions BoundaryOptions;

            public BeanDefinitionIndex(
                List<BeanDefinition> definitions,
                ModuleBoundaryOptions boundaryOptions)
            {
                Definitions = definitions;
                BoundaryOptions = boundaryOptions ?? new ModuleBoundaryOptions();
            }
        }

        /// <summary>Contracts being resolved on this thread, outermost first (for error messages).</summary>
        [ThreadStatic]
        private static List<Type> s_ResolutionPath;

        private readonly BeanDefinitionIndex m_Index;
        private readonly List<BeanDefinition> m_Definitions;
        private readonly BeanFactory m_BeanFactory;
        private readonly ApplicationContext m_Parent;
        private readonly int m_OwningThreadId;
        private readonly List<ApplicationContext> m_Children =
            new List<ApplicationContext>();
        private readonly Dictionary<BeanDefinition, object> m_Instances =
            new Dictionary<BeanDefinition, object>();
        private readonly Dictionary<Type, object> m_RegisteredInstances =
            new Dictionary<Type, object>();
        private readonly HashSet<BeanDefinition> m_Creating =
            new HashSet<BeanDefinition>();
        private readonly List<IDisposable> m_Disposables =
            new List<IDisposable>();
        private readonly Dictionary<Type, object> m_StrategyRegistries =
            new Dictionary<Type, object>();

        /// <summary>The root's, shared by its scopes: interceptors and proxies (see <see cref="IMethodInterceptor"/>).</summary>
        private readonly AopProxyFactory m_Aop;

        private bool m_Disposed;
        private bool m_Disposing;

        /// <summary>Root only: the lifecycle beans started, in start order.</summary>
        private readonly List<ILifecycle> m_Started = new List<ILifecycle>();
        private IReadOnlyCollection<string> m_Profiles = Array.Empty<string>();

        /// <summary>Root only: set when warm-up begins (see <see cref="AdviseRegistered"/>).</summary>
        private bool m_WarmUpStarted;

        /// <summary>Root only: beans made by <see cref="IContractProvider"/>s, by contract.</summary>
        private readonly Dictionary<Type, object> m_Provided = new Dictionary<Type, object>();
        private IContractProvider[] m_Providers;
        private bool m_CollectingProviders;

        private ApplicationContext(
            BeanDefinitionIndex index,
            BeanLifetime lifetime,
            ApplicationContext parent,
            int owningThreadId)
        {
            m_Index = index;
            m_Definitions = index.Definitions;
            Lifetime = lifetime;
            m_Parent = parent;
            m_OwningThreadId = owningThreadId;
            m_Aop = parent?.m_Aop ?? new AopProxyFactory();
            m_BeanFactory = new BeanFactory(Get, CheckBoundary);
        }

        public BeanLifetime Lifetime { get; }

        /// <summary>The profiles the container was built with (see <see cref="ProfileAttribute"/>).</summary>
        public IReadOnlyCollection<string> Profiles => RootContext().m_Profiles;

        public ApplicationContext Parent => m_Parent;

        public bool IsDisposed
        {
            get
            {
                AssertOwningThread();
                return m_Disposed;
            }
        }

        public static ApplicationContext Build(IEnumerable<Assembly> assemblies)
        {
            return Build(assemblies, null, null);
        }

        /// <summary>
        /// Two-phase build: scan the beans, let the caller register instance
        /// overrides (for example authored configuration assets) and only then
        /// warm up. Instance registrations therefore take effect for services
        /// created during <c>WarmUp</c>, instead of arriving after the services
        /// already read their defaults.
        /// </summary>
        public static ApplicationContext Build(
            IEnumerable<Assembly> assemblies,
            Action<ApplicationContext> configureBeforeWarmUp)
        {
            return Build(assemblies, null, configureBeforeWarmUp);
        }

        /// <summary>
        /// Builds a container with an optional module-boundary policy. Pass
        /// <paramref name="boundaryOptions"/> to enable the "Api-only between
        /// modules" check; the default policy is disabled.
        /// </summary>
        public static ApplicationContext Build(
            IEnumerable<Assembly> assemblies,
            ModuleBoundaryOptions boundaryOptions,
            Action<ApplicationContext> configureBeforeWarmUp)
        {
            return Build(assemblies, new ContextOptions { Boundaries = boundaryOptions, Configure = configureBeforeWarmUp });
        }

        /// <summary>
        /// Builds a container from an explicit set of component types. Use it
        /// in tests and when a composition root should scan a known list
        /// instead of whole assemblies.
        /// </summary>
        public static ApplicationContext Build(IEnumerable<Type> componentTypes)
        {
            return Build(componentTypes, null, null);
        }

        /// <summary>
        /// Builds a container from an explicit set of component types and lets
        /// the caller register instance overrides before warm-up.
        /// </summary>
        public static ApplicationContext Build(
            IEnumerable<Type> componentTypes,
            Action<ApplicationContext> configureBeforeWarmUp)
        {
            return Build(componentTypes, null, configureBeforeWarmUp);
        }

        /// <summary>
        /// Builds a container from an explicit set of component types with an
        /// optional module-boundary policy.
        /// </summary>
        public static ApplicationContext Build(
            IEnumerable<Type> componentTypes,
            ModuleBoundaryOptions boundaryOptions,
            Action<ApplicationContext> configureBeforeWarmUp)
        {
            return Build(componentTypes, new ContextOptions { Boundaries = boundaryOptions, Configure = configureBeforeWarmUp });
        }

        /// <summary>Builds a container from the scanned assemblies with the given options.</summary>
        public static ApplicationContext Build(IEnumerable<Assembly> assemblies, ContextOptions options)
        {
            options = options ?? new ContextOptions();
            var profiles = options.Profiles ?? ContextOptions.DefaultProfiles();
            return BuildFromDefinitions(
                ComponentScanner.Scan(assemblies, profiles), options.Boundaries, options.Configure, profiles);
        }

        /// <summary>Builds a container from an explicit set of component types with the given options.</summary>
        public static ApplicationContext Build(IEnumerable<Type> componentTypes, ContextOptions options)
        {
            options = options ?? new ContextOptions();
            var profiles = options.Profiles ?? ContextOptions.DefaultProfiles();
            return BuildFromDefinitions(
                ComponentScanner.Scan(componentTypes, profiles), options.Boundaries, options.Configure, profiles);
        }

        /// <summary>Builds a container from an explicit set of component types (focused tests).</summary>
        internal static ApplicationContext BuildFromTypes(
            IEnumerable<Type> componentTypes,
            Action<ApplicationContext> configureBeforeWarmUp = null)
        {
            return Build(componentTypes, new ContextOptions { Configure = configureBeforeWarmUp });
        }

        /// <summary>
        /// A failed warm-up disposes every bean already created (files,
        /// subscriptions, the logging backend) and rethrows the FIRST error
        /// unchanged; a dispose failure is attached to it, never substituted.
        /// </summary>
        private static ApplicationContext BuildFromDefinitions(
            List<BeanDefinition> definitions,
            ModuleBoundaryOptions boundaryOptions,
            Action<ApplicationContext> configureBeforeWarmUp,
            ICollection<string> profiles)
        {
            var context = new ApplicationContext(
                new BeanDefinitionIndex(definitions, boundaryOptions),
                BeanLifetime.Application,
                null,
                Thread.CurrentThread.ManagedThreadId);
            context.m_Profiles = new List<string>(profiles);
            try
            {
                configureBeforeWarmUp?.Invoke(context);
                context.WarmUp();
                context.StartLifecycle();
                return context;
            }
            catch (Exception primary)
            {
                try
                {
                    context.Dispose();
                }
                catch (Exception disposeFailure)
                {
                    primary.Data["UniSpring.DI.DisposeFailure"] = disposeFailure.ToString();
                }

                throw;
            }
        }

        /// <summary>
        /// Cross-module dependencies on non-Api types seen so far (see
        /// <see cref="ModuleBoundaryPolicy"/>). Empty when every module talks to
        /// its neighbours through their Api assemblies only.
        /// </summary>
        public IReadOnlyList<string> BoundaryViolations => m_Index.BoundaryViolations;

        /// <summary>Human-readable list of definitions and which ones this scope created.</summary>
        public string Describe()
        {
            AssertOwningThread();
            var builder = new System.Text.StringBuilder();
            builder.Append("UniSpring DI ").Append(Lifetime).Append(" scope: ")
                .Append(m_Definitions.Count).Append(" definitions, ")
                .Append(m_Instances.Count).Append(" created, ")
                .Append(m_RegisteredInstances.Count).Append(" registered instances\n");
            for (var index = 0; index < m_Definitions.Count; index++)
            {
                var definition = m_Definitions[index];
                builder.Append(m_Instances.ContainsKey(definition) ? "  [created] " : "  [ ]       ")
                    .Append(definition.Lifetime).Append(' ')
                    .Append(definition.ImplementationType.FullName);
                if (definition.Primary)
                    builder.Append(" [Primary]");
                builder.Append('\n');
            }

            foreach (var registered in m_RegisteredInstances)
                builder.Append("  [instance] ").Append(registered.Key.FullName).Append('\n');
            return builder.ToString();
        }

        public ApplicationContext CreateScope(BeanLifetime lifetime)
        {
            AssertOwningThread();
            ThrowIfDisposed();

            if (lifetime == BeanLifetime.Application)
            {
                throw new ArgumentException(
                    "Application lifetime is owned by the root ApplicationContext.",
                    nameof(lifetime));
            }

            if (lifetime == BeanLifetime.Transient)
            {
                throw new ArgumentException(
                    "Transient lifetime does not own a scope.",
                    nameof(lifetime));
            }

            var scope = new ApplicationContext(
                m_Index,
                lifetime,
                this,
                m_OwningThreadId);
            m_Children.Add(scope);
            return scope;
        }

        /// <summary>
        /// Registers an externally owned instance under the contract <typeparamref name="T"/>. It wins over
        /// scanned beans for that contract; collections include it alongside them. Register a strategy instance
        /// before a matching <see cref="StrategyRegistry{TKey,TStrategy}"/> is resolved: a registry is a snapshot.
        /// </summary>
        public void RegisterInstance<T>(T instance)
            where T : class
        {
            AssertOwningThread();
            ThrowIfDisposed();

            if (instance == null)
                throw new ArgumentNullException(nameof(instance));
            if (HasRegistryFor(instance))
            {
                throw new InvalidOperationException(
                    "UniSpring DI: a StrategyRegistry that would include " + instance.GetType().FullName +
                    " is already built in this scope or its children. Register strategy instances before " +
                    "the registry is resolved.");
            }

            var contract = typeof(T);
            if (m_RegisteredInstances.ContainsKey(contract))
            {
                throw new InvalidOperationException(
                    "UniSpring DI already has a registered instance for " +
                    contract.FullName + " in " + Lifetime + " scope.");
            }

            m_RegisteredInstances.Add(contract, instance);
        }

        public T Get<T>()
        {
            return (T)Get(typeof(T));
        }

        public object Get(Type contract)
        {
            AssertOwningThread();
            ThrowIfDisposed();

            if (contract == null)
                throw new ArgumentNullException(nameof(contract));

            var path = s_ResolutionPath ?? (s_ResolutionPath = new List<Type>());
            path.Add(contract);
            try
            {
                return GetCore(contract);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        private object GetCore(Type contract)
        {
            if (contract == typeof(ApplicationContext))
                return this;

            object registered;
            if (TryGetRegisteredInstance(contract, out registered))
                return AdviseRegistered(registered, contract);

            if (IsStrategyRegistryType(contract))
                return GetStrategyRegistry(contract);

            Type collectionElement;
            if (TryGetCollectionElementType(contract, out collectionElement))
                return CreateCollection(contract, collectionElement);

            var candidates = FindCandidates(contract);
            if (candidates.Count == 0 && TryGetAssignableRegisteredInstance(contract, out registered))
                return AdviseRegistered(registered, contract);

            object provided;
            if (candidates.Count == 0 && RootContext().TryProvide(contract, out provided))
                return provided;

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "UniSpring DI has no bean assignable to " +
                    contract.FullName + "." + DescribePath());
            }

            var selected = SelectSingleCandidate(contract, candidates);
            return Advise(Resolve(selected), contract);
        }

        private static string DescribePath()
        {
            var path = s_ResolutionPath;
            if (path == null || path.Count < 2)
                return string.Empty;
            var names = new string[path.Count];
            for (var index = 0; index < path.Count; index++)
                names[index] = path[index].Name;
            return " Resolution path: " + string.Join(" -> ", names) + ".";
        }

        private void CheckBoundary(Type consumer, Type dependency)
        {
            var options = m_Index.BoundaryOptions;
            var violation = ModuleBoundaryPolicy.Check(consumer, dependency, options);
            if (violation == null)
                return;
            if (options.Strict)
                throw new InvalidOperationException("UniSpring DI module boundary: " + violation);
            if (m_Index.ReportedBoundaries.Add(violation))
                m_Index.BoundaryViolations.Add(violation);
        }

        public IReadOnlyList<T> GetAll<T>()
        {
            AssertOwningThread();
            ThrowIfDisposed();

            var values = GetAll(typeof(T));
            var result = new T[values.Count];
            for (var index = 0; index < values.Count; index++)
                result[index] = (T)values[index];

            return result;
        }

        public void Autowire(object target)
        {
            AssertOwningThread();
            ThrowIfDisposed();
            m_BeanFactory.Autowire(target);
        }

        public void Dispose()
        {
            AssertOwningThread();

            // A Stop that disposes the container (it may: the container can be injected) must not stop everything
            // again; the beans stay usable until every Stop has run.
            if (m_Disposed || m_Disposing)
                return;

            m_Disposing = true;
            Exception disposeFailure = StopLifecycle();
            m_Disposed = true;

            var children = m_Children.ToArray();
            for (var index = children.Length - 1; index >= 0; index--)
            {
                try
                {
                    children[index].Dispose();
                }
                catch (Exception exception)
                {
                    if (disposeFailure == null)
                        disposeFailure = exception;
                }
            }

            m_Children.Clear();

            for (var index = m_Disposables.Count - 1; index >= 0; index--)
            {
                try
                {
                    m_Disposables[index].Dispose();
                }
                catch (Exception exception)
                {
                    if (disposeFailure == null)
                        disposeFailure = exception;
                }
            }

            m_Disposables.Clear();
            m_StrategyRegistries.Clear();
            m_Provided.Clear();
            m_Instances.Clear();
            m_RegisteredInstances.Clear();
            m_Creating.Clear();
            m_Parent?.m_Children.Remove(this);

            if (disposeFailure != null)
            {
                throw new InvalidOperationException(
                    "UniSpring DI failed while disposing " +
                    Lifetime + " scope.",
                    disposeFailure);
            }
        }

        /// <summary>Root only: starts the lifecycle beans in bean order once every application bean exists.</summary>
        private void StartLifecycle()
        {
            var beans = GetAllInScope(typeof(ILifecycle));
            for (var index = 0; index < beans.Count; index++)
            {
                var bean = (ILifecycle)beans[index];
                bean.Start();
                m_Started.Add(bean);
            }
        }

        /// <summary>Stops the started lifecycle beans in reverse order; the first failure is returned, not thrown.</summary>
        private Exception StopLifecycle()
        {
            Exception failure = null;
            for (var index = m_Started.Count - 1; index >= 0; index--)
            {
                try
                {
                    m_Started[index].Stop();
                }
                catch (Exception exception)
                {
                    if (failure == null)
                        failure = exception;
                }
            }

            m_Started.Clear();
            return failure;
        }

        /// <summary>
        /// The beans this scope owns or inherits that are assignable to <paramref name="contract"/>, in bean order
        /// (registered instances first), each handed out as <paramref name="contract"/> (through its proxy when it
        /// has advised methods). Unlike <see cref="GetAll{T}"/> it skips Transient beans (they belong to a consumer,
        /// and collecting them would create instances nobody owns) and beans of a scope this one is not inside (a
        /// Scene bean seen from the root): for infrastructure that collects beans by role, such as lifecycle beans,
        /// listeners and tickables.
        /// </summary>
        internal IReadOnlyList<object> GetAllInScope(Type contract)
        {
            AssertOwningThread();
            ThrowIfDisposed();

            var result = new List<object>();
            var seen = new List<object>();
            for (var current = this; current != null; current = current.m_Parent)
            {
                foreach (var registered in current.m_RegisteredInstances.Values)
                {
                    if (!contract.IsInstanceOfType(registered) || seen.Contains(registered))
                        continue;
                    seen.Add(registered);
                    result.Add(AdviseRegistered(registered, contract));
                }
            }

            var candidates = FindCandidates(contract);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (candidate.Lifetime == BeanLifetime.Transient || FindScopeOwner(candidate.Lifetime) == null)
                    continue;
                result.Add(Advise(Resolve(candidate), contract));
            }

            return result;
        }

        /// <summary>The bean as <paramref name="contract"/>: through its proxy when that interface has advised methods.</summary>
        internal object AdviseAs(object instance, Type contract)
        {
            AssertOwningThread();
            return Advise(instance, contract);
        }

        private void WarmUp()
        {
            RootContext().m_WarmUpStarted = true;
            // Interceptors first: every bean made after them is checked and handed out through its proxies.
            EnsureInterceptors();
            for (var index = 0; index < m_Definitions.Count; index++)
            {
                var definition = m_Definitions[index];
                if (definition.Lifetime == BeanLifetime.Application)
                    Resolve(definition);
            }
        }

        private bool TryGetRegisteredInstance(Type contract, out object instance)
        {
            for (var current = this; current != null; current = current.m_Parent)
            {
                if (current.m_RegisteredInstances.TryGetValue(contract, out instance))
                    return true;
            }

            instance = null;
            return false;
        }

        /// <summary>Registered instances assignable to the contract first, then every bean.</summary>
        private IReadOnlyList<object> GetAll(Type contract, bool ownTransients = false)
        {
            var result = new List<object>();
            var seen = new List<object>();
            for (var current = this; current != null; current = current.m_Parent)
            {
                foreach (var registered in current.m_RegisteredInstances.Values)
                {
                    if (!contract.IsInstanceOfType(registered) || seen.Contains(registered))
                        continue;
                    seen.Add(registered);
                    result.Add(AdviseRegistered(registered, contract));
                }
            }

            var candidates = FindCandidates(contract);
            for (var index = 0; index < candidates.Count; index++)
            {
                var instance = Resolve(candidates[index]);
                // A transient kept by something this scope owns (a strategy registry) belongs to the scope.
                if (ownTransients && candidates[index].Lifetime == BeanLifetime.Transient && instance is IDisposable disposable)
                    m_Disposables.Add(disposable);
                result.Add(Advise(instance, contract));
            }

            return result;
        }

        /// <summary>
        /// A registered instance found by an interface it implements, used only
        /// when no bean matches (e.g. RegisterInstance&lt;Config&gt; then Get&lt;IConfig&gt;).
        /// </summary>
        private bool TryGetAssignableRegisteredInstance(Type contract, out object instance)
        {
            instance = null;
            var found = 0;
            for (var current = this; current != null; current = current.m_Parent)
            {
                foreach (var registered in current.m_RegisteredInstances.Values)
                {
                    if (!contract.IsInstanceOfType(registered) || ReferenceEquals(registered, instance))
                        continue;
                    instance = registered;
                    found++;
                }
            }

            if (found > 1)
            {
                throw new InvalidOperationException(
                    "UniSpring DI found several registered instances assignable to " +
                    contract.FullName + "; register one under that contract explicitly." + DescribePath());
            }

            return found == 1;
        }

        private object Resolve(BeanDefinition definition)
        {
            if (definition.Lifetime == BeanLifetime.Transient)
                return Create(definition, false);

            var owner = FindScopeOwner(definition.Lifetime);
            if (owner == null)
            {
                throw new InvalidOperationException(
                    "UniSpring DI cannot resolve " +
                    definition.Lifetime + " bean " +
                    definition.ImplementationType.FullName +
                    " from " + Lifetime +
                    " scope. Create the required child scope and resolve the dependency from it.");
            }

            return owner.GetOrCreate(definition);
        }

        private ApplicationContext FindScopeOwner(BeanLifetime lifetime)
        {
            for (var current = this; current != null; current = current.m_Parent)
            {
                if (current.Lifetime == lifetime)
                    return current;
            }

            return null;
        }

        private object GetOrCreate(BeanDefinition definition)
        {
            object existing;
            if (m_Instances.TryGetValue(definition, out existing))
                return existing;

            return Create(definition, true);
        }

        private object Create(BeanDefinition definition, bool cache)
        {
            if (!m_Creating.Add(definition))
            {
                throw new InvalidOperationException(
                    "UniSpring DI circular dependency while creating " +
                    definition.ImplementationType.FullName + "." + DescribePath());
            }

            var path = s_ResolutionPath ?? (s_ResolutionPath = new List<Type>());
            var implementation = definition.ImplementationType;
            var pushed = path.Count == 0 || path[path.Count - 1] != implementation;
            if (pushed)
                path.Add(implementation);
            try
            {
                // Refused before it is made: a bean whose advice could never run is never created.
                EnsureInterceptors();
                m_Aop.Validate(implementation);
                var instance = m_BeanFactory.Create(definition);
                if (cache)
                    m_Instances.Add(definition, instance);

                // Transients belong to their consumer: tracking them here would
                // keep every one alive until the scope ends and dispose them twice.
                var disposable = instance as IDisposable;
                if (cache && disposable != null)
                    m_Disposables.Add(disposable);

                return instance;
            }
            finally
            {
                m_Creating.Remove(definition);
                if (pushed)
                    path.RemoveAt(path.Count - 1);
            }
        }

        /// <summary>Beans are handed out as <paramref name="contract"/>: through a proxy when it has advised methods.</summary>
        private object Advise(object instance, Type contract)
        {
            EnsureInterceptors();
            return m_Aop.Advise(instance, contract);
        }

        /// <summary>
        /// A registered instance is advised like a bean: checked once and
        /// handed out through a proxy when the contract has advised methods,
        /// so its advice is never lost silently. Inside the configure hook
        /// (before warm-up) it is returned as it is: the interceptors may
        /// depend on instances that are still being registered. A warning
        /// is logged later if such an instance has advised methods.
        /// </summary>
        private object AdviseRegistered(object instance, Type contract)
        {
            // Not ready yet: handed out as it is, and reported if it turns out to be advised.
            if (!RootContext().m_WarmUpStarted && !m_Aop.IsReady)
                return m_Aop.Advise(instance, contract);
            EnsureInterceptors();
            m_Aop.Validate(instance.GetType());
            return m_Aop.Advise(instance, contract);
        }

        /// <summary>Root only: the bean a provider makes for the contract, created once.</summary>
        private bool TryProvide(Type contract, out object bean)
        {
            if (m_Provided.TryGetValue(contract, out bean))
                return true;

            var providers = Providers();
            for (var index = 0; index < providers.Length; index++)
            {
                if (!providers[index].Provides(contract))
                    continue;

                bean = providers[index].Create(contract, this);
                if (bean == null)
                {
                    throw new InvalidOperationException(
                        "UniSpring DI: " + providers[index].GetType().FullName + " provided no bean for " +
                        contract.FullName + "." + DescribePath());
                }

                m_Provided.Add(contract, bean);
                var disposable = bean as IDisposable;
                if (disposable != null)
                    m_Disposables.Add(disposable);
                return true;
            }

            bean = null;
            return false;
        }

        /// <summary>The provider beans, created on first need; while they are being created nothing is provided.</summary>
        private IContractProvider[] Providers()
        {
            if (m_Providers != null)
                return m_Providers;
            if (m_CollectingProviders)
                return Array.Empty<IContractProvider>();

            m_CollectingProviders = true;
            try
            {
                var beans = GetAll(typeof(IContractProvider));
                var providers = new IContractProvider[beans.Count];
                for (var index = 0; index < beans.Count; index++)
                    providers[index] = (IContractProvider)beans[index];
                m_Providers = providers;
            }
            finally
            {
                m_CollectingProviders = false;
            }

            return m_Providers;
        }

        private ApplicationContext RootContext()
        {
            var root = this;
            while (root.m_Parent != null)
                root = root.m_Parent;
            return root;
        }

        /// <summary>The interceptor beans, created once from the root; what they depend on is handed out as it is.</summary>
        private void EnsureInterceptors()
        {
            if (m_Aop.IsReady)
                return;
            var root = RootContext();
            m_Aop.Collect(() => root.GetAll(typeof(IMethodInterceptor)));
        }

        private List<BeanDefinition> FindCandidates(Type contract)
        {
            List<BeanDefinition> cached;
            if (m_Index.Candidates.TryGetValue(contract, out cached))
                return cached;

            var candidates = new List<BeanDefinition>();
            for (var index = 0; index < m_Definitions.Count; index++)
            {
                var definition = m_Definitions[index];
                if (contract.IsAssignableFrom(definition.ImplementationType))
                    candidates.Add(definition);
            }

            candidates.Sort(BeanDefinitionOrdering.Compare);
            m_Index.Candidates[contract] = candidates;
            return candidates;
        }

        private static BeanDefinition SelectSingleCandidate(
            Type contract,
            List<BeanDefinition> candidates)
        {
            if (candidates.Count == 1)
                return candidates[0];

            BeanDefinition primary = null;
            var primaryCount = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                if (!candidates[index].Primary)
                    continue;

                primary = candidates[index];
                primaryCount++;
            }

            if (primaryCount == 1)
                return primary;

            throw new InvalidOperationException(
                "UniSpring DI found multiple beans assignable to " +
                contract.FullName +
                " and exactly one [Primary] was not defined: " +
                string.Join(", ", candidates
                    .Select(candidate => candidate.ImplementationType.FullName)
                    .ToArray()) +
                "." + DescribePath());
        }

        private object GetStrategyRegistry(Type registryType)
        {
            object registry;
            if (m_StrategyRegistries.TryGetValue(registryType, out registry))
            {
                if (registry == null)
                {
                    throw new InvalidOperationException(
                        "UniSpring DI circular strategy registry: " + registryType.FullName +
                        " is requested by one of its own strategies." + DescribePath());
                }

                return registry;
            }

            // Placeholder while the strategies are created: one of them asking for this registry is a cycle.
            m_StrategyRegistries.Add(registryType, null);
            try
            {
                registry = CreateStrategyRegistry(registryType);
                if (!m_Aop.IsReady)
                {
                    // Built for an interceptor's dependency chain: its strategies are not proxied (that is
                    // reported). It is not kept, so the registry handed out later holds the proxies.
                    m_StrategyRegistries.Remove(registryType);
                    return registry;
                }

                m_StrategyRegistries[registryType] = registry;
                return registry;
            }
            catch
            {
                m_StrategyRegistries.Remove(registryType);
                throw;
            }
        }

        private bool HasRegistryFor(object instance)
        {
            foreach (var registryType in m_StrategyRegistries.Keys)
            {
                if (registryType.GetGenericArguments()[1].IsInstanceOfType(instance))
                    return true;
            }

            for (var index = 0; index < m_Children.Count; index++)
            {
                if (m_Children[index].HasRegistryFor(instance))
                    return true;
            }

            return false;
        }

        private object CreateStrategyRegistry(Type registryType)
        {
            var arguments = registryType.GetGenericArguments();
            var strategyType = arguments[1];
            var beans = GetAll(strategyType, true);
            var strategyArray = Array.CreateInstance(strategyType, beans.Count);
            for (var index = 0; index < beans.Count; index++)
                strategyArray.SetValue(beans[index], index);

            return Activator.CreateInstance(registryType, new object[] { strategyArray });
        }

        private static bool IsStrategyRegistryType(Type contract)
        {
            return contract.IsGenericType &&
                   contract.GetGenericTypeDefinition() == typeof(StrategyRegistry<,>);
        }

        private object CreateCollection(Type collectionType, Type elementType)
        {
            var beans = GetAll(elementType);
            var array = Array.CreateInstance(elementType, beans.Count);
            for (var index = 0; index < beans.Count; index++)
                array.SetValue(beans[index], index);

            if (collectionType.IsArray || collectionType.IsAssignableFrom(array.GetType()))
                return array;

            if (collectionType.IsGenericType)
            {
                var genericDefinition = collectionType.GetGenericTypeDefinition();
                if (genericDefinition == typeof(ICollection<>) ||
                    genericDefinition == typeof(IReadOnlyCollection<>))
                {
                    return array;
                }
            }

            throw new InvalidOperationException(
                "UniSpring DI collection type is unsupported: " +
                collectionType.FullName + ".");
        }

        private static bool TryGetCollectionElementType(Type contract, out Type elementType)
        {
            elementType = null;
            if (contract.IsArray)
            {
                elementType = contract.GetElementType();
                if (elementType == null || contract != elementType.MakeArrayType())
                {
                    throw new InvalidOperationException(
                        "UniSpring DI injects one-dimensional arrays only: " + contract.FullName + ".");
                }

                return true;
            }

            if (!contract.IsGenericType)
                return false;

            // Only read-only contracts: IList<T>/ICollection<T> would suggest the
            // consumer can change the container's set of beans. Every resolution
            // gets its own array.
            var definition = contract.GetGenericTypeDefinition();
            if (definition != typeof(IEnumerable<>) &&
                definition != typeof(IReadOnlyList<>) &&
                definition != typeof(IReadOnlyCollection<>))
            {
                return false;
            }

            elementType = contract.GetGenericArguments()[0];
            return true;
        }

        private void AssertOwningThread()
        {
            if (Thread.CurrentThread.ManagedThreadId == m_OwningThreadId)
                return;

            throw new InvalidOperationException(
                "UniSpring DI ApplicationContext can only be accessed from its owning thread.");
        }

        private void ThrowIfDisposed()
        {
            if (!m_Disposed)
                return;

            throw new ObjectDisposedException(
                nameof(ApplicationContext),
                "UniSpring DI " + Lifetime + " scope is already disposed.");
        }
    }
}
