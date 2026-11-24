using System;

namespace UniSpring.DependencyInjection
{
    internal sealed class BeanDefinition
    {
        public Type ImplementationType;
        public int Order;
        public bool Primary;
        public BeanLifetime Lifetime;
    }

    internal static class BeanDefinitionOrdering
    {
        public static int Compare(BeanDefinition left, BeanDefinition right)
        {
            var order = left.Order.CompareTo(right.Order);
            if (order != 0)
                return order;

            return string.Compare(
                left.ImplementationType.FullName,
                right.ImplementationType.FullName,
                StringComparison.Ordinal);
        }
    }
}
