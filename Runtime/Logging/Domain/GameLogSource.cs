using System;
using System.IO;
using System.Text;

namespace UniSpring.Logging.Domain
{
    /// <summary>
    /// How a source name is derived from <c>[CallerFilePath]</c>.
    /// </summary>
    public sealed class GameLogSourceOptions
    {
        /// <summary>
        /// Path fragment that marks the root of the source tree, for example
        /// <c>/Assets/Src/</c> or <c>/Assets/</c>. When empty, only the file
        /// name is used as the source.
        /// </summary>
        public string SourceRoot { get; set; }

        /// <summary>
        /// Folder names skipped while building the dotted source path, for
        /// example <c>Scripts</c> and <c>Internal</c>.
        /// </summary>
        public string[] IgnoredSegments { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Log source names derived from the caller's file path. When
    /// <see cref="GameLogSourceOptions.SourceRoot"/> is configured, a file
    /// under that root gets a dotted module path
    /// (<c>Scripts/Inventory/Internal/Application/Foo.cs</c> →
    /// <c>Inventory.Application.Foo</c>). Otherwise the file name is
    /// used, so the default is safe for any project layout.
    /// </summary>
    public static class GameLogSource
    {
        public const string Unknown = "<Unknown>";

        private static GameLogSourceOptions s_Options = new GameLogSourceOptions();

        /// <summary>
        /// Applies source naming options. Call this before the first log entry;
        /// later changes are picked up by the next call.
        /// </summary>
        public static void Configure(GameLogSourceOptions options)
        {
            s_Options = Normalize(options);
        }

        public static string FromCallerPath(string callerPath)
        {
            if (string.IsNullOrWhiteSpace(callerPath))
                return Unknown;

            var normalized = "/" + callerPath.Replace('\\', '/').TrimStart('/');
            var fileName = TrimPartialSuffix(Path.GetFileNameWithoutExtension(normalized));
            var options = s_Options;
            if (string.IsNullOrEmpty(options.SourceRoot))
                return fileName;

            var marker = normalized.LastIndexOf(options.SourceRoot, StringComparison.Ordinal);
            if (marker < 0)
                return fileName;

            var relative = normalized.Substring(marker + options.SourceRoot.Length);
            var segments = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var segment = segments[index];
                if (IsIgnored(segment, options.IgnoredSegments))
                    continue;
                builder.Append(segment).Append('.');
            }

            return builder.Append(fileName).ToString();
        }

        /// <summary>Last segment (the file/class name) shown in the console prefix.</summary>
        public static string ShortName(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return Unknown;
            var separator = source.LastIndexOf('.');
            return separator < 0 ? source : source.Substring(separator + 1);
        }

        /// <summary>First segment: the owning module (Inventory, Audio, ...).</summary>
        public static string Module(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return Unknown;
            var separator = source.IndexOf('.');
            return separator < 0 ? source : source.Substring(0, separator);
        }

        /// <summary>The enclosing scope of a dotted source, or null at the top.</summary>
        public static string ParentOf(string source)
        {
            if (string.IsNullOrEmpty(source))
                return null;
            var separator = source.LastIndexOf('.');
            return separator > 0 ? source.Substring(0, separator) : null;
        }

        private static GameLogSourceOptions Normalize(GameLogSourceOptions options)
        {
            if (options == null)
                return new GameLogSourceOptions();

            return new GameLogSourceOptions
            {
                SourceRoot = NormalizeRoot(options.SourceRoot),
                IgnoredSegments = options.IgnoredSegments ?? Array.Empty<string>()
            };
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return null;

            var value = root.Replace('\\', '/').Trim();
            if (!value.StartsWith("/", StringComparison.Ordinal))
                value = "/" + value;
            if (!value.EndsWith("/", StringComparison.Ordinal))
                value += "/";
            return value;
        }

        private static bool IsIgnored(string segment, string[] ignored)
        {
            for (var index = 0; index < ignored.Length; index++)
            {
                if (string.Equals(segment, ignored[index], StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string TrimPartialSuffix(string fileName)
        {
            var separator = fileName.IndexOf('.');
            return separator > 0 ? fileName.Substring(0, separator) : fileName;
        }
    }
}
