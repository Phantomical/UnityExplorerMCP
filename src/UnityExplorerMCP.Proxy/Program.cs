using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityExplorerMCP.Proxy
{
    static class Program
    {
        const string Usage =
            @"Usage: UnityExplorerMCP.Proxy [options]

Runs a stdio MCP server that relays to the UnityExplorerMCP mod inside KSP,
reconnecting automatically whenever the game starts.

Options:
  --port <port>    Port of the in-game server (default 7192)
  --url <url>      Base URL of the in-game server (default http://127.0.0.1:<port>/)
  --cache <path>   Where to remember the tool list between runs
                   (default %LOCALAPPDATA%\UnityExplorerMCP\tools-cache.json)
  --no-cache       Don't remember the tool list
  -h, --help       Show this help";

        static async Task<int> Main(string[] args)
        {
            int port = 7192;
            string url = null;
            string cachePath = ToolCache.DefaultPath;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next() =>
                    i + 1 < args.Length
                        ? args[++i]
                        : throw new ArgumentException($"{arg} requires a value");

                try
                {
                    switch (arg)
                    {
                        case "--port":
                            port = int.Parse(Next());
                            break;
                        case "--url":
                            url = Next();
                            break;
                        case "--cache":
                            cachePath = Next();
                            break;
                        case "--no-cache":
                            cachePath = null;
                            break;
                        case "-h":
                        case "--help":
                            Console.Error.WriteLine(Usage);
                            return 0;
                        default:
                            throw new ArgumentException($"Unknown option {arg}");
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    Console.Error.WriteLine(ex.Message);
                    Console.Error.WriteLine(Usage);
                    return 2;
                }
            }

            url ??= $"http://127.0.0.1:{port}/";
            if (!url.EndsWith('/'))
                url += "/";

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var server = new ProxyServer(new Uri(url), new ToolCache(cachePath));
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            return 0;
        }
    }
}
