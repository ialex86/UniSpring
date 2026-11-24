using UniSpring.DependencyInjection;
using UniSpring.Logging;
using UnityEngine;

namespace UniSpring.Samples.BasicSetup
{
    /// <summary>
    /// Creates a scene object with this component and presses Play. The
    /// container resolves <see cref="IGreetingService"/> and GameLog writes one
    /// entry to the Console.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class BasicSetup : AutowiredMonoBehaviour
    {
        [Autowired]
        private IGreetingService m_Greeting;

        protected override void OnDependenciesInjected()
        {
            GameLog.Info(m_Greeting.Greet("UniSpring"));
        }
    }
}
