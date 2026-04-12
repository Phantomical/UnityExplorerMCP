namespace UnityExplorerMCP.ObjectRegistry
{
    /// <summary>
    /// Constants and helpers for opaque object handle strings.
    /// Unity objects: "u:{instanceId}" — stable for the object's lifetime.
    /// Managed objects: "m:{handleId}" — monotonic counter for non-Unity C# objects.
    /// </summary>
    public static class ObjectHandle
    {
        public const string UnityPrefix = "u:";
        public const string ManagedPrefix = "m:";

        public static string ForUnity(int instanceId) => $"{UnityPrefix}{instanceId}";

        public static string ForManaged(long handleId) => $"{ManagedPrefix}{handleId}";

        public static bool IsUnityHandle(string handle) =>
            handle != null && handle.StartsWith(UnityPrefix);

        public static bool IsManagedHandle(string handle) =>
            handle != null && handle.StartsWith(ManagedPrefix);

        public static bool TryParseUnityId(string handle, out int instanceId)
        {
            instanceId = 0;
            if (!IsUnityHandle(handle))
                return false;
            return int.TryParse(handle.Substring(UnityPrefix.Length), out instanceId);
        }

        public static bool TryParseManagedId(string handle, out long handleId)
        {
            handleId = 0;
            if (!IsManagedHandle(handle))
                return false;
            return long.TryParse(handle.Substring(ManagedPrefix.Length), out handleId);
        }
    }
}
