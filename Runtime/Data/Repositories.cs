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

    /// <summary>The one object of its type the game keeps (a wallet, the progress of a campaign).</summary>
    public interface IDocument<T>
        where T : class
    {
        /// <summary>The stored object, or null when there is none (or it is damaged or invalid; that is logged).</summary>
        T Load();

        bool Exists();

        /// <summary>Stores the object. Throws <see cref="DocumentValidationException"/> when a validator refuses it.</summary>
        void Save(T value);

        void Delete();
    }

    /// <summary>Many objects of one type, each under its own id.</summary>
    public interface IRepository<T>
        where T : class
    {
        /// <summary>The object stored under the id, or null when there is none (or it is damaged or invalid; that is logged).</summary>
        T Find(string id);

        bool Exists(string id);

        /// <summary>Stores the object under the id. Throws <see cref="DocumentValidationException"/> when a validator refuses it.</summary>
        void Save(string id, T value);

        void Delete(string id);

        /// <summary>Every readable object, in the order of their ids.</summary>
        IReadOnlyList<T> All();

        /// <summary>The ids in use, sorted ordinally.</summary>
        IReadOnlyList<string> Ids();
    }
}
