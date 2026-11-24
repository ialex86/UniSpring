using System;
using System.Collections.Generic;
using System.Reflection;

namespace UniSpring.Data.Internal
{
    /// <summary>
    /// Makes the object behind a <see cref="RepositoryAttribute"/> interface: finds which stored type it is for,
    /// wires that type's mapper and validators, and prepares a handler for every method. Everything that can be
    /// wrong with the interface is reported here, when the repository is created.
    /// </summary>
    internal sealed class RepositoryFactory
    {
        private readonly IDocumentStore m_Store;
        private readonly IDocumentSerializer m_Serializer;
        private readonly Dictionary<Type, RecordMapping> m_Mappings = new Dictionary<Type, RecordMapping>();
        private readonly Dictionary<Type, List<DocumentValidation>> m_Validations =
            new Dictionary<Type, List<DocumentValidation>>();
        private readonly Dictionary<string, Type> m_Areas = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<Type, object> m_Repositories = new Dictionary<Type, object>();

        public RepositoryFactory(
            IDocumentStore store,
            IDocumentSerializer serializer,
            IEnumerable<IRecordMapper> mappers,
            IEnumerable<IDocumentValidator> validators)
        {
            m_Store = store ?? throw new ArgumentNullException(nameof(store));
            m_Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            foreach (var mapper in mappers)
            {
                foreach (var mapping in RecordMapping.Of(mapper))
                    AddMapping(mapping, mapper);
            }

            foreach (var validator in validators)
            {
                foreach (var validation in DocumentValidation.Of(validator))
                    ValidationsOf(validation.DomainType).Add(validation);
            }
        }

        public object Create(Type contract)
        {
            object existing;
            if (m_Repositories.TryGetValue(contract, out existing))
                return existing;

            if (!contract.IsInterface || !contract.IsDefined(typeof(RepositoryAttribute), false))
                throw new InvalidOperationException(
                    "UniSpring Data: " + contract.FullName + " is not an interface marked [Repository].");

            var kind = KindOf(contract);
            var domain = kind.GetGenericArguments()[0];
            var single = kind.GetGenericTypeDefinition() == typeof(IDocument<>);
            var access = new DocumentAccess(
                domain, m_Store, m_Serializer, MappingOf(domain), ValidationsOf(domain).ToArray());

            var handlers = new Dictionary<MethodInfo, Func<object[], object>>();
            var area = single ? DocumentAccess.SingleArea : Claim(domain.Name, domain);
            if (single)
                AddDocumentHandlers(handlers, kind, access, Claim(DocumentAccess.SingleArea + "/" + domain.Name, domain));
            else
                AddRepositoryHandlers(handlers, kind, access, area);

            foreach (var method in DeclaredMethods(contract, kind))
            {
                if (single)
                    throw new InvalidOperationException(
                        "UniSpring Data cannot implement " + contract.FullName + "." + method.Name +
                        ": a single document has no query methods.");
                handlers.Add(method, QueryMethods.Build(method, access, area));
            }

            var repository = RepositoryProxy.Create(contract, handlers);
            m_Repositories.Add(contract, repository);
            return repository;
        }

        private static void AddDocumentHandlers(
            Dictionary<MethodInfo, Func<object[], object>> handlers, Type kind, DocumentAccess access, string claimed)
        {
            const string area = DocumentAccess.SingleArea;
            var name = access.DomainType.Name;
            handlers.Add(kind.GetMethod(nameof(IDocument<object>.Load)), arguments => access.Load(area, name));
            handlers.Add(kind.GetMethod(nameof(IDocument<object>.Exists)), arguments => access.Exists(area, name));
            handlers.Add(kind.GetMethod(nameof(IDocument<object>.Save)), arguments =>
            {
                access.Save(area, name, arguments[0]);
                return null;
            });
            handlers.Add(kind.GetMethod(nameof(IDocument<object>.Delete)), arguments =>
            {
                access.Delete(area, name);
                return null;
            });
        }

        private static void AddRepositoryHandlers(
            Dictionary<MethodInfo, Func<object[], object>> handlers, Type kind, DocumentAccess access, string area)
        {
            handlers.Add(kind.GetMethod(nameof(IRepository<object>.Find)), arguments => access.Load(area, (string)arguments[0]));
            handlers.Add(kind.GetMethod(nameof(IRepository<object>.Exists)), arguments => access.Exists(area, (string)arguments[0]));
            handlers.Add(kind.GetMethod(nameof(IRepository<object>.Save)), arguments =>
            {
                access.Save(area, (string)arguments[0], arguments[1]);
                return null;
            });
            handlers.Add(kind.GetMethod(nameof(IRepository<object>.Delete)), arguments =>
            {
                access.Delete(area, (string)arguments[0]);
                return null;
            });
            handlers.Add(kind.GetMethod(nameof(IRepository<object>.Ids)), arguments => access.Names(area));
            handlers.Add(kind.GetMethod(nameof(IRepository<object>.All)), arguments =>
            {
                var ids = access.Names(area);
                var values = new List<object>();
                for (var index = 0; index < ids.Count; index++)
                {
                    var value = access.Load(area, ids[index]);
                    if (value != null)
                        values.Add(value);
                }

                var array = Array.CreateInstance(access.DomainType, values.Count);
                for (var index = 0; index < values.Count; index++)
                    array.SetValue(values[index], index);
                return array;
            });
        }

        /// <summary>The closed <see cref="IRepository{T}"/> or <see cref="IDocument{T}"/> the interface derives from.</summary>
        private static Type KindOf(Type contract)
        {
            Type kind = null;
            foreach (var face in contract.GetInterfaces())
            {
                if (!face.IsGenericType)
                    continue;
                var definition = face.GetGenericTypeDefinition();
                if (definition != typeof(IRepository<>) && definition != typeof(IDocument<>))
                    continue;
                if (kind != null)
                    throw new InvalidOperationException(
                        "UniSpring Data: " + contract.FullName + " derives from several repository interfaces; " +
                        "it must derive from exactly one IRepository<T> or IDocument<T>.");
                kind = face;
            }

            if (kind == null)
                throw new InvalidOperationException(
                    "UniSpring Data: " + contract.FullName + " must derive from IRepository<T> or IDocument<T>.");
            return kind;
        }

        /// <summary>The methods the application added: everything but those of the base interface.</summary>
        private static IEnumerable<MethodInfo> DeclaredMethods(Type contract, Type kind)
        {
            foreach (var method in contract.GetMethods())
                yield return method;
            foreach (var face in contract.GetInterfaces())
            {
                if (face == kind)
                    continue;
                foreach (var method in face.GetMethods())
                    yield return method;
            }
        }

        /// <summary>Two stored types with one name would share their documents: refused.</summary>
        private string Claim(string area, Type domain)
        {
            if (area == DocumentAccess.SingleArea || area == MigrationEngine.Area)
                throw new InvalidOperationException(
                    "UniSpring Data: " + domain.FullName + " cannot be stored: the name '" + area +
                    "' is used by the module itself.");

            Type owner;
            if (m_Areas.TryGetValue(area, out owner) && owner != domain)
                throw new InvalidOperationException(
                    "UniSpring Data: " + domain.FullName + " and " + owner.FullName + " would both be stored as '" +
                    area + "'. Stored types need distinct names.");
            m_Areas[area] = domain;
            return area;
        }

        private void AddMapping(RecordMapping mapping, IRecordMapper mapper)
        {
            if (m_Mappings.ContainsKey(mapping.DomainType))
                throw new InvalidOperationException(
                    "UniSpring Data: " + mapping.DomainType.FullName + " has more than one record mapper (" +
                    mapper.GetType().FullName + " is the second).");
            m_Mappings.Add(mapping.DomainType, mapping);
        }

        private RecordMapping MappingOf(Type domain)
        {
            RecordMapping mapping;
            return m_Mappings.TryGetValue(domain, out mapping) ? mapping : null;
        }

        private List<DocumentValidation> ValidationsOf(Type domain)
        {
            List<DocumentValidation> validations;
            if (!m_Validations.TryGetValue(domain, out validations))
            {
                validations = new List<DocumentValidation>();
                m_Validations.Add(domain, validations);
            }

            return validations;
        }
    }
}
