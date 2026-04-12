using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// MonoBehaviour that dequeues and executes actions on the Unity main thread.
    /// Unity API calls are not thread-safe and must be dispatched through this.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        static MainThreadDispatcher _instance;
        public static MainThreadDispatcher Instance => _instance;

        readonly ConcurrentQueue<Action> _queue = new();

        public static void Initialize(GameObject parent)
        {
            if (_instance != null)
                return;
            _instance = parent.AddComponent<MainThreadDispatcher>();
        }

        /// <summary>
        /// Enqueue an action to run on the main thread. The action receives a callback
        /// to signal completion with a result string (JSON).
        /// </summary>
        public void Enqueue(Action<Action<string>> work, Action<string> onComplete)
        {
            _queue.Enqueue(() =>
            {
                try
                {
                    work(onComplete);
                }
                catch (Exception ex)
                {
                    onComplete($"{{\"error\":\"{EscapeJson(ex.ToString())}\"}}");
                }
            });
        }

        /// <summary>
        /// Enqueue a simple action with no result.
        /// </summary>
        public void Enqueue(Action action)
        {
            _queue.Enqueue(action);
        }

        /// <summary>
        /// Enqueue work and block the calling thread until it completes on the main thread.
        /// Returns the result string. Do NOT call from the main thread.
        /// </summary>
        public string EnqueueAndWait(Func<string> work)
        {
            string result = null;
            Exception caught = null;
            using var waitHandle = new ManualResetEventSlim(false);

            _queue.Enqueue(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            waitHandle.Wait();

            if (caught != null)
                throw new Exception("Main thread execution failed", caught);

            return result;
        }

        void Update()
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnityExplorerMCP] MainThreadDispatcher error: {ex}");
                }
            }
        }

        static string EscapeJson(string s)
        {
            return s
                ?.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }
    }
}
