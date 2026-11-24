using System;
using UniSpring.Logging.Domain;

namespace UniSpring.Logging.Integration
{
    /// <summary>
    /// One formatting rule for the Unity sink and the startup fallback:
    /// <c>[LEVEL][ShortName] message</c>. A legacy hand-written prefix that only
    /// repeats the source (<c>[Inventory] ...</c> in an Inventory file)
    /// is dropped so it is not printed twice.
    /// </summary>
    internal static class LogMessageFormat
    {
        public static string Line(GameLogLevel level, string source, object message, Exception exception)
        {
            var prefix = "[" + level.ToString().ToUpperInvariant() + "][" + GameLogSource.ShortName(source) + "]";
            return prefix + " " + Body(source, message, exception);
        }

        private static string Body(string source, object rawMessage, Exception exception)
        {
            if (rawMessage == null && exception != null)
                return exception.GetType().Name + ": " + exception.Message;
            if (rawMessage == null)
                return "<null>";
            return StripRepeatedSource(source, rawMessage.ToString());
        }

        private static string StripRepeatedSource(string source, string message)
        {
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(source) || message[0] != '[')
                return message;

            var closingBracket = message.IndexOf(']');
            if (closingBracket <= 1)
                return message;

            var legacy = message.Substring(1, closingBracket - 1);
            if (!RepeatsSource(legacy, source))
                return message;

            return message.Substring(closingBracket + 1).TrimStart();
        }

        private static bool RepeatsSource(string legacy, string source)
        {
            var shortName = GameLogSource.ShortName(source);
            var module = GameLogSource.Module(source);
            return legacy.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                   legacy.Equals(shortName, StringComparison.OrdinalIgnoreCase) ||
                   legacy.StartsWith(shortName + "/", StringComparison.OrdinalIgnoreCase) ||
                   legacy.Equals(module, StringComparison.OrdinalIgnoreCase) ||
                   legacy.StartsWith(module + ".", StringComparison.OrdinalIgnoreCase) ||
                   legacy.StartsWith(module + "/", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Carries the GameLog line as the message of an exception entry, so the
    /// console shows source, text and the original stack trace as ONE entry.
    /// </summary>
    public sealed class GameLogException : Exception
    {
        public GameLogException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
