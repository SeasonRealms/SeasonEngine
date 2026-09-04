// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

internal unsafe sealed class TextGlyphBufferLease
{
    public ID3D12Resource* Buffer;
    public byte* MappedPtr;
    public GpuDescriptorHandle SrvHandle;
    public int DescriptorId = -1;
    public int Capacity;
    // The lease reference can be shared by multiple struct copies inside TextInstanceState.
    // Under races it may be returned twice; this flag makes duplicate returns a no-op.
    public bool Pooled;
}

internal unsafe sealed class TextGlyphBufferPool
{
    readonly object _sync = new();
    readonly Dictionary<int, Stack<TextGlyphBufferLease>> _available = new();

    static int GetBucketCapacity(int requiredCount)
    {
        int capacity = 16;
        int target = Math.Max(1, requiredCount);
        while (capacity < target)
            capacity <<= 1;
        return capacity;
    }

    public TextGlyphBufferLease Rent(int requiredCount)
    {
        int capacity = GetBucketCapacity(requiredCount);

        lock (_sync)
        {
            if (_available.TryGetValue(capacity, out var stack) && stack.Count > 0)
            {
                var pooled = stack.Pop();
                pooled.Pooled = false;
                return pooled;
            }
        }

        int descriptorId = Device.DescriptorAllocator.Allocate();
        var buffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload,
            (ulong)(capacity * Unsafe.SizeOf<TextGlyphData>()),
            ResourceStates.GenericRead);

        void* pGlyph;
        buffer->Map(0, null, &pGlyph);
        var mappedPtr = (byte*)pGlyph;
        Unsafe.InitBlock(mappedPtr, 0, (uint)(capacity * Unsafe.SizeOf<TextGlyphData>()));

        var srv = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)(capacity * 12),
                StructureByteStride = (uint)sizeof(float),
                Flags = BufferSrvFlags.None
            }
        };

        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(descriptorId);
        Device.D3dDevice->CreateShaderResourceView(buffer, &srv, cpuHandle);

        return new TextGlyphBufferLease
        {
            Buffer = buffer,
            MappedPtr = mappedPtr,
            DescriptorId = descriptorId,
            SrvHandle = Device.SrvHeapManager.GetGpuHandle(descriptorId),
            Capacity = capacity,
        };
    }

    public void Return(TextGlyphBufferLease lease)
    {
        if (lease == null || lease.Buffer == null || lease.Capacity <= 0)
            return;

        lock (_sync)
        {
            // Idempotent: ignore duplicate returns of a lease already in the pool to avoid renting
            // the same buffer to two users.
            if (lease.Pooled)
                return;

            lease.Pooled = true;

            if (!_available.TryGetValue(lease.Capacity, out var stack))
            {
                stack = new Stack<TextGlyphBufferLease>();
                _available[lease.Capacity] = stack;
            }

            stack.Push(lease);
        }
    }
}
