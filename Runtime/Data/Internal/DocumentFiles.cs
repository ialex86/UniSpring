using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UniSpring.Logging;

namespace UniSpring.Data.Internal
{
    /// <summary>One change waiting for the disk: a write (bytes) or a delete (null).</summary>
    internal sealed class DocumentChange
    {
        public DocumentChange(string area, string name, byte[] bytes, long version)
        {
            Area = area;
            Name = name;
            Bytes = bytes;
            Version = version;
        }

        public string Area { get; }
        public string Name { get; }
        public byte[] Bytes { get; }
        public long Version { get; }
        public bool IsDelete => Bytes == null;
    }

    /// <summary>
    /// The disk refused while a journaled step was being put in place: the step is committed (its journal is on
    /// disk) and is completed when the store opens next, if it is not completed before.
    /// </summary>
    internal sealed class StepCommittedException : Exception
    {
        public StepCommittedException(Exception inner)
            : base("The disk refused while a committed step was put in place; it is completed when the store opens.", inner)
        {
        }
    }

    /// <summary>
    /// The documents on disk. Each document is a file <c>&lt;root&gt;/&lt;area&gt;/&lt;hex of the name&gt;.doc</c>
    /// with a header (length and SHA-256 of the payload). A write goes to a temporary file first and then replaces
    /// the document atomically, keeping the previous copy as <c>.bak</c>; a read that finds the document damaged
    /// returns the backup. Several changes go to disk through a journal: their new contents are written first,
    /// then the journal (the commit point), then they are put in place; a journal left by a crash is completed
    /// when the store opens, an incomplete one is dropped.
    /// </summary>
    internal sealed class DocumentFiles
    {
        public const int MaxNameBytes = 100;

        private const string DocumentMagic = "UNISPRING-DOC 1";
        private const string JournalMagic = "UNISPRING-TX 1";
        private const string DocumentExtension = ".doc";
        private const string BackupExtension = ".bak";
        private const string TempExtension = ".tmp";
        private const string JournalExtension = ".journal";
        private const string JournalFolder = ".journal";
        private const int MaxHeaderBytes = 1024;

        private static readonly Encoding Ascii = Encoding.ASCII;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly string m_Root;
        private readonly IFileSystem m_Files;

        public DocumentFiles(string root, IFileSystem files)
        {
            m_Root = root;
            m_Files = files;
        }

        // ---------------------------------------------------------------- names and paths

        /// <summary>Segments of letters, digits, '-' and '_' joined by '/'.</summary>
        public static void RequireArea(string area)
        {
            if (string.IsNullOrEmpty(area))
                throw new ArgumentException("The document area is empty.", nameof(area));
            foreach (var segment in area.Split('/'))
            {
                if (segment.Length == 0 || segment.Length > 64)
                    throw new ArgumentException(InvalidArea(area), nameof(area));
                foreach (var c in segment)
                {
                    var allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                    if (!allowed)
                        throw new ArgumentException(InvalidArea(area), nameof(area));
                }
            }
        }

        public static void RequireName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("The document name is empty.", nameof(name));
            if (Utf8.GetByteCount(name) > MaxNameBytes)
                throw new ArgumentException(
                    "The document name '" + name + "' is longer than " + MaxNameBytes + " UTF-8 bytes.", nameof(name));
        }

        /// <summary>The file name of a document, without extension: the name's UTF-8 bytes in hex (case-safe, reversible).</summary>
        public static string FileBase(string name)
        {
            var bytes = Utf8.GetBytes(name);
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static bool TryNameOf(string fileBase, out string name)
        {
            name = null;
            if (fileBase.Length == 0 || fileBase.Length % 2 != 0)
                return false;
            var bytes = new byte[fileBase.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(fileBase.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                    return false;
            }

            try
            {
                name = Utf8.GetString(bytes);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public string AreaFolder(string area) => Path.Combine(m_Root, area.Replace('/', Path.DirectorySeparatorChar));

        public string DocumentPath(string area, string name) => Path.Combine(AreaFolder(area), FileBase(name) + DocumentExtension);

        private string BackupPath(string area, string name) => Path.Combine(AreaFolder(area), FileBase(name) + BackupExtension);

        private string TempPath(string area, string name, long step)
            => Path.Combine(AreaFolder(area), FileBase(name) + "." + step.ToString(CultureInfo.InvariantCulture) + TempExtension);

        private string JournalPath(long step)
            => Path.Combine(m_Root, JournalFolder, step.ToString(CultureInfo.InvariantCulture) + JournalExtension);

        // ---------------------------------------------------------------- reads

        public DocumentRead Read(string area, string name)
        {
            var main = DocumentPath(area, name);
            var backup = BackupPath(area, name);
            var mainExists = m_Files.Exists(main);
            var backupExists = m_Files.Exists(backup);
            if (!mainExists && !backupExists)
                return DocumentRead.Missing;

            string mainError = null;
            if (mainExists)
            {
                byte[] payload;
                if (TryReadCopy(main, out payload, out mainError))
                    return DocumentRead.Found(payload);
            }

            if (backupExists)
            {
                byte[] payload;
                string backupError;
                if (TryReadCopy(backup, out payload, out backupError))
                {
                    if (!mainExists)
                        return DocumentRead.Found(payload); // a replace was interrupted: the backup is the content
                    GameLog.Warn("Document " + area + "/" + name + " is damaged (" + mainError + "); its backup is read.");
                    return DocumentRead.FoundInBackup(payload, mainError);
                }

                return DocumentRead.Damaged((mainError ?? "the document file is missing") + "; backup: " + backupError);
            }

            return DocumentRead.Damaged(mainError);
        }

        public IReadOnlyList<string> Names(string area)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var folder = AreaFolder(area);
            AddNames(m_Files.Files(folder, "*" + DocumentExtension), DocumentExtension, names);
            AddNames(m_Files.Files(folder, "*" + BackupExtension), BackupExtension, names);
            return new List<string>(names);
        }

        /// <summary>Every area that holds at least one document file (readable or not), sorted ordinally.</summary>
        public IReadOnlyList<string> Areas()
        {
            var areas = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var pattern in new[] { "*" + DocumentExtension, "*" + BackupExtension })
            {
                foreach (var file in m_Files.FilesBelow(m_Root, pattern))
                {
                    var folder = Path.GetDirectoryName(file);
                    if (folder == null || folder.Length <= m_Root.Length)
                        continue;
                    var area = folder.Substring(m_Root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (IsArea(area))
                        areas.Add(area);
                }
            }

            return new List<string>(areas);
        }

        private static bool IsArea(string area)
        {
            try
            {
                RequireArea(area);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void AddNames(IReadOnlyList<string> files, string extension, SortedSet<string> names)
        {
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.EndsWith(extension, StringComparison.Ordinal))
                    continue;
                string name;
                if (TryNameOf(fileName.Substring(0, fileName.Length - extension.Length), out name))
                    names.Add(name);
            }
        }

        private bool TryReadCopy(string path, out byte[] payload, out string error)
        {
            payload = null;
            byte[] raw;
            try
            {
                raw = m_Files.ReadAllBytes(path);
            }
            catch (Exception exception)
            {
                error = "the file cannot be read: " + exception.Message;
                return false;
            }

            return TryDecode(raw, out payload, out error);
        }

        // ---------------------------------------------------------------- writes

        /// <summary>
        /// Puts the changes on disk as one step: a single change directly, several through a journal.
        /// <para>
        /// A committed journal is never rewritten or deleted before it is complete: a journal an earlier attempt left
        /// (the disk refused while it was put in place) is completed first, and only then is this step prepared,
        /// under a step id never used before (<paramref name="step"/> must be unique). So the old content lands
        /// before the new one, and a failure while preparing this step cannot touch the earlier journal.
        /// </para>
        /// When the disk refuses before this step is committed, nothing of it has changed and the exception is the
        /// disk's own; once a journal is committed (this one or an earlier one), <see cref="StepCommittedException"/>.
        /// </summary>
        public void Apply(IReadOnlyList<DocumentChange> changes, long step)
        {
            CompletePending();
            if (changes.Count == 0)
                return;
            if (changes.Count == 1)
            {
                ApplySingle(changes[0], step);
                return;
            }

            ApplyJournaled(changes, step);
        }

        /// <summary>
        /// Completes every committed journal on disk, oldest first, and drops incomplete ones. Throws
        /// <see cref="StepCommittedException"/> when the disk refuses to finish one; it then stays for the next try.
        /// </summary>
        private void CompletePending()
        {
            var journals = new List<KeyValuePair<long, string>>();
            foreach (var journal in m_Files.Files(Path.Combine(m_Root, JournalFolder), "*" + JournalExtension))
            {
                long id;
                if (!long.TryParse(Path.GetFileNameWithoutExtension(journal), NumberStyles.None, CultureInfo.InvariantCulture, out id))
                    id = long.MaxValue;
                journals.Add(new KeyValuePair<long, string>(id, journal));
            }

            journals.Sort((left, right) => left.Key.CompareTo(right.Key));
            foreach (var journal in journals)
            {
                long id;
                IReadOnlyList<KeyValuePair<string, string>> entries;
                if (!TryReadJournal(journal.Value, out id, out entries))
                {
                    GameLog.Warn("UniSpring Data dropped an incomplete save step: " + Path.GetFileName(journal.Value));
                    m_Files.Delete(journal.Value);
                    continue;
                }

                try
                {
                    Replay(entries, id);
                    m_Files.Delete(journal.Value);
                }
                catch (Exception exception)
                {
                    throw new StepCommittedException(exception);
                }

                GameLog.Info("UniSpring Data completed an interrupted save step (" + entries.Count + " documents).");
            }
        }

        private void ApplyJournaled(IReadOnlyList<DocumentChange> changes, long step)
        {
            var temps = new List<string>();
            var journal = JournalPath(step);
            var lines = new StringBuilder();
            try
            {
                foreach (var change in changes)
                {
                    m_Files.CreateDirectory(AreaFolder(change.Area));
                    if (change.IsDelete)
                    {
                        lines.Append("D ").Append(change.Area).Append(' ').Append(FileBase(change.Name)).Append('\n');
                        continue;
                    }

                    var temp = TempPath(change.Area, change.Name, step);
                    temps.Add(temp);
                    m_Files.WriteDurable(temp, Encode(change.Bytes));
                    lines.Append("W ").Append(change.Area).Append(' ').Append(FileBase(change.Name)).Append('\n');
                }

                var body = JournalMagic + "\n" + lines;
                m_Files.CreateDirectory(Path.GetDirectoryName(journal));
                m_Files.WriteDurable(journal, Ascii.GetBytes(body + "sha256 " + Sha256(Ascii.GetBytes(body)) + "\n"));
            }
            catch (Exception)
            {
                temps.Add(journal);
                DeleteQuietly(temps);
                throw;
            }

            try
            {
                Replay(ParseEntries(lines.ToString()), step);
                m_Files.Delete(journal);
            }
            catch (Exception exception)
            {
                throw new StepCommittedException(exception);
            }
        }

        private void ApplySingle(DocumentChange change, long step)
        {
            if (change.IsDelete)
            {
                DeleteDocument(change.Area, FileBase(change.Name));
                return;
            }

            m_Files.CreateDirectory(AreaFolder(change.Area));
            var temp = TempPath(change.Area, change.Name, step);
            try
            {
                m_Files.WriteDurable(temp, Encode(change.Bytes));
                m_Files.Replace(temp, DocumentPath(change.Area, change.Name), BackupPath(change.Area, change.Name));
            }
            catch (Exception)
            {
                DeleteQuietly(new[] { temp });
                throw;
            }
        }

        private void DeleteQuietly(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    m_Files.Delete(path);
                }
                catch (Exception)
                {
                    // Left for the next start, which removes temporary files and incomplete journals.
                }
            }
        }

        /// <summary>The backup goes first: a document must never be left with only its old copy.</summary>
        private void DeleteDocument(string area, string fileBase)
        {
            var folder = AreaFolder(area);
            m_Files.Delete(Path.Combine(folder, fileBase + BackupExtension));
            m_Files.Delete(Path.Combine(folder, fileBase + DocumentExtension));
        }

        private void Replay(IReadOnlyList<KeyValuePair<string, string>> entries, long step)
        {
            var stepText = "." + step.ToString(CultureInfo.InvariantCulture) + TempExtension;
            foreach (var entry in entries)
            {
                var kind = entry.Key[0];
                var area = entry.Key.Substring(2);
                var fileBase = entry.Value;
                if (kind == 'D')
                {
                    DeleteDocument(area, fileBase);
                    continue;
                }

                var folder = AreaFolder(area);
                var temp = Path.Combine(folder, fileBase + stepText);
                if (!m_Files.Exists(temp))
                    continue; // already in place
                m_Files.Replace(temp, Path.Combine(folder, fileBase + DocumentExtension), Path.Combine(folder, fileBase + BackupExtension));
            }
        }

        /// <summary>("W area" or "D area", file base) per journal line.</summary>
        private static IReadOnlyList<KeyValuePair<string, string>> ParseEntries(string lines)
        {
            var entries = new List<KeyValuePair<string, string>>();
            foreach (var line in lines.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ');
                if (parts.Length != 3 || (parts[0] != "W" && parts[0] != "D"))
                    throw new FormatException("bad journal line '" + line + "'");
                RequireArea(parts[1]);
                string ignored;
                if (!TryNameOf(parts[2], out ignored))
                    throw new FormatException("bad document in journal line '" + line + "'");
                entries.Add(new KeyValuePair<string, string>(parts[0] + " " + parts[1], parts[2]));
            }

            return entries;
        }

        // ---------------------------------------------------------------- recovery

        /// <summary>
        /// Completes the journals a crash left behind (they were committed) and drops incomplete ones, then removes
        /// every temporary file. Runs once, when the store opens, before anything else touches the files.
        /// </summary>
        public void Recover()
        {
            CompletePending();
            foreach (var temp in m_Files.FilesBelow(m_Root, "*" + TempExtension))
                m_Files.Delete(temp);
        }

        private bool TryReadJournal(string path, out long step, out IReadOnlyList<KeyValuePair<string, string>> entries)
        {
            entries = null;
            step = 0;
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!long.TryParse(fileName, NumberStyles.None, CultureInfo.InvariantCulture, out step))
                return false;
            try
            {
                var text = Ascii.GetString(m_Files.ReadAllBytes(path));
                var checksumAt = text.LastIndexOf("sha256 ", StringComparison.Ordinal);
                if (checksumAt < 0 || !text.StartsWith(JournalMagic + "\n", StringComparison.Ordinal))
                    return false;
                var body = text.Substring(0, checksumAt);
                var checksum = text.Substring(checksumAt + 7).TrimEnd('\n');
                if (!string.Equals(checksum, Sha256(Ascii.GetBytes(body)), StringComparison.Ordinal))
                    return false;
                entries = ParseEntries(body.Substring(JournalMagic.Length + 1));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- envelope

        internal static byte[] Encode(byte[] payload)
        {
            var header = DocumentMagic + "\n" +
                         "length " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\n" +
                         "sha256 " + Sha256(payload) + "\n\n";
            var head = Ascii.GetBytes(header);
            var result = new byte[head.Length + payload.Length];
            Buffer.BlockCopy(head, 0, result, 0, head.Length);
            Buffer.BlockCopy(payload, 0, result, head.Length, payload.Length);
            return result;
        }

        private static bool TryDecode(byte[] raw, out byte[] payload, out string error)
        {
            payload = null;
            error = null;
            var end = -1;
            for (var i = 0; i + 1 < raw.Length && i < MaxHeaderBytes; i++)
            {
                if (raw[i] == (byte)'\n' && raw[i + 1] == (byte)'\n')
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                error = "the file has no document header";
                return false;
            }

            var lines = Ascii.GetString(raw, 0, end).Split('\n');
            int length;
            if (lines.Length != 3 || lines[0] != DocumentMagic || !lines[1].StartsWith("length ", StringComparison.Ordinal) ||
                !lines[2].StartsWith("sha256 ", StringComparison.Ordinal) ||
                !int.TryParse(lines[1].Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out length))
            {
                error = "the document header is not recognised";
                return false;
            }

            var start = end + 2;
            if (length != raw.Length - start)
            {
                error = "the file is truncated";
                return false;
            }

            payload = new byte[length];
            Buffer.BlockCopy(raw, start, payload, 0, length);
            if (!string.Equals(Sha256(payload), lines[2].Substring(7), StringComparison.Ordinal))
            {
                payload = null;
                error = "the checksum does not match";
                return false;
            }

            return true;
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static string InvalidArea(string area)
            => "The document area '" + area + "' must be segments of letters, digits, '-' or '_' joined by '/'.";
    }
}
