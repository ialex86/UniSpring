using System;

namespace UniSpring.DependencyInjection
{
    public enum BeanLifetime
    {
        Application,
        Scene,
        Transient
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class LifetimeAttribute : Attribute
    {
        public BeanLifetime Value { get; }

        public LifetimeAttribute(BeanLifetime value)
        {
            Value = value;
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class ComponentAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class ServiceAttribute : ComponentAttribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class StrategyAttribute : ComponentAttribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class ComponentFamilyAttribute : Attribute
    {
    }

    [AttributeUsage(
        AttributeTargets.Constructor | AttributeTargets.Field | AttributeTargets.Property,
        Inherited = true)]
    public sealed class AutowiredAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PrimaryAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class OrderAttribute : Attribute
    {
        public int Value { get; }

        public OrderAttribute(int value)
        {
            Value = value;
        }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class PostConstructAttribute : Attribute
    {
    }
}
