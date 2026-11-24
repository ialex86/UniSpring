using System;
using System.Collections.Generic;
using System.IO;

namespace UniSpring.Data.Internal
{
    /// <summary>The file operations the file store needs; tests inject failures through it.</summary>
    internal interface IFileSystem
    {
        /// <summary>False only when the file is absent; throws when it cannot be checked.</summary>
        bool Exists(string path);

        byte[] ReadAllBytes(string path);

        /// <summary>Writes the file and flushes it to the disk before returning.</summary>
        void WriteDurable(string path, byte[] bytes);

        /// <summary>
        /// Puts <paramref name="source"/> in place of <paramref name="destination"/>, keeping the old one as
        /// <paramref name="backup"/>; a plain move when there is no destination yet.
        /// </summary>
        void Replace(string source, string destination, string backup);

        void Delete(string path);

        void CreateDirectory(string path);

        /// <summary>
        /// Files directly in the folder matching the pattern; empty when the folder does not exist. Throws when the
        /// folder cannot be checked.
        /// </summary>
        IReadOnlyList<string> Files(string directory, string pattern);

        /// <summary>Files in the folder and every subfolder matching the pattern.</summary>
        IReadOnlyList<string> FilesBelow(string directory, string pattern);
    }

    internal sealed class SystemFileSystem : IFileSystem
    {
        public static readonly SystemFileSystem Instance = new SystemFileSystem();

        /// <summary>
        /// False only when the file is really absent: a file the system cannot check (no access, I/O error) throws,
        /// so recovery never takes "cannot look" for "not there".
        /// </summary>
        public bool Exists(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static bool DirectoryExists(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteDurable(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        public void Replace(string source, string destination, string backup)
        {
            if (!Exists(destination))
            {
                File.Move(source, destination);
                return;
            }

            if (Exists(backup))
                File.Delete(backup);
            File.Replace(source, destination, backup, true);
        }

        public void Delete(string path)
        {
            if (Exists(path))
                File.Delete(path);
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public IReadOnlyList<string> Files(string directory, string pattern)
            => DirectoryExists(directory) ? Directory.GetFiles(directory, pattern) : Array.Empty<string>();

        public IReadOnlyList<string> FilesBelow(string directory, string pattern)
            => DirectoryExists(directory)
                ? Directory.GetFiles(directory, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();
    }
}
