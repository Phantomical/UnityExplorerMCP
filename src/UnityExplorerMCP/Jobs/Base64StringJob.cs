using System;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityExplorerMCP.Util;

namespace UnityExplorerMCP.Jobs;

internal struct Base64StringJob : IJob
{
    public ObjectHandle<TaskCompletionSource<string>> tcs;

    [DeallocateOnJobCompletion]
    public NativeBox<NativeArray<byte>> base64;

    public void Execute()
    {
        using var handle = this.tcs;
        var tcs = handle.Target;

        try
        {
            using var base64 = this.base64.Value;
            tcs.SetResult(Encoding.ASCII.GetString([.. base64], 0, base64.Length));
        }
        catch (Exception e)
        {
            tcs.TrySetException(e);
        }
    }
}
