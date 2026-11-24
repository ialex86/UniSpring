using System;
using System.Collections.Generic;
using UniSpring.DependencyInjection;

namespace UniSpring.Data
{
    /// <summary>
    /// A one-time change of stored data, as a Flyway migration: a bean with a number. When the container starts,
    /// the migrations with a number above the one the store remembers run in order, each in one transaction with
    /// the new number. The number lives in its own document; the business documents carry no version.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class MigrationAttribute : ComponentAttribute
    {
        public MigrationAttribute(int version)
        {
            Version = version;
        }

        /// <summary>Positive and unique; migrations run in ascending order.</summary>
        public int Version { get; }
    }

    public interface IMigration
    {
        void Run(IMigrationStore store);
    }

    /// <summary>
    /// The store as a migration sees it: records in the shape they are stored in (old and new), read and written
    /// through the serializer, without mappers or validators.
    /// </summary>
    public interface IMigrationStore
    {
        /// <summary>The single document of a type, by the type's name; null when there is none.</summary>
        T Load<T>(string document) where T : class;

        void Save(string document, object record);

        void Delete(string document);

        /// <summary>The ids of a repository, by the stored type's name.</summary>
        IReadOnlyList<string> Ids(string collection);

        T Load<T>(string collection, string id) where T : class;

        void Save(string collection, string id, object record);

        void Delete(string collection, string id);
    }
}
