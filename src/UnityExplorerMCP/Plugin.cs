using System;
using UnityEngine;
using UnityExplorerMCP.Server;
using UnityExplorerMCP.Tools;

namespace UnityExplorerMCP
{
    /// <summary>
    /// KSP addon entry point. Creates a persistent GameObject that hosts
    /// the MCP server and main thread dispatcher.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Plugin : MonoBehaviour
    {
        static Plugin _instance;
        McpServer _server;
        ToolRegistry _tools;
        ObjectRegistry.ObjectRegistry _registry;

        void Awake()
        {
            if (_instance != null)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            int port = ReadPort();

            _registry = new ObjectRegistry.ObjectRegistry();
            _tools = new ToolRegistry();
            MainThreadDispatcher.Initialize(gameObject);

            RegisterAllTools();

            _server = new McpServer(port, _tools);
            _server.Start();

            Debug.Log($"[UnityExplorerMCP] Plugin initialized, MCP server on port {port}");
        }

        void OnDestroy()
        {
            _server?.Stop();
            if (_instance == this)
                _instance = null;
        }

        void RegisterAllTools()
        {
            new SceneTools(_registry, _tools).Register();
            new GameObjectTools(_registry, _tools).Register();
            new ComponentTools(_registry, _tools).Register();
            new ReflectionTools(_registry, _tools).Register();
            new SearchTools(_registry, _tools).Register();
            new ConsoleTools(_tools).Register();
            new LogTools(_tools).Register();
            new RaycastTools(_registry, _tools).Register();
        }

        static int ReadPort()
        {
            // Try to read port from config. Default to 7192.
            try
            {
                var nodes = GameDatabase.Instance?.GetConfigNodes("UNITY_EXPLORER_MCP");
                if (nodes != null && nodes.Length > 0)
                {
                    if (int.TryParse(nodes[0].GetValue("port"), out int port))
                        return port;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityExplorerMCP] Could not read config: {ex.Message}");
            }
            return 7192;
        }
    }
}
