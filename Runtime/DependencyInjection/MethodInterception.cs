using System.Reflection;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// An aspect: around advice for bean methods, in the spirit of Spring AOP. Any <c>[Component]</c> or
    /// <c>[Service]</c> implementing this interface is an interceptor; the container asks it once per bean method
    /// (<see cref="Matches"/>) and, when it matches, hands the bean out through a proxy that calls
    /// <see cref="Invoke"/> around that method. Several interceptors of one method run in their bean order
    /// (<see cref="OrderAttribute"/>), the first outermost.
    /// <para>
    /// As with Spring's interface proxies: only calls made through an interface the bean was resolved by are
    /// intercepted — a call the bean makes to its own method (<c>this.Method()</c>) is not, and a bean resolved by
    /// its class is never proxied (the container refuses it when the bean has advised methods). A matching method
    /// that implements no interface method is refused at the bean's creation, so advice is never lost silently.
    /// Interceptors are created first in the warm-up and are application beans; what they depend on is handed out
    /// unproxied. When such a dependency has advised methods itself, a warning says so: the interceptor's own
    /// reference skips the advice, everyone who resolves the bean later gets the proxy.
    /// </para>
    /// <para>
    /// The proxies are generated at run time (<see cref="DispatchProxy"/>, Reflection.Emit): Mono only, not IL2CPP.
    /// </para>
    /// </summary>
    public interface IMethodInterceptor
    {
        /// <summary>
        /// The pointcut: whether this interceptor advises <paramref name="targetMethod"/>, the implementation's
        /// method (its attributes are where the advice is declared). Asked once per bean type, at its creation;
        /// may throw to refuse a method it cannot advise.
        /// </summary>
        bool Matches(MethodInfo targetMethod);

        /// <summary>
        /// The advice. Calls <see cref="IMethodInvocation.Proceed"/> once to run the rest of the chain and the
        /// method itself, and returns what the method is to return.
        /// </summary>
        object Invoke(IMethodInvocation invocation);
    }

    /// <summary>One intercepted call.</summary>
    public interface IMethodInvocation
    {
        /// <summary>The interface method that was called.</summary>
        MethodInfo Method { get; }

        /// <summary>The bean's method that implements it.</summary>
        MethodInfo TargetMethod { get; }

        /// <summary>The bean (never the proxy).</summary>
        object Target { get; }

        /// <summary>The arguments; out and ref values are read back from here when the call returns.</summary>
        object[] Arguments { get; }

        /// <summary>Runs the next interceptor, or the method when this one is the last; its exceptions come through unwrapped.</summary>
        object Proceed();
    }
}
