using System;
using System.Collections.Generic;
using UniSpring.Logging;

namespace UniSpring.Data.Internal
{
    /// <summary>
    /// The path of one stored type between the application and the store.
    /// Save: object, validators, mapper, serializer, store. Load: the same steps back. A document that cannot be
    /// read, mapped or validated is logged and read as missing, so one bad file never stops the game.
    /// </summary>
    internal sealed class DocumentAccess
    {
        /// <summary>The area that holds the single documents, each named after its type.</summary>
        public const string SingleArea = "Documents";

        private readonly IDocumentStore m_Store;
        private readonly IDocumentSerializer m_Serializer;
        private readonly RecordMapping m_Mapping;
        private readonly DocumentValidation[] m_Validations;

        // Documents a read found broken: a save over them is refused until they are deleted.
        private readonly HashSet<string> m_Broken = new HashSet<string>(StringComparer.Ordinal);

        public DocumentAccess(
            Type domainType,
            IDocumentStore store,
            IDocumentSerializer serializer,
            RecordMapping mapping,
            DocumentValidation[] validations)
        {
            DomainType = domainType;
            m_Store = store;
            m_Serializer = serializer;
            m_Mapping = mapping;
            m_Validations = validations;
        }

        public Type DomainType { get; }

        private Type RecordType => m_Mapping != null ? m_Mapping.RecordType : DomainType;

        public bool Exists(string area, string name)
            => m_Store.Read(area, RequireName(name)).Status == DocumentStatus.Found;

        public IReadOnlyList<string> Names(string area) => m_Store.Names(area);

        public void Delete(string area, string name)
        {
            m_Store.Delete(area, RequireName(name));
            lock (m_Broken)
                m_Broken.Remove(area + "/" + name);
        }

        public void Save(string area, string name, object value)
        {
            RequireName(name);
            if (value == null)
                throw new ArgumentNullException(nameof(value), Describe(area, name) + ": there is nothing to save.");
            lock (m_Broken)
            {
                if (m_Broken.Contains(area + "/" + name))
                    throw new DocumentDamagedException(Describe(area, name));
            }

            var errors = Validate(name, value);
            if (errors.Count > 0)
                throw new DocumentValidationException(Describe(area, name), errors);

            var record = m_Mapping != null ? m_Mapping.ToRecord(value) : value;
            if (record == null)
                throw new InvalidOperationException(Describe(area, name) + ": the mapper returned no record.");
            var bytes = m_Serializer.Write(record);
            if (bytes == null)
                throw new InvalidOperationException(Describe(area, name) + ": the serializer returned no bytes.");
            m_Store.Write(area, name, bytes);
        }

        /// <summary>The lenient read: a broken document is logged and read as missing.</summary>
        public object Load(string area, string name)
        {
            LoadStatus status;
            string error;
            Exception exception;
            var value = Read(area, name, out status, out error, out exception);
            if (status == LoadStatus.Found || status == LoadStatus.Missing)
                return value;
            if (exception != null)
                GameLog.Error(Describe(area, name) + " could not be read and is read as missing.", exception);
            else
                GameLog.Error(Describe(area, name) + " is " + status.ToString().ToLowerInvariant() + " and is read as missing: " + error);
            return null;
        }

        /// <summary>
        /// The strict read: the object with what was found. A broken document is remembered, so a later save over it
        /// is refused (<see cref="DocumentDamagedException"/>) until it is deleted.
        /// </summary>
        public object Read(string area, string name, out LoadStatus status, out string error, out Exception exception)
        {
            exception = null;
            error = null;
            var read = m_Store.Read(area, RequireName(name));
            object value = null;
            if (read.Status == DocumentStatus.Missing)
            {
                status = LoadStatus.Missing;
            }
            else if (read.Status == DocumentStatus.Damaged)
            {
                status = LoadStatus.Damaged;
                error = read.Error;
            }
            else
            {
                try
                {
                    var record = m_Serializer.Read(RecordType, read.Bytes);
                    value = record != null && m_Mapping != null ? m_Mapping.ToDomain(record) : record;
                    status = value != null ? LoadStatus.Found : LoadStatus.Unreadable;
                    if (value == null)
                        error = "it holds no object";
                }
                catch (Exception failure)
                {
                    status = LoadStatus.Unreadable;
                    error = failure.Message;
                    exception = failure;
                }

                if (status == LoadStatus.Found)
                {
                    var errors = Validate(name, value);
                    if (errors.Count > 0)
                    {
                        status = LoadStatus.Invalid;
                        error = errors.ToString();
                        value = null;
                    }
                }
            }

            lock (m_Broken)
            {
                if (status == LoadStatus.Found || status == LoadStatus.Missing)
                    m_Broken.Remove(area + "/" + name);
                else
                    m_Broken.Add(area + "/" + name);
            }

            return status == LoadStatus.Found ? value : null;
        }

        /// <summary>The strict read as the typed result an interface method returns.</summary>
        public object ReadResult(string area, string name)
        {
            LoadStatus status;
            string error;
            Exception exception;
            var value = Read(area, name, out status, out error, out exception);
            return Activator.CreateInstance(typeof(LoadResult<>).MakeGenericType(DomainType), status, value, error);
        }

        private ValidationErrors Validate(string name, object value)
        {
            var errors = new ValidationErrors();
            for (var index = 0; index < m_Validations.Length; index++)
                m_Validations[index].Validate(name, value, errors);
            return errors;
        }

        private static string RequireName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("The document id is empty.", nameof(name));
            return name;
        }

        private static string Describe(string area, string name) => "Document " + area + "/" + name;
    }
}
