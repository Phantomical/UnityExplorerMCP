using System;
using UnityEngine;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// Logging that is safe to call from any thread. Unity runs log subscribers on
    /// the calling thread, and KSP's DebugScreenConsole instantiates TextMeshPro
    /// objects from its subscriber, so a Debug.Log from a worker thread creates
    /// Unity objects off the main thread and can segfault the process.
    /// </summary>
    internal static class McpLog
    {
        public static void Info(string message) => Post(() => Debug.Log(message));

        public static void Warn(string message) => Post(() => Debug.LogWarning(message));

        public static void Error(string message) => Post(() => Debug.LogError(message));

        static void Post(Action action)
        {
            if (MainThreadDispatcher.IsMainThread)
            {
                action();
                return;
            }

            // ReferenceEquals avoids UnityEngine.Object's overloaded null check, which
            // is not meant to be called off the main thread. With no dispatcher there
            // is no safe way to log from this thread, so the message is dropped.
            var dispatcher = MainThreadDispatcher.Instance;
            if (!ReferenceEquals(dispatcher, null))
                dispatcher.Enqueue(action);
        }
    }
}
