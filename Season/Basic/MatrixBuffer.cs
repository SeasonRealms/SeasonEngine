// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

// Per-object matrices only (128B). View / Projection / PrevViewProjection moved to the per-view
// ViewConstants buffer, so the same object buffer can be drawn into any number of views.
[StructLayout(LayoutKind.Sequential)]
struct MatrixBuffer
{
    public System.Numerics.Matrix4x4 World;

    /// <summary>
    /// Contract 2-3, clause 6 (offset 64): previous-frame World using the same transpose convention as <see cref="World"/>.
    /// All-zero (<c>_m33 == 0</c>) means it was not written, and the shader falls back to <see cref="World"/>
    /// so only camera motion contributes to velocity.
    /// Historical frames must never be read back from the N-buffer CB ring; the CPU shadow copy is the source of truth.
    /// </summary>
    public System.Numerics.Matrix4x4 PrevWorld;
}
