// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Ring buffer for text instance data used by GPU instancing.
/// Uses an Upload Heap mapped pointer plus fence synchronization for lock-free CPU writes.
/// 
/// Layout: StructuredBuffer&lt;float&gt; at t5, 12 floats per instance (48 bytes):
///   [0-3]: uvRect  (SourceX, SourceY, SourceWidth, SourceHeight) normalized 0..1
///   [4-7]: color   (R, G, B, A)
///   [8-11]: glyphSizePx (width, height, pxRange, reserved)
/// </summary>
internal unsafe class TextInstanceRingBuffer
{
    internal const int FloatsPerInstance = 12;       // 48 bytes
    const int DefaultMaxInstancesPerFrame = 65536;  // ~3 MB / frame, enough for all text glyphs in one frame

    ID3D12Resource*[] _buffers;
    byte*[] _mappedPtrs;
    GpuDescriptorHandle[] _srvHandles;
    int[] _descriptorIds;
    int _capacityPerFrame;
    int _currentFrameIndex;
    int _writeOffset;        // Counted in instances
    int _frameInstanceCount; // Total instances allocated in the current frame, used for debugging

    internal ID3D12Resource* Buffer => _buffers[_currentFrameIndex];
    internal GpuDescriptorHandle SrvHandle => _srvHandles[_currentFrameIndex];
    internal int Capacity => _capacityPerFrame;
    internal byte* MappedPtr => _mappedPtrs[_currentFrameIndex];
    internal int WriteOffsetInstances => _writeOffset;

    /// <summary>Called at the beginning of each frame to switch to the current in-flight frame slice and reset the allocation pointer.</summary>
    internal void BeginFrame(uint frameIndex)
    {
        _currentFrameIndex = (int)(frameIndex % Device.frameCount);
        _writeOffset = 0;
        _frameInstanceCount = 0;
    }

    public void Init(int maxInstancesPerFrame = DefaultMaxInstancesPerFrame)
    {
        _capacityPerFrame = maxInstancesPerFrame;
        int frameCount = (int)Device.frameCount;
        int totalFloats = _capacityPerFrame * FloatsPerInstance;

        _buffers = new ID3D12Resource*[frameCount];
        _mappedPtrs = new byte*[frameCount];
        _srvHandles = new GpuDescriptorHandle[frameCount];
        _descriptorIds = new int[frameCount];

        var srv = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)totalFloats,
                StructureByteStride = (uint)sizeof(float),
                Flags = BufferSrvFlags.None
            }
        };

        for (int i = 0; i < frameCount; i++)
        {
            _descriptorIds[i] = Device.DescriptorAllocator.Allocate();
            _buffers[i] = Device.ResourceManager.CreateBuffer(
                HeapType.Upload,
                (ulong)(totalFloats * sizeof(float)),
                ResourceStates.GenericRead);

            void* p;
            _buffers[i]->Map(0, null, &p);
            _mappedPtrs[i] = (byte*)p;

            var cpuHandle = Device.SrvHeapManager.GetCpuHandle(_descriptorIds[i]);
            Device.D3dDevice->CreateShaderResourceView(_buffers[i], &srv, cpuHandle);
            _srvHandles[i] = Device.SrvHeapManager.GetGpuHandle(_descriptorIds[i]);
        }
    }

    /// <summary>
    /// Allocate a writable region for instanceCount instances.
    /// Returns (floatOffset, startInstance):
    ///   - floatOffset: used to index into MappedPtr, in floats
    ///   - startInstance: used as StartInstanceLocation for DrawIndexedInstanced
    /// Ring-style writing wraps back to 0 when it reaches the end of capacity.
    /// If a single allocation request exceeds total capacity, returns (-1, 0) to indicate overflow.
    /// </summary>
    internal (int floatOffset, int startInstance) Allocate(int instanceCount)
    {
        // A single allocation request that exceeds total capacity fails permanently.
        if (instanceCount > _capacityPerFrame)
        {
            return (-1, 0);
        }

        // Each in-flight frame owns an independent upload slice. The current frame only does
        // linear allocation, so going past capacity fails for this frame.
        if (_writeOffset + instanceCount > _capacityPerFrame)
        {
            return (-1, 0);
        }

        int startInstance = _writeOffset;
        int floatOffset = _writeOffset * FloatsPerInstance;
        _writeOffset += instanceCount;
        _frameInstanceCount += instanceCount;
        return (floatOffset, startInstance);
    }

    /// <summary>
    /// Kept for symmetric call-site cleanup. No extra work is needed in the per-in-flight-frame slice model.
    /// </summary>
    internal void EndFrame()
    {
    }
}
