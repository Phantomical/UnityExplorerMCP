using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityExplorerMCP.Util;

namespace UnityExplorerMCP.Jobs;

/// <summary>
/// Job that PNG-encodes raw pixel data and then base64-encodes the result,
/// running entirely on a worker thread.
/// </summary>
[BurstCompile]
struct ScreenshotEncodeJob : IJob
{
    [DeallocateOnJobCompletion]
    public NativeArray<byte> pixels;
    public NativeBox<NativeArray<byte>> output;

    public int width;
    public int height;

    public void Execute()
    {
        using var data = ImageConversion.EncodeNativeArrayToPNG(
            pixels,
            GraphicsFormat.R8G8B8A8_UNorm,
            (uint)width,
            (uint)height
        );

        var base64len = (data.Length + 2) / 3 * 4;
        var base64 = new NativeArray<byte>(
            base64len,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory
        );

        int remainder = data.Length % 3;
        int count = data.Length - remainder;
        int outidx = 0;

        int i = 0;
        for (; i < count; i += 3)
        {
            int b0 = data[i];
            int b1 = data[i + 1];
            int b2 = data[i + 2];

            base64[outidx++] = ToBase64Char(b0 >> 2);
            base64[outidx++] = ToBase64Char(((b0 & 0x03) << 4) | (b1 >> 4));
            base64[outidx++] = ToBase64Char(((b1 & 0x0F) << 2) | (b2 >> 6));
            base64[outidx++] = ToBase64Char(b2 & 0x3F);
        }

        if (remainder == 1)
        {
            int b0 = data[i];
            base64[outidx++] = ToBase64Char(b0 >> 2);
            base64[outidx++] = ToBase64Char((b0 & 0x03) << 4);
            base64[outidx++] = (byte)'=';
            base64[outidx++] = (byte)'=';
        }
        else if (remainder == 2)
        {
            int b0 = data[i];
            int b1 = data[i + 1];
            base64[outidx++] = ToBase64Char(b0 >> 2);
            base64[outidx++] = ToBase64Char(((b0 & 0x03) << 4) | (b1 >> 4));
            base64[outidx++] = ToBase64Char((b1 & 0x0F) << 2);
            base64[outidx++] = (byte)'=';
        }

        output.Value = base64;
    }

    static byte ToBase64Char(int sixBit)
    {
        if (sixBit < 26)
            return (byte)('A' + sixBit);
        if (sixBit < 52)
            return (byte)('a' + sixBit - 26);
        if (sixBit < 62)
            return (byte)('0' + sixBit - 52);
        return sixBit == 62 ? (byte)'+' : (byte)'/';
    }
}
