// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Season.Rendering;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// D3D12 RenderTarget implementation.
/// Step 1 wraps existing swapchain backbuffers / MSAA targets.
/// Step 2 adds offscreen color RT support
/// (OwnsResource=true, with its own SRV for FinalBlit sampling).
/// Step 3 adds depth-only RT support
/// (shadow maps with private DSV + depth SRV) and non-full-size RTs.
/// Its core responsibility is to carry RTV/DSV handle indices and track the
/// current ResourceStates so barriers are driven by the tracked real state
/// instead of hard-coded assumptions.
///
/// State-tracking ownership rule
/// (D3D12-specific, fixed in Step 4): any state transition for Color or Depth
/// must go through TransitionTo / TransitionDepthTo. Direct barriers that skip
/// tracking are forbidden because they desynchronize state tracking and break
/// idempotence checks, causing missed or duplicated transitions.
/// The only exception is the internal transition inside
/// Device.CopyBackBufferForCapture, which writes back CurrentState afterward
/// (see FinishBackbufferForPresent).
/// Calls are restricted to BeginPass / EndPass / platform binding APIs such as
/// BlitToBackbuffer and the Target path of DispatchCompute. Pass-body content
/// code must not touch them, matching the strictest VK render-pass rule that
/// forbids barriers inside a render pass.
/// </summary>
internal unsafe sealed class DXRenderTarget : RenderTarget
{
    /// <summary>"No RTV" sentinel for RtvIndex. Depth-only RTs do not occupy an
    /// RTV slot.</summary>
    public const uint NoRtv = uint.MaxValue;

    /// <summary>Color resource. Backbuffers and MSAA targets are managed by
    /// SwapChain/Display, offscreen RTs are owned by this class, and depth-only
    /// RTs keep this null.</summary>
    public ID3D12Resource* Color;

    /// <summary>RTV index in RtvHeapManager.</summary>
    public uint RtvIndex;

    /// <summary>Whether depth is bound. In Step 1 this reuses the single global
    /// DSV, always at index 0.</summary>
    public bool HasDepth;

    /// <summary>Current resource state used as barrier source. Backbuffers start
    /// in Present, MSAA targets start in RenderTarget.</summary>
    public ResourceStates CurrentState;

    /// <summary>Resolve destination for MSAA. Only populated on Scene RTs in
    /// MSAA scenarios, where BeginPass binds either the current-frame backbuffer
    /// or offscreen SceneColor.</summary>
    public DXRenderTarget? ResolveDest;

    // Offscreen-RT-only fields (Step 2)

    /// <summary>True means the resource was created by
    /// Device.CreateRenderTarget and will be released there. False means this
    /// object only wraps an existing SwapChain/Display resource.</summary>
    public bool OwnsResource;

    /// <summary>SRV descriptor ID allocated by DescriptorAllocator for
    /// FinalBlit sampling. `-1` means there is no SRV, as with wrapped
    /// backbuffer / MSAA targets.</summary>
    public int SrvIndex = -1;

    /// <summary>GPU SRV handle for offscreen RTs, used for sampling by
    /// BlitPipeline and later passes.</summary>
    public GpuDescriptorHandle GpuSrvHandle;

    // Non-full-size / depth-only support (Step 3)

    /// <summary>Target size, recorded when an offscreen RT is created or rebuilt.
    /// Drives BeginPass viewport/scissor. Unused for wrapped resources.</summary>
    public uint Width;
    public uint Height;

    /// <summary>Native format of the color plane, used to keep resource/SRV/clear
    /// behavior consistent. Valid for offscreen color RTs.</summary>
    public Format ColorNativeFormat;

    /// <summary>Owned depth resource for depth-only shadow-map RTs.
    /// Null means no private depth resource and the Scene path reuses the global DSV.</summary>
    public ID3D12Resource* Depth;

    /// <summary>Slot of the private DSV in DsvHeapManager.
    /// `-1` means none, and slot 0 is the global depth buffer.</summary>
    public int DsvIndex = -1;

    /// <summary>Current state of the depth plane. CurrentState only tracks the
    /// color plane.</summary>
    public ResourceStates DepthCurrentState;

    public DXRenderTarget(ID3D12Resource* color, uint rtvIndex, ResourceStates initialState, bool hasDepth)
    {
        Color = color;
        RtvIndex = rtvIndex;
        CurrentState = initialState;
        HasDepth = hasDepth;
    }

    /// <summary>Transitions the color resource into the target state.
    /// If it is already there, the call is skipped. This is idempotent and emits
    /// no redundant barrier.</summary>
    public void TransitionTo(ID3D12GraphicsCommandList* cmd, ResourceStates newState)
    {
        if (CurrentState == newState)
            return;

        var barrier = Device.InitTransition(Color, CurrentState, newState);
        cmd->ResourceBarrier(1, &barrier);
        CurrentState = newState;
    }

    /// <summary>Transitions the depth-plane state. Idempotent.
    /// Used to switch between DepthWrite for shadow writes and
    /// PixelShaderResource for sampling in later passes.</summary>
    public void TransitionDepthTo(ID3D12GraphicsCommandList* cmd, ResourceStates newState)
    {
        if (DepthCurrentState == newState)
            return;

        var barrier = Device.InitTransition(Depth, DepthCurrentState, newState);
        cmd->ResourceBarrier(1, &barrier);
        DepthCurrentState = newState;
    }

    // Offscreen RTs with OwnsResource=true are released by Device, which also
    // reclaims RTV/SRV slots. Wrapped backbuffers and MSAA targets are still
    // owned by SwapChain / Display and are not released here.
    public override void Dispose()
    {
        if (OwnsResource)
            Device.DestroyOffscreenRenderTarget(this);
    }
}
