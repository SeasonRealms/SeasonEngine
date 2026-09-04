// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex
{
    public System.Numerics.Vector3 Position;
    public System.Numerics.Vector2 TexCoord;
    public System.Numerics.Vector3 Normal;   // Added: normal
    public System.Numerics.Vector4 Tangent;  // Added: tangent

    // Added: skinning data
    public System.Numerics.Vector4 Joints;  // Joint indices
    public System.Numerics.Vector4 Weights; // Weights

    // Provide a Size property for buffer creation.
    public static uint Size => (uint)Marshal.SizeOf<Vertex>();
}
