// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Pipeline modes:
/// - Opaque:      Opaque materials (blending disabled, DepthWrite=All, DepthFunc=Less).
/// - Transparent: True BLEND materials (alpha blending, DepthWrite=Zero, DepthFunc=LessEqual),
///                avoiding self-occlusion between translucent surfaces using the same material.
/// - Fade:        Whole-model fade in/out (alpha blending, DepthWrite=All, DepthFunc=Less).
///                Used to display an originally opaque mesh with uniform translucency via Model.Alpha,
///                while still writing depth to block back faces and inner meshes from blending again,
///                avoiding excessive transparency or inner-geometry bleed such as 1-(1-a)^N on complex models.
/// </summary>
internal enum PipelineMode
{
    Opaque,
    Transparent,
    Fade,
}

internal enum PipelineCullVariant
{
    Back,
    None,
    Front,
}

internal static unsafe class Pipeline
{
    internal static ID3D12RootSignature* RootSignature;

    internal static ID3D12PipelineState* OpaquePipelineState;
    internal static ID3D12PipelineState* OpaqueDoubleSidedPipelineState;

    internal static ID3D12PipelineState* TransparentPipelineState;
    internal static ID3D12PipelineState* TransparentDoubleSidedPipelineState;
    internal static ID3D12PipelineState* TransparentBackFacePipelineState;

    internal static ID3D12PipelineState* FadePipelineState;
    internal static ID3D12PipelineState* FadeDoubleSidedPipelineState;

    // Overlay-pass-specific variants (no depth, single-sample, backbuffer format): Overlay renders
    // directly to the backbuffer and the OM has no DSV, so the main PSO baked for
    // SceneColorFormat/MSAA/DSV is invalid under the Overlay OM and triggers a device error at draw time.
    internal static ID3D12PipelineState* OpaqueOverlayPipelineState;
    internal static ID3D12PipelineState* OpaqueDoubleSidedOverlayPipelineState;
    internal static ID3D12PipelineState* TransparentOverlayPipelineState;
    internal static ID3D12PipelineState* TransparentDoubleSidedOverlayPipelineState;
    internal static ID3D12PipelineState* FadeOverlayPipelineState;
    internal static ID3D12PipelineState* FadeDoubleSidedOverlayPipelineState;

    /// <summary>2-2 contract clause 7: AoExempt NoDepth variants (Opaque/Fade only zero the depth-write mask,
    /// with all other states kept byte-for-byte identical; when Mesh3D.ExcludeFromAo=true, the draw bucket
    /// selects them via PrimitiveData.AoExempt).</summary>
    internal static ID3D12PipelineState* OpaqueNoDepthPipelineState;
    internal static ID3D12PipelineState* OpaqueNoDepthDoubleSidedPipelineState;
    internal static ID3D12PipelineState* FadeNoDepthPipelineState;
    internal static ID3D12PipelineState* FadeNoDepthDoubleSidedPipelineState;

    /// <summary>Outline2D mask pass specific PSO: outputs a pure white mask, reads depth, and does not write depth.</summary>
    internal static ID3D12PipelineState* OutlineMaskPipelineState;
    internal static ID3D12PipelineState* OutlineMaskDoubleSidedPipelineState;

    /// <summary>1-5 shadow PSO (depth-only, CullNone, bias baked in; created when ShadowsEnabled is on).</summary>
    internal static ID3D12PipelineState* ShadowPipelineState;

    /// <summary>Identity instance buffer used as the slot 1 placeholder for regular draws (64B, permanently reused;
    /// also referenced by the sprite quad path to avoid debug layer #202 spam for missing VB bindings).</summary>
    static ID3D12Resource* IdentityInstanceBuffer;
    internal static VertexBufferView IdentityInstanceBufferView;

    // Phase 3: default Morph Target buffer (1 vertex x 9 floats, all zero, used for static-model binding)
    static ID3D12Resource* DefaultMorphDeltasBuffer;
    static GpuDescriptorHandle DefaultMorphDeltasSrvHandle;
    static int DefaultMorphDescriptorId = -1;

    // Phase 4: default per-instance bone buffer (one identity matrix, used as the non-instanced draw placeholder)
    static ID3D12Resource* DefaultInstanceBoneBuffer;
    internal static GpuDescriptorHandle DefaultInstanceBoneSrvHandle;
    static int DefaultInstanceBoneDescriptorId = -1;

    // 2-3 Step C: default zero-valued buffers for previous-frame data (sentinel semantics:
    // matrix _m33==0 / all-zero weights => the shader falls back to current-frame data).
    // The three prev SB slots (t8/t9/t10) all bind their corresponding default buffers when the caller
    // does not provide valid handles.
    static ID3D12Resource* DefaultPrevBoneBuffer;
    internal static GpuDescriptorHandle DefaultPrevBoneSrvHandle;
    static int DefaultPrevBoneDescriptorId = -1;

    static ID3D12Resource* DefaultPrevInstanceWorldBuffer;
    internal static GpuDescriptorHandle DefaultPrevInstanceWorldSrvHandle;
    static int DefaultPrevInstanceWorldDescriptorId = -1;

    static ID3D12Resource* DefaultPrevMorphWeightsBuffer;
    internal static GpuDescriptorHandle DefaultPrevMorphWeightsSrvHandle;
    static int DefaultPrevMorphWeightsDescriptorId = -1;

    // -- Shared resources for Text GPU instancing --
    // All Texts controls share one unit-quad VB + IB (UV 0..1, position +-0.5)
    internal static ID3D12Resource* UnitQuadVertexBuffer;
    internal static VertexBufferView UnitQuadVertexBufferView;
    internal static ID3D12Resource* UnitQuadIndexBuffer;
    internal static IndexBufferView UnitQuadIndexBufferView;

    // Default TextGlyphData buffer (one empty instance, used as the t5 placeholder for non-text draws)
    static ID3D12Resource* DefaultTextInstancesBuffer;
    internal static GpuDescriptorHandle DefaultTextInstancesSrvHandle;
    static int DefaultTextInstancesDescriptorId = -1;

    static ID3D12Resource* DefaultTextMaterialBuffer;
    internal static ulong DefaultTextMaterialGpuAddress;

    static ID3D12Resource* DefaultTextDrawParamsBuffer;
    internal static ulong DefaultTextDrawParamsGpuAddress;

    // 2-4 clause 10: the current-frame DDGI irradiance atlas (compute 2D texture). SetLighting resolves it
    // once per frame (mirroring DXTextureCube.Active), and DrawPrimitive consumes it. Falls back to
    // Device.White when null. Always null when DDGI is disabled or not ready.
    internal static DXTexture DdgiAtlasActive;

    // 2-4 Step 3: the current-frame DDGI depth-moment atlas (compute 2D rg16float texture). Same pattern as
    // DdgiAtlasActive: resolved once per frame in SetLighting, consumed by DrawPrimitive, and falling back
    // to Device.White when null. Always null when DDGI is disabled or not ready.
    internal static DXTexture DdgiDepthActive;

    // 2-5 Step C: the current-frame pre-baked cloud noise (compute 2D rgba8unorm texture). Follows the
    // same pattern as DdgiAtlasActive: resolved once per frame in SetLighting, consumed by DrawPrimitive,
    // and falling back to Device.White when null.
    // Note that the all-white fallback is itself a dangerous "max density" value, so the consumer side
    // also gates on cloudParams0.w (layer count). SkyLighting.Apply writes a non-zero layer count only when
    // FrameSchedule.CloudNoiseTexture is not null.
    internal static DXTexture CloudNoiseActive;

    // 2-5 Step E: the current-frame aerial perspective froxel volume (compute **3D** rgba16float texture).
    // Same pattern as above, except resolution goes through the DXTexture3D registry
    // (3D and 2D use separate dictionaries; see 1-8). Falls back to a 1x1x1 all-zero dummy when null.
    // Unlike the cloud-noise case, this fallback is an identity element rather than a dangerous value, so
    // the apParams0.x gate only exists to save one sample.
    internal static DXTexture3D AerialLutActive;

    public static void Init()
    {
        RootSignature = CreateRootSignature();

        OpaquePipelineState = CreatePipelineState(PipelineMode.Opaque);
        OpaqueDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, PipelineCullVariant.None);

        TransparentPipelineState = CreatePipelineState(PipelineMode.Transparent);
        TransparentDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Transparent, PipelineCullVariant.None);
        TransparentBackFacePipelineState = CreatePipelineState(PipelineMode.Transparent, PipelineCullVariant.Front);

        FadePipelineState = CreatePipelineState(PipelineMode.Fade);
        FadeDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Fade, PipelineCullVariant.None);

        // Overlay-specific variants (overlay: true): no depth / single-sample / backbuffer format,
        // eagerly baked in the same style as the three main modes
        OpaqueOverlayPipelineState = CreatePipelineState(PipelineMode.Opaque, overlay: true);
        OpaqueDoubleSidedOverlayPipelineState = CreatePipelineState(PipelineMode.Opaque, PipelineCullVariant.None, overlay: true);
        TransparentOverlayPipelineState = CreatePipelineState(PipelineMode.Transparent, overlay: true);
        TransparentDoubleSidedOverlayPipelineState = CreatePipelineState(PipelineMode.Transparent, PipelineCullVariant.None, overlay: true);
        FadeOverlayPipelineState = CreatePipelineState(PipelineMode.Fade, overlay: true);
        FadeDoubleSidedOverlayPipelineState = CreatePipelineState(PipelineMode.Fade, PipelineCullVariant.None, overlay: true);

        // 2-2 contract clause 7: AoExempt NoDepth variants (eagerly baked in the same style as the
        // three main modes; only the depth-write mask differs, and the shader bytecode is byte-for-byte
        // identical to the corresponding regular variants).
        OpaqueNoDepthPipelineState = CreatePipelineState(PipelineMode.Opaque, depthWrite: false);
        OpaqueNoDepthDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, PipelineCullVariant.None, depthWrite: false);
        FadeNoDepthPipelineState = CreatePipelineState(PipelineMode.Fade, depthWrite: false);
        FadeNoDepthDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Fade, PipelineCullVariant.None, depthWrite: false);
        OutlineMaskPipelineState = CreatePipelineState(PipelineMode.Opaque, depthWrite: false, outlineMask: true);
        OutlineMaskDoubleSidedPipelineState = CreatePipelineState(PipelineMode.Opaque, PipelineCullVariant.None, depthWrite: false, outlineMask: true);

        // 1-5 shadow PSO: decided at initialization from the tiered ShadowsEnabled setting
        // (contract clause 4), and not created when disabled
        if (RenderQuality.Current.ShadowsEnabled)
            ShadowPipelineState = CreatePipelineState(PipelineMode.Opaque, PipelineCullVariant.None, shadowPass: true);

        // Create the identity instance buffer: used as the slot 1 placeholder during regular draws,
        // providing an identity world matrix (4x float4 rows) plus zero morph weights (64 bytes)
        var identityData = new InstanceTransformData[]
        {
            new InstanceTransformData
            {
                Row0 = new Vector4(1, 0, 0, 0),
                Row1 = new Vector4(0, 1, 0, 0),
                Row2 = new Vector4(0, 0, 1, 0),
                Row3 = new Vector4(0, 0, 0, 1),
                MorphWeights = Vector4.Zero,
            }
        };
        IdentityInstanceBuffer = Device.CreateVertexBuffer(identityData, out IdentityInstanceBufferView);

        // Create the default morph delta buffer (1 vertex x 9 floats, all zero),
        // used as the slot 9 (t5) placeholder for static models without morph targets.
        var defaultMorphData = new float[9];
        DefaultMorphDescriptorId = Device.DescriptorAllocator.Allocate();
        DefaultMorphDeltasBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, (ulong)(defaultMorphData.Length * sizeof(float)),
            ResourceStates.GenericRead);
        fixed (float* pSrc = defaultMorphData)
        {
            void* pDst;
            DefaultMorphDeltasBuffer->Map(0, null, &pDst);
            Unsafe.CopyBlock(pDst, pSrc, (uint)(defaultMorphData.Length * sizeof(float)));
            DefaultMorphDeltasBuffer->Unmap(0, null);
        }
        var defaultMorphSrv = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)defaultMorphData.Length,
                StructureByteStride = (uint)sizeof(float),
                Flags = BufferSrvFlags.None
            }
        };
        var defaultMorphCpuHandle = Device.SrvHeapManager.GetCpuHandle(DefaultMorphDescriptorId);
        Device.D3dDevice->CreateShaderResourceView(DefaultMorphDeltasBuffer, &defaultMorphSrv, defaultMorphCpuHandle);
        DefaultMorphDeltasSrvHandle = Device.SrvHeapManager.GetGpuHandle(DefaultMorphDescriptorId);

        // Create the default per-instance bone StructuredBuffer (one identity float4x4),
        // used as the slot 10 (t6) placeholder for non-instanced draws.
        var defaultBoneData = new Matrix4x4[1] { Matrix4x4.Identity };
        DefaultInstanceBoneDescriptorId = Device.DescriptorAllocator.Allocate();
        DefaultInstanceBoneBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, (ulong)(defaultBoneData.Length * sizeof(Matrix4x4)),
            ResourceStates.GenericRead);
        fixed (Matrix4x4* pSrc = defaultBoneData)
        {
            void* pDst;
            DefaultInstanceBoneBuffer->Map(0, null, &pDst);
            Unsafe.CopyBlock(pDst, pSrc, (uint)(defaultBoneData.Length * sizeof(Matrix4x4)));
            DefaultInstanceBoneBuffer->Unmap(0, null);
        }
        var defaultBoneSrv = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = 1,
                StructureByteStride = (uint)sizeof(Matrix4x4),
                Flags = BufferSrvFlags.None
            }
        };
        var defaultBoneCpuHandle = Device.SrvHeapManager.GetCpuHandle(DefaultInstanceBoneDescriptorId);
        Device.D3dDevice->CreateShaderResourceView(DefaultInstanceBoneBuffer, &defaultBoneSrv, defaultBoneCpuHandle);
        DefaultInstanceBoneSrvHandle = Device.SrvHeapManager.GetGpuHandle(DefaultInstanceBoneDescriptorId);

        // 2-3 Step C: create the three default zero-valued prev buffers
        // (each with one entry, all zero; sentinel semantics).
        // t8: prev bone (one all-zero float4x4; shader-side _m33==0 triggers fallback)
        {
            var zeroBone = new Matrix4x4[1] { default };
            DefaultPrevBoneDescriptorId = Device.DescriptorAllocator.Allocate();
            DefaultPrevBoneBuffer = Device.ResourceManager.CreateBuffer(
                HeapType.Upload, (ulong)(zeroBone.Length * sizeof(Matrix4x4)),
                ResourceStates.GenericRead);
            fixed (Matrix4x4* pSrc = zeroBone)
            {
                void* pDst;
                DefaultPrevBoneBuffer->Map(0, null, &pDst);
                Unsafe.CopyBlock(pDst, pSrc, (uint)(zeroBone.Length * sizeof(Matrix4x4)));
                DefaultPrevBoneBuffer->Unmap(0, null);
            }
            var prevBoneSrv = new ShaderResourceViewDesc
            {
                Format = Silk.NET.DXGI.Format.FormatUnknown,
                ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
                Shader4ComponentMapping = 0x00001688u,
                Buffer = new BufferSrv { FirstElement = 0, NumElements = 1, StructureByteStride = (uint)sizeof(Matrix4x4), Flags = BufferSrvFlags.None }
            };
            var prevBoneCpu = Device.SrvHeapManager.GetCpuHandle(DefaultPrevBoneDescriptorId);
            Device.D3dDevice->CreateShaderResourceView(DefaultPrevBoneBuffer, &prevBoneSrv, prevBoneCpu);
            DefaultPrevBoneSrvHandle = Device.SrvHeapManager.GetGpuHandle(DefaultPrevBoneDescriptorId);
        }
        // t9: prev instanceWorld (one all-zero float4x4)
        {
            var zeroWorld = new Matrix4x4[1] { default };
            DefaultPrevInstanceWorldDescriptorId = Device.DescriptorAllocator.Allocate();
            DefaultPrevInstanceWorldBuffer = Device.ResourceManager.CreateBuffer(
                HeapType.Upload, (ulong)(zeroWorld.Length * sizeof(Matrix4x4)),
                ResourceStates.GenericRead);
            fixed (Matrix4x4* pSrc = zeroWorld)
            {
                void* pDst;
                DefaultPrevInstanceWorldBuffer->Map(0, null, &pDst);
                Unsafe.CopyBlock(pDst, pSrc, (uint)(zeroWorld.Length * sizeof(Matrix4x4)));
                DefaultPrevInstanceWorldBuffer->Unmap(0, null);
            }
            var prevWorldSrv = new ShaderResourceViewDesc
            {
                Format = Silk.NET.DXGI.Format.FormatUnknown,
                ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
                Shader4ComponentMapping = 0x00001688u,
                Buffer = new BufferSrv { FirstElement = 0, NumElements = 1, StructureByteStride = (uint)sizeof(Matrix4x4), Flags = BufferSrvFlags.None }
            };
            var prevWorldCpu = Device.SrvHeapManager.GetCpuHandle(DefaultPrevInstanceWorldDescriptorId);
            Device.D3dDevice->CreateShaderResourceView(DefaultPrevInstanceWorldBuffer, &prevWorldSrv, prevWorldCpu);
            DefaultPrevInstanceWorldSrvHandle = Device.SrvHeapManager.GetGpuHandle(DefaultPrevInstanceWorldDescriptorId);
        }
        // t10: prev morphWeights (one all-zero float4)
        {
            var zeroWeights = new Vector4[1] { Vector4.Zero };
            DefaultPrevMorphWeightsDescriptorId = Device.DescriptorAllocator.Allocate();
            DefaultPrevMorphWeightsBuffer = Device.ResourceManager.CreateBuffer(
                HeapType.Upload, (ulong)(zeroWeights.Length * sizeof(Vector4)),
                ResourceStates.GenericRead);
            fixed (Vector4* pSrc = zeroWeights)
            {
                void* pDst;
                DefaultPrevMorphWeightsBuffer->Map(0, null, &pDst);
                Unsafe.CopyBlock(pDst, pSrc, (uint)(zeroWeights.Length * sizeof(Vector4)));
                DefaultPrevMorphWeightsBuffer->Unmap(0, null);
            }
            var prevMorphSrv = new ShaderResourceViewDesc
            {
                Format = Silk.NET.DXGI.Format.FormatUnknown,
                ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
                Shader4ComponentMapping = 0x00001688u,
                Buffer = new BufferSrv { FirstElement = 0, NumElements = 1, StructureByteStride = (uint)sizeof(Vector4), Flags = BufferSrvFlags.None }
            };
            var prevMorphCpu = Device.SrvHeapManager.GetCpuHandle(DefaultPrevMorphWeightsDescriptorId);
            Device.D3dDevice->CreateShaderResourceView(DefaultPrevMorphWeightsBuffer, &prevMorphSrv, prevMorphCpu);
            DefaultPrevMorphWeightsSrvHandle = Device.SrvHeapManager.GetGpuHandle(DefaultPrevMorphWeightsDescriptorId);
        }

        // -- Text GPU instancing: create the shared unit-quad VB + IB --
        //   VB: 4 vertices forming a unit square (position +-0.5, UV 0..1)
        //   IB: 6 indices forming 2 triangles [0,1,2, 1,3,2]
        var quadVertices = new Vertex[]
        {
            new Vertex { Position = new Vector3(-0.5f, -0.5f, 0), TexCoord = new Vector2(0, 1), Normal = Vector3.UnitZ, Tangent = new Vector4(1,0,0,1), Joints = Vector4.Zero, Weights = Vector4.Zero },
            new Vertex { Position = new Vector3( 0.5f, -0.5f, 0), TexCoord = new Vector2(1, 1), Normal = Vector3.UnitZ, Tangent = new Vector4(1,0,0,1), Joints = Vector4.Zero, Weights = Vector4.Zero },
            new Vertex { Position = new Vector3(-0.5f,  0.5f, 0), TexCoord = new Vector2(0, 0), Normal = Vector3.UnitZ, Tangent = new Vector4(1,0,0,1), Joints = Vector4.Zero, Weights = Vector4.Zero },
            new Vertex { Position = new Vector3( 0.5f,  0.5f, 0), TexCoord = new Vector2(1, 0), Normal = Vector3.UnitZ, Tangent = new Vector4(1,0,0,1), Joints = Vector4.Zero, Weights = Vector4.Zero },
        };
        var quadIndices = new uint[] { 0, 1, 2, 1, 3, 2 };
        UnitQuadVertexBuffer = Device.CreateVertexBuffer(quadVertices, out UnitQuadVertexBufferView);
        UnitQuadIndexBuffer = Device.ResourceManager.CreateIndexBuffer(quadIndices, out UnitQuadIndexBufferView);

        // -- Default TextGlyphData buffer (one empty glyph, used as the t5 placeholder for non-text draws) --
        DefaultTextInstancesDescriptorId = Device.DescriptorAllocator.Allocate();
        DefaultTextInstancesBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, (ulong)Unsafe.SizeOf<TextGlyphData>(),
            ResourceStates.GenericRead);
        {
            void* pDst;
            DefaultTextInstancesBuffer->Map(0, null, &pDst);
            Unsafe.InitBlock(pDst, 0, (uint)Unsafe.SizeOf<TextGlyphData>());
            DefaultTextInstancesBuffer->Unmap(0, null);
        }
        var defaultTiSrv = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = 12,
                StructureByteStride = (uint)sizeof(float),
                Flags = BufferSrvFlags.None
            }
        };
        var defaultTiCpuHandle = Device.SrvHeapManager.GetCpuHandle(DefaultTextInstancesDescriptorId);
        Device.D3dDevice->CreateShaderResourceView(DefaultTextInstancesBuffer, &defaultTiSrv, defaultTiCpuHandle);
        DefaultTextInstancesSrvHandle = Device.SrvHeapManager.GetGpuHandle(DefaultTextInstancesDescriptorId);

        DefaultTextMaterialBuffer = Device.ResourceManager.CreateConstantBuffer(
            (uint)Unsafe.SizeOf<MaterialParams>(), out var defaultTextMaterialPtr);
        var defaultTextMaterial = new MaterialParams
        {
            BaseColor = new Vector4(1, 1, 1, 1),
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = 1,
            RenderMode = 2,
            IsInstanced = 1,
        };
        Unsafe.Write(defaultTextMaterialPtr, defaultTextMaterial);
        DefaultTextMaterialGpuAddress = DefaultTextMaterialBuffer->GetGPUVirtualAddress();

        DefaultTextDrawParamsBuffer = Device.ResourceManager.CreateConstantBuffer(
            (uint)Unsafe.SizeOf<TextDrawParams>(), out var defaultTextDrawParamsPtr);
        var defaultTextDrawParams = new TextDrawParams
        {
            PxRange = Season.Fonts.Font.PixelRange,
            AtlasSize = Vector2.One,
            GlobalAlpha = 1f,
            TextColor = Vector4.One,
        };
        Unsafe.Write(defaultTextDrawParamsPtr, defaultTextDrawParams);
        DefaultTextDrawParamsGpuAddress = DefaultTextDrawParamsBuffer->GetGPUVirtualAddress();

        // FinalBlit pipeline (Step 2): independent RootSig + PSO, initialized once together with the main pipeline
        BlitPipeline.Init();

        // 1-7: prebuild the 1x1 all-black dummy cube (the t11 fallback). Build it during initialization to
        // avoid allocating descriptors or creating resources mid-frame on the render thread. DrawPrimitive
        // only consumes it and never triggers lazy creation again.
        _ = DXTextureCube.DummyBlack;
    }

    static unsafe ID3D12RootSignature* CreateRootSignature()
    {
        using ComPtr<ID3D10Blob> signature = null;
        using ComPtr<ID3D10Blob> error = null;

        // Root parameters: 23 total (five CBVs b0-b4 + 16 SRV tables t0-t7 + t8/t9/t10
        // + 1-7 envCube t11 + 2-4 ddgiAtlas t12 + 2-4 ddgiDepth t13 + 2-5 cloudNoise t14
        // + 2-5 apLut t15 + 1-5 shadow root constants b5 + Outline2D mask color root constants b6)
        var rootParameters = new RootParameter[23];

        // Constant buffer (b0)
        rootParameters[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            Descriptor = new RootDescriptor { ShaderRegister = 0 },
            ShaderVisibility = ShaderVisibility.Vertex
        };

        rootParameters[1] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            Descriptor = new RootDescriptor { ShaderRegister = 1 }, // b1
            ShaderVisibility = ShaderVisibility.Pixel
        };

        rootParameters[2] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            Descriptor = new RootDescriptor { ShaderRegister = 2 }, // b2
            ShaderVisibility = ShaderVisibility.All // VS also reads isInstanced
        };

        // Create descriptor ranges for the 16 SRVs covering textures / morph / instance bone / shadow atlas /
        // prev bone / prev instance world / prev morph weights / env cube / DdgiAtlas / DdgiDepth /
        // CloudNoise / AerialLut (from t0 to t15)
        var descriptorRanges = new DescriptorRange[16];
        for (int i = 0; i < 5; i++)
        {
            descriptorRanges[i] = new DescriptorRange
            {
                RangeType = DescriptorRangeType.Srv,
                NumDescriptors = 1,
                BaseShaderRegister = (uint)i, // t0, t1, t2, t3, t4
                RegisterSpace = 0
            };
        }
        // Morph Target delta buffer SRV (t5)
        descriptorRanges[5] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 5, // t5
            RegisterSpace = 0
        };
        // Per-Instance Bone StructuredBuffer SRV (t6)
        descriptorRanges[6] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 6, // t6
            RegisterSpace = 0
        };
        // 1-5: Shadow atlas SRV (t7)
        descriptorRanges[7] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 7, // t7
            RegisterSpace = 0
        };
        // 2-3 Step C: previous-frame data SBs (t8/t9/t10)
        descriptorRanges[8] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 8, // t8 prev bone
            RegisterSpace = 0
        };
        descriptorRanges[9] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 9, // t9 prev instanceWorld
            RegisterSpace = 0
        };
        descriptorRanges[10] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 10, // t10 prev morphWeights
            RegisterSpace = 0
        };
        // 1-7: environment radiance cube SRV (t11), sampled by the specular term in the main-pass PS
        descriptorRanges[11] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 11, // t11 envCube
            RegisterSpace = 0
        };
        // 2-4 clause 10: DDGI irradiance probe atlas SRV (t12), sampled by the main-pass PS for diffuse indirect light
        descriptorRanges[12] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 12, // t12 ddgiAtlas
            RegisterSpace = 0
        };
        // 2-4 Step 3: DDGI depth-moment atlas SRV (t13), sampled by the main-pass PS for Chebyshev visibility
        descriptorRanges[13] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 13, // t13 ddgiDepth
            RegisterSpace = 0
        };
        // 2-5 Step C: pre-baked cloud-noise SRV (t14), shared by visible clouds in the sky branch and cloud shadows in the geometry PS
        descriptorRanges[14] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 14, // t14 cloudNoise
            RegisterSpace = 0
        };
        // 2-5 Step E: aerial-perspective 3D LUT SRV (t15), used for atmospheric in-scattering accumulation in the PBR path
        // (**the only Texture3D slot**)
        descriptorRanges[15] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 15, // t15 apLut
            RegisterSpace = 0
        };

        // Pin the descriptorRanges array with a fixed block
        fixed (DescriptorRange* pDescriptorRanges = descriptorRanges)
        {
            // Create 5 descriptor tables (one table per texture)
            for (int i = 0; i < 5; i++)
            {
                rootParameters[i + 3] = new RootParameter
                {
                    ParameterType = RootParameterType.TypeDescriptorTable,
                    DescriptorTable = new RootDescriptorTable
                    {
                        NumDescriptorRanges = 1,
                        // Use pointer arithmetic to get the address of the current element
                        PDescriptorRanges = pDescriptorRanges + i
                    },
                    ShaderVisibility = ShaderVisibility.Pixel
                };
            }

            // Morph Target delta StructuredBuffer SRV (t5)
            rootParameters[9] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 5
                },
                ShaderVisibility = ShaderVisibility.Vertex
            };

            // Per-Instance Bone StructuredBuffer SRV (t6)
            rootParameters[10] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 6
                },
                ShaderVisibility = ShaderVisibility.Vertex
            };

            // 1-5: shadow atlas SRV (t7), comparison-sampled by the main-pass PS
            rootParameters[12] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 7
                },
                ShaderVisibility = ShaderVisibility.Pixel
            };

            // 2-3 Step C: previous-frame data SBs (t8/t9/t10), read by the VS to reconstruct prevClip
            rootParameters[14] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 8
                },
                ShaderVisibility = ShaderVisibility.Vertex
            };
            rootParameters[15] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 9
                },
                ShaderVisibility = ShaderVisibility.Vertex
            };
            rootParameters[16] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 10
                },
                ShaderVisibility = ShaderVisibility.Vertex
            };

            // 1-7: environment radiance cube (t11), sampled by the PS specular term. The shader statically
            // references envCube, so this table always needs a valid descriptor. When no environment map is
            // present, DrawPrimitive binds a 1x1 all-black dummy cube.
            rootParameters[17] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 11
                },
                ShaderVisibility = ShaderVisibility.Pixel
            };

            // 2-4 clause 10: DDGI irradiance probe atlas (t12), sampled by the PS for diffuse indirect light.
            // The shader statically references ddgiAtlas, so this table always needs a valid descriptor.
            // DrawPrimitive binds a 1x1 white dummy when it is not ready.
            rootParameters[18] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 12
                },
                ShaderVisibility = ShaderVisibility.Pixel
            };

            // 2-4 Step 3: DDGI depth-moment atlas (t13), sampled by the PS for Chebyshev visibility.
            // The shader statically references ddgiDepth, so this table always needs a valid descriptor.
            // DrawPrimitive binds a 1x1 white dummy when it is not ready.
            rootParameters[19] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 13
                },
                ShaderVisibility = ShaderVisibility.Pixel
            };

            // 2-5 Step C: pre-baked cloud noise (t14), shared by visible-cloud composition in the PS and
            // by cloud shadows. The shader statically references cloudNoise, so this table always needs a
            // valid descriptor. When not ready, DrawPrimitive binds a 1x1 white dummy. Because white noise
            // would be remapped into a full sheet of dead-gray fake overcast, the consumer side also gates
            // on cloudParams0.w (layer count), which SkyLighting.Apply decides from whether
            // FrameSchedule.CloudNoiseTexture is non-null (see RenderPass).
            rootParameters[21] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 14
                },
                ShaderVisibility = ShaderVisibility.Pixel
            };

            // 2-5 Step E: aerial-perspective 3D LUT (t15), used for atmospheric in-scattering accumulation
            // in the PBR path. The shader statically references apLut, so this table always needs a valid
            // descriptor. DrawPrimitive binds a 1x1x1 all-zero dummy volume when it is not ready.
            // Unlike the cases above, this all-zero fallback is an **identity element** rather than a dangerous
            // value (the a channel stores opacity), so the apParams0.x gate only saves a sample and is not
            // required for correctness (see DXTexture3D.DummyBlack). Trilinear filtering and three-axis Clamp
            // are already guaranteed by the static sampler s0, so no extra sampler is needed.
            rootParameters[22] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = pDescriptorRanges + 15
                },
                ShaderVisibility = ShaderVisibility.Pixel
            };
        }

        // Added: bone-matrix buffer
        rootParameters[8] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            Descriptor = new RootDescriptor { ShaderRegister = 3 }, // b3
            ShaderVisibility = ShaderVisibility.Vertex
        };

        rootParameters[11] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            Descriptor = new RootDescriptor { ShaderRegister = 4 }, // b4
            ShaderVisibility = ShaderVisibility.All
        };

        // 1-5: shadow-pass light-space ViewProj root constants
        // (b5, float4x4 = 16 DWORDs, written per quadrant, zero CB allocations)
        rootParameters[13] = new RootParameter
        {
            ParameterType = RootParameterType.Type32BitConstants,
            Constants = new RootConstants { ShaderRegister = 5, RegisterSpace = 0, Num32BitValues = 16 },
            ShaderVisibility = ShaderVisibility.Vertex
        };

        // Outline2D mask root constants (b6, float4x2 = 8 DWORDs, visible to VS+PS, written per group):
        // outlineMaskColor + outlineMaskBoneBase. boneBase is the bone-palette slot base for per-instance
        // mask draws. In D3D12, SV_InstanceID does not include StartInstanceLocation, so per-slot draws with
        // instanceCount=1 always read slot 0. The slot is therefore carried explicitly via root constants,
        // mirroring Metal's per-draw instanceBoneBufferOffset. Other passes reset boneBase=0 through
        // SetPipeline/SetShadowPipeline, with no side effects.
        rootParameters[20] = new RootParameter
        {
            ParameterType = RootParameterType.Type32BitConstants,
            Constants = new RootConstants { ShaderRegister = 6, RegisterSpace = 0, Num32BitValues = 8 },
            ShaderVisibility = ShaderVisibility.All
        };

        // Static samplers
        // AddressMode uses Clamp because all project texture UVs stay within [0,1] and do not need tiling.
        // Bilinear filtering fetches positions outside the source pixel at the edge; Wrap would jump to the
        // opposite side and cause cross-texture color bleeding (for example a 1-pixel bright seam across
        // skybox face boundaries). Clamp samples only the outermost pixel, matching Wrap for standalone
        // [0,1] textures while removing the seam.
        var staticSamplers = stackalloc StaticSamplerDesc[3];
        staticSamplers[0] = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ShaderRegister = 0, // s0
            ShaderVisibility = ShaderVisibility.Pixel
        };
        // 1-5 contract clause 5: hardware comparison sampler for shadows
        // (paired with SampleCmpLevelZero). Border=OpaqueWhite means samples outside the atlas quadrant
        // always pass the depth comparison (1.0 >= ref) and therefore produce no shadow, eliminating
        // dark leakage at the edges.
        staticSamplers[1] = new StaticSamplerDesc
        {
            Filter = Filter.ComparisonMinMagLinearMipPoint,
            AddressU = TextureAddressMode.Border,
            AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Border,
            ComparisonFunc = ComparisonFunc.LessEqual,
            BorderColor = StaticBorderColor.OpaqueWhite,
            ShaderRegister = 1, // s1
            ShaderVisibility = ShaderVisibility.Pixel
        };
        // 2-5 Step C: linear-wrap sampler dedicated to cloud noise (s2). This is the only sampling path
        // in the whole pipeline that needs Wrap. Cloud noise is strictly periodic by CloudNoiseBasePeriod
        // (integer lattice hashes are modulo-wrapped by the octave lattice count), so uv and uv+1 land on
        // the same lattice set and Wrap tiles seamlessly. Using s0 Clamp instead would stretch the outermost
        // column into a stripe once uv crosses 1, showing up as a stationary band in the sky as the wind moves.
        staticSamplers[2] = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ShaderRegister = 2, // s2
            ShaderVisibility = ShaderVisibility.Pixel
        };

        // Pin the rootParameters array with a fixed block
        fixed (RootParameter* pRootParameters = rootParameters)
        {
            // Fill the root-signature description
            var rootSignatureDesc = new RootSignatureDesc
            {
                Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
                NumParameters = (uint)rootParameters.Length,
                PParameters = pRootParameters,
                NumStaticSamplers = 3,
                PStaticSamplers = staticSamplers
            };

            var result0 = DirectX.Device.D3D12.SerializeRootSignature
                (
                    &rootSignatureDesc, D3DRootSignatureVersion.Version1, signature.GetAddressOf(),
                    error.GetAddressOf()
                );
            DirectX.Device.CheckResult(result0);
        }

        ID3D12RootSignature* rootSignature;

        var iid = ID3D12RootSignature.Guid;
        var result = DirectX.Device.D3dDevice->CreateRootSignature(nodeMask: 0, signature.Get().GetBufferPointer(), signature.Get().GetBufferSize(), &iid, (void**)&rootSignature);
        DirectX.Device.CheckResult(result);

        return rootSignature;
    }

    static unsafe ID3D12PipelineState* CreatePipelineState(PipelineMode mode, PipelineCullVariant cullVariant = PipelineCullVariant.Back, bool shadowPass = false, bool depthWrite = true, bool overlay = false, bool outlineMask = false)
    {
        var compileFlags = 0u;

#if DEBUG
        // Enable better shader debugging with the graphics debugging tools.
        compileFlags |= (1 << 0) | (1 << 2);
#endif

        // 2-3 contract clause 3: the only new compile-time switch. When VELOCITY_OUTPUT=1, the main PS
        // becomes MRT (SV_Target0=color, SV_Target1=velocity) and the VS adds prevClip reconstruction.
        // Shadow pass has no color targets, so it always uses 0.
        // This does not double the variant matrix: MotionVectors is fixed during initialization, so only
        // one shape is baked per process. Overlay always stays single-target: even when MotionVectors is on,
        // the overlay PSO does not bake velocity slot 1 because the Overlay OM binds only one RTV, and a
        // dual-target bake would make the OM/PSO combination invalid at draw time.
        var velocityOutput = !overlay && RenderQuality.Current.MotionVectors && !shadowPass;

        // HDR chain switch (1-4 Step A): injected at compile time, with zero runtime branch cost.
        // When HDR_CHAIN=1, gamma encoding moves to the FinalBlit tonemap variant and Scene output stays
        // in pre-encoding space. Overlay renders directly to the backbuffer and does not go through FinalBlit,
        // so HDR_CHAIN is forcibly baked as 0 there to match the Metal overlay library:
        // Sprite2D outputs gamma-encoded color and text skips inverse-ACES pre-distortion, producing display-space
        // output that is pixel-equivalent to the LDR baseline. The linear direct output / inverse-ACES path under
        // HDR_CHAIN=1 is specific to the Scene->FinalBlit pre-encoding semantics and cannot be reused.
        // 1-5 dual shadow switches (contract clause 3): SHADOW_ENABLED controls PCF sampling in the main PS
        // (following the selected quality tier), while SHADOW_PASS selects the depth-only VS variant
        // (reusing all deformation stages from the main VS plus light-space projection, with no PS).
        var hlsl = ((!overlay && DirectX.Device.HdrSceneColor) ? "#define HDR_CHAIN 1\n" : "#define HDR_CHAIN 0\n")
            + (RenderQuality.Current.ShadowsEnabled ? "#define SHADOW_ENABLED 1\n" : "#define SHADOW_ENABLED 0\n")
            + (shadowPass ? "#define SHADOW_PASS 1\n" : "#define SHADOW_PASS 0\n")
            + (velocityOutput ? "#define VELOCITY_OUTPUT 1\n" : "#define VELOCITY_OUTPUT 0\n")
            // Step 6: DDGI tiering now prioritizes Settings.RenderQuality (persistable; null falls back to the
            // static default source), using the same gate as DdgiEffect.Initialize so the main shader variants
            // and atlas resources are created in sync.
            + ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == GiMode.Ddgi ? "#define DDGI_ENABLED 1\n" : "#define DDGI_ENABLED 0\n")
            + @"cbuffer Matrices : register(b0)
{
    float4x4 world;
    float4x4 view;
    float4x4 projection;
    // 2-3 contract clause 6: history matrices (same transpose convention as world/view/projection).
    // _m33 == 0 means the field was never written (all zero), and the VS degrades based on that sentinel;
    // see the end of VSMain.
    float4x4 prevWorld;
    float4x4 prevViewProjection;
};

// Legacy bone-matrix CBV (kept for compatibility binding; both regular and instanced skinned paths now use t6 StructuredBuffer)
cbuffer BoneMatrices : register(b3)
{
    float4x4 boneMatrices[100]; // Up to 100 bones
};

// 1-2 lighting system: unified light structure (64B, byte-for-byte aligned with C# GpuLight;
// see RenderQuality section 1-2 for the contract)
struct GpuLight
{
    float4 posRange;        // xyz=world position (ignored for directional), w=attenuation radius range (<=0 falls back to pure 1/d^2)
    float4 colorIntensity;  // xyz=linear color, w=intensity
    float4 dirType;         // xyz=light direction (used by spot/directional), w=type (0=point, 1=spot, 2=directional)
    float4 spotParams;      // x=cosInner, y=cosOuter (precomputed on CPU), zw=reserved
};

cbuffer SceneLights : register(b1)
{
    float4 cameraPos;
    float4 ambientParams;   // xyz=ambient light color, w=intensity (replaces the old hardcoded 0.5)
    // x=lightCount, y=hdrExposure (C# side SceneLightParams.Params0.Y; SetLighting injects Device.HdrExposure every frame),
    // z=directionalIndex (index of the directional light in lights that casts CSM, -1 if none),
    // w=spotShadowIndex (index of the spotlight that casts the 2D shadow map, -1 if none)
    float4 params0;
    // Directional lights are already folded into this array (dirType.w=2); there is no separate sun field.
    // The unified lighting loop below dispatches by type.
    GpuLight lights[8];
    // 1-5 shadow fields (contract clause 1): matrices are stored verbatim in row-major order, and
    // row_major declarations + mul(v, M) keep the result consistent with CPU-side pos*M.
    row_major float4x4 cascadeViewProj[4];
    row_major float4x4 spotShadowViewProj;
    float4 cascadeSplits;   // Far view-space depth of each cascade (last valid component = max shadow distance)
    float4 shadowParams0;   // x=sunEnabled, y=cascadeCount, z=1/atlasSize, w=reserved; all zero = shadows fully disabled
    float4 shadowParams1;   // x=spotEnabled, y=shadowStrength, zw=reserved
    // 2-3 contract clause 6: xy=subpixel jitter for this frame (NDC units), z=1/screenWidth, w=1/screenHeight.
    // Injected once per frame by SetLighting (same convention as hdrExposure); all zero = no jitter.
    float4 velocityParams;
    // 1-7 contract clause 4:
    // x=specular intensity multiplier, y=ambient diffuse intensity multiplier,
    // z=diffuse switch (>0.5 uses irradianceSH9, otherwise uses ambientParams constant ambient - mutually exclusive, never additive),
    // w=specular switch (>0.5 enables envCube LOD0 specular). All zero = full fallback to the 1-2 constant ambient model.
    float4 envParams;
    // 1-7 contract clause 7: SH9 environment irradiance (xyz=RGB, w reserved). The CPU has already
    // pre-multiplied the convolution coefficient A_l, so this side only evaluates the 9-term linear
    // combination. Only valid when envParams.z > 0.5.
    float4 irradianceSH9[9];
    // 2-4 DDGI clause 10 (starting at offset 1104):
    // giParams0=probeGridMin.xyz/spacing,
    // giParams1=gridXYZ(as float)/GiIntensity, giParams2=normalBias/chebyshev/atlasReady/_.
    float4 giParams0;
    float4 giParams1;
    float4 giParams2;
    // 2-5 Step B (b11, starting at offset 1152): analytic sun/moon discs + star field.
    // skyParams0=sunDir.xyz/cos(solar angular radius),
    // skyParams1=sun disc radiance.xyz/star-field radiance (already includes twilight visibility),
    // skyParams2=moonDir.xyz/cos(lunar angular radius),
    // skyParams3=moon disc radiance.xyz/StarRotation,
    // skyParams4=celestial-pole axis.xyz (extended in Step C and already normalized) / observer radius in km
    // (used by cloud shell intersection).
    // All zero = non-procedural sky tier (the only consumer-side gate is skyParams0.w > 0); injected once
    // by SkyLighting.Apply.
    float4 skyParams0;
    float4 skyParams1;
    float4 skyParams2;
    float4 skyParams3;
    float4 skyParams4;
    // 2-5 Step C (starting at offset 1232): procedural clouds. The max layer count is hardcoded as 3 on
    // all backends, matching SkyState.MaxLayers.
    // cloudLayerA[i] = cloud-base height km / geometric thickness km / coverage / density (1/km)
    // cloudLayerB[i] = wind offset.xy (km, world XZ) / reciprocal tiling scale (1/km) / high-frequency erosion strength
    // cloudParams0   = cloud scattering color.xyz (already includes sun/moon lighting and transmittance) /
    //                  **layer count = the only global gate**
    // cloudParams1   = cloud-shadow strength / phase g / ambient bottom light / forward gain
    // When the noise texture is not ready, the CPU writes layer count = 0 (see FrameSchedule.CloudNoiseTexture),
    // so zero-residue semantics apply here.
    float4 cloudLayerA[3];
    float4 cloudLayerB[3];
    float4 cloudParams0;
    float4 cloudParams1;
    // 2-5 Step E (starting at offset 1360): consumer-side parameters for the aerial-perspective 3D LUT.
    // apParams0 = x max distance in km (**the only gate**; >0 means the LUT is ready) / y intensity multiplier / zw reserved
    // When the LUT has not been built or the tier is disabled, the CPU writes all zero
    // (see FrameSchedule.AerialLutTexture), so zero-residue semantics apply here as well.
    float4 apParams0;
};

#if SHADOW_PASS
// 1-5 shadow-pass light-space ViewProj (root constants b5, written per quadrant; passed verbatim in row-major order)
cbuffer ShadowPassParams : register(b5)
{
    row_major float4x4 lightViewProj;
};
#endif

cbuffer MaterialParams : register(b2)
{
    float4 materialColor;
    float4 emissiveFactor;
    float metallicFactor;
    float roughnessFactor;
    uint useAlbedoMap;
    uint useNormalMap;
    uint useMetallicRoughnessMap;
    uint useAoMap;
    uint useEmissiveMap;
    float alphaCutoff;
    uint alphaMode;
    uint renderMode;   // 0=Sprite2D, 1=Pbr3D, 2=TextMsdf, 3=ProceduralSky
    uint bonePaletteStride;
    uint isInstanced;  // 0=regular draw, 1=GPU instancing
    uint isSkinned;    // 0=static, 1=skinned
    uint hasMorphTargets;  // 0=none, 1=has morph-target delta data
    uint morphTargetCount; // Number of active morph targets
    uint morphVertexCount; // Total vertex count (used for stride calculation)
    float4 morphWeights;   // Up to 4 morph-target weights
    // 2-3 Step C: valid-data sentinels for previous-frame data (0=this prev SB path is not ready / not applicable,
    // so the shader falls back to current data). Default 0 means the first frame or a path without prev SB
    // naturally takes the degraded route, equivalent to behavior before Step C.
    uint hasPrevBones;          // 0/1, whether the prev bone SB is valid
    uint hasPrevInstanceWorld;  // 0/1, whether the prev instanceWorld SB is valid
    uint hasPrevMorph;          // 0/1, whether the prev morphWeights SB is valid
    uint _padMat;               // 16B alignment padding
};

// Outline2D mask root constants (b6, 8 DWORDs, written per group): outlineMaskColor provides the color
// for PSOutlineMask, while outlineMaskBoneBase.x is used by the VS for bone addressing. During per-instance
// mask draws, SV_InstanceID is always 0 because it does not include StartInstanceLocation, so the slot base
// is carried explicitly by this constant. Regular and shadow passes always reset it to 0.
cbuffer OutlineMaskParams : register(b6)
{
    float4 outlineMaskColor;
    float4 outlineMaskBoneBase;
};

cbuffer TextDrawParams : register(b4)
{
    float textPxRange;
    float2 textAtlasSize;
    float textGlobalAlpha;
    float4 textBaseColor;
};

struct TextGlyphData
{
    float4 uvRect;
    float4 color;
    float4 metrics;
};

// t5: Morph Target delta data (float), also reused as per-text glyph data for Text GPU instancing.
// The two uses are mutually exclusive: renderMode != 2 reads morph deltas, while
// renderMode == 2 && isInstanced interprets the buffer as TextGlyphData with 12 floats per entry.
StructuredBuffer<float> morphDeltas : register(t5);

TextGlyphData LoadTextGlyph(uint instanceID)
{
    uint tBase = instanceID * 12;
    TextGlyphData glyph;
    glyph.uvRect = float4(morphDeltas[tBase    ], morphDeltas[tBase + 1],
                          morphDeltas[tBase + 2], morphDeltas[tBase + 3]);
    glyph.color = float4(morphDeltas[tBase + 4], morphDeltas[tBase + 5],
                         morphDeltas[tBase + 6], morphDeltas[tBase + 7]);
    glyph.metrics = float4(morphDeltas[tBase + 8], morphDeltas[tBase + 9],
                           morphDeltas[tBase + 10], morphDeltas[tBase + 11]);
    return glyph;
}

// Per-instance bone-matrix StructuredBuffer (GPU instancing path)
// Layout: [(outlineMaskBoneBase.x + instanceID) * bonePaletteStride + jointIndex]
// Regular and shadow passes always use boneBase=0. OutlineMask carries the slot base through root constants
// for per-slot drawing.
StructuredBuffer<float4x4> g_InstanceBoneMatrices : register(t6);

// 2-3 Step C: previous-frame data SBs (same layout as t6, indexed by instanceID/jointIndex;
// non-instanced paths always treat instanceID as 0; sentinels are matrix _m33==0 / all-zero weights)
StructuredBuffer<float4x4> g_PrevInstanceBoneMatrices : register(t8);
StructuredBuffer<float4x4> g_PrevInstanceWorlds : register(t9);
StructuredBuffer<float4> g_PrevMorphWeights : register(t10);

struct VSInput
{
    float3 position : POSITION;
    float2 texCoord : TEXCOORD0;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float4 jointIndices : JOINTINDICES;  // Joint indices (x, y, z, w map to 4 bones)
    float4 weights : WEIGHTS;            // Weights (x, y, z, w map to 4 bones)
    float4 instanceWorld0 : INSTANCEWORLD0;  // Per-instance world matrix (row0)
    float4 instanceWorld1 : INSTANCEWORLD1;  // (row1)
    float4 instanceWorld2 : INSTANCEWORLD2;  // (row2)
    float4 instanceWorld3 : INSTANCEWORLD3;  // (row3)
    float4 instanceMorphWeights : INSTANCEWEIGHTS; // per-instance morph weights
};

struct PSInput
{
    float4 position : SV_POSITION;
    float3 worldPos : POSITION1;
    float2 texCoord : TEXCOORD0;
    float3 normal : NORMAL;
    float4 tangent : TEXCOORD1;
    float4 instanceColor : COLOR0;  // Per-instance text color (renderMode==2); (1,1,1,1) in non-text modes
    float viewDepth : TEXCOORD2;    // 1-5: view-space depth (used for cascade selection; always 0 in shadow pass)
#if VELOCITY_OUTPUT
    // 2-3: previous-frame clip-space position without jitter. w <= 0 means no history, so the PS outputs zero velocity.
    float4 prevClip : TEXCOORD3;
#endif
};

PSInput VSMain(VSInput input, uint vertexId : SV_VertexID, uint instanceID : SV_InstanceID)
{
    PSInput output;
    
    // Initial local position and normal
    // 2-3: restPosition is the rest-pose local position. The velocity path must reconstruct prev from it
    // as the starting point (see the VELOCITY_OUTPUT section).
    float4 restPosition = float4(input.position, 1.0);
    float4 localPosition = restPosition;
    float3 localNormal = input.normal;
    float3 localTangentXYZ = input.tangent.xyz;

    // Current-frame morph weights (reused when prev data is unavailable on the velocity path so prev == cur
    // and morphing produces no velocity)
    float4 curMorphW = isInstanced ? input.instanceMorphWeights : morphWeights;
    
    // Morph Target blending (guarded by hasMorphTargets)
    // Modifies the rest-pose mesh shape before skinning
    if (hasMorphTargets && morphTargetCount > 0)
    {
        float3 morphPosDelta = float3(0, 0, 0);
        float3 morphNormalDelta = float3(0, 0, 0);
        float3 morphTangentDelta = float3(0, 0, 0);
        
        for (uint t = 0; t < morphTargetCount && t < 4; t++)
        {
            // GPU instancing uses per-instance morph weights; regular draws use the shared CB morphWeights
            float w = curMorphW[t];
            if (abs(w) < 1e-6)
                continue;
            // Layout: [targetIndex * vertexCount + vertexId] * 9 floats (pos.xyz + normal.xyz + tangent.xyz)
            uint baseIdx = (t * morphVertexCount + vertexId) * 9;
            morphPosDelta += float3(morphDeltas[baseIdx    ], morphDeltas[baseIdx + 1], morphDeltas[baseIdx + 2]) * w;
            morphNormalDelta += float3(morphDeltas[baseIdx + 3], morphDeltas[baseIdx + 4], morphDeltas[baseIdx + 5]) * w;
            morphTangentDelta += float3(morphDeltas[baseIdx + 6], morphDeltas[baseIdx + 7], morphDeltas[baseIdx + 8]) * w;
        }
        
        localPosition.xyz += morphPosDelta;
        localNormal = normalize(localNormal + morphNormalDelta);
        localTangentXYZ = normalize(localTangentXYZ + morphTangentDelta);
    }
    
    // Skeletal skinning
    float totalWeight = input.weights.x + input.weights.y + input.weights.z + input.weights.w;
    if (totalWeight > 0.0)
    {
        float4 skinnedPosition = float4(0, 0, 0, 0);
        float3 skinnedNormal = float3(0, 0, 0);
        float3 skinnedTangent = float3(0, 0, 0);
        
        for (int i = 0; i < 4; i++)
        {
            float weight = input.weights[i];
            if (weight > 0.0)
            {
                int jointIndex = (int)input.jointIndices[i];
                
                // Both regular models and GPU-instanced draws read from the dynamic bone StructuredBuffer.
                if (isSkinned)
                {
                    int baseIdx = isInstanced ? ((int)outlineMaskBoneBase.x + (int)instanceID) * (int)bonePaletteStride + jointIndex : jointIndex;
                    float4x4 boneMatrix = g_InstanceBoneMatrices[baseIdx];
                    skinnedPosition += mul(localPosition, boneMatrix) * weight;
                    float3x3 boneMatrix3x3 = (float3x3)boneMatrix;
                    skinnedNormal += mul(localNormal, boneMatrix3x3) * weight;
                    skinnedTangent += mul(localTangentXYZ, boneMatrix3x3) * weight;
                }
                else
                {
                    skinnedPosition += mul(localPosition, boneMatrices[jointIndex]) * weight;
                    float3x3 boneMatrix3x3 = (float3x3)boneMatrices[jointIndex];
                    skinnedNormal += mul(localNormal, boneMatrix3x3) * weight;
                    skinnedTangent += mul(localTangentXYZ, boneMatrix3x3) * weight;
                }
            }
        }
        
        localPosition = skinnedPosition;
        localNormal = normalize(skinnedNormal);
        localTangentXYZ = normalize(skinnedTangent);
    }
    
    // World matrix: use the per-instance matrix when isInstanced=1, otherwise use b0 world.
    // Note that instanceWorld0-3 are already the four matrix rows (Row0-Row3). In HLSL's column-major
    // convention, float4x4(Row0,Row1,Row2,Row3) automatically produces the correct column vectors,
    // equivalent to Transpose(world) in b0, so no extra transpose is required.
    float4x4 worldMatrix;
    if (isInstanced)
    {
        worldMatrix = float4x4(
            input.instanceWorld0,
            input.instanceWorld1,
            input.instanceWorld2,
            input.instanceWorld3);
    }
    else
    {
        worldMatrix = world;
    }
    
    float4 worldPos = mul(localPosition, worldMatrix);
    output.worldPos = worldPos.xyz;
    
#if SHADOW_PASS
    // depth-only (contract clause 3): the deformation stages stay byte-for-byte identical to the main path;
    // only the projection changes to the light-space matrix.
    output.position = mul(worldPos, lightViewProj);
    output.viewDepth = 0.0;
#else
    float4 viewPos = mul(worldPos, view);
    output.position = mul(viewPos, projection);
    output.viewDepth = viewPos.z;
#endif

#if VELOCITY_OUTPUT
    // 2-3 contract clauses 6/8: the history-matrix-not-written sentinel is all zero
    // (C# default, and Transpose(all-zero) is still all-zero).
    //
    // Existence of prevViewProjection is tested against its 4th column
    // (_m03_m13_m23_m33 = C# M14..M44), not a single _m33:
    //   * all zero (not written)    -> (0,0,0,0)                         -> no history
    //   * perspective View*Projection -> (fwd.xyz, -dot(pos,fwd)), |fwd|==1 -> always non-zero
    //   * orthographic / identity   -> (0,0,0,1)                         -> always non-zero
    // Testing only _m33 is wrong: it equals -dot(cameraPos, fwd), which can legally be 0 when the camera
    // is at the world origin (or the position vector is perpendicular to the forward direction), silently
    // zeroing velocity for the whole screen. And identity with _m33==1 would not fall into the no-history branch.
    //
    // Step C completion: when prevViewProj is valid, reconstruct prev local position in the exact order
    // prev morph -> prev skinning -> prev world, strictly mirroring the current path
    // (morph -> skinning -> world). If any prev SB path is not ready (hasPrev*==0) or its sentinel triggers
    // (matrix _m33==0 / all-zero weights), fall back to current data, equivalent to the degraded behavior
    // before Step C where only camera motion contributes to velocity.
    float4 prevClip = float4(0, 0, 0, 0);
    [branch] if (any(prevViewProjection._m03_m13_m23_m33 != 0.0))
    {
        // 1) prev morph: must restart from the rest pose rather than accumulating on already deformed /
        // skinned localPosition.
        //    Previous-frame position = rest + sum(prevW * delta), exactly symmetric with the current path
        //    rest + sum(curW * delta). Starting from localPosition would produce
        //    rest + sum(curW*d) + sum(prevW*d) (double deformation) and then skin it again, which makes
        //    the velocity completely wrong.
        //    When prev is not ready (hasPrevMorph==0), reuse current weights so prev degenerates to current
        //    and morphing contributes no velocity.
        // morphDeltas shares the exact same layout as the current path:
        // [targetIdx * morphVertexCount + vertexId] * 9 floats (pos/normal/tangent).
        // Deltas are static geometric differences, so there is no separate previous-frame delta, only
        // different weights.
        float4 prevLocalPos = restPosition;
        if (hasMorphTargets != 0 && morphTargetCount > 0)
        {
            float4 prevW = hasPrevMorph != 0
                ? (isInstanced != 0 ? g_PrevMorphWeights[instanceID] : g_PrevMorphWeights[0])
                : curMorphW;
            for (uint t = 0; t < morphTargetCount && t < 4; t++)
            {
                float w = prevW[t];
                if (abs(w) < 1e-6)
                    continue;
                uint baseIdx = (t * morphVertexCount + vertexId) * 9;
                prevLocalPos.xyz += float3(morphDeltas[baseIdx    ], morphDeltas[baseIdx + 1], morphDeltas[baseIdx + 2]) * w;
            }
        }

        // 2) prev skinning: same order as current skinning, acting on the same unskinned rest+morph position,
        // but using g_PrevInstanceBoneMatrices instead.
        // Sentinel: prev bone _m33==0 means the entry was not written, so fall back to the current bone on a
        // per-joint basis.
        float totalWeight = input.weights.x + input.weights.y + input.weights.z + input.weights.w;
        if (totalWeight > 0.0 && isSkinned != 0)
        {
            float4 skinnedPos = float4(0, 0, 0, 0);
            for (int i = 0; i < 4; i++)
            {
                float w = input.weights[i];
                if (w <= 0.0)
                    continue;
                int j = (int)input.jointIndices[i];
                int idx = isInstanced != 0 ? (((int)outlineMaskBoneBase.x + (int)instanceID) * (int)bonePaletteStride + j) : j;
                float4x4 bm;
                if (hasPrevBones != 0)
                {
                    bm = g_PrevInstanceBoneMatrices[idx];
                    if (bm._m33 == 0.0)
                        bm = isInstanced != 0 ? g_InstanceBoneMatrices[idx] : boneMatrices[j];
                }
                else
                {
                    bm = isInstanced != 0 ? g_InstanceBoneMatrices[idx] : boneMatrices[j];
                }
                skinnedPos += mul(prevLocalPos, bm) * w;
            }
            prevLocalPos = skinnedPos;
        }

        // 3) prev world: instanced draws use the g_PrevInstanceWorlds SB, non-instanced draws use the
        // b0 prevWorld CB.
        // Sentinel: _m33==0 means not written, so fall back to the current worldMatrix
        // (same semantics as before Step C).
        float4x4 prevWorldMatrix;
        if (isInstanced != 0 && hasPrevInstanceWorld != 0)
        {
            prevWorldMatrix = g_PrevInstanceWorlds[instanceID];
            if (prevWorldMatrix._m33 == 0.0)
                prevWorldMatrix = worldMatrix;
        }
        else
        {
            prevWorldMatrix = (prevWorld._m33 != 0.0) ? prevWorld : worldMatrix;
        }

        prevClip = mul(mul(prevLocalPos, prevWorldMatrix), prevViewProjection);
    }
    output.prevClip = prevClip;
#endif

    // -- Text GPU instancing: remap the unit-quad UV to the atlas sub-rect --
    float4 textColor = float4(1, 1, 1, 1);
    if (renderMode == 2 && isInstanced)
    {
        TextGlyphData glyph = LoadTextGlyph(instanceID);
        float4 uvRect = glyph.uvRect;
        textColor = glyph.metrics.w > 0.5 ? glyph.color : textBaseColor;
        // Remap unit-quad UV (0..1) to the atlas sub-rectangle UV
        output.texCoord = uvRect.xy + input.texCoord * uvRect.zw;
    }
    else
    {
        output.texCoord = input.texCoord;
    }
    output.instanceColor = textColor;
    output.normal = normalize(mul(localNormal, (float3x3)worldMatrix));
    output.tangent = float4(normalize(mul(localTangentXYZ, (float3x3)worldMatrix)), input.tangent.w);

    return output;
}

// Texture-array declarations (using consecutive registers)
//Texture2D textures[5] : register(t0); // t0 to t4

Texture2D albedoMap : register(t0);
Texture2D normalMap : register(t1);
Texture2D metallicRoughnessMap : register(t2);
Texture2D aoMap : register(t3);
Texture2D emissiveMap : register(t4);

SamplerState linearSampler : register(s0);

// 1-7: environment radiance cube (t11, single mip). A 1x1 all-black dummy is bound when no environment
// map is available, so sampling is always valid here. envParams.w carries the enable switch
// (see EnvironmentSpecular).
TextureCube envCube : register(t11);

// 2-4 clause 10: DDGI irradiance probe atlas (t12, rgba16float, sampled with linearSampler).
// It is always declared because the root signature always contains this slot, and a 1x1 white dummy is
// bound when not ready. Actual sampling is gated by DDGI_ENABLED + giParams2.z(atlasReady) + giParams1.w.
Texture2D ddgiAtlas : register(t12);

// 2-4 Step 3: DDGI depth-moment atlas (t13, rg16float, .x=mean/.y=mean^2). Always declared because the
// root signature always contains this slot, with a 1x1 white dummy bound when not ready.
// Chebyshev visibility testing is runtime-gated by giParams2.y and is skipped when disabled.
Texture2D ddgiDepth : register(t13);

// 2-5 Step C: pre-baked cloud noise (t14, rgba8unorm: R=low-frequency contour FBM, G=Worley clumps,
// B=high-frequency erosion, A=very-low-frequency coverage modulation). Always declared because the root
// signature always contains this slot, with a 1x1 white dummy bound when not ready.
// Actual sampling is runtime-gated by cloudParams0.w (layer count): the all-white fallback cannot be treated
// as real noise because it would saturate density.
Texture2D cloudNoise : register(t14);

// 2-5 Step C: linear-**wrap** sampler dedicated to cloud noise (s2). This is the only sampling path in
// the whole pipeline that needs Wrap. The noise tiles on a fixed period (integer lattice hashes are modulo-
// wrapped by the octave lattice count), while wind offset can push uv outside [0,1].
// Using s0 Clamp instead would stretch the outermost column into a stationary stripe in the sky.
SamplerState wrapSampler : register(s2);

// 2-5 Step E: aerial-perspective froxel volume (t15, 32^3 rgba16float: rgb=accumulated in-scattered
// radiance from the camera to that distance in linear HDR, a=accumulated opacity). This is the only
// Texture3D slot in the entire pipeline. It is always declared because the root signature always contains
// this slot, with a 1x1x1 all-zero dummy bound when not ready. The a channel stores opacity rather than
// transmittance, so all zero is exactly the identity element of the compositing formula
// (see DXTexture3D.DummyBlack). The apParams0.x gate only skips one sample. Three-axis Clamp and trilinear
// filtering are both already provided by the static sampler s0, so no new sampler is needed.
Texture3D<float4> apLut : register(t15);

// 1-7 contract clause 7: evaluate SH9 environment irradiance. The coefficients are already pre-multiplied
// by the convolution coefficient A_l on the CPU, so this side only computes the 9-term basis-function
// linear combination. Returns the ambient diffuse irradiance along the normal direction.
float3 EvaluateIrradianceSH9(float3 n)
{
    float3 result = irradianceSH9[0].rgb;
    result += irradianceSH9[1].rgb * n.y;
    result += irradianceSH9[2].rgb * n.z;
    result += irradianceSH9[3].rgb * n.x;
    result += irradianceSH9[4].rgb * (n.x * n.y);
    result += irradianceSH9[5].rgb * (n.y * n.z);
    result += irradianceSH9[6].rgb * (3.0 * n.z * n.z - 1.0);
    result += irradianceSH9[7].rgb * (n.x * n.z);
    result += irradianceSH9[8].rgb * (n.x * n.x - n.y * n.y);
    return max(result, 0.0);
}

#if DDGI_ENABLED
// 2-4 clauses 9/10: sample probe irradiance. Octahedral decoding strictly mirrors the
// ddgiProbeUpdate OctDecode/tile layout
// (tile 8^2 = 6^2 core + 1px gutter; center-texel absolute pixel = tile*8 + 1 + oct*6, so normalized uv
// simply divides by atlas size).
// worldPos is offset along the normal by giParams2.x (normalBias), then the 8 neighboring probes are
// blended with trilinear weights multiplied by cosine-direction weights. linearSampler bilinearly samples
// the octahedral core of each probe, with the gutter absorbing seam spill. The result is scaled by GiIntensity.
// From Step 3 onward, when giParams2.y>0.5, each probe also runs a Chebyshev variance test using the
// depth-moment atlas, multiplying visibility into the weight to suppress light leakage through wall cracks,
// contact regions, and back faces. From Step 5 onward, invalid probes with tile alpha<0.5
// (back-face hit rate above the threshold; clause 13) are removed from the weights. If all 8 neighbors are
// invalid, the shader falls back to SH9 environment irradiance. This stays line-by-line identical across
// all four backends.
float2 DdgiOctEncode(float3 dir)
{
    float3 a = abs(dir);
    float2 p = dir.xy / (a.x + a.y + a.z);
    if (dir.z < 0.0)
        p = (1.0 - abs(float2(p.y, p.x))) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}

// fallback = the diffuse term that would have been used without DDGI
// (the SH9 / constant ambient either-or result), also used as the Step 5 fallback for invalid probes
float3 SampleProbeIrradiance(float3 worldPos, float3 N, float3 fallback)
{
    float3 gridMin = giParams0.xyz;
    float spacing = giParams0.w;
    float3 dims = giParams1.xyz;
    float2 atlasSize = float2(dims.x * dims.z * 8.0, dims.y * 8.0);
    float2 oct = DdgiOctEncode(N) * 0.5 + 0.5;

    float3 wp = worldPos + N * giParams2.x;
    float3 gc = (wp - gridMin) / spacing - 0.5;
    float3 base = floor(gc);
    float3 f = gc - base;

    float3 sum = float3(0.0, 0.0, 0.0);
    float wsum = 0.0;
    float wraw = 0.0;
    for (int i = 0; i < 8; i++)
    {
        float3 off = float3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        float3 tri = lerp(1.0 - f, f, off);
        float w = tri.x * tri.y * tri.z;
        float3 pi = clamp(base + off, float3(0.0, 0.0, 0.0), dims - 1.0);
        float3 probePos = gridMin + (pi + 0.5) * spacing;
        float wdir = max(dot(normalize(probePos - worldPos), N), 0.0);
        w *= wdir * wdir + 0.01;
        float2 tile = float2(pi.x + pi.z * dims.x, pi.y);
        float2 uv = (tile * 8.0 + 1.0 + oct * 6.0) / atlasSize;
        // Step 5 validity weighting (clause 13): alpha is constant classification data inside a tile,
        // so any point inside the tile is enough for sampling.
        // Use continuous weighting instead of a hard step threshold: alpha is the temporal EMA of a
        // classifier, and hard gating would amplify probe flicker around the threshold.
        // wraw accumulates the purely geometric weight without validity, so the final stage can estimate
        // how much of this shading point falls onto valid probes.
        float valid = saturate(ddgiAtlas.SampleLevel(linearSampler, (tile * 8.0 + 4.0) / atlasSize, 0).a);
        if (giParams2.y > 0.5)
        {
            float3 dirPW = wp - probePos;
            float distPW = length(dirPW);
            float2 octD = DdgiOctEncode(normalize(dirPW)) * 0.5 + 0.5;
            float2 depAtlasSize = float2(dims.x * dims.z * 16.0, dims.y * 16.0);
            float2 uvD = (tile * 16.0 + 1.0 + octD * 14.0) / depAtlasSize;
            float2 m = ddgiDepth.SampleLevel(linearSampler, uvD, 0).xy;
            float variance = max(m.y - m.x * m.x, 0.0);
            float d2 = distPW - m.x;
            float cheb = distPW <= m.x ? 1.0 : variance / (variance + d2 * d2);
            float cheb3 = cheb * cheb * cheb;
            // Visibility floor: keep 20% indirect light even under full occlusion to prevent
            // AABB-based proxy over-occlusion (cheb^3 amplifies occlusion cubically) from turning walls pure black.
            w *= 0.2 + 0.8 * cheb3;
        }
        wraw += w;
        w *= valid;
        sum += ddgiAtlas.SampleLevel(linearSampler, uv, 0).rgb * w;
        wsum += w;
    }
    // Step 5: wsum/wraw is the fraction of this shading point's interpolation weight that lands on valid probes.
    // Use it to linearly blend between probe irradiance and fallback
    // (= the diffuse term that would be used without DDGI, either SH9 or constant ambient).
    // If all 8 neighbors are invalid, including the zero-initialized atlas before the first update,
    // the result naturally falls back to pure fallback, with a continuous transition and no threshold jumps or flicker.
    float3 probeIrr = wsum > 1e-6 ? sum / wsum : float3(0.0, 0.0, 0.0);
    float vfrac = saturate(wsum / max(wraw, 1e-6));
    return lerp(fallback, probeIrr * giParams1.w, vfrac);
}
#endif

#if SHADOW_ENABLED
// 1-5 shadow sampling (contract clauses 2/5): a single D32 atlas split into four quadrants, with slot i
// origin at ((i%2)*1/2, (i/2)*1/2). Slots 0..2 are cascades and slot 3 is the spotlight.
// Uses hardware comparison sampling via SampleCmpLevelZero plus fixed 3x3 PCF with step size shadowParams0.z.
Texture2D shadowAtlas : register(t7);
SamplerComparisonState shadowSampler : register(s1);

float SampleShadowTile(int slot, float3 shadowNdc)
{
    // NDC -> tile UV (D3D depth is [0,1], Y is flipped). Anything outside the light frustum or near plane
    // is treated as unoccluded, so result starts at 1.0.
    // Single exit + preinitialized result avoids the fxc X4000 false positive
    // (potentially uninitialized) on early returns from inlined functions.
    float result = 1.0;
    float2 uv = float2(shadowNdc.x * 0.5 + 0.5, 0.5 - shadowNdc.y * 0.5);
    if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0 && shadowNdc.z > 0.0 && shadowNdc.z < 1.0)
    {
        float texel = shadowParams0.z; // 1/atlasSize
        float2 tileOrigin = float2(slot & 1, slot >> 1) * 0.5;
        // Shrink the quadrant inward by 1.5 texels to prevent bilinear leakage across quadrants
        // on the outer ring of the 3x3 PCF kernel.
        float2 tileMin = tileOrigin + texel * 1.5;
        float2 tileMax = tileOrigin + 0.5 - texel * 1.5;
        float2 atlasUV = tileOrigin + uv * 0.5;

        float sum = 0.0;
        [unroll]
        for (int dy = -1; dy <= 1; dy++)
        {
            [unroll]
            for (int dx = -1; dx <= 1; dx++)
            {
                float2 sampleUV = clamp(atlasUV + float2(dx, dy) * texel, tileMin, tileMax);
                sum += shadowAtlas.SampleCmpLevelZero(shadowSampler, sampleUV, shadowNdc.z);
            }
        }
        result = sum / 9.0;
    }
    return result;
}

// Directional-light CSM: choose the cascade by view-space depth -> project to light space -> run PCF;
// returns visibility after strength interpolation.
float ComputeSunShadow(float3 worldPos, float viewDepth)
{
    float result = 1.0;
    int cascadeCount = (int)shadowParams0.y;
    // Sample only when sunEnabled is on and the pixel is within shadow range; otherwise keep it unshadowed.
    [branch] if (shadowParams0.x >= 0.5 && viewDepth <= cascadeSplits[cascadeCount - 1])
    {
        int slot = cascadeCount - 1;
        for (int c = cascadeCount - 1; c >= 0; c--)
        {
            if (viewDepth <= cascadeSplits[c])
                slot = c;
        }

        // Orthographic projection has w=1, but keep the division conservatively.
        float4 lightPos = mul(float4(worldPos, 1.0), cascadeViewProj[slot]);
        float visibility = SampleShadowTile(slot, lightPos.xyz / lightPos.w);
        result = lerp(1.0, visibility, shadowParams1.y);
    }
    return result;
}

// Spotlight shadow (contract clause 8: Lights[0] only): perspective projection, sampled from slot 3 after dividing by w
float ComputeSpotShadow(float3 worldPos)
{
    float result = 1.0;
    [branch] if (shadowParams1.x >= 0.5)
    {
        float4 lightPos = mul(float4(worldPos, 1.0), spotShadowViewProj);
        if (lightPos.w > 0.0)
        {
            float visibility = SampleShadowTile(3, lightPos.xyz / lightPos.w);
            result = lerp(1.0, visibility, shadowParams1.y);
        }
    }
    return result;
}
#endif

static const float PI = 3.14159265359;

float msdfMedian(float r, float g, float b)
{
    return max(min(r, g), min(max(r, g), b));
}

// Normal distribution function (Trowbridge-Reitz GGX)
float DistributionGGX(float3 N, float3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float nom = a2;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;

    return nom / max(denom, 0.0001);
}

// Geometry function (Schlick GGX)
float GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;

    float nom = NdotV;
    float denom = NdotV * (1.0 - k) + k;

    return nom / denom;
}

float GeometrySmith(float3 N, float3 V, float3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx1 = GeometrySchlickGGX(NdotV, roughness);
    float ggx2 = GeometrySchlickGGX(NdotL, roughness);
    return ggx1 * ggx2;
}

// Fresnel-Schlick approximation
float3 FresnelSchlick(float cosTheta, float3 F0)
{
    cosTheta = saturate(cosTheta);
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

// Cook-Torrance direct-light contribution for a single light
// (1-2 contract: formula stays byte-for-byte identical across all four backends; radiance already includes intensity * attenuation * cone falloff)
float3 EvaluatePbrLight(float3 N, float3 V, float3 L, float3 albedo, float metallic, float roughness, float3 F0, float3 radiance)
{
    float3 H = normalize(V + L);

    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    float3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0);
    float3 specular = numerator / max(denominator, 0.0001);

    float3 kS = F;
    float3 kD = (float3(1.0, 1.0, 1.0) - kS) * (1.0 - metallic);

    float NdotL = max(dot(N, L), 0.0);
    return (kD * albedo / PI + specular) * radiance * NdotL;
}

#if HDR_CHAIN
// Closed-form inverse of ACES (Narkowicz 2015 fit):
// y = x(2.51x+0.03) / (x(2.43x+0.59)+0.14), solved by taking the positive root of the quadratic equation.
// Used for inverse compensation on text: pre-distort into linear scene space so the full FinalBlit chain
// exposure + ACES + gamma reconstructs the designed color exactly.
// The curve asymptote is around y~=1.033, so clamp the input below 1 first to avoid degenerating the discriminant.
float3 AcesFilmInv(float3 y)
{
    y = min(y, 0.999);
    float3 A = 2.51 - 2.43 * y;
    float3 B = 0.03 - 0.59 * y;
    return (-B + sqrt(B * B + 4.0 * A * (0.14 * y))) / (2.0 * A);
}
#endif

// -- 2-5 Step B (b11): analytic sun/moon discs + procedural star field --
// These three items intentionally stay **out of** the Sky-View LUT. The LUT is 256x128 with about 1.4 deg
// per texel, while the solar disc diameter is only 0.53 deg. Putting it into the LUT would only yield a bright
// block whose energy is diluted by roughly (0.53/1.4)^2 and that flickers texel-by-texel as the body moves.
// All data comes from skyParams0..3 (see the SceneLightParams header). Disc radiance has already been multiplied
// on the CPU by the **mean transmittance inside the disc** using the same evaluation fed into direct lighting,
// so the sun in the sky and the sunlight on the ground fade together at the same rate.

// Integer-bit-mixing hash (xxhash finalizer style). Avoid frac(sin(...)) style hashes because they depend on
// large-argument sin precision, which makes the star field diverge across drivers and compilers and produces
// moire striping in high-frequency regions.
uint StarHash(uint3 v)
{
    uint h = v.x * 1597334677u ^ v.y * 3812015801u ^ v.z * 2654435761u;
    h ^= h >> 15u; h *= 2246822519u;
    h ^= h >> 13u; h *= 3266489917u;
    h ^= h >> 16u;
    return h;
}

// Map one 16-bit slice of the hash to [0,1). Different shifts read non-overlapping bit ranges so the
// random draws stay independent. Multiplying h by a constant and taking the low bits does not work:
// multiplication mixes low bits poorly and makes the jittered x/y visibly correlated into diagonal lines.
float StarSlice(uint h, uint shift)
{
    return float((h >> shift) & 0xFFFFu) * (1.0 / 65536.0);
}

// Direction -> cube-face index + face-local uv in [0,1]^2. Use a cube instead of a lat-long grid because
// the latter degenerates into thin strips near the zenith and nadir, causing stars to line up in fake radial patterns.
void StarFaceUv(float3 d, out uint face, out float2 uv)
{
    float3 a = abs(d);
    if (a.x >= a.y && a.x >= a.z)  { uv = float2(d.z, d.y) / a.x; face = d.x > 0.0 ? 0u : 1u; }
    else if (a.y >= a.z)           { uv = float2(d.x, d.z) / a.y; face = d.y > 0.0 ? 2u : 3u; }
    else                           { uv = float2(d.x, d.y) / a.z; face = d.z > 0.0 ? 4u : 5u; }
    uv = saturate(uv * 0.5 + 0.5);   // Clamp to [0,1]: floating-point overshoot at t=+-1 would make floor below hit cell -1
}

// **Additional** radiance from celestial discs + stars (linear HDR). It adds to the Sky-View LUT instead of
// replacing it: the LUT stores in-scattering along the ray, while this function computes the intrinsic radiance
// of celestial bodies and stars reaching the observer through the atmosphere, so the two are physically additive.
// pxAng is the angular size per pixel in radians, provided by the caller. Both disc-edge AA and star radius
// are driven by it, so the features stay about one pixel wide without baking in a fixed pixel count and without
// blurring or aliasing as resolution or FOV changes.
float3 SkyCelestialRadiance(float3 dir, float pxAng)
{
    float3 L = float3(0.0, 0.0, 0.0);

    // -- Solar disc: criterion is dot(dir, sunDir) > cos(angular radius)
    // (the second consumer of Atmosphere.SunAngularRadiusDeg) --
    // AA width conversion: the slope of cos at the disc edge is -sin(angular radius), so a 1-pixel change
    // corresponds to a cosine delta of pxAng * sin.
    float sunSin = sqrt(max(1.0 - skyParams0.w * skyParams0.w, 1e-12));
    float aaSun = pxAng * sunSin;
    float sunMask = smoothstep(skyParams0.w - aaSun, skyParams0.w + aaSun, dot(dir, skyParams0.xyz));
    L += skyParams1.xyz * sunMask;

    // -- Lunar disc + phase --
    float cosMoon = dot(dir, skyParams2.xyz);
    float moonSin = sqrt(max(1.0 - skyParams2.w * skyParams2.w, 1e-12));
    float aaMoon = pxAng * moonSin;
    float moonMask = smoothstep(skyParams2.w - aaMoon, skyParams2.w + aaMoon, cosMoon);
    [branch] if (moonMask > 0.0)
    {
        // Spherical normal of a point inside the disc, which is where the moon phase gets its **zero-parameter**
        // derivation from. Normalize the tangent offset of the view direction relative to the moon center by
        // disc radius to get s in [0,1] (0=center, 1=edge). The normal is then tangent*s - moonDir*sqrt(1-s^2):
        // at the disc center the normal points directly at the observer (= -moon center direction), and at the
        // edge it becomes perpendicular to the view ray. This is the geometry of orthographic projection on a sphere,
        // so it needs no extra parameters.
        float3 tangent = dir - skyParams2.xyz * cosMoon;
        float tanLen = length(tangent);
        float s = saturate(tanLen / moonSin);
        float3 tDir = tanLen > 1e-8 ? tangent / tanLen : float3(1.0, 0.0, 0.0);
        float3 nrm = tDir * s - skyParams2.xyz * sqrt(max(1.0 - s * s, 0.0));

        // The moon is lit by the sun, so the incident cosine is the lunar phase and evolves automatically
        // with sunDir/moonDir - no phase parameter and no artist curve.
        // nrm is the negative outward normal (pointing toward the observer) while sunDir is the propagation
        // direction, so the two negative signs cancel and we take a positive dot product here.
        // The square root is a cheap approximation of strong back-scattering from lunar regolith
        // (opposition surge): pure Lambert would make the full moon visibly dark at the rim, while the real
        // full moon looks much closer to a uniformly bright disc. The floor 0.015 models earthshine
        // (Earth-reflected light on the dark side), about 1.5% of the full-moon level. That is what makes the
        // visible dark disc in a crescent moon, not an artist-added fill light.
        // Step C was recomputed offline line-by-line (.qoder\cphase Group8): when phase angle alpha goes from
        // 12 deg to 168 deg, the terminator sweeps across the whole disc and the mean lit value inside the disc
        // falls from 0.784 to 0.019, with the latter already close to the earthshine floor 0.015, i.e. a crescent.
        // The full lunar cycle is therefore derived entirely from the two direction vectors, with zero backend-specific changes.
        float lit = max(sqrt(saturate(dot(nrm, skyParams0.xyz))), 0.015);
        L += skyParams3.xyz * (moonMask * lit);
    }

    // -- Procedural star field (skyParams1.w already includes twilight visibility derived from
    // StarVisibilityTwilightDeg, and is always 0 during daytime) --
    [branch] if (skyParams1.w > 0.0)
    {
        // Rotate back into the star-fixed frame before drawing random values. The star map is pinned in
        // that frame, so when StarRotation changes the stars perform a coherent diurnal motion instead of
        // being resampled every frame and flickering across the sky.
        // Use skyParams4.xyz, the **celestial-pole axis**, instead of world +Y. Before Step C the axis could
        // not be passed down and the code was hardwired to rotate around Y, which is equivalent to placing the
        // observer at the north pole: stars only slide along circles of constant altitude and never rise or set.
        // Rotating around the celestial-pole axis is what produces real rising/setting motion and circumpolar stars.
        // This is Rodrigues inverse rotation (angle = -theta, so the cross term is negated). The axis is already
        // normalized on the CPU, but we still guard against zero length: if it is zero (not injected), fall back
        // to +Y instead of collapsing dir into cos(theta)*dir and flipping it.
        // Runtime A/B evidence (.qoder\d1 and d1_ab) measured star-field displacement frame by frame at night with
        // no moon: around +Y, three windows showed dy fixed at 0/+/-1 px and dx~=+20, i.e. pure horizontal motion
        // with unchanged altitude. Around the celestial-pole axis, the center window moved about dx~=+9/dy~=+6,
        // while the east-side window had dy~=-6, opposite in sign, matching real rise/set behavior across 15 frame pairs.
        // A non-sky region in the same frames stayed at (0,0), ruling out camera drift.
        float3 axis = dot(skyParams4.xyz, skyParams4.xyz) > 1e-8 ? normalize(skyParams4.xyz) : float3(0.0, 1.0, 0.0);
        float ca = cos(skyParams3.w);
        float sa = sin(skyParams3.w);
        float3 sd = dir * ca - cross(axis, dir) * sa + axis * (dot(axis, dir) * (1.0 - ca));

        uint face;
        float2 uv;
        StarFaceUv(sd, face, uv);

        const float gridN = 96.0;        // 6x96^2 ~= 55k cells
        const float starDensity = 0.1;   // Roughly 5.5k stars, same order of magnitude as the ~6k stars visible to the naked eye across the whole sky
        float2 g = uv * gridN;
        float2 ci = floor(g);
        float2 cf = g - ci;

        uint h = StarHash(uint3((uint)ci.x, (uint)ci.y, face));
        [branch] if (StarSlice(h, 0u) < starDensity)
        {
            uint hj = StarHash(uint3(h, 0x9E3779B9u, 1u));
            uint hm = StarHash(uint3(h, 0x85EBCA6Bu, 2u));

            // Jitter the position within the cell and keep a 0.15 margin so stars never cross cell boundaries.
            // This avoids adjacent cells each drawing half a star and revealing the grid.
            float2 pos = float2(0.15 + 0.7 * StarSlice(hj, 0u), 0.15 + 0.7 * StarSlice(hj, 16u));

            // Angular size per cell (analytic and continuous everywhere, so there are no seams on cube edges;
            // using fwidth(uv) here would explode into bright lines along the edges).
            // Face-tangent coordinate t=uv*2-1 satisfies tan(theta)=t, so dtheta/dt~=1/(1+|t|^2), and one cell
            // spans 2/gridN units of t.
            float2 t = uv * 2.0 - 1.0;
            float radPerCell = (2.0 / gridN) / (1.0 + dot(t, t));
            float distRad = length(cf - pos) * radPerCell;
            float star = 1.0 - smoothstep(pxAng * 0.5, pxAng * 1.8, distRad);

            // Magnitude power law: dim stars vastly outnumber bright ones, so cubing a uniform random draw
            // makes the brightest 10% contribute most of the luminous flux.
            float mag = StarSlice(hm, 0u);
            float weight = mag * mag * mag;

            // Randomize color temperature from warm (K/M type) to cool (O/B type). Keep the amplitude small
            // because real stars have very low color saturation.
            float3 tint = lerp(float3(1.0, 0.92, 0.82), float3(0.82, 0.9, 1.0), StarSlice(hm, 16u));

            // Fade out near the horizon over about 3 degrees. That region is occupied by ground geometry and
            // horizon glow, so drawing stars there would only make them cut through the ground.
            L += skyParams1.w * weight * star * tint * saturate(dir.y * 20.0);
        }
    }

    return L;
}

// -- 2-5 Step C: procedural clouds (pre-baked noise + multi-layer parallax composition) --
// Like the analytic celestial discs, this is evaluated per pixel and kept **out of** the Sky-View LUT.
// Each LUT texel is about 1.4 deg, which expands to about 45 pixels at 1080p / 60 deg FOV, turning cloud
// edges into mushy fog blobs. All data comes from cloudLayerA/B + cloudParams0/1.
//
// Coordinate convention: each cloud layer is a horizontal slab at height h, and sample points index the noise
// using **world XZ in kilometers** (engine world units are meters, so multiply by 0.001).
// Visible clouds and cloud shadows share the same indexing, the same noise, and the same coverage remapping,
// which guarantees that the clouds you see are the clouds that cast the shadows. The two paths differ only
// in how they compute intersections (see CloudLayerHitKm).

// Density of one cloud layer at a given world XZ position in km (0..1, already including coverage remapping and high-frequency erosion)
float CloudDensityAt(float2 posKm, int layer)
{
    float2 uv = (posKm + cloudLayerB[layer].xy) * cloudLayerB[layer].z;
    float4 n = cloudNoise.SampleLevel(wrapSampler, uv, 0.0);

    // Use the A channel (very low frequency) to modulate the shape so cloud-rich regions form large patches
    // instead of being spread uniformly. Real skies cluster clouds into large masses.
    float shape = n.r * lerp(1.0, n.a, 0.7);

    // Coverage remapping: linearly remap the portion above the threshold back into 0..1.
    // Divide by coverage instead of a fixed slope so density approaches saturation when coverage->1
    // (full overcast) and the whole cloud field disappears when coverage->0 instead of merely thinning out.
    float coverage = cloudLayerA[layer].z;
    float d = saturate((shape - (1.0 - coverage)) / max(coverage, 1e-3));

    // High-frequency erosion only trims edges and never thickens the core, so use multiplication.
    // Additive erosion would fatten the cloud interior into cotton-candy blobs.
    // Blend Worley clumping and high-frequency FBM half-and-half: the former gives cumulus-like chunky edges,
    // the latter gives wispy torn features like cirrus.
    float erode = cloudLayerB[layer].w * (0.5 * n.g + 0.5 * n.b);
    return saturate(d * saturate(1.0 - erode));
}

// Distance in km from the view ray to the intersection with the cloud layer. Use a **spherical shell**
// instead of a plane. The planar approximation t=h/dir.y diverges near the horizon, stretching clouds into
// infinitely long streaks, while the spherical-shell solution converges to sqrt(2Rh) as dir.y->0
// (~142 km for R=6360 and h=1.6 km). That is exactly why clouds collapse into a band at the horizon, and
// also why low clouds move faster than high clouds when looking upward.
// The observer is at (0,R,0) with the planet center at the origin. Solve the positive root of |p+t*d| = R+h.
// R comes from skyParams4.w (CPU GroundRadiusKm + ViewAltitudeKm).
// This only makes sense for dir.y > 0. Looking downward would intersect the far side through the planet, so
// the caller must gate it first.
float CloudLayerHitKm(float3 dir, float layerAltKm)
{
    float r = max(skyParams4.w, 1.0);
    float b = r * dir.y;
    return -b + sqrt(max(b * b + 2.0 * r * layerAltKm + layerAltKm * layerAltKm, 0.0));
}

// Cloud forward-scattering silver lining, normalized into 0..1 with straight-ahead scattering = 1.
// Use the HG phase-function shape instead of pow(cos). g is cloudParams1.y, the same control used on the CPU.
// Self-normalizing avoids writing the peak constant as a second source of truth.
float CloudSilverLining(float cosTheta, float g)
{
    float g2 = g * g;
    float dn = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
    float p = (1.0 - g2) / (dn * sqrt(dn));
    float dp = max(1.0 + g2 - 2.0 * g, 1e-4);
    float peak = (1.0 - g2) / (dp * sqrt(dp));
    return saturate(p / max(peak, 1e-6));
}

// Composite clouds onto sky radiance (used only by the renderMode==3 branch). Ordering matters:
// clouds lie in front of **all** sky components. The Sky-View LUT is in-scattering from infinity, and the
// sun/moon discs and stars are too, so layers are alpha-over composited first and then the accumulated
// transmittance attenuates the sky behind them. This is also what makes clouds naturally occlude the sun and stars.
// Layer order is height order: when dir.y>0, higher layers are farther away, and the CPU presets the layers in ascending height order.
float3 CloudComposite(float3 skyRadiance, float3 dir, float2 camXZKm)
{
    float3 acc = float3(0.0, 0.0, 0.0);
    float trans = 1.0;

    // Compute forward scattering only against the sun. At moonlight levels the silver lining is invisible,
    // so evaluating another HG lobe would be wasted work.
    float fwd = cloudParams1.w * CloudSilverLining(dot(dir, skyParams0.xyz), cloudParams1.y);

    // Fade out near the horizon over about 1.4 degrees, using the same saturate(dir.y*20) pattern as the
    // star field. Otherwise dir.y=0 would leave a hard edge, especially in scenes without ground geometry.
    float horizonFade = saturate(dir.y * 40.0);

    int count = int(cloudParams0.w);
    for (int i = 0; i < count; ++i)
    {
        float tKm = CloudLayerHitKm(dir, cloudLayerA[i].x);
        float d = CloudDensityAt(camXZKm + dir.xz * tKm, i);

        // Oblique traversal: the flatter the view ray, the longer the geometric path through the same layer.
        // Clamp the denominator to 0.05 (~3 degrees); below that the spherical-shell convergence should take over,
        // otherwise the horizon ring turns into a hard black wall.
        float tau = d * cloudLayerA[i].w * cloudLayerA[i].y / max(dir.y, 0.05);
        float alpha = saturate(1.0 - exp(-tau)) * horizonFade;

        // Self-occlusion proxy with zero extra taps: optically thicker cloud cores become darker while edges
        // stay brighter, matching the look of cumulus clouds from below.
        // The true solution would require several extra resampling steps along the light ray, and that cost is
        // left for future quality tiers.
        float lit = saturate(1.0 - d);
        float3 radiance = cloudParams0.rgb * lerp(cloudParams1.z, 1.0, lit) * (1.0 + fwd);

        acc += trans * alpha * radiance;
        trans *= 1.0 - alpha;
    }

    return skyRadiance * trans + acc;
}

// Cloud shadows: visibility of a world point after cloud occlusion along a given light direction
// (1=fully lit, minimum is 1 - cloud-shadow strength).
// Shares the same noise, coverage remapping, and wind offset as visible clouds, ensuring that
// the clouds you see are the clouds that cast the shadows.
//
// Use a **plane** instead of a spherical shell for intersection here, unlike CloudLayerHitKm.
// This ray starts near the ground and ends at cloud altitude, so the path is only a few kilometers long and
// curvature correction is far smaller than one noise texel. The planar solution costs one division, while
// the spherical-shell solution costs one square root. This function runs for every directional light and
// every pixel, so the saved work is real.
//
// Single exit + preinitialized result: fxc flags multi-exit called functions as potentially uninitialized
// (X4000), and this project treats warnings as fatal.
float ComputeCloudShadow(float3 worldPos, float3 toLight)
{
    float result = 1.0;
    int count = int(cloudParams0.w);
    [branch] if (count > 0 && cloudParams1.x > 0.0 && toLight.y > 0.0)
    {
        float2 originKm = worldPos.xz * 0.001;

        // Clamp toLight.y to 0.05 (~3 degrees): when a celestial body hugs the horizon, the light ray becomes
        // almost horizontal and the planar solution would fling the sample point hundreds of kilometers away
        // into unrelated clouds. At that point the direct light itself has already been attenuated close to zero
        // by atmospheric transmittance (mean transmittance inside the disc; see
        // SkyLighting.EvaluateDiskTransmittance), so this clamp introduces no visible error.
        float invY = 1.0 / max(toLight.y, 0.05);

        float tau = 0.0;
        for (int i = 0; i < count; i++)
        {
            // Layer height is measured relative to the observer, and observer altitude is already baked into
            // Atmosphere.ViewAltitudeKm, so world y=0 becomes the observer plane here
            // (engine world units are meters, hence *0.001).
            // Height differences of tens of meters inside the scene shift the shadow position by less than one
            // noise texel for clouds several kilometers up, but we still subtract it so shadows naturally
            // disappear when flying or climbing through the cloud layer instead of sticking to the sky.
            float hKm = max(cloudLayerA[i].x - worldPos.y * 0.001, 0.0);
            float2 posKm = originKm + toLight.xz * (hKm * invY);
            tau += CloudDensityAt(posKm, i) * cloudLayerA[i].w * cloudLayerA[i].y * invY;
        }

        // The strength knob mirrors the shape of 1-5 shadowParams1.y: it interpolates between
        // fully lit and the physical transmittance. So ShadowStrength=1 gives the physical value,
        // and 0 disables cloud shadows completely with no residue.
        result = 1.0 - cloudParams1.x * saturate(1.0 - exp(-tau));
    }
    return result;
}

// 2-3 contract clause 3: the velocity variant changes only the PSMain signature;
// the shading body itself stays byte-for-byte unchanged, so there is no second copy of the PBR code.
//
// Why not factor this into a ShadePixel helper wrapped by PSMain? Because this function contains several
// early returns inside [branch] blocks (Text MSDF, Sprite2D, and other dispatch paths).
// fxc tolerates multi-exit control flow on an entry point, but once the code becomes a callee, its return
// slot is flagged as potentially uninitialized, producing warning X4000. This project treats warnings as fatal,
// so Pipeline.Init would throw immediately. Forcing a single exit would require restructuring the entire
// ~200-line dispatch body, which costs more than it saves.
//
// Therefore the velocity path uses an entry-point out parameter (SV_Target1). PSMain remains the entry point,
// keeping the multi-exit tolerance, and outVelocity is definitely assigned before any early return,
// so there is no uninitialized warning.
#if VELOCITY_OUTPUT
float4 PSMain(PSInput input, out float2 outVelocity : SV_Target1) : SV_Target0
#else
float4 PSMain(PSInput input) : SV_TARGET
#endif
{
#if VELOCITY_OUTPUT
    // 2-3 contract clause 5: this must be unconditionally initialized before any early return.
    // cur is reconstructed from SV_Position back to NDC and then de-jittered by subtracting this frame's jitter.
    // prev comes from perspective-dividing prevClip, whose source matrix prevViewProjection is itself unjittered.
    // Their difference is then converted into UV space with Y inverted.
    // w <= 0 means no history, including all 2D/UI/text paths, so velocity stays zero.
    outVelocity = float2(0, 0);
    [branch] if (input.prevClip.w > 0.0)
    {
        float2 curNdc = input.position.xy * velocityParams.zw * float2(2.0, -2.0) + float2(-1.0, 1.0);
        curNdc -= velocityParams.xy;
        float2 prevNdc = input.prevClip.xy / input.prevClip.w;
        outVelocity = (curNdc - prevNdc) * float2(0.5, -0.5);
    }
#endif
    // Debug mode: return the debug color directly
    //#ifdef DEBUG_SKINNING
    //    return input.debugColor;
    //#endif
    //if (materialColor.x + materialColor.y + materialColor.z + materialColor.w > 0.0)
    //{
    //    return materialColor;
    //}

    // Default values
    const float3 defaultAlbedo = float3(1.0, 1.0, 1.0); // White
    const float3 defaultNormal = float3(0.5, 0.5, 1.0); // Tangent-space normal
    const float3 defaultMetallicRoughness = float3(0.0, 0.5, 0.0); // x: metallic, y: roughness, z: unused
    const float defaultAo = 1.0; // Fully lit ambient occlusion
    const float3 defaultEmissive = float3(0.0, 0.0, 0.0); // No emissive term

    // Resolve material properties from flags
    float3 albedo = materialColor.rgb;
    float alpha = materialColor.a;
    float3 metallicRoughness = defaultMetallicRoughness;
    float ao = defaultAo;
    float3 emissive = defaultEmissive;
    
    [branch] if (renderMode == 2)
    {
        float4 sampledMsdf = albedoMap.Sample(linearSampler, input.texCoord);
        float msdfDist = msdfMedian(sampledMsdf.r, sampledMsdf.g, sampledMsdf.b) - 0.5;
        float trueDist = sampledMsdf.a - 0.5;
        float signedDistance = (msdfDist * trueDist > 0.0) ? msdfDist : trueDist;
        float pxRange = max(textPxRange, 1.0);
        float2 glyphTextureSize = max(textAtlasSize, float2(1.0, 1.0));
        float2 unitRange = float2(pxRange, pxRange) / glyphTextureSize;
        float2 screenTexSize = max(float2(1.0, 1.0) / max(fwidth(input.texCoord), float2(1e-5, 1e-5)), float2(1.0, 1.0));
        float screenPxRange = max(0.5 * dot(unitRange, screenTexSize), 1.0);
        float screenPxDistance = screenPxRange * signedDistance;
        float coverage = saturate(screenPxDistance + 0.5);
        // GPU instancing: the default color comes from b4.TextColor, while per-glyph overrides come from instanceColor.
#if HDR_CHAIN
        // Inverse-ACES compensation: pre-distort the text color, which is authored in display space, into a
        // linear scene-space value so the full FinalBlit chain
        // exposure -> ACES -> pow(1/2.2) reconstructs the design color exactly, even inside the glyph interior.
        // Dividing by exposure makes text immune to exposure changes: the scene brightness can change with
        // HdrExposure while text remains stable.
        // Fallback: if exposure reads as 0 because the CB has not yet been injected by SetLighting, use a
        // neutral exposure of 1.0 to avoid blowing out on division by a tiny number.
        float3 target = saturate(albedo * input.instanceColor.rgb);
        float safeExposure = params0.y > 0.0 ? params0.y : 1.0;
        float3 textColor = AcesFilmInv(pow(target, 2.2)) / safeExposure;
        return float4(textColor, alpha * input.instanceColor.a * textGlobalAlpha * coverage);
#else
        return float4(albedo * input.instanceColor.rgb, alpha * input.instanceColor.a * textGlobalAlpha * coverage);
#endif
    }

    // 2-5 procedural sky: reconstruct the Sky-View LUT UV from the world-space view direction
    // (**ignoring vertex UVs**), so this must run before the useAlbedoMap sampling block below.
    // Otherwise albedo would be polluted by an earlier sample at input.texCoord.
    // The parameterization's single source of truth lives in the Season.Rendering.Atmosphere header and stays
    // byte-for-byte aligned with the inverse mapping used by the skyView kernel:
    // the seam in u sits at +Z (north), which celestial arcs never cross, so the Mie peak never hits the seam;
    // v uses a sqrt fold to concentrate resolution toward the horizon, where evenly spaced v would band.
    // The LUT is rgba16float with no mips, and implicit derivatives across the seam would produce bad LODs,
    // so SampleLevel(...,0) is always used.
    [branch] if (renderMode == 3)
    {
        float3 skyDir = normalize(input.worldPos - cameraPos.xyz);
        float2 skyUv;
        skyUv.x = atan2(skyDir.x, -skyDir.z) * (0.5 / PI) + 0.5;
        skyUv.y = 0.5 - 0.5 * sign(skyDir.y) * sqrt(abs(skyDir.y));
        float3 skyRadiance = albedoMap.SampleLevel(linearSampler, skyUv, 0.0).rgb * materialColor.rgb;

        // 2-5 Step B (b11): add the analytic sun/moon discs and the star field. Gate on skyParams0.w > 0:
        // all four fields being zero means the non-procedural sky tier, and under real angular radii
        // cos(theta) is about 0.99999 and never 0, so the StaticCube tier leaves a clean zero residue here.
        // Compute pxAng outside the function and pass it in: fwidth is a gradient operation and cannot live
        // inside the non-uniform disc/star branches.
        // The two branch conditions here (renderMode and skyParams0.w) are both cbuffer constants, which fxc
        // treats as uniform, same as the existing Sample inside the useAlbedoMap branch below.
        [branch] if (skyParams0.w > 0.0)
        {
            float pxAng = max(length(fwidth(skyDir)), 1e-6);
            skyRadiance += SkyCelestialRadiance(skyDir, pxAng) * materialColor.rgb;
        }

        // 2-5 Step C: procedural cloud composition. This must happen **after** the celestial discs because
        // clouds are in front of all sky components, so they need to occlude the sun and stars
        // (the skyRadiance*trans term at the end of CloudComposite performs exactly that occlusion).
        // There are two gates: cloudParams0.w (layer count, a cbuffer constant that also implies the noise
        // texture is ready) and dir.y > 0 (per-pixel: downward rays hit the far side of the planet in the
        // shell intersection and are meaningless).
        // Internally this path uses only SampleLevel with explicit LOD 0, so the non-uniform branch does not
        // involve implicit derivatives.
        [branch] if (cloudParams0.w > 0.0 && skyDir.y > 0.0)
            skyRadiance = CloudComposite(skyRadiance, skyDir, cameraPos.xz * 0.001);
#if HDR_CHAIN
        // The LUT already stores linear HDR radiance, so output it directly and let the
        // FinalBlit exposure + ACES + gamma chain close it out (1-4 contract).
        return float4(skyRadiance, alpha);
#else
        // LDR baseline (Overlay always takes this path): gamma-encode in place.
        // max(...,0) is not a quality safeguard. Radiance is physically non-negative, but the compiler cannot
        // infer that from sampled value * material color, so a raw pow would trigger X3571.
        // ShaderCompiler throws on any non-empty errorBlob because warnings are treated as fatal, so we cannot
        // depend on the current D3DCompile happening not to report it - SDK fxc already does.
        return float4(pow(max(skyRadiance, 0.0), 1.0 / 2.2), alpha);
#endif
    }

    // Sample textures when enabled
    [branch] if (useAlbedoMap != 0)
    {
        float4 sampledAlbedo = albedoMap.Sample(linearSampler, input.texCoord);
        albedo *= sampledAlbedo.rgb;
        alpha *= sampledAlbedo.a;
    }
    
    // MASK mode: clip pixels below the threshold
    [branch] if (alphaMode == 1)
    {
        clip(alpha - alphaCutoff);
    }

    // Explicit render-mode dispatch
    [branch] if (renderMode == 0)
    {
#if HDR_CHAIN
        // Sprite2D: unlit, output linear color directly and let the FinalBlit exposure + ACES + gamma chain close it out
        return float4(albedo, alpha);
#else
        // Sprite2D: unlit, output texture color directly with gamma correction.
        // max(...,0) mirrors the sky-path X3571 guard: fxc reports raw pow of a possibly-negative base,
        // and ShaderCompiler treats any non-empty errorBlob as fatal.
        float3 color = pow(max(albedo, 0.0), 1.0 / 2.2);
        return float4(color, alpha);
#endif
    }
    // renderMode == 1: PBR path (below)
    // renderMode == 2: reserved for TextMsdf, currently falls back to PBR
    
    [branch] if (useMetallicRoughnessMap != 0)
        metallicRoughness = metallicRoughnessMap.Sample(linearSampler, input.texCoord).rgb;
    else
    {
        // Use material parameters when there is no metallic-roughness map
        metallicRoughness.b = metallicFactor; // Metallic
        metallicRoughness.g = roughnessFactor; // Roughness
    }
    
    [branch] if (useAoMap != 0)
        ao = aoMap.Sample(linearSampler, input.texCoord).r;
    
    [branch] if (useEmissiveMap != 0)
    {
        emissive = emissiveMap.Sample(linearSampler, input.texCoord).rgb;
    }
    else
    {
        emissive = emissiveFactor.rgb; // Use the emissive factor from the material parameters
    }

    // Extract material parameters
    float metallic = metallicRoughness.b;
    float roughness = metallicRoughness.g;

    // Rebuild the TBN matrix
    float3 N = normalize(input.normal); //input.normal;
    float3 T = normalize(input.tangent.xyz);  //input.tangent.xyz;
    T = normalize(T - dot(T, N) * N);

    float3 B = cross(N, T) * input.tangent.w; // Note the cross-product order: cross(T, N) * input.tangent.w would be wrong here
    float3x3 TBN = float3x3(T, B, N);

    //if (abs(length(cross(T, B)) - 1.0) > 0.1 || abs(dot(T, B)) > 0.1)
    //{
    //    // Return different colors to represent different errors
    //    if (abs(dot(T, B)) > 0.1) 
    //        return float4(1, 1, 0, 1); // Yellow: not orthogonal
    //    else 
    //        return float4(0, 1, 1, 1); // Cyan: length is not 1
    //}

    // Apply the normal map when present
    if (useNormalMap != 0)
    {
        float normalStrength = 1.0; // Strength multiplier
        float3 normal = normalMap.Sample(linearSampler, input.texCoord).rgb * 2.0 - 1.0;
        normal.xy *= normalStrength;
        //normal = normalize(normal);
        //normal.y = -normal.y; // Match the DirectX texture-coordinate convention
        N = mul(normal, TBN);
        
        //float3 debug = normal * 0.5 + 0.5;
        //return float4(debug, 1.0);
    }

    // Compute the view direction
    float3 V = normalize(cameraPos.xyz - input.worldPos);

    // Compute reflectance (F0)
    float3 F0 = float3(0.04, 0.04, 0.04);
    F0 = lerp(F0, albedo, metallic);

    // Accumulate direct lighting (1-2 contract clause 2: directional, point, and spot lights all live in
    // the same lights array and are dispatched in a single loop by dirType.w)
    float3 Lo = float3(0.0, 0.0, 0.0);

    int lightCount = min(int(params0.x), 8);
    int dirShadowIdx = int(params0.z);      // Index of the directional light casting CSM (-1 if none)
    int spotShadowIdx = int(params0.w);     // Index of the spotlight casting the 2D shadow map (-1 if none)
    for (int i = 0; i < lightCount; i++)
    {
        float type = lights[i].dirType.w;
        float3 L;
        float3 radiance;

        [branch] if (type >= 1.5)
        {
            // Directional light (sun/moon): L is constant with no attenuation, and
            // radiance = color * intensity (* shadow visibility)
            L = normalize(-lights[i].dirType.xyz);
            radiance = lights[i].colorIntensity.xyz * lights[i].colorIntensity.w;
#if SHADOW_ENABLED
            [branch] if (i == dirShadowIdx)
                radiance *= ComputeSunShadow(input.worldPos, input.viewDepth);
#endif
            // 2-5 Step C: cloud shadows are evaluated independently for **every** directional light using its own L,
            // so sun and moon cast their own shadows. Unlike CSM, which only applies to dirShadowIdx,
            // cloud shadows do not consume atlas quadrants and are not restricted to a single light.
            // This is deliberately not gated by SHADOW_ENABLED: that switch controls the CSM/shadow atlas,
            // while cloud shadows are independent of the atlas and should remain visible even when CSM is disabled.
            radiance *= ComputeCloudShadow(input.worldPos, L);
        }
        else
        {
            float3 toLight = lights[i].posRange.xyz - input.worldPos;
            float dist = length(toLight);
            L = toLight / max(dist, 0.0001);

            // Attenuation (contract clause 3, aligned with KHR_lights_punctual): when range>0, apply
            // the window-function cutoff; when range<=0, fall back to pure 1/d^2.
            float attenuation = 1.0 / max(dist * dist, 0.0001);
            float range = lights[i].posRange.w;
            [branch] if (range > 0.0)
            {
                float win = saturate(1.0 - pow(dist / range, 4.0));
                attenuation *= win * win;
            }

            // Spotlight cone (contract clause 4): cosine values are precomputed on the CPU,
            // and the edge is softened with smoothstep.
            [branch] if (type > 0.5)
            {
                attenuation *= smoothstep(lights[i].spotParams.y, lights[i].spotParams.x,
                                          dot(-L, normalize(lights[i].dirType.xyz)));
            }

            radiance = lights[i].colorIntensity.xyz * lights[i].colorIntensity.w * attenuation;
#if SHADOW_ENABLED
            // Spotlight shadow (contract clause 8): only the spotlight pointed to by params0.w participates,
            // because atlas slot 3 has only one shadow map.
            [branch] if (i == spotShadowIdx && type > 0.5)
                radiance *= ComputeSpotShadow(input.worldPos);
#endif
        }

        Lo += EvaluatePbrLight(N, V, L, albedo, metallic, roughness, F0, radiance);
    }

    // Ambient lighting (contract clause 6: parameterized, with default (0.5,0.5,0.5)*1.0 matching the old hardcoded look)
    // 1-7 contract clause 5: diffuse is either-or, never additive. When envParams.z>0.5 use SH9 irradiance,
    // otherwise use the constant ambientParams.
    // The (1-metallic) gate exists because metals have no diffuse term - all incident light goes to specular
    // with no subsurface scattering. This matches the same physical convention as
    // EvaluatePbrLight, where kD=(1-kS)*(1-metallic). kS is omitted here because ambient diffuse has no single
    // incident direction and there is no Fresnel term to evaluate, so the standard approximation keeps only
    // (1-metallic).
    // This gate applies equally to both the constant ambient and SH9 paths since they share the same units.
    // Without it, metals would always be washed out by ambient light. At constant 0.1 this is subtle, but once
    // 1-7 SH9 landed with DC~=0.45, metallic=1 surfaces would turn into solid white blobs.
    // 2-4 contract clause 9: diffuse has three mutually exclusive choices, never additive. When DDGI is ready
    // and GiIntensity>0, probe irradiance replaces the SH9/constant-ambient either-or result; otherwise the
    // path fully falls back to 1-7/1-2. The specular term is unchanged.
    // Clause 13: the probe side continuously blends back to giDiffuse based on validity, so giDiffuse also
    // serves as the Step 5 fallback.
    float3 envDiffuse = EvaluateIrradianceSH9(N) * envParams.y;
    float3 constAmbient = ambientParams.xyz * ambientParams.w;
    float3 giDiffuse = lerp(constAmbient, envDiffuse, step(0.5, envParams.z));
#if DDGI_ENABLED
    if (giParams2.z > 0.5 && giParams1.w > 0.0)
        giDiffuse = SampleProbeIrradiance(input.worldPos, N, giDiffuse);
#endif
    float3 ambient = giDiffuse * albedo * ao * (1.0 - metallic);

    // 1-7 contract clause 6: the specular term uses the mirror-reflection sample from radiance cube LOD0.
    // There is no mip chain and no GGX prefiltering, so it is masked by (1-roughness)^2; the environment
    // energy for rough surfaces is carried by the SH9 diffuse term above.
    float3 R = reflect(-V, N);
    float3 envSpecular = envCube.SampleLevel(linearSampler, R, 0).rgb * envParams.x;
    float specMask = (1.0 - roughness) * (1.0 - roughness);
    ambient += envSpecular * F0 * specMask * ao * step(0.5, envParams.w);
    
    // Emissive contribution
    float3 emissiveContribution = emissive;
    
    // Combine all lighting contributions
    float3 color = ambient + Lo + emissiveContribution;

    // 2-5 Step E: apply aerial perspective. This is intentionally placed in the **linear HDR domain** before
    // tonemapping because atmospheric in-scattering is a real radiance contribution. Applying a curve first
    // and then adding it would wash distant blue haze into gray-white.
    // Only the renderMode==1 PBR path reaches this point. Sprite2D, TextMsdf, and ProceduralSky all return
    // earlier, so the sky never gets fogged twice.
    // The z axis uses sqrt(distance/maxDistance), which is the inverse of the slice-center distribution used
    // when baking skyAerial: maxDist*((k+0.5)/N)^2. This makes slices dense near the camera and sparse in the
    // distance, matching the fact that AP gradients are concentrated in the first few kilometers.
    // This block is intentionally not factored into a helper because multi-exit logic inside PSMain would
    // otherwise trigger X4000 (see the 2-5 HLSL discipline).
    [branch] if (apParams0.x > 0.0)
    {
        float2 apUv = input.position.xy * velocityParams.zw;
        float distKm = length(input.worldPos - cameraPos.xyz) * 0.001;
        float apW = sqrt(saturate(distKm / apParams0.x));
        float4 ap = apLut.SampleLevel(linearSampler, float3(apUv, apW), 0.0);
        color = lerp(color, color * (1.0 - ap.a) + ap.rgb, apParams0.y);
    }

#if HDR_CHAIN
    // Step B: output true linear HDR values directly, with no compression or encoding.
    // exposure + ACES + gamma are all closed out in the FinalBlit tonemap variant.
#else
    // Tone mapping and gamma correction (LDR baseline: inline Reinhard + gamma).
    // max(...,0) mirrors the sky-path X3571 guard: the compiler cannot prove the base is non-negative,
    // and ShaderCompiler treats any non-empty errorBlob as fatal.
    color = color / (color + 1.0);
    color = pow(max(color, 0.0), 1.0 / 2.2);
#endif

    return float4(color, alpha);
}

float4 PSOutlineMask(PSInput input) : SV_TARGET
{
    float alpha = materialColor.a;

    [branch] if (useAlbedoMap != 0)
        alpha *= albedoMap.Sample(linearSampler, input.texCoord).a;

    [branch] if (alphaMode == 1)
        clip(alpha - alphaCutoff);

    // Pass the per-group color straight through: the composite pass tests alpha to detect pixels inside the
    // mask and uses that pixel's RGB as the outline color, allowing multiple outline colors in the same frame.
    // Alpha stays at 1 so any outline color, including pure black, remains valid. RGB is quantized through
    // the RGBA8 mask RT, matching the final display path.
    return float4(outlineMaskColor.rgb, 1.0);
}
";

        ID3D10Blob* vertexShaderBlob = ShaderCompiler.CompileShaderFromSource(hlsl, "VSMain", "vs_5_0", compileFlags);
        // Shadow pass has no color target, so it neither compiles nor binds a PS (contract clause 3).
        ID3D10Blob* pixelShaderBlob = shadowPass ? null : ShaderCompiler.CompileShaderFromSource(hlsl, outlineMask ? "PSOutlineMask" : "PSMain", "ps_5_0", compileFlags);

        var positionPtr = (byte*)SilkMarshal.StringToPtr("POSITION");
        var texCoordPtr = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        var normalPtr = (byte*)SilkMarshal.StringToPtr("NORMAL");
        var tangentPtr = (byte*)SilkMarshal.StringToPtr("TANGENT");
        var jointPtr = (byte*)SilkMarshal.StringToPtr("JOINTINDICES");
        var weightPtr = (byte*)SilkMarshal.StringToPtr("WEIGHTS");
        var instanceWorldPtr = (byte*)SilkMarshal.StringToPtr("INSTANCEWORLD");
        var instanceWeightsPtr = (byte*)SilkMarshal.StringToPtr("INSTANCEWEIGHTS");

        const uint InputElementDescsCount = 11;

        var inputElementDescs = stackalloc InputElementDesc[(int)InputElementDescsCount]
        {
            // slot 0: per-vertex attributes (stride 80 = Vertex.Size)
            new InputElementDesc { SemanticName = positionPtr, Format = Format.FormatR32G32B32Float, InputSlotClass = InputClassification.PerVertexData, InputSlot = 0 },
            new InputElementDesc { SemanticName = texCoordPtr, Format = Format.FormatR32G32Float, AlignedByteOffset = 12, InputSlotClass = InputClassification.PerVertexData, InputSlot = 0 },
            new InputElementDesc { SemanticName = normalPtr, Format = Format.FormatR32G32B32Float, AlignedByteOffset = 20, InputSlotClass = InputClassification.PerVertexData, InputSlot = 0 },
            new InputElementDesc { SemanticName = tangentPtr, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 32, InputSlotClass = InputClassification.PerVertexData, InputSlot = 0 },
            new InputElementDesc { SemanticName = jointPtr, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 48, InputSlotClass = InputClassification.PerVertexData, InputSlot = 0 },
            new InputElementDesc { SemanticName = weightPtr, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 64, InputSlotClass = InputClassification.PerVertexData, InputSlot = 0 },
            // slot 1: per-instance world matrix (stride 64 = 4x float4 + morph weights)
            new InputElementDesc { SemanticName = instanceWorldPtr, SemanticIndex = 0, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1, InputSlot = 1 },
            new InputElementDesc { SemanticName = instanceWorldPtr, SemanticIndex = 1, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 16, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1, InputSlot = 1 },
            new InputElementDesc { SemanticName = instanceWorldPtr, SemanticIndex = 2, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 32, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1, InputSlot = 1 },
            new InputElementDesc { SemanticName = instanceWorldPtr, SemanticIndex = 3, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 48, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1, InputSlot = 1 },
            // per-instance morph weights (offset 64, stride 64)
            new InputElementDesc { SemanticName = instanceWeightsPtr, Format = Format.FormatR32G32B32A32Float, AlignedByteOffset = 64, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1, InputSlot = 1 },
        };

        var defaultRenderTargetBlend = new RenderTargetBlendDesc()
        {
            BlendEnable = 0,
            LogicOpEnable = 0,
            SrcBlend = Blend.One,
            DestBlend = Blend.Zero,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All
        };

        var defaultStencilOp = new DepthStencilopDesc
        {
            StencilFailOp = StencilOp.Keep,
            StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep,
            StencilFunc = ComparisonFunc.Always
        };

        var transparentRenderTargetBlend = new RenderTargetBlendDesc()
        {
            BlendEnable = 1,
            LogicOpEnable = 0,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All
        };

        // Get the current MSAA settings
        uint sampleCount = DirectX.Device._msaaSampleCount;
        uint sampleQuality = DirectX.Device._msaaQualityLevels > 0 ? DirectX.Device._msaaQualityLevels - 1 : 0;

        // Choose blend and depth state based on PipelineMode.
        // - Opaque:      default blend + DepthWrite=All  + DepthFunc=Less
        // - Transparent: alpha blend   + DepthWrite=Zero + DepthFunc=LessEqual (true BLEND, no self-occlusion)
        // - Fade:        alpha blend   + DepthWrite=All  + DepthFunc=Less      (whole-model fade, depth writes block multi-mesh alpha stacking)
        RenderTargetBlendDesc rtBlend;
        DepthWriteMask depthWriteMask;
        ComparisonFunc depthFunc;
        switch (mode)
        {
            case PipelineMode.Transparent:
                rtBlend = transparentRenderTargetBlend;
                depthWriteMask = DepthWriteMask.Zero;
                depthFunc = ComparisonFunc.LessEqual;
                break;
            case PipelineMode.Fade:
                rtBlend = transparentRenderTargetBlend;
                depthWriteMask = DepthWriteMask.All;
                depthFunc = ComparisonFunc.Less;
                break;
            default: // Opaque
                rtBlend = defaultRenderTargetBlend;
                depthWriteMask = DepthWriteMask.All;
                depthFunc = ComparisonFunc.Less;
                break;
        }

        // OutlineMask: the mask uses the same geometry and matrices as the Scene, so surface depths are equal
        // pixel by pixel. Using Less would reject the entire surface, producing an empty mask and therefore no
        // composited outline on screen. Switching to LessEqual lets the surface pixels pass while still rejecting
        // foreground occluders with smaller depth, keeping outline occlusion correct
        // (wireframe / bounds shells only pass Less when their geometry is actually expanded outward).
        if (outlineMask)
            depthFunc = ComparisonFunc.LessEqual;

        // 2-2 contract clause 7: the AoExempt NoDepth variants only zero the depth-write mask while leaving all
        // other states byte-for-byte identical. SceneDepth then stays at the clear value 1.0, exempting the sky
        // branch in GTAO. Shadow pass always writes depth and is unaffected.
        if (!depthWrite)
            depthWriteMask = DepthWriteMask.Zero;

        // 2-3 contract clause 7: velocity (slot 1) is never blended, and Transparent/Fade modes set its write
        // mask to 0 so translucent geometry does not pollute velocity that does not belong to it,
        // without introducing any shader branches.
        var velocityBlend = defaultRenderTargetBlend;
        velocityBlend.RenderTargetWriteMask = (byte)(mode == PipelineMode.Opaque ? ColorWriteEnable.All : 0);
        var slot1Blend = velocityOutput ? velocityBlend : rtBlend;

        GraphicsPipelineStateDesc psoDesc = new GraphicsPipelineStateDesc
        {
            InputLayout = new InputLayoutDesc
            {
                PInputElementDescs = inputElementDescs,
                NumElements = InputElementDescsCount,
            },
            PRootSignature = RootSignature,
            VS = new ShaderBytecode(vertexShaderBlob->GetBufferPointer(), vertexShaderBlob->GetBufferSize()),
            PS = shadowPass ? default : new ShaderBytecode(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize()),
            RasterizerState = new RasterizerDesc
            {
                FillMode = FillMode.Solid,
                CullMode = cullVariant switch
                {
                    PipelineCullVariant.None => CullMode.None,
                    PipelineCullVariant.Front => CullMode.Front,
                    _ => CullMode.Back,
                },
                FrontCounterClockwise = 0, // DirectX convention: CW = Front
                DepthClipEnable = 1,
                MultisampleEnable = (sampleCount > 1) ? 1u : 0u,
            },
            BlendState = new BlendDesc
            {
                AlphaToCoverageEnable = 0,
                IndependentBlendEnable = velocityOutput ? 1u : 0u,
                RenderTarget = new BlendDesc.RenderTargetBuffer()
                {
                    [0] = rtBlend,
                    [1] = slot1Blend,
                    [2] = rtBlend,
                    [3] = rtBlend,
                    [4] = rtBlend,
                    [5] = rtBlend,
                    [6] = rtBlend,
                    [7] = rtBlend
                }
            },
            DepthStencilState = new DepthStencilDesc
            {
                // Overlay always disables depth: the Overlay OM has no DSV, so keeping DepthEnable=1 would
                // create an invalid state at draw time.
                DepthEnable = overlay ? 0u : 1u,
                DepthWriteMask = depthWriteMask,
                DepthFunc = depthFunc,
                StencilEnable = 0,
                StencilReadMask = D3D12.DefaultStencilReadMask,
                StencilWriteMask = D3D12.DefaultStencilWriteMask,
                FrontFace = defaultStencilOp,
                BackFace = defaultStencilOp
            },
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = shadowPass ? 0u : (velocityOutput ? 2u : 1u),
            SampleDesc = (overlay || shadowPass || outlineMask) ? new SampleDesc(count: 1, quality: 0) : new SampleDesc(count: sampleCount, quality: sampleQuality),
        };
        if (shadowPass)
        {
            // 1-5 contract clause 4: depth-only (no RTV) + CullNone + baked-in slope-scaled bias;
            // the atlas is always non-MSAA.
            psoDesc.RasterizerState.MultisampleEnable = 0;
            psoDesc.RasterizerState.DepthBias = RenderQuality.Current.ShadowDepthBias;
            psoDesc.RasterizerState.SlopeScaledDepthBias = RenderQuality.Current.ShadowSlopeScaledDepthBias;
            psoDesc.DSVFormat = Format.FormatD32Float;
        }
        else if (overlay)
        {
            // Overlay target = backbuffer (single-sample, no depth): format, sample count, and DSV must match
            // the OM binding exactly, otherwise the OM/PSO combination becomes invalid at draw time.
            // The main PSO is baked for SceneColorFormat/MSAA/DSV and cannot be reused here.
            psoDesc.RTVFormats[0] = DirectX.Device.BackBufferFormat;
            psoDesc.DSVFormat = Format.FormatUnknown;
        }
        else if (outlineMask)
        {
            psoDesc.RTVFormats[0] = DirectX.Device.BackBufferFormat;
            psoDesc.DSVFormat = DirectX.Device.DepthBufferFormat;
        }
        else
        {
            // Scene target format follows the HDR switch (1-4 Step A): LDR=R8G8B8A8Unorm as the baseline, HDR=RGBA16F
            psoDesc.RTVFormats[0] = DirectX.Device.SceneColorFormat;
            // 2-3 contract clause 2: slot 1 = SceneVelocity (rg16float), matching Device.ToNativeColorFormat
            if (velocityOutput)
                psoDesc.RTVFormats[1] = Format.FormatR16G16Float;
            psoDesc.DSVFormat = DirectX.Device.DepthBufferFormat;
        }

        ID3D12PipelineState* pipelineState;

        var iid = ID3D12PipelineState.Guid;
        var result = DirectX.Device.D3dDevice->CreateGraphicsPipelineState(&psoDesc, &iid, (void**)&pipelineState);
        DirectX.Device.CheckResult(result);

        // Release shader blobs
        vertexShaderBlob->Release();
        if (pixelShaderBlob != null)
            pixelShaderBlob->Release();

        return pipelineState;
    }

    internal static void SetPipeline(PipelineMode mode, bool doubleSided = false)
    {
        SetPipeline(mode, doubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back);
    }

    internal static void SetPipeline(PipelineMode mode, PipelineCullVariant cullVariant)
    {
        SetPipeline(mode, cullVariant, depthWrite: true);
    }

    /// <summary>2-2 contract clause 7: depthWrite=false routes to the NoDepth variants
    /// (only Opaque/Fade have dedicated PSOs; Transparent already does not write depth and therefore falls back to
    /// the regular variant).
    /// Overlay pass (Device.ActivePassId == Overlay) always routes to the dedicated no-depth / single-sample /
    /// backbuffer-format variants and ignores depthWrite because overlay variants are already depthless.</summary>
    internal static void SetPipeline(PipelineMode mode, PipelineCullVariant cullVariant, bool depthWrite)
    {
        // Set the dedicated RootSignature
        DirectX.Device.GraphicsCommandList->SetGraphicsRootSignature(RootSignature);

        // Reset b6 boneBase: regular draw bone addressing always uses (0 + instanceID) * stride as the base.
        // Mask draws write boneBase separately in SetOutlineMaskColor for per-slot rendering.
        // This reset writes only DWORDs 4..7 and leaves the color components untouched, which keeps it compatible
        // with the host-side activation order of "set color first, then SetPipeline".
        ResetOutlineMaskBoneBase();

        // Choose the PSO by PipelineMode. Overlay uses dedicated variants because the main PSO's OM
        // combination is invalid under Overlay.
        ID3D12PipelineState* pso;
        if (DirectX.Device.ActivePassId == RenderPassId.OutlineMask)
        {
            pso = cullVariant == PipelineCullVariant.None ? OutlineMaskDoubleSidedPipelineState : OutlineMaskPipelineState;
        }
        else if (DirectX.Device.ActivePassId == RenderPassId.Overlay)
        {
            pso = mode switch
            {
                PipelineMode.Transparent when cullVariant == PipelineCullVariant.None => TransparentDoubleSidedOverlayPipelineState,
                PipelineMode.Transparent => TransparentOverlayPipelineState,
                PipelineMode.Fade when cullVariant == PipelineCullVariant.None => FadeDoubleSidedOverlayPipelineState,
                PipelineMode.Fade => FadeOverlayPipelineState,
                _ when cullVariant == PipelineCullVariant.None => OpaqueDoubleSidedOverlayPipelineState,
                _ => OpaqueOverlayPipelineState,
            };
        }
        else
        {
            pso = mode switch
            {
                PipelineMode.Transparent when cullVariant == PipelineCullVariant.None => TransparentDoubleSidedPipelineState,
                PipelineMode.Transparent when cullVariant == PipelineCullVariant.Front => TransparentBackFacePipelineState,
                PipelineMode.Transparent => TransparentPipelineState,
                PipelineMode.Fade when !depthWrite => cullVariant == PipelineCullVariant.None ? FadeNoDepthDoubleSidedPipelineState : FadeNoDepthPipelineState,
                PipelineMode.Fade => cullVariant == PipelineCullVariant.None ? FadeDoubleSidedPipelineState : FadePipelineState,
                _ when !depthWrite => cullVariant == PipelineCullVariant.None ? OpaqueNoDepthDoubleSidedPipelineState : OpaqueNoDepthPipelineState,
                _ => cullVariant == PipelineCullVariant.None ? OpaqueDoubleSidedPipelineState : OpaquePipelineState,
            };
        }
        DirectX.Device.GraphicsCommandList->SetPipelineState(pso);
    
        // Set the primitive topology
        DirectX.Device.GraphicsCommandList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
    }

    // ============================================================
    // Unified DrawPrimitive path (shared by regular and instanced draws)
    // ============================================================

    /// <summary>
    /// Draw a single primitive. The backend always uses DrawIndexedInstanced.
    /// When instanceBufferView is null, it automatically falls back to the identity instance buffer.
    /// </summary>
    public static void DrawPrimitive(
        PrimitiveData primitiveData,
        ID3D12Resource* lightConstantBuffer,
        ID3D12Resource* matrixBuffer,
        VertexBufferView* instanceBufferView,
        uint instanceCount,
        uint startInstanceLocation,
        GpuDescriptorHandle instanceBoneSrvHandle = default,
        GpuDescriptorHandle prevBoneSrvHandle = default,
        GpuDescriptorHandle prevInstanceWorldSrvHandle = default,
        GpuDescriptorHandle prevMorphSrvHandle = default)
    {
        var cmdList = Device.GraphicsCommandList;

        primitiveData.BaseColorTexture.EnsureReadyForRendering(cmdList);
        primitiveData.NormalTexture.EnsureReadyForRendering(cmdList);
        primitiveData.MetallicRoughnessTexture.EnsureReadyForRendering(cmdList);
        primitiveData.OcclusionTexture.EnsureReadyForRendering(cmdList);
        primitiveData.EmissiveTexture?.EnsureReadyForRendering(cmdList);

        var vertexViews = stackalloc VertexBufferView[2];
        vertexViews[0] = primitiveData.VertexBufferView;
        vertexViews[1] = instanceBufferView != null ? *instanceBufferView : IdentityInstanceBufferView;
        cmdList->IASetVertexBuffers(0, 2, vertexViews);

        fixed (IndexBufferView* ibv = &primitiveData.IndexBufferView)
            cmdList->IASetIndexBuffer(ibv);

        int fi = (int)Device.FrameIndex;
        cmdList->SetGraphicsRootConstantBufferView(0, matrixBuffer->GetGPUVirtualAddress());
        cmdList->SetGraphicsRootConstantBufferView(1, lightConstantBuffer->GetGPUVirtualAddress());
        cmdList->SetGraphicsRootConstantBufferView(2, primitiveData.MaterialBuffers[fi]->GetGPUVirtualAddress());
        cmdList->SetGraphicsRootConstantBufferView(11, DefaultTextDrawParamsGpuAddress);

        cmdList->SetGraphicsRootDescriptorTable(3, primitiveData.BaseColorTexture.GpuDescriptorHandle);
        cmdList->SetGraphicsRootDescriptorTable(4, primitiveData.NormalTexture.GpuDescriptorHandle);
        cmdList->SetGraphicsRootDescriptorTable(5, primitiveData.MetallicRoughnessTexture.GpuDescriptorHandle);
        cmdList->SetGraphicsRootDescriptorTable(6, primitiveData.OcclusionTexture.GpuDescriptorHandle);
        cmdList->SetGraphicsRootDescriptorTable(7, primitiveData.EmissiveTexture.GpuDescriptorHandle);

        // Morph Target delta SRV (t5): use the primitive-specific buffer when morph data exists,
        // otherwise bind the default zero-valued buffer.
        cmdList->SetGraphicsRootDescriptorTable(9,
            primitiveData.MorphDeltasSrvHandle.Ptr != 0
                ? primitiveData.MorphDeltasSrvHandle
                : DefaultMorphDeltasSrvHandle);

        // Per-instance bone StructuredBuffer SRV (t6): bind the identity buffer by default
        cmdList->SetGraphicsRootDescriptorTable(10,
            instanceBoneSrvHandle.Ptr != 0
                ? instanceBoneSrvHandle
                : DefaultInstanceBoneSrvHandle);

        // 1-5: shadow atlas SRV (t7). During the Scene pass, EndPass has already transitioned the atlas to
        // PixelShaderResource. Skip binding when it has not been created
        // (ShadowsEnabled=false or not registered); shader-side all-zero shadowParams then prevent sampling.
        if (FrameSchedule.ShadowMap is DXRenderTarget shadowRT && shadowRT.GpuSrvHandle.Ptr != 0)
            cmdList->SetGraphicsRootDescriptorTable(12, shadowRT.GpuSrvHandle);

        // 2-3 Step C: previous-frame data SBs (t8/t9/t10). Bind the default zero-valued buffers when the
        // caller does not provide them, preserving sentinel semantics.
        cmdList->SetGraphicsRootDescriptorTable(14,
            prevBoneSrvHandle.Ptr != 0 ? prevBoneSrvHandle : DefaultPrevBoneSrvHandle);
        cmdList->SetGraphicsRootDescriptorTable(15,
            prevInstanceWorldSrvHandle.Ptr != 0 ? prevInstanceWorldSrvHandle : DefaultPrevInstanceWorldSrvHandle);
        cmdList->SetGraphicsRootDescriptorTable(16,
            prevMorphSrvHandle.Ptr != 0 ? prevMorphSrvHandle : DefaultPrevMorphWeightsSrvHandle);

        // 1-7: environment radiance cube (t11). The shader statically references envCube, so this table must
        // be bound on every draw. Bind the 1x1 all-black dummy when no environment map exists
        // (envParams.w is also 0 in that case, so the sample result never contributes to shading).
        // Active is resolved once per frame by SetLighting. EnsureReadyForRendering closes out CopyFence waits
        // and sampling-state transitions.
        var envCube = DXTextureCube.Active ?? DXTextureCube.DummyBlack;
        envCube.EnsureReadyForRendering(cmdList);
        cmdList->SetGraphicsRootDescriptorTable(17, envCube.GpuDescriptorHandle);

        // 2-4 clause 10: DDGI irradiance probe atlas (t12). The shader statically references ddgiAtlas, so this
        // table must be bound on every draw. Bind a 1x1 white dummy when it is not ready
        // (DDGI_ENABLED / atlasReady / GiIntensity gating guarantees the sampled value does not contribute).
        // Active is resolved once per frame by SetLighting, and DispatchCompute has already left the atlas in
        // PixelShaderResource state.
        var ddgiAtlas = DdgiAtlasActive ?? Device.White;
        ddgiAtlas.EnsureReadyForRendering(cmdList);
        cmdList->SetGraphicsRootDescriptorTable(18, ddgiAtlas.GpuDescriptorHandle);

        // 2-4 Step 3: DDGI depth-moment atlas (t13). The shader statically references ddgiDepth, so this table
        // must be bound on every draw. Bind a 1x1 white dummy when it is not ready
        // (giParams2.y gating guarantees Chebyshev visibility does not participate in shading).
        // Active is resolved once per frame by SetLighting, and DispatchCompute has already left the atlas in
        // PixelShaderResource state.
        var ddgiDepth = DdgiDepthActive ?? Device.White;
        ddgiDepth.EnsureReadyForRendering(cmdList);
        cmdList->SetGraphicsRootDescriptorTable(19, ddgiDepth.GpuDescriptorHandle);

        // 2-5 Step C: pre-baked cloud noise (t14). The shader statically references cloudNoise, so this table
        // must be bound on every draw. Bind a 1x1 white dummy when not ready
        // (cloudParams0.w==0 then guarantees the whole cloud branch is skipped; without that gate,
        // remapping all-white noise would produce a sheet of dead-gray fake overcast).
        // Active is resolved once per frame by SetLighting.
        var cloudNoise = CloudNoiseActive ?? Device.White;
        cloudNoise.EnsureReadyForRendering(cmdList);
        cmdList->SetGraphicsRootDescriptorTable(21, cloudNoise.GpuDescriptorHandle);

        // 2-5 Step E: aerial-perspective 3D LUT (t15). The shader statically references apLut, so this table
        // must be bound on every draw. Bind a 1x1x1 all-zero dummy volume when not ready
        // (a=opacity=0, so the compositing formula remains the identity and the image is unaffected).
        // Active is resolved once per frame by SetLighting via DXTexture3D.Find, which uses an independent 3D registry.
        var apLut = AerialLutActive ?? DXTexture3D.DummyBlack;
        apLut.EnsureReadyForRendering(cmdList);
        cmdList->SetGraphicsRootDescriptorTable(22, apLut.GpuDescriptorHandle);

        cmdList->DrawIndexedInstanced((uint)primitiveData.Indices.Length, instanceCount, 0, 0, startInstanceLocation);
    }

    // ============================================================
    // 1-5 Shadow pass (depth-only)
    // ============================================================

    /// <summary>Switch to the shadow PSO. It shares the main RootSignature, so all root parameters need to be rebound after the switch.
    /// The pass-level constant b4 (TextDrawParams placeholder; unread by the shadow VS and present only to keep the
    /// root signature complete) is bound here once. Nothing else in this pass touches root parameter 11, so the
    /// binding stays alive across all atlas quadrants and primitive groups.</summary>
    internal static void SetShadowPipeline()
    {
        var cmdList = DirectX.Device.GraphicsCommandList;
        cmdList->SetGraphicsRootSignature(RootSignature);
        ResetOutlineMaskBoneBase();
        cmdList->SetPipelineState(ShadowPipelineState);
        cmdList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        cmdList->SetGraphicsRootConstantBufferView(11, DefaultTextDrawParamsGpuAddress);
    }

    /// <summary>
    /// 1-5 shadow-pass group-invariant bindings (t6 bone palette / t8 prev bone / t9 prev instanceWorld /
    /// t10 prev morph). All primitives in one primitive group share the same handles, so rebinding per primitive
    /// would only repeat loop-invariant work. Each DrawShadow path calls this once before the primitive loop,
    /// and <see cref="DrawShadowPrimitive"/> does not bind them again.
    ///
    /// 2-3 Step C: the shadow pass does not output velocity, but the VS still runs the deformation stages.
    /// Leaving t8/t9/t10 unbound would trigger debug-layer warning #721 for missing SRVs, so bind the default
    /// zero-valued buffers to preserve sentinel semantics (matrix _m33==0 / all-zero weights => no history).
    /// </summary>
    internal static void SetShadowGroupBindings(
        GpuDescriptorHandle instanceBoneSrvHandle,
        GpuDescriptorHandle prevBoneSrvHandle,
        GpuDescriptorHandle prevInstanceWorldSrvHandle,
        GpuDescriptorHandle prevMorphSrvHandle)
    {
        var cmdList = DirectX.Device.GraphicsCommandList;
        cmdList->SetGraphicsRootDescriptorTable(10,
            instanceBoneSrvHandle.Ptr != 0 ? instanceBoneSrvHandle : DefaultInstanceBoneSrvHandle);
        cmdList->SetGraphicsRootDescriptorTable(14,
            prevBoneSrvHandle.Ptr != 0 ? prevBoneSrvHandle : DefaultPrevBoneSrvHandle);
        cmdList->SetGraphicsRootDescriptorTable(15,
            prevInstanceWorldSrvHandle.Ptr != 0 ? prevInstanceWorldSrvHandle : DefaultPrevInstanceWorldSrvHandle);
        cmdList->SetGraphicsRootDescriptorTable(16,
            prevMorphSrvHandle.Ptr != 0 ? prevMorphSrvHandle : DefaultPrevMorphWeightsSrvHandle);
    }

    /// <summary>Write the light-space ViewProj per quadrant (root constants b5, passed verbatim in row-major order and adapted by row_major on the shader side).</summary>
    internal static void SetShadowViewProj(in Matrix4x4 lightViewProj)
    {
        fixed (Matrix4x4* p = &lightViewProj)
            DirectX.Device.GraphicsCommandList->SetGraphicsRoot32BitConstants(13, 16, p, 0);
    }

    /// <summary>Write Outline2D mask outline parameters per group (root constants b6, 8 DWORDs):
    /// color is read by PSOutlineMask, and boneBase is used by the VS for bone addressing
    /// (the slot base for per-instance mask draws; host-wide activation and non-instanced paths always pass 0).</summary>
    internal static void SetOutlineMaskColor(in Vector4 color, uint boneBase = 0)
    {
        float* values = stackalloc float[8];
        values[0] = color.X;
        values[1] = color.Y;
        values[2] = color.Z;
        values[3] = color.W;
        values[4] = boneBase;
        values[5] = 0f;
        values[6] = 0f;
        values[7] = 0f;
        DirectX.Device.GraphicsCommandList->SetGraphicsRoot32BitConstants(20, 8, values, 0);
    }

    /// <summary>Reset the outlineMaskBoneBase part of b6 to 0 (write only DWORDs 4..7 and leave the color untouched).
    /// Called after every PSO switch in SetPipeline/SetShadowPipeline, and RenderOutlineMask performs one extra
    /// safety reset after the mask pass. This guarantees that bone addressing in the main and shadow passes always
    /// returns to (0 + instanceID) * stride and never leaks mask slot state.</summary>
    internal static void ResetOutlineMaskBoneBase()
    {
        float* zero = stackalloc float[4];
        zero[0] = 0f;
        zero[1] = 0f;
        zero[2] = 0f;
        zero[3] = 0f;
        DirectX.Device.GraphicsCommandList->SetGraphicsRoot32BitConstants(20, 4, zero, 4);
    }

    /// <summary>Write only the outlineMaskBoneBase part of b6 (DWORDs 4..7, leaving the color untouched).
    /// Used by per-slot draws of instanced skinned shells: since SV_InstanceID does not include
    /// StartInstanceLocation, the slot base must be carried explicitly in root constants.
    /// This has the same meaning as the boneBase parameter in SetOutlineMaskColor, but leaves color untouched
    /// because shell draws do not sample the mask color. The write must happen after SetPipeline
    /// (which resets outlineMaskBoneBase) and before DrawPrimitive.</summary>
    internal static void SetOutlineMaskBoneBase(uint boneBase)
    {
        float* values = stackalloc float[4];
        values[0] = boneBase;
        values[1] = 0f;
        values[2] = 0f;
        values[3] = 0f;
        DirectX.Device.GraphicsCommandList->SetGraphicsRoot32BitConstants(20, 4, values, 4);
    }

    /// <summary>
    /// Draw a single primitive in the shadow pass (null PS): skip texture EnsureReady and bindings for tables 3-7 / b1
    /// because the pass forbids barriers and the atlas is currently in DepthWrite, not usable as an SRV.
    /// This method binds only the two items that actually vary per primitive: the b0 matrix CB and the VB/IB.
    /// The b2 material CB and t5 morph deltas are gated by <paramref name="bindMaterial"/>.
    /// The other loop-invariant bindings have already been hoisted:
    /// b4 placeholder -> once per pass in <see cref="SetShadowPipeline"/>;
    /// t6/t8/t9/t10 -> once per group in <see cref="SetShadowGroupBindings"/>;
    /// b3 (legacy bone CBV) is bound by the caller in OnBeforeDraw, and b5 is written per quadrant by SetShadowViewProj.
    /// </summary>
    /// <param name="bindMaterial">
    /// false = reuse the b2/t5 bindings already active on the command list and skip rebinding them.
    /// The caller must first verify group-level sharing through DXPrimitiveGroup.CanShareShadowMaterial, then pass
    /// true for the first primitive in the group so that first bind becomes the group-wide binding.
    /// Nothing else in this pass writes root parameters 2 or 9, so the binding stays alive for the following primitives.
    /// </param>
    public static void DrawShadowPrimitive(
        PrimitiveData primitiveData,
        ID3D12Resource* matrixBuffer,
        VertexBufferView* instanceBufferView,
        uint instanceCount,
        uint startInstanceLocation,
        bool bindMaterial = true)
    {
        var cmdList = Device.GraphicsCommandList;

        var vertexViews = stackalloc VertexBufferView[2];
        vertexViews[0] = primitiveData.VertexBufferView;
        vertexViews[1] = instanceBufferView != null ? *instanceBufferView : IdentityInstanceBufferView;
        cmdList->IASetVertexBuffers(0, 2, vertexViews);

        fixed (IndexBufferView* ibv = &primitiveData.IndexBufferView)
            cmdList->IASetIndexBuffer(ibv);

        cmdList->SetGraphicsRootConstantBufferView(0, matrixBuffer->GetGPUVirtualAddress());

        if (bindMaterial)
        {
            int fi = (int)Device.FrameIndex;
            cmdList->SetGraphicsRootConstantBufferView(2, primitiveData.MaterialBuffers[fi]->GetGPUVirtualAddress());

            // t5 morph: required by the VS deformation stages and shared with the main pass path
            cmdList->SetGraphicsRootDescriptorTable(9,
                primitiveData.MorphDeltasSrvHandle.Ptr != 0
                    ? primitiveData.MorphDeltasSrvHandle
                    : DefaultMorphDeltasSrvHandle);
        }

        cmdList->DrawIndexedInstanced((uint)primitiveData.Indices.Length, instanceCount, 0, 0, startInstanceLocation);
    }
}
