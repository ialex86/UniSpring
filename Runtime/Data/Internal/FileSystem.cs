using System;
using System.Collections.Generic;
using System.IO;

namespace UniSpring.Data.Internal
{
    /// <summary>The file operations the file store needs; tests inject failures through it.</summary>
    internal interface IFileSystem
    {
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

        /// <summary>Files directly in the folder matching the pattern; empty when the folder does not exist.</summary>
        IReadOnlyList<string> Files(string directory, string pattern);

        /// <summary>Files in the folder and every subfolder matching the pattern.</summary>
        IReadOnlyList<string> FilesBelow(string directory, string pattern);
    }

    internal sealed class SystemFileSystem : IFileSystem
    {
        public static readonly SystemFileSystem Instance = new SystemFileSystem();

        public bool Exists(string path) => File.Exists(path);

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
            if (!File.Exists(destination))
            {
                File.Move(source, destination);
                return;
            }

            if (File.Exists(backup))
                File.Delete(backup);
            File.Replace(source, destination, backup, true);
        }

        public void Delete(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public IReadOnlyList<string> Files(string directory, string pattern)
            => Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : Array.Empty<string>();

        public IReadOnlyList<string> FilesBelow(string directory, string pattern)
            => Directory.Exists(directory)
                ? Directory.GetFiles(directory, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();
    }
}
