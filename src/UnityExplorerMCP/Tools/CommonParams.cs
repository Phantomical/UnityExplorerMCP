using UnityEngine;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Reusable parameter struct for 3D vector values (position, direction, scale, etc.).
    /// </summary>
    public struct Vec3Param
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    /// <summary>
    /// Reusable parameter struct for 2D vector values (screen coordinates, etc.).
    /// </summary>
    public struct Vec2Param
    {
        public float X { get; set; }
        public float Y { get; set; }
    }
}
