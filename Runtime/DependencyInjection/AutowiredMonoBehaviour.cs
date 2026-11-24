using UnityEngine;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Centralized Unity composition lifecycle for MonoBehaviours with [Autowired] members.
    /// A component is autowired from the Scene scope of its own scene
    /// (<see cref="RuntimeContext.ScopeOf"/>): it sees the Scene beans of that
    /// scene and every Application bean, and the Scene beans go away with the
    /// scene. Do NOT declare Awake in a subclass: it hides this one and the
    /// component silently gets no dependencies.
    /// Override <see cref="OnDependenciesInjected"/> instead.
    /// </summary>
    public abstract class AutowiredMonoBehaviour : MonoBehaviour
    {
        protected void Awake()
        {
            RuntimeContext.ScopeOf(gameObject.scene).Autowire(this);
            OnDependenciesInjected();
        }

        /// <summary>
        /// Override instead of declaring Awake so dependencies are available before initialization.
        /// </summary>
        protected virtual void OnDependenciesInjected()
        {
        }
    }
}
