using System;
using System.Collections.Generic;

namespace UniSpring.DependencyInjection
{
    public interface IKeyedStrategy<TKey>
    {
        IEnumerable<TKey> Keys { get; }
    }

    public sealed class StrategyRegistry<TKey, TStrategy>
        where TStrategy : IKeyedStrategy<TKey>
    {
        private readonly Dictionary<TKey, TStrategy> m_Strategies =
            new Dictionary<TKey, TStrategy>();

        public StrategyRegistry(IEnumerable<TStrategy> strategyBeans)
        {
            if (strategyBeans == null)
                throw new ArgumentNullException(nameof(strategyBeans));

            foreach (var strategy in strategyBeans)
            {
                if (strategy == null)
                    continue;

                var keys = strategy.Keys;
                if (keys == null)
                {
                    throw new InvalidOperationException(
                        "UniSpring DI strategy " + strategy.GetType().FullName +
                        " returned null Keys.");
                }

                foreach (var key in keys)
                {
                    TStrategy existing;
                    if (m_Strategies.TryGetValue(key, out existing))
                    {
                        throw new InvalidOperationException(
                            "UniSpring DI duplicate strategy key '" + key + "' for " +
                            typeof(TStrategy).FullName + ": " +
                            existing.GetType().FullName + " and " +
                            strategy.GetType().FullName + ".");
                    }

                    m_Strategies.Add(key, strategy);
                }
            }
        }

        public TStrategy Require(TKey key)
        {
            TStrategy strategy;
            if (!m_Strategies.TryGetValue(key, out strategy))
            {
                throw new InvalidOperationException(
                    "UniSpring DI has no " + typeof(TStrategy).FullName +
                    " strategy for key '" + key + "'.");
            }

            return strategy;
        }

        public bool TryGet(TKey key, out TStrategy strategy)
        {
            return m_Strategies.TryGetValue(key, out strategy);
        }
    }
}
