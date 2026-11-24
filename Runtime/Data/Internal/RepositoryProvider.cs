using System;
using UniSpring.DependencyInjection;

namespace UniSpring.Data.Internal
{
    /// <summary>
    /// Hands the container the object behind every <see cref="RepositoryAttribute"/> interface a bean asks for.
    /// The store, the serializer, the mappers and the validators are taken from the container when the first
    /// repository is needed, so an application that declares no repository needs none of them.
    /// </summary>
    [Component]
    internal sealed class RepositoryProvider : IContractProvider
    {
        private RepositoryFactory m_Factory;

        public bool Provides(Type contract)
            => contract.IsInterface && contract.IsDefined(typeof(RepositoryAttribute), false);

        public object Create(Type contract, ApplicationContext context)
        {
            if (m_Factory == null)
            {
                m_Factory = new RepositoryFactory(
                    context.Get<IDocumentStore>(),
                    context.Get<IDocumentSerializer>(),
                    context.GetAll<IRecordMapper>(),
                    context.GetAll<IDocumentValidator>());
            }

            return m_Factory.Create(contract);
        }
    }
}
