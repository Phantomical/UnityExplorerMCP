using System.Text.Json.Nodes;
using UnityEngine;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class RaycastTools : ToolGroupBase
    {
        public RaycastTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools) { }

        #region Parameter Types

        public struct RaycastParams
        {
            [McpParam(
                "'screen' (from camera through screen point) or 'world' (explicit origin+direction)",
                Required = true,
                EnumValues = new[] { "screen", "world" }
            )]
            public string Mode { get; set; }

            [McpParam("Screen coordinates {x,y}. Required for mode='screen'.")]
            public Vec2Param? ScreenPosition { get; set; }

            [McpParam("Ray origin {x,y,z}. Required for mode='world'.")]
            public Vec3Param? Origin { get; set; }

            [McpParam("Ray direction {x,y,z}. Required for mode='world'.")]
            public Vec3Param? Direction { get; set; }

            [McpParam("Max ray distance (default 1000)")]
            public float? MaxDistance { get; set; }

            [McpParam("Physics layer mask (default: all layers)")]
            public int? LayerMask { get; set; }
        }

        #endregion

        public override void Register()
        {
            Tools.Register<RaycastParams>(
                "raycast",
                "Cast a ray into the scene and return what it hits. Use 'screen' mode with a screen position (uses main camera) or 'world' mode with an origin and direction.",
                Raycast
            );
        }

        McpProtocol.ToolCallResult Raycast(RaycastParams args)
        {
            float maxDistance = args.MaxDistance ?? 1000f;
            int layerMask = args.LayerMask ?? -1; // -1 = all layers

            Ray ray;

            if (args.Mode == "screen")
            {
                var cam = Camera.main;
                if (cam == null)
                    return McpProtocol.ToolError("No main camera found.");

                if (!args.ScreenPosition.HasValue)
                    return McpProtocol.ToolError("screenPosition is required for screen mode.");

                var sp = args.ScreenPosition.Value;
                ray = cam.ScreenPointToRay(new Vector3(sp.X, sp.Y, 0));
            }
            else if (args.Mode == "world")
            {
                if (!args.Origin.HasValue || !args.Direction.HasValue)
                    return McpProtocol.ToolError(
                        "origin and direction are required for world mode."
                    );

                ray = new Ray(args.Origin.Value.ToVector3(), args.Direction.Value.ToVector3());
            }
            else
            {
                return McpProtocol.ToolError(
                    $"Invalid mode: {args.Mode}. Use 'screen' or 'world'."
                );
            }

            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, layerMask))
            {
                var go = hit.collider.gameObject;
                return McpProtocol.ToolSuccess(
                    new JsonObject
                    {
                        ["hit"] = true,
                        ["hitInfo"] = new JsonObject
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

            return McpProtocol.ToolSuccess(new JsonObject { ["hit"] = false, ["hitInfo"] = null });
        }

        static JsonObject Vec3(Vector3 v) =>
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
