using System;
using System.Collections.Generic;

namespace UniSpring.Data
{
    /// <summary>
    /// Marks an interface the container implements: declare it, derive it from <see cref="IRepository{T}"/> or
    /// <see cref="IDocument{T}"/>, and ask for it in a constructor. As Spring Data's repositories, the
    /// implementation is made at run time (a proxy: Mono only).
    /// <para>
    /// A repository interface may add query methods named after the stored type's public fields and properties:
    /// <c>FindByOwner(owner)</c>, <c>FindByOwnerAndModel(owner, model)</c>, <c>CountByOwner(owner)</c>,
    /// <c>ExistsByModel(model)</c>, <c>DeleteByOwner(owner)</c>. Members are compared for equality. A name that
    /// does not follow the rules is refused when the repository is created, not when the method is called.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, Inherited = false)]
    public sealed class RepositoryAttribute : Attribute
    {
    }

    /// <summary>What a strict read found.</summary>
    public enum LoadStatus
    {
        /// <summary>Nothing is stored under the id: it was never saved, or it was deleted.</summary>
        Missing = 0,

        /// <summary>The object was read, mapped and validated.</summary>
        Found = 1,

        /// <summary>The store has the document but no intact copy of its bytes.</summary>
        Damaged = 2,

        /// <summary>The bytes are intact but the serializer or the mapper could not turn them into an object.</summary>
        Unreadable = 3,

        /// <summary>The object was read but a validator refused it.</summary>
        Invalid = 4
    }

    /// <summary>The result of <see cref="IDocument{T}.Read"/> or <see cref="IRepository{T}.Read"/>.</summary>
    public readonly struct LoadResult<T>
        where T : class
    {
        public LoadResult(LoadStatus status, T value, string error)
        {
            Status = status;
            Value = value;
            Error = error ?? string.Empty;
        }

        public LoadStatus Status { get; }

        /// <summary>The object when <see cref="Found"/>; null otherwise.</summary>
        public T Value { get; }

        /// <summary>What went wrong, for the Damaged, Unreadable and Invalid statuses.</summary>
        public string Error { get; }

        public bool Found => Status == LoadStatus.Found;

        /// <summary>Something is stored but cannot be used: Damaged, Unreadable or Invalid.</summary>
        public bool IsBroken => Status != LoadStatus.Found && Status != LoadStatus.Missing;
    }

    /// <summary>
    /// A save was refused because the document under the id was found broken by a read: saving would replace the
    /// player's data with a new object. Delete it first to replace it on purpose.
    /// </summary>
    public sealed class DocumentDamagedException : Exception
    {
        public DocumentDamagedException(string document)
            : base(document + " was found broken when it was read and is not replaced by a save. Delete it first " +
                   "to replace it on purpose.")
        {
        }
    }

    /// <summary>The one object of its type the game keeps (a wallet, the progress of a campaign).</summary>
    public interface IDocument<T>
        where T : class
    {
        /// <summary>
        /// The stored object, or null when there is none or it is broken (logged). For data the player must not
        /// lose, use <see cref="Read"/>: a null here does not tell a new game from a damaged save.
        /// </summary>
        T Load();

        /// <summary>The stored object with what the read found: missing, found, or broken and why.</summary>
        LoadResult<T> Read();

        bool Exists();

        /// <summary>
        /// Stores the object. Throws <see cref="DocumentValidationException"/> when a validator refuses it, and
        /// <see cref="DocumentDamagedException"/> when a read found the stored document broken.
        /// </summary>
        void Save(T value);

        void Delete();
    }

    /// <summary>Many objects of one type, each under its own id.</summary>
    public interface IRepository<T>
        where T : class
    {
        /// <summary>The object stored under the id, or null when there is none or it is broken (logged).</summary>
        T Find(string id);

        /// <summary>The object under the id with what the read found: missing, found, or broken and why.</summary>
        LoadResult<T> Read(string id);

        bool Exists(string id);

        /// <summary>
        /// Stores the object under the id. Throws <see cref="DocumentValidationException"/> when a validator refuses
        /// it, and <see cref="DocumentDamagedException"/> when a read found the stored document broken.
        /// </summary>
        void Save(string id, T value);

        void Delete(string id);

        /// <summary>Every readable object, in the order of their ids.</summary>
        IReadOnlyList<T> All();

        /// <summary>The ids in use, sorted ordinally.</summary>
        IReadOnlyList<string> Ids();
    }
}
