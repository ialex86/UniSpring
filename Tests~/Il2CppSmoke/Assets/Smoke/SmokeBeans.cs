using System.Collections.Generic;
using UniSpring.DependencyInjection;

namespace UniSpringSmoke
{
    public enum SmokeKind
    {
        Alpha,
        Beta
    }

    public interface ISmokeStrategy : IKeyedStrategy<SmokeKind>
    {
        string Name { get; }
    }

    [Strategy]
    public sealed class AlphaStrategy : ISmokeStrategy
    {
        public IEnumerable<SmokeKind> Keys
        {
            get { yield return SmokeKind.Alpha; }
        }

        public string Name => "alpha";
    }

    [Strategy]
    public sealed class BetaStrategy : ISmokeStrategy
    {
        public IEnumerable<SmokeKind> Keys
        {
            get { yield return SmokeKind.Beta; }
        }

        public string Name => "beta";
    }

    [Component]
    public sealed class SmokeCounters
    {
        public int Ticks;
        public int Starts;
        public string Strategy;
    }

    [Component]
    public sealed class SmokeTicker : ITickable, ILifecycle
    {
        private readonly SmokeCounters m_Counters;

        // A registry keyed by an enum, injected: IL2CPP compiles its constructor only through the generated hint.
        public SmokeTicker(SmokeCounters counters, StrategyRegistry<SmokeKind, ISmokeStrategy> strategies)
        {
            m_Counters = counters;
            m_Counters.Strategy = strategies.Require(SmokeKind.Alpha).Name;
        }

        public void Tick(float deltaTime) => m_Counters.Ticks++;

        public void Start() => m_Counters.Starts++;

        public void Stop()
        {
        }
    }
}
