// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows.DirectX;

internal class DescriptorAllocator
{
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

    public void Free(int index)
    {
        _freeList.Push(index);
    }
}
