// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Allocates SRV/UAV descriptor indices. Called concurrently by the loading thread
/// (TextGlyphBufferPool.Rent from LoadTexts/AppendTexts, DXTexture creation) and the
/// render thread (runtime texture replacement such as video-frame TextureOverride,
/// deferred-release Free execution). Without the lock, two threads can receive the
/// same index (two CreateShaderResourceView calls overwrite one descriptor slot) or
/// corrupt the free-list, producing garbage indices whose CPU handles write past the
/// descriptor heap and damage D3D12 internals; the damage then surfaces as a random
/// SEHException in an unrelated command-list record call (for example
/// IASetVertexBuffers inside DrawTexts). Same cross-thread class as the deferred
/// release queue / TransitionCommandList separation.
/// </summary>
internal class DescriptorAllocator
{
    readonly object _sync = new();
    readonly Stack<int> _freeList = new();
    readonly int _capacity;
    int _nextIndex;

    public DescriptorAllocator() : this(2048) { }

    public DescriptorAllocator(int capacity)
    {
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Allocate()
    {
        lock (_sync)
        {
            if (_freeList.Count > 0)
            {
                return _freeList.Pop();
            }

            if (_nextIndex >= _capacity)
            {
                throw new System.Exception($"Descriptor heap exhausted (capacity: {_capacity})");
            }

            return _nextIndex++;
        }
    }

    public void Free(int index)
    {
        lock (_sync)
        {
            _freeList.Push(index);
        }
    }
}
