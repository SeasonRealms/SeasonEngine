// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace Season.Platforms.Windows.DirectX;

internal unsafe class Display : IDisposable
{
    public ID3D12Resource* MsaaRenderTarget { get; private set; }
    public ID3D12Resource* DepthStencil { get; private set; }
    public uint MsaaSampleCount { get; private set; }
    public uint MsaaQualityLevels { get; private set; }
    public Format DepthBufferFormat { get; }
    public Format BackBufferFormat { get; }
    public Viewport Viewport { get; private set; }
    public Box2D<int> ScissorRect { get; private set; }

    private readonly ID3D12Device* _device;
    private Vector4 _clearColor;

    public Display(ID3D12Device* device, Format backBufferFormat, Format depthBufferFormat, uint msaaSampleCount = 4)
    {
        _device = device;
        BackBufferFormat = backBufferFormat;
        DepthBufferFormat = depthBufferFormat;
        MsaaSampleCount = msaaSampleCount;
        _clearColor = new Vector4(1f, 1f, 1f, 1f);
    }

    public void SetClearColor(Vector4 color)
    {
        _clearColor = color;
    }

    public void Initialize(int width, int height)
    {
        // Check MSAA support
        CheckMsaaSupport();

        // Create the MSAA render target if needed
        if (MsaaSampleCount > 1)
        {
            CreateMsaaRenderTarget(width, height);
        }

        // Create the depth-stencil buffer
        CreateDepthStencil(width, height);

        // Update the viewport and scissor rectangle
        UpdateViewportAndScissor(width, height);
    }

    public void Resize(int width, int height)
    {
        // Release old resources
        ReleaseMsaaAndDepth();

        // Recreate resources
        if (MsaaSampleCount > 1)
        {
            CreateMsaaRenderTarget(width, height);
        }
        CreateDepthStencil(width, height);
        UpdateViewportAndScissor(width, height);
    }

    private void CheckMsaaSupport()
    {
        // Check multisample support for the Scene target format
        // (under the HDR chain the MSAA target is RGBA16F, following
        // Device.SceneColorFormat)
        var qualityLevels = new FeatureDataMultisampleQualityLevels
        {
            Format = Device.SceneColorFormat,
            SampleCount = MsaaSampleCount,
            Flags = MultisampleQualityLevelFlags.None
        };

        _device->CheckFeatureSupport(
            Silk.NET.Direct3D12.Feature.MultisampleQualityLevels,
            &qualityLevels,
            (uint)sizeof(FeatureDataMultisampleQualityLevels)
        );

        MsaaQualityLevels = qualityLevels.NumQualityLevels;

        // The backbuffer format does not support the current SampleCount, so we
        // must downgrade immediately. Otherwise MsaaQualityLevels - 1 underflows
        // as uint and becomes a huge value, leading to an invalid call.
        if (MsaaQualityLevels == 0)
        {
            MsaaSampleCount = 1;
            MsaaQualityLevels = 1; // Keep >= 1 to avoid SampleDesc.Quality underflow
            return;
        }

        // Check multisample support for the depth-buffer format
        var depthQualityLevels = new FeatureDataMultisampleQualityLevels
        {
            Format = DepthBufferFormat,
            SampleCount = MsaaSampleCount,
            Flags = MultisampleQualityLevelFlags.None
        };

        _device->CheckFeatureSupport(
            Silk.NET.Direct3D12.Feature.MultisampleQualityLevels,
            &depthQualityLevels,
            (uint)sizeof(FeatureDataMultisampleQualityLevels)
        );

        if (depthQualityLevels.NumQualityLevels == 0)
        {
            // Fall back to no MSAA if the depth buffer format does not support it
            MsaaSampleCount = 1;
            MsaaQualityLevels = 1; // Keep >= 1 to avoid SampleDesc.Quality underflow
        }
    }

    /// <summary>
    /// Safely derives the quality value. MsaaQualityLevels may be 1, which means
    /// no MSAA, so subtracting 1 yields 0 without underflow.
    /// </summary>
    private uint SafeQuality => MsaaQualityLevels > 0 ? MsaaQualityLevels - 1 : 0;

    private void CreateMsaaRenderTarget(int width, int height)
    {
        // Follow the Scene target format (RGBA16F under the HDR chain); EndPass
        // resolves into SceneColor/backbuffer using the same format.
        var format = Device.SceneColorFormat;
        var desc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (uint)width,
            Height = (uint)height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDesc = new SampleDesc(MsaaSampleCount, SafeQuality),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowRenderTarget
        };

        var heapProps = new HeapProperties(HeapType.Default);
        // Keep the optimized clear value aligned with BeginPass clear behavior
        // (clear colors are linearized under the HDR chain)
        var cc = Device.HdrSceneColor ? Device.LinearizeClearColor(_clearColor) : _clearColor;
        var clearValue = new ClearValue { Format = format };
        clearValue.Anonymous.Color[0] = cc.X;
        clearValue.Anonymous.Color[1] = cc.Y;
        clearValue.Anonymous.Color[2] = cc.Z;
        clearValue.Anonymous.Color[3] = cc.W;

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var result = _device->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &desc,
            ResourceStates.RenderTarget,
            &clearValue,
            &iid,
            (void**)&resource
        );

        if (result != 0)
        {
            // Creation failed, fall back to no MSAA
            MsaaSampleCount = 1;
            MsaaQualityLevels = 0;
        }
        else
        {
            MsaaRenderTarget = resource;
        }
    }

    private void CreateDepthStencil(int width, int height)
    {
        uint depthSampleCount = MsaaSampleCount > 1 ? MsaaSampleCount : 1;
        uint depthSampleQuality = (MsaaSampleCount > 1) ? SafeQuality : 0;

        var heapProperties = new HeapProperties(HeapType.Default);
        var resourceDesc = new ResourceDesc(
            ResourceDimension.Texture2D,
            0ul,
            (ulong)width,
            (uint)height,
            1,
            1,
            DepthBufferFormat,
            new SampleDesc(depthSampleCount, depthSampleQuality),
            TextureLayout.LayoutUnknown,
            ResourceFlags.AllowDepthStencil
        );

        var clearValue = new ClearValue
        {
            Format = DepthBufferFormat,
            Anonymous = new ClearValueUnion
            {
                DepthStencil = new DepthStencilValue(1.0f, 0)
            }
        };

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var result = _device->CreateCommittedResource(
            &heapProperties,
            HeapFlags.None,
            &resourceDesc,
            ResourceStates.DepthWrite,
            &clearValue,
            &iid,
            (void**)&resource
        );

        if (result != 0)
        {
            throw new Exception($"Failed to create depth stencil: {result}");
        }

        DepthStencil = resource;
    }

    private void UpdateViewportAndScissor(int width, int height)
    {
        Viewport = new Viewport
        {
            TopLeftX = 0,
            TopLeftY = 0,
            Width = width,
            Height = height,
            MinDepth = 0,
            MaxDepth = 1
        };

        ScissorRect = new Box2D<int>(Vector2D<int>.Zero, new Vector2D<int>(width, height));
    }

    private void ReleaseMsaaAndDepth()
    {
        if (MsaaRenderTarget != null)
        {
            MsaaRenderTarget->Release();
            MsaaRenderTarget = null;
        }

        if (DepthStencil != null)
        {
            DepthStencil->Release();
            DepthStencil = null;
        }
    }

    public void Dispose()
    {
        ReleaseMsaaAndDepth();
    }
}
