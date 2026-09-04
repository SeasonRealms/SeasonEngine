// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Aligns with the DX12 and Vulkan FrameContext implementations by holding the current IMTLCommandBuffer,
/// RenderEncoder, and fence target value for each frame.
/// On Metal, command pools and allocators are managed internally by IMTLCommandQueue,
/// so this class only serves as a per-frame reference slot.
/// </summary>
internal sealed class FrameContext
{
    public IMTLCommandBuffer? CommandBuffer { get; set; }

    public IMTLRenderCommandEncoder? Encoder { get; set; }

    /// <summary>The monotonic value assigned by CommandQueue.RegisterSignal when this frame is submitted.</summary>
    public ulong FenceValue { get; set; }

    public void Reset()
    {
        CommandBuffer = null;
        Encoder = null;
    }
}
