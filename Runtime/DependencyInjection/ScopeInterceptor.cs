using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Around advice that holds a scope open for the whole call: opened before the method runs and disposed when it
    /// has finished — for a method that returns <see cref="Task"/> or <see cref="Task{TResult}"/>, when that task
    /// completes, not when the method returns its task. The scope is disposed whether the method succeeds or throws.
    /// An async method's scope stays in its own flow: whatever the scope sets in an <c>AsyncLocal</c> is seen by the
    /// method and every call it awaits, not by its caller after the call returns the task. Other awaitables
    /// (<c>ValueTask</c> and the like) are refused, so a scope never closes before the work it covers.
    /// </summary>
    public abstract class ScopeInterceptor : IMethodInterceptor
    {
        /// <summary>Whether the method is advised; whether it can be is checked here, at the bean's creation.</summary>
        public bool Matches(MethodInfo targetMethod)
        {
            if (targetMethod == null || !Applies(targetMethod))
                return false;
            RequireAdvisable(targetMethod);
            MethodScope.For(targetMethod.ReturnType);
            return true;
        }

        /// <summary>
        /// A generic method's return type is known only per call, and an iterator runs after it returns: either would
        /// let the scope close before the work it covers.
        /// </summary>
        private void RequireAdvisable(MethodInfo targetMethod)
        {
            var name = targetMethod.DeclaringType?.FullName + "." + targetMethod.Name;
            if (targetMethod.IsGenericMethodDefinition)
                throw new NotSupportedException(GetType().Name + " cannot advise the generic method " + name + ".");
            if (targetMethod.IsDefined(typeof(IteratorStateMachineAttribute), false) ||
                targetMethod.IsDefined(typeof(AsyncIteratorStateMachineAttribute), false))
                throw new NotSupportedException(GetType().Name + " cannot advise the iterator " + name +
                                                ": it runs after the call returns, outside the scope.");
        }

        public object Invoke(IMethodInvocation invocation)
            => MethodScope.For(invocation.Method.ReturnType).Run(this, invocation);

        /// <summary>The pointcut proper: usually an attribute on <paramref name="targetMethod"/>.</summary>
        protected abstract bool Applies(MethodInfo targetMethod);

        /// <summary>Opens the scope of one call.</summary>
        protected abstract IDisposable Open(IMethodInvocation invocation);

        /// <summary>
        /// Called once when the call ends, before the scope is disposed:
        /// <paramref name="error"/> is null when the method returned normally.
        /// The default does nothing; override it when the advice has to commit
        /// or roll back. An exception thrown here replaces the method's own.
        /// </summary>
        protected virtual void Complete(IDisposable scope, Exception error)
        {
        }

        internal IDisposable OpenFor(IMethodInvocation invocation)
            => Open(invocation) ?? throw new InvalidOperationException(
                GetType().FullName + " opened no scope for " + invocation.Method.Name + ".");

        internal void CompleteFor(IDisposable scope, Exception error)
            => Complete(scope, error);
    }

    /// <summary>How a scope spans a call, by the method's return type; one per return type.</summary>
    internal abstract class MethodScope
    {
        private static readonly ConcurrentDictionary<Type, MethodScope> s_ByReturnType =
            new ConcurrentDictionary<Type, MethodScope>();

        public static MethodScope For(Type returnType) => s_ByReturnType.GetOrAdd(returnType, Create);

        public abstract object Run(ScopeInterceptor interceptor, IMethodInvocation invocation);

        /// <remarks>
        /// The DI core cannot take its own strategies from the container it is building, so the two shapes (a call
        /// that ends when it returns, a call that ends with its task) are chosen here, once per return type.
        /// </remarks>
        private static MethodScope Create(Type returnType)
        {
            if (typeof(Task).IsAssignableFrom(returnType))
                return CreateAwaited(returnType);
            if (returnType.GetMethod("GetAwaiter", Type.EmptyTypes) != null)
                throw new NotSupportedException(
                    "UniSpring DI scope advice supports methods that return a value, Task or Task<T>; " +
                    returnType.FullName + " would close the scope before the work it covers.");
            return new Synchronous();
        }

        private static MethodScope CreateAwaited(Type returnType)
        {
            var result = returnType.IsGenericType ? returnType.GetGenericArguments()[0] : typeof(object);
            return (MethodScope)Activator.CreateInstance(typeof(Awaited<>).MakeGenericType(result));
        }

        private sealed class Synchronous : MethodScope
        {
            public override object Run(ScopeInterceptor interceptor, IMethodInvocation invocation)
            {
                var scope = interceptor.OpenFor(invocation);
                try
                {
                    object result;
                    try
                    {
                        result = invocation.Proceed();
                    }
                    catch (Exception exception)
                    {
                        interceptor.CompleteFor(scope, exception);
                        throw;
                    }

                    interceptor.CompleteFor(scope, null);
                    return result;
                }
                finally
                {
                    scope.Dispose();
                }
            }
        }

        /// <summary>A plain <see cref="Task"/> runs as <c>Task&lt;object&gt;</c>, which is a Task too.</summary>
        private sealed class Awaited<TResult> : MethodScope
        {
            public override object Run(ScopeInterceptor interceptor, IMethodInvocation invocation)
                => RunAsync(interceptor, invocation);

            // An async method: what Open sets in an AsyncLocal stays in this flow and does not leak to the caller.
            private static async Task<TResult> RunAsync(ScopeInterceptor interceptor, IMethodInvocation invocation)
            {
                var scope = interceptor.OpenFor(invocation);
                try
                {
                    TResult result;
                    try
                    {
                        var task = invocation.Proceed() as Task ?? throw new InvalidOperationException(
                            invocation.TargetMethod.DeclaringType?.FullName + "." + invocation.TargetMethod.Name +
                            " returned no task.");
                        // No hop to the caller's context here: the scope closes as soon as the method's task completes,
                        // and the caller's own await goes back to its context as it always does.
                        await task.ConfigureAwait(false);
                        result = task is Task<TResult> typed ? typed.Result : default;
                    }
                    catch (Exception exception)
                    {
                        interceptor.CompleteFor(scope, exception);
                        throw;
                    }

                    interceptor.CompleteFor(scope, null);
                    return result;
                }
                finally
                {
                    scope.Dispose();
                }
            }
        }
    }
}
