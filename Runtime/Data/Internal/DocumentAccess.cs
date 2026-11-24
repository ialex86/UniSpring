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

        public void Delete(string area, string name) => m_Store.Delete(area, RequireName(name));

        public void Save(string area, string name, object value)
        {
            RequireName(name);
            if (value == null)
                throw new ArgumentNullException(nameof(value), Describe(area, name) + ": there is nothing to save.");

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

        public object Load(string area, string name)
        {
            var read = m_Store.Read(area, RequireName(name));
            if (read.Status == DocumentStatus.Missing)
                return null;
            if (read.Status == DocumentStatus.Damaged)
            {
                GameLog.Error(Describe(area, name) + " is damaged and is read as missing: " + read.Error);
                return null;
            }

            object value;
            try
            {
                var record = m_Serializer.Read(RecordType, read.Bytes);
                value = record != null && m_Mapping != null ? m_Mapping.ToDomain(record) : record;
            }
            catch (Exception exception)
            {
                GameLog.Error(Describe(area, name) + " could not be read and is read as missing.", exception);
                return null;
            }

            if (value == null)
            {
                GameLog.Error(Describe(area, name) + " holds no object and is read as missing.");
                return null;
            }

            var errors = Validate(name, value);
            if (errors.Count > 0)
            {
                GameLog.Error(Describe(area, name) + " is invalid and is read as missing: " + errors);
                return null;
            }

            return value;
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
