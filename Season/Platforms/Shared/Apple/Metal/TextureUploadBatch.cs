// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal batch texture upload aligned one to one with DX12 and Vulkan TextureUploadBatch:
///   1. collect Texture tasks uniformly and allocate a single staging IMTLBuffer using StorageModeShared
///   2. on an IMTLCommandBuffer from the graphics queue:
///         - CreateBlitCommandEncoder
///         - CopyFromBuffer from staging into IMTLTexture
///         - EndEncoding
///   3. Commit plus WaitUntilCompleted, where synchronous waiting stays simple and reliable and Metal handles ownership automatically
/// Differences from Vulkan:
///   - no explicit layout transition is required, because StorageMode.Private becomes GPU-only visible automatically
///   - no dedicated transfer queue or pool is required, because Metal command pools are managed internally by IMTLCommandQueue
/// </summary>
internal sealed class TextureUploadBatch : IDisposable
{
    readonly List<Texture> _tasks = new();

    readonly IMTLDevice _device;

    readonly CommandQueue _queue;

    public TextureUploadBatch(IMTLDevice device, CommandQueue queue)
    {
        _device = device;
        _queue = queue;
    }

    public List<Texture> GetTasks() => _tasks;

    public void AddTextureUpload(Texture texture) => _tasks.Add(texture);

    /// <summary>Execute one batch upload and wait synchronously for GPU completion.</summary>
    public unsafe void Execute()
    {
        if (_tasks.Count == 0) return;

        // 1. Compute the total size and align it to 4 bytes.
        ulong totalSize = 0;
        var offsets = new ulong[_tasks.Count];
        for (int i = 0; i < _tasks.Count; i++)
        {
            offsets[i] = totalSize;
            ulong size = (ulong)(_tasks[i].Width * _tasks[i].Height * 4u);
            totalSize += AlignUp(size, 4);
        }

        // 2. Allocate the staging buffer and copy pixels into it using StorageModeShared for direct CPU writes.
        var staging = _device.CreateBuffer((nuint)totalSize, MTLResourceOptions.StorageModeShared)
                      ?? throw new Exception("staging IMTLBuffer.CreateBuffer failed");

        try
        {
            byte* basePtr = (byte*)staging.Contents;
            for (int i = 0; i < _tasks.Count; i++)
            {
                var t = _tasks[i];
                if (t.ImageData == null) continue;
                fixed (byte* pSrc = t.ImageData)
                    Unsafe.CopyBlock(basePtr + offsets[i], pSrc, (uint)(t.Width * t.Height * 4u));
            }

            // 3. Record the blit command buffer.
            var cmd = _queue.CreateCommandBuffer();
            var blit = cmd.CreateBlitCommandEncoder(new MTLBlitPassDescriptor()) ?? throw new Exception("CreateBlitCommandEncoder failed");

            for (int i = 0; i < _tasks.Count; i++)
            {
                var t = _tasks[i];
                blit.CopyFromBuffer(
                    sourceBuffer: staging,
                    sourceOffset: (nuint)offsets[i],
                    sourceBytesPerRow: (nuint)(t.Width * 4u),
                    sourceBytesPerImage: (nuint)(t.Width * t.Height * 4u),
                    sourceSize: new MTLSize((nint)t.Width, (nint)t.Height, 1),
                    destinationTexture: t.Image,
                    destinationSlice: 0,
                    destinationLevel: 0,
                    destinationOrigin: new MTLOrigin(0, 0, 0));
            }

            blit.EndEncoding();

            // 4. Commit, wait for completion, and mark each texture.
            ulong signal = _queue.RegisterSignal(cmd);
            cmd.Commit();
            cmd.WaitUntilCompleted();

            foreach (var t in _tasks)
            {
                t.UploadFenceValue = signal;
                t.Ready = true;
                t.ImageData = null;
            }
        }
        finally
        {
            staging.Dispose();
            _tasks.Clear();
        }
    }

    static ulong AlignUp(ulong v, ulong align) => (v + align - 1) & ~(align - 1);

    public void Dispose() => _tasks.Clear();
}
