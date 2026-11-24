using System;
using System.Collections.Generic;
using UniSpring.DependencyInjection;
using UniSpring.Logging.Domain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UniSpring.Logging.Integration
{
    public interface IUnityLogLevelStrategy : IKeyedStrategy<GameLogLevel>
    {
        void Write(string message, Object context);

        /// <summary>One console entry with the GameLog line and the exception's stack.</summary>
        void WriteException(string message, Exception exception, Object context);
    }

    [Strategy]
    public sealed class UnityPlainLogLevelStrategy : IUnityLogLevelStrategy
    {
        public IEnumerable<GameLogLevel> Keys
        {
            get
            {
                yield return GameLogLevel.Debug;
                yield return GameLogLevel.Info;
            }
        }

        [HideInCallstack]
        public void Write(string message, Object context)
        {
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, context, "{0}", message);
        }

        [HideInCallstack]
        public void WriteException(string message, Exception exception, Object context)
        {
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, context, "{0}\n{1}", message, exception);
        }
    }

    [Strategy]
    public sealed class UnityWarningLogLevelStrategy : IUnityLogLevelStrategy
    {
        public IEnumerable<GameLogLevel> Keys
        {
            get { yield return GameLogLevel.Warn; }
        }

        [HideInCallstack]
        public void Write(string message, Object context)
        {
            Debug.LogWarning(message, context);
        }

        [HideInCallstack]
        public void WriteException(string message, Exception exception, Object context)
        {
            Debug.LogWarning(message + "\n" + exception, context);
        }
    }

    [Strategy]
    public sealed class UnityErrorLogLevelStrategy : IUnityLogLevelStrategy
    {
        public IEnumerable<GameLogLevel> Keys
        {
            get { yield return GameLogLevel.Error; }
        }

        [HideInCallstack]
        public void Write(string message, Object context)
        {
            Debug.LogError(message, context);
        }

        [HideInCallstack]
        public void WriteException(string message, Exception exception, Object context)
        {
            Debug.LogException(new GameLogException(message, exception), context);
        }
    }
}
