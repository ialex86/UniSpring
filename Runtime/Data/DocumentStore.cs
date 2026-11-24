using System.Collections.Generic;

namespace UniSpring.Data
{
    public enum DocumentStatus
    {
        /// <summary>Never written, or deleted.</summary>
        Missing = 0,

        /// <summary>The bytes were read intact.</summary>
        Found = 1,

        /// <summary>The store has the document but cannot read it intact.</summary>
        Damaged = 2
    }

    /// <summary>What a read of one document found.</summary>
    public readonly struct DocumentRead
    {
        public readonly DocumentStatus Status;
        public readonly byte[] Bytes;
        public readonly string Error;

        public DocumentRead(DocumentStatus status, byte[] bytes, string error)
        {
            Status = status;
            Bytes = bytes;
            Error = error ?? string.Empty;
        }

        public static DocumentRead Missing => new DocumentRead(DocumentStatus.Missing, null, null);

        public static DocumentRead Found(byte[] bytes) => new DocumentRead(DocumentStatus.Found, bytes, null);

        public static DocumentRead Damaged(string error) => new DocumentRead(DocumentStatus.Damaged, null, error);
    }

    /// <summary>
    /// Where the bytes of the documents live: the port between the repositories and the disk. A document is
    /// addressed by an area (one per stored type) and a name inside it.
    /// <para>
    /// The contract every implementation keeps: a write or delete may return before the disk has it (a store may
    /// write on its own thread so the game never waits), but every read made after it, from any thread, sees the
    /// new content. A store that supports transactions also implements
    /// <see cref="UniSpring.Transactions.ITransactionManager"/>: writes made inside a transaction are seen by
    /// its own flow at once and by everyone else when it commits.
    /// </para>
    /// </summary>
    public interface IDocumentStore
    {
        DocumentRead Read(string area, string name);

        void Write(string area, string name, byte[] bytes);

        void Delete(string area, string name);

        /// <summary>Names of the documents in the area, sorted ordinally.</summary>
        IReadOnlyList<string> Names(string area);
    }
}
