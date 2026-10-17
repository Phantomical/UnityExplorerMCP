using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityExplorerMCP.Jobs;
using UnityExplorerMCP.Server;
using UnityExplorerMCP.Util;

namespace UnityExplorerMCP.Tools
{
    public class ScreenshotTools
    {
        readonly ToolRegistry _tools;

        public ScreenshotTools(ToolRegistry tools)
        {
            _tools = tools;
        }

        #region Parameter Types

        public struct TakeScreenshotParams
        {
            [McpParam(
                "Max width in pixels. Image will be downscaled to fit while preserving aspect ratio."
            )]
            public int? MaxWidth { get; set; }

            [McpParam(
                "Max height in pixels. Image will be downscaled to fit while preserving aspect ratio."
            )]
            public int? MaxHeight { get; set; }

            [McpParam(
                "Hide the developer console and UnityExplorer UI while capturing (default true)."
            )]
            public bool? HideOverlays { get; set; }
        }

        #endregion

        public void Register()
        {
            _tools.RegisterCoroutine<TakeScreenshotParams>(
                "take_screenshot",
                "Capture a screenshot of the current game view. Returns a base64-encoded PNG image.",
                TakeScreenshot
            );
        }

        IEnumerator TakeScreenshot(
            TakeScreenshotParams args,
            Action<McpProtocol.ToolCallResult> callback
        )
        {
            // 1. Optionally hide overlays, then wait a frame so the hidden
            //    state is rendered before we capture.
            ScreenOverlays.State overlays = null;
            if (args.HideOverlays ?? true)
            {
                overlays = ScreenOverlays.Hide();
                yield return null;
            }

            // Wait until end of frame so the screen has been fully rendered
            yield return new WaitForEndOfFrame();

            // 2. Determine target size
            int screenW = Screen.width;
            int screenH = Screen.height;
            ComputeTargetSize(
                screenW,
                screenH,
                args.MaxWidth,
                args.MaxHeight,
                out int targetW,
                out int targetH
            );

            // 3. Copy the screen into a full-size render target. Blitting the
            //    backbuffer is a plain copy that ignores scale/offset, so the
            //    downscale and flip happen in a separate RT-to-RT blit.
            var screenRt = RenderTexture.GetTemporary(
                screenW,
                screenH,
                0,
                RenderTextureFormat.ARGB32
            );
            var cmd = new CommandBuffer();
            cmd.Blit(BuiltinRenderTextureType.CurrentActive, screenRt);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            ScreenOverlays.Restore(overlays);

            // The backbuffer is stored top-down on D3D-like APIs while render
            // textures (and the PNG encoder) expect bottom-up rows, so flip Y.
            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            screenRt.filterMode = FilterMode.Bilinear;
            var prevActive = RenderTexture.active;
            if (SystemInfo.graphicsUVStartsAtTop)
                Graphics.Blit(screenRt, rt, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else
                Graphics.Blit(screenRt, rt);
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(screenRt);

            // 4. Schedule an AsyncGPUReadback from the render target
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);

            // 5. Wait for the readback to complete
            while (!request.done)
                yield return null;

            RenderTexture.ReleaseTemporary(rt);

            if (request.hasError)
            {
                callback(McpProtocol.ToolError("GPU readback failed."));
                yield break;
            }

            var readbackData = request.GetData<byte>();

            // 6-8. Schedule a job to PNG-encode and base64-encode off the main thread
            var base64data = new NativeBox<NativeArray<byte>>(Allocator.Persistent);
            var tcs = new TaskCompletionSource<string>();
            var pixelData = new NativeArray<byte>(
                readbackData.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );

            // Copy the data into a separate persistent native array
            JobHandle copyHandle = new CopyJob
            {
                source = readbackData,
                dest = pixelData,
            }.Schedule();

            JobHandle handle;
            handle = new ScreenshotEncodeJob
            {
                pixels = pixelData,
                width = targetW,
                height = targetH,
                output = base64data,
            }.Schedule(copyHandle);
            handle = new Base64StringJob { tcs = new(tcs), base64 = base64data }.Schedule(handle);

            JobHandle.ScheduleBatchedJobs();

            if (!copyHandle.IsCompleted)
            {
                // The readback array only lasts until the end of the frame.
                // We try to complete it async but block if it doesn't finish
                // fast enough.
                yield return new WaitForEndOfFrame();
                copyHandle.Complete();
            }

            while (!handle.IsCompleted)
                yield return null;
            handle.Complete();

            // 9. Return the base64 data as an image
            callback(McpProtocol.ToolSuccessImage(tcs.Task.Result, "image/png"));
        }

        static void ComputeTargetSize(
            int srcW,
            int srcH,
            int? maxW,
            int? maxH,
            out int w,
            out int h
        )
        {
            w = srcW;
            h = srcH;

            if (maxW.HasValue && w > maxW.Value)
            {
                float scale = (float)maxW.Value / w;
                w = maxW.Value;
                h = Mathf.RoundToInt(h * scale);
            }

            if (maxH.HasValue && h > maxH.Value)
            {
                float scale = (float)maxH.Value / h;
                h = maxH.Value;
                w = Mathf.RoundToInt(w * scale);
            }

            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
        }
    }
}
