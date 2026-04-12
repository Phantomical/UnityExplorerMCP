using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class RaycastTools : ToolGroupBase
    {
        public RaycastTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools) { }

        public override void Register()
        {
            Tools.Register(
                "raycast",
                "Cast a ray into the scene and return what it hits. Use 'screen' mode with a screen position (uses main camera) or 'world' mode with an origin and direction.",
                @"{
                    ""mode"":           { ""type"": ""string"", ""enum"": [""screen"",""world""], ""description"": ""'screen' (from camera through screen point) or 'world' (explicit origin+direction)"" },
                    ""screenPosition"": { ""type"": ""object"", ""description"": ""Screen coordinates {x,y}. Required for mode='screen'."", ""properties"": {""x"":{""type"":""number""},""y"":{""type"":""number""}} },
                    ""origin"":         { ""type"": ""object"", ""description"": ""Ray origin {x,y,z}. Required for mode='world'."", ""properties"": {""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}} },
                    ""direction"":      { ""type"": ""object"", ""description"": ""Ray direction {x,y,z}. Required for mode='world'."", ""properties"": {""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}} },
                    ""maxDistance"":     { ""type"": ""number"", ""description"": ""Max ray distance (default 1000)"" },
                    ""layerMask"":       { ""type"": ""integer"", ""description"": ""Physics layer mask (default: all layers)"" }
                }",
                new[] { "mode" },
                Raycast
            );
        }

        McpProtocol.ToolCallResult Raycast(JObject args)
        {
            string mode = GetString(args, "mode");
            float maxDistance = args?["maxDistance"]?.Value<float>() ?? 1000f;
            int layerMask = GetInt(args, "layerMask", -1); // -1 = all layers

            Ray ray;

            if (mode == "screen")
            {
                var cam = Camera.main;
                if (cam == null)
                    return McpProtocol.ToolError("No main camera found.");

                var screenPos = args?["screenPosition"] as JObject;
                if (screenPos == null)
                    return McpProtocol.ToolError("screenPosition is required for screen mode.");

                float x = screenPos["x"]?.Value<float>() ?? 0;
                float y = screenPos["y"]?.Value<float>() ?? 0;
                ray = cam.ScreenPointToRay(new Vector3(x, y, 0));
            }
            else if (mode == "world")
            {
                var originObj = args?["origin"] as JObject;
                var dirObj = args?["direction"] as JObject;
                if (originObj == null || dirObj == null)
                    return McpProtocol.ToolError(
                        "origin and direction are required for world mode."
                    );

                var origin = new Vector3(
                    originObj["x"]?.Value<float>() ?? 0,
                    originObj["y"]?.Value<float>() ?? 0,
                    originObj["z"]?.Value<float>() ?? 0
                );
                var direction = new Vector3(
                    dirObj["x"]?.Value<float>() ?? 0,
                    dirObj["y"]?.Value<float>() ?? 0,
                    dirObj["z"]?.Value<float>() ?? 0
                );
                ray = new Ray(origin, direction);
            }
            else
            {
                return McpProtocol.ToolError($"Invalid mode: {mode}. Use 'screen' or 'world'.");
            }

            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, layerMask))
            {
                var go = hit.collider.gameObject;
                return McpProtocol.ToolSuccess(
                    new JObject
                    {
                        ["hit"] = true,
                        ["hitInfo"] = new JObject
                        {
                            ["objectHandle"] = Registry.Register(go),
                            ["gameObjectName"] = go.name,
                            ["gameObjectPath"] = GetFullPath(go.transform),
                            ["point"] = Vec3(hit.point),
                            ["normal"] = Vec3(hit.normal),
                            ["distance"] = hit.distance,
                            ["colliderType"] = hit.collider.GetType().Name,
                        },
                    }
                );
            }

            return McpProtocol.ToolSuccess(new JObject { ["hit"] = false, ["hitInfo"] = null });
        }

        static JObject Vec3(Vector3 v) =>
            new()
            {
                ["x"] = v.x,
                ["y"] = v.y,
                ["z"] = v.z,
            };

        static string GetFullPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return "/" + path;
        }
    }
}
