using System;
using System.Collections.Generic;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UniSpring.Logging.Dto;

namespace UniSpring.Logging.Services
{
    [Service]
    public sealed class LoggingPropertiesParser
    {
        public LoggingSettings Parse(IEnumerable<string> lines)
        {
            if (lines == null)
                throw new ArgumentNullException(nameof(lines));

            var result = new LoggingSettings();
            var lineNumber = 0;

            foreach (var rawLine in lines)
            {
                lineNumber++;

                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                var line = rawLine.Trim();
                if (line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    throw new FormatException($"Invalid logging configuration at line {lineNumber}: {line}");

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();

                if (string.IsNullOrWhiteSpace(key))
                    throw new FormatException($"Empty logging configuration key at line {lineNumber}.");

                if (string.IsNullOrWhiteSpace(value))
                    throw new FormatException($"Empty value for '{key}' at line {lineNumber}.");

                if (key.Equals("sinks", StringComparison.OrdinalIgnoreCase))
                {
                    result.SinkSelection = value;
                    continue;
                }

                const string sinkPrefix = "sinks.";
                if (key.StartsWith(sinkPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var source = key.Substring(sinkPrefix.Length).Trim();
                    if (source.Length == 0)
                        throw new FormatException($"Empty sink source at line {lineNumber}.");

                    result.SourceSinkSelections[source] = value;
                    continue;
                }

                var level = ParseLevel(value, lineNumber);

                if (key.Equals("root", StringComparison.OrdinalIgnoreCase))
                {
                    result.RootLevel = level;
                    continue;
                }

                result.ClassLevels[key] = level;
            }

            return result;
        }

        private static GameLogLevel ParseLevel(string value, int lineNumber)
        {
            // Enum.TryParse also accepts numbers ("7", "-1"); only the names are valid here.
            GameLogLevel level;
            if (char.IsLetter(value[0]) &&
                Enum.TryParse(value, true, out level) &&
                Enum.IsDefined(typeof(GameLogLevel), level))
            {
                return level;
            }

            throw new FormatException($"Unknown log level '{value}' at line {lineNumber}. Expected Debug, Info, Warn, Error or None.");
        }
    }
}
