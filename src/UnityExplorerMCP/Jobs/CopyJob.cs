using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace UnityExplorerMCP.Jobs;

[BurstCompile]
internal struct CopyJob : IJob
{
    [ReadOnly]
    public NativeArray<byte> source;

    [WriteOnly]
    public NativeArray<byte> dest;

    public void Execute()
    {
        dest.CopyFrom(source);
    }
}
