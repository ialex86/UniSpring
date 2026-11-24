using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using UnityEngine;

namespace UniSpring.Data.Internal
{
    /// <summary>
    /// Makes the objects behind <see cref="CatalogAttribute"/> interfaces and binds
    /// <see cref="ConfigurationPropertiesAttribute"/> classes. Everything is loaded and checked here, once; a
    /// problem is an error of the game's data and stops the start.
    /// </summary>
    internal sealed class CatalogFactory : IDisposable
    {
        private readonly IResourceSource m_Source;
        private readonly Func<IDocumentSerializer> m_Serializer;
        private readonly Dictionary<Type, RecordMapping> m_Mappings = new Dictionary<Type, RecordMapping>();
        private readonly Dictionary<Type, List<DocumentValidation>> m_Validations = new Dictionary<Type, List<DocumentValidation>>();
        private readonly Dictionary<Type, object> m_Made = new Dictionary<Type, object>();
        private readonly Dictionary<Type, IRecordMapper> m_MapperOf = new Dictionary<Type, IRecordMapper>();
        private readonly List<ScriptableObject> m_Defaults = new List<ScriptableObject>();

        public CatalogFactory(
            IResourceSource source,
            Func<IDocumentSerializer> serializer,
            IEnumerable<IRecordMapper> mappers,
            IEnumerable<IDocumentValidator> validators)
        {
            m_Source = source ?? throw new ArgumentNullException(nameof(source));
            m_Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            foreach (var mapper in mappers)
            {
                foreach (var mapping in RecordMapping.Of(mapper))
                {
                    IRecordMapper other;
                    if (m_MapperOf.TryGetValue(mapping.DomainType, out other))
                        throw new InvalidOperationException(
                            "UniSpring Data: two mappers for " + mapping.DomainType.FullName + ": " +
                            other.GetType().FullName + " and " + mapper.GetType().FullName + ".");
                    m_MapperOf.Add(mapping.DomainType, mapper);
                    m_Mappings.Add(mapping.DomainType, mapping);
                }
            }

            foreach (var validator in validators)
            {
                foreach (var validation in DocumentValidation.Of(validator))
                {
                    List<DocumentValidation> list;
                    if (!m_Validations.TryGetValue(validation.DomainType, out list))
                        m_Validations.Add(validation.DomainType, list = new List<DocumentValidation>());
                    list.Add(validation);
                }
            }
        }

        public static bool IsCatalog(Type contract)
            => contract.IsInterface && contract.IsDefined(typeof(CatalogAttribute), false);

        public static bool IsConfiguration(Type contract)
            => contract.IsClass && !contract.IsAbstract && contract.IsDefined(typeof(ConfigurationPropertiesAttribute), false);

        public object Create(Type contract)
        {
            object made;
            if (m_Made.TryGetValue(contract, out made))
                return made;
            made = IsCatalog(contract) ? CreateCatalog(contract) : CreateConfiguration(contract);
            m_Made.Add(contract, made);
            return made;
        }

        // ---------------------------------------------------------------- catalogs

        private object CreateCatalog(Type contract)
        {
            var kind = KindOf(contract);
            var domain = kind.GetGenericArguments()[0];
            var folder = contract.GetCustomAttribute<CatalogAttribute>(false).Folder;
            var items = LoadItems(contract, domain, folder);

            var byId = new Dictionary<string, object>(StringComparer.Ordinal);
            var ids = new List<string>();
            foreach (var item in items)
            {
                byId.Add(item.Key, item.Value);
                ids.Add(item.Key);
            }

            var array = Array.CreateInstance(domain, items.Count);
            for (var index = 0; index < items.Count; index++)
                array.SetValue(items[index].Value, index);
            // Read-only views: a caller cannot change the catalog through what it was given.
            var all = Activator.CreateInstance(typeof(ReadOnlyCollection<>).MakeGenericType(domain), array);
            IReadOnlyList<string> idList = new ReadOnlyCollection<string>(ids);

            var handlers = new Dictionary<MethodInfo, Func<object[], object>>
            {
                { kind.GetMethod(nameof(ICatalog<object>.Find)), arguments => Lookup(byId, (string)arguments[0]) },
                { kind.GetMethod(nameof(ICatalog<object>.Exists)), arguments => arguments[0] != null && byId.ContainsKey((string)arguments[0]) },
                { kind.GetMethod(nameof(ICatalog<object>.All)), arguments => all },
                { kind.GetMethod(nameof(ICatalog<object>.Ids)), arguments => idList },
            };

            foreach (var method in contract.GetMethods())
                handlers.Add(method, QueryMethods.Build(method, domain, () => items, null));
            foreach (var face in contract.GetInterfaces())
            {
                if (face == kind)
                    continue;
                foreach (var method in face.GetMethods())
                    handlers.Add(method, QueryMethods.Build(method, domain, () => items, null));
            }

            return RepositoryProxy.Create(contract, handlers);
        }

        private static object Lookup(Dictionary<string, object> byId, string id)
        {
            object value;
            return id != null && byId.TryGetValue(id, out value) ? value : null;
        }

        private List<KeyValuePair<string, object>> LoadItems(Type contract, Type domain, string folder)
        {
            var assets = new List<KeyValuePair<string, byte[]>>(m_Source.LoadAll(folder));
            assets.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
            var items = new List<KeyValuePair<string, object>>(assets.Count);
            var serializer = assets.Count > 0 ? m_Serializer() : null;
            for (var index = 0; index < assets.Count; index++)
            {
                var id = assets[index].Key;
                if (index > 0 && assets[index - 1].Key == id)
                    throw Broken(contract, folder, id, "two assets have this name");

                object value;
                try
                {
                    var mapping = MappingOf(domain);
                    var record = serializer.Read(mapping != null ? mapping.RecordType : domain, assets[index].Value);
                    value = record != null && mapping != null ? mapping.ToDomain(record) : record;
                }
                catch (Exception exception)
                {
                    throw Broken(contract, folder, id, "it cannot be read", exception);
                }

                if (value == null)
                    throw Broken(contract, folder, id, "it holds no item");
                var errors = Validate(domain, id, value);
                if (errors.Count > 0)
                    throw Broken(contract, folder, id, errors.ToString());
                items.Add(new KeyValuePair<string, object>(id, value));
            }

            return items;
        }

        private static Type KindOf(Type contract)
        {
            foreach (var face in contract.GetInterfaces())
            {
                if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(ICatalog<>))
                    return face;
            }

            throw new InvalidOperationException("UniSpring Data: " + contract.FullName + " must derive from ICatalog<T>.");
        }

        private static InvalidOperationException Broken(Type contract, string folder, string id, string reason, Exception inner = null)
            => new InvalidOperationException(
                "UniSpring Data: catalog " + contract.Name + " item Resources/" + folder + "/" + id + " is broken: " + reason + ".",
                inner);

        // ---------------------------------------------------------------- configuration

        private object CreateConfiguration(Type contract)
        {
            var attribute = contract.GetCustomAttribute<ConfigurationPropertiesAttribute>(false);
            object value;
            if (typeof(ScriptableObject).IsAssignableFrom(contract))
            {
                value = m_Source.LoadAsset(attribute.Path, contract);
                if (value == null)
                {
                    value = Missing(contract, attribute);
                    if (value == null)
                    {
                        var defaults = ScriptableObject.CreateInstance(contract);
                        m_Defaults.Add(defaults);
                        value = defaults;
                    }
                }
            }
            else
            {
                var bytes = m_Source.Load(attribute.Path);
                if (bytes == null)
                {
                    value = Missing(contract, attribute) ?? Activator.CreateInstance(contract);
                }
                else
                {
                    try
                    {
                        value = m_Serializer().Read(contract, bytes);
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            "UniSpring Data: settings " + contract.Name + " in Resources/" + attribute.Path + " cannot be read.", exception);
                    }

                    if (value == null)
                        throw new InvalidOperationException(
                            "UniSpring Data: settings " + contract.Name + " in Resources/" + attribute.Path + " hold nothing.");
                }
            }

            var errors = Validate(contract, attribute.Path, value);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "UniSpring Data: settings " + contract.Name + " in Resources/" + attribute.Path + " are invalid: " + errors + ".");
            return value;
        }

        /// <summary>Null when defaults may be used; throws for required settings.</summary>
        private static object Missing(Type contract, ConfigurationPropertiesAttribute attribute)
        {
            if (attribute.Required)
                throw new InvalidOperationException(
                    "UniSpring Data: settings " + contract.Name + " need Resources/" + attribute.Path + ", which does not exist.");
            return null;
        }

        /// <summary>Destroys the settings objects made here for missing assets; loaded assets belong to Unity.</summary>
        public void Dispose()
        {
            foreach (var defaults in m_Defaults)
            {
                if (defaults == null)
                    continue;
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(defaults);
                else
                    UnityEngine.Object.DestroyImmediate(defaults);
            }

            m_Defaults.Clear();
        }

        private RecordMapping MappingOf(Type domain)
        {
            RecordMapping mapping;
            return m_Mappings.TryGetValue(domain, out mapping) ? mapping : null;
        }

        private ValidationErrors Validate(Type type, string id, object value)
        {
            var errors = new ValidationErrors();
            List<DocumentValidation> validations;
            if (m_Validations.TryGetValue(type, out validations))
            {
                foreach (var validation in validations)
                    validation.Validate(id, value, errors);
            }

            return errors;
        }
    }
}
