// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public struct Camera
{
    public System.Numerics.Matrix4x4 View;

    public System.Numerics.Matrix4x4 Projection;

    /// <summary>
    /// Contract 2-3, clause 6: previous-frame unjittered View x Projection
    /// in the engine's row-major, non-transposed form.
    /// Each backend forwards it from <c>Camera3D.PrevViewProjection</c> at a single update point;
    /// all-zero means no history. MatrixBuffer writers copy it through as-is,
    /// and the shader outputs zero velocity when <c>_m33 == 0</c>.
    /// </summary>
    public System.Numerics.Matrix4x4 PrevViewProjection;
}

// Use a struct containing three matrices (contract 2-3 appends two history matrices, extending the tail to 320B).
[StructLayout(LayoutKind.Sequential)]
struct MatrixBuffer
{
    public System.Numerics.Matrix4x4 World;
    public System.Numerics.Matrix4x4 View;
    public System.Numerics.Matrix4x4 Projection;

    /// <summary>
    /// Contract 2-3, clause 6 (offset 192): previous-frame World using the same transpose convention as <see cref="World"/>.
    /// All-zero (<c>_m33 == 0</c>) means it was not written, and the shader falls back to <see cref="World"/>
    /// so only camera motion contributes to velocity.
    /// Historical frames must never be read back from the N-buffer CB ring; the CPU shadow copy is the source of truth.
    /// </summary>
    public System.Numerics.Matrix4x4 PrevWorld;

    /// <summary>
    /// Contract 2-3, clause 6 (offset 256): previous-frame unjittered View x Projection
    /// using the same transpose convention.
    /// All-zero (<c>_m33 == 0</c>) means it was not written, and the shader falls back to View x Projection,
    /// which collapses velocity to zero.
    /// </summary>
    public System.Numerics.Matrix4x4 PrevViewProjection;
}
