// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal struct InstanceTransformData
{
    public Vector4 Row0;
    public Vector4 Row1;
    public Vector4 Row2;
    public Vector4 Row3;
    public Vector4 MorphWeights;

    public static InstanceTransformData FromWorld(Matrix4x4 world)
    {
        return new InstanceTransformData
        {
            Row0 = new Vector4(world.M11, world.M12, world.M13, world.M14),
            Row1 = new Vector4(world.M21, world.M22, world.M23, world.M24),
            Row2 = new Vector4(world.M31, world.M32, world.M33, world.M34),
            Row3 = new Vector4(world.M41, world.M42, world.M43, world.M44),
            MorphWeights = Vector4.Zero,
        };
    }
}

/// <summary>
/// Base class for GPU-instanced primitive groups (Vulkan).
/// Unifies instance-buffer management, transparent sorting,
/// and three-bucket grouped drawing.
/// Derived classes: VKInstancedModel (glTF template cloning) /
/// VKInstancedMesh3D (code-generated geometry).
/// </summary>
internal unsafe abstract class VKInstancedPrimitiveGroup : VKPrimitiveGroup
{
    protected readonly List<PrimitiveData> _primitives = new();
    protected readonly List<PrimitiveData> _opaquePrimitives = new();
    protected readonly List<PrimitiveData> _transparentPrimitives = new();
    protected readonly List<int> _transparentInstanceOrder = new();

    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();
    protected Matrix4x4[] _instanceWorlds = Array.Empty<Matrix4x4>();

    protected BufferResource _instanceBuffer;
    protected int _instanceCapacity;
    protected int _instanceCount;

    /// <summary>Whether any instance enabled Wireframe highlighting in this frame
    /// (written during Update and used as a zero-cost gate during Draw;
    /// reset at the start of each derived Update).</summary>
    protected bool _wireframeActive;

    // Phase 4: per-instance Outline2D collection
    // (host activation uses the full mask and ignores the per-instance list)

    /// <summary>Compressed writeIndex slot list for instances that enabled Outline2D in this frame
    /// (rebuilt every Update).</summary>
    protected readonly List<int> _outline2DInstances = new();

    /// <summary>Per-instance outline colors aligned with _outline2DInstances
    /// (rebuilt every frame; the per-slot mask fetches color slot by slot).</summary>
    protected readonly List<Vector4> _outline2DInstanceColors = new();

    /// <summary>Outline color/width of the first active instance
    /// (the composited frame color uses the first active instance,
    /// matching the per-instance panel color written by ObjectPicker).</summary>
    protected Vector4 _outline2DInstanceColor;

    /// <summary>Outline width of the first active instance.</summary>
    protected float _outline2DInstanceWidth;

    /// <summary>Host-level (non-instanced) Outline2D activation.
    /// When true, DrawOutlineMask renders the full batch at once.</summary>
    protected bool _outline2DHostActive;

    protected VKInstancedPrimitiveGroup(string name)
    {
        Name = name;
    }

    protected void RebuildPrimitiveBuckets()
    {
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();

        foreach (var primitive in _primitives)
        {
            if (primitive.IsTransparent)
                _transparentPrimitives.Add(primitive);
            else
                _opaquePrimitives.Add(primitive);
        }
    }

    protected void EnsureInstanceBufferCapacity(int count)
    {
        if (count <= 0)
            return;

        if (_instanceBuffer.Buffer.Handle != 0 && _instanceCapacity == count)
            return;

        if (_instanceBuffer.Buffer.Handle != 0)
            Device.ResourceManager.DestroyBuffer(_instanceBuffer);

        _instanceBuffer = Device.ResourceManager.CreateVertexBuffer<InstanceTransformData>((uint)count);
        _instanceCapacity = count;
    }

    public new void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var cmd = Device.GraphicsCommandBuffer;
        bool forceFadeByAlpha = _currentAlpha < 1.0f;
        int fi = (int)Device.FrameIndex;

        // 1. Opaque
        // Use the Opaque PSO when overall Alpha >= 1, otherwise use Fade
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.SetPipeline(cmd, forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                _instanceBuffer.Buffer, (uint)_instanceCount, 0);
        }

        // 2. Transparent
        // True BLEND materials
        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            BuildTransparentInstanceOrder(primitive);

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
                    Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                        primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                        _instanceBuffer.Buffer, 1, (uint)instanceIndex);
                }

                Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    _instanceBuffer.Buffer, 1, (uint)instanceIndex);
            }
        }

        // Unified highlighting: per-instance highlights
        // (Bounds boxes + Wireframe shell boxes; for instances enabled in this frame,
        // transparent faces use 2-pass rendering plus opaque edges,
        // finalized after all surfaces)
        if (_boundsActive)
            DrawBoundsBoxes();
        if (_wireframeActive)
            DrawShellBoxes(_instanceBuffer);
    }

    /// <summary>
    /// 1-5 Shadow pass: draw the full set of opaque primitives against the full set of instances
    /// in one batch (no culling, no sorting; transparent buckets are skipped).
    /// Shares the group-level Matrix UBO + instance buffer and uses the same source as the main pass.
    /// Mirrors DX 1:1.
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        OnBeforeDraw();

        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.DrawShadowPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                _instanceBuffer.Buffer, (uint)_instanceCount, 0);
        }
    }

    /// <summary>
    /// Outline2D mask: rendered through the instance stream
    /// (host-level activation -> one full batch; per-instance activation -> one draw per instance
    /// with instanceCount = 1 by writeIndex slot).
    /// The base non-instanced DrawOutlineMask renders the instance stream with an identity matrix
    /// (causing missing/misaligned instances), so this must be overridden.
    /// Transparent buckets are skipped, matching the base mask semantics.
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        // Write outline color per group through the FS push constant.
        // For multi-color cases in the same frame, use the color fixed during Update
        // (instance color or host color).
        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetOutlineMaskColor(cmd, _outline2DColor);

        int fi = (int)Device.FrameIndex;
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var p = _opaquePrimitives[i];
            Pipeline.SetPipeline(cmd, PipelineMode.Opaque,
                p.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite: false);
            OnBeforeDraw();
            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
                    p.DescriptorSets[fi], (uint)p.Indices.Length,
                    _instanceBuffer.Buffer, (uint)_instanceCount, 0);
            }
            else
            {
                for (int k = 0; k < _outline2DInstances.Count; k++)
                {
                    int idx = _outline2DInstances[k];
                    if ((uint)idx >= (uint)_instanceCount)
                        continue;
                    // Write this instance's own OutlineColor slot by slot
                    // (the per-slot mask fetches color per slot)
                    Pipeline.SetOutlineMaskColor(cmd, _outline2DInstanceColors[k]);
                    Pipeline.DrawPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
                        p.DescriptorSets[fi], (uint)p.Indices.Length,
                        _instanceBuffer.Buffer, 1, (uint)idx);
                }
            }
        }
    }

    protected void BuildTransparentInstanceOrder(PrimitiveData primitive)
    {
        _transparentInstanceOrder.Clear();
        for (int i = 0; i < _instanceCount; i++)
            _transparentInstanceOrder.Add(i);

        _transparentInstanceOrder.Sort((a, b) =>
        {
            float depthA = ComputeTransparentDepth(_instanceWorlds[a], primitive.LocalBoundsCenter);
            float depthB = ComputeTransparentDepth(_instanceWorlds[b], primitive.LocalBoundsCenter);
            return depthB.CompareTo(depthA);
        });
    }

    protected static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        var center = Vector3.Transform(localCenter, world);
        var app = DeviceServices.BaseApp;
        if (app == null)
            return center.Z;

        var forward = app.CameraTarget - app.CameraPos;
        if (forward.LengthSquared() < 1e-6f)
            forward = Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);

        return Vector3.Dot(center - app.CameraPos, forward);
    }

    /// <summary>Return the owned Primitives collection
    /// for base operations such as SyncAlpha / ReplaceTextureBySlot.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(_primitives);
    }

    /// <summary>
    /// Destroy the instance buffer and clear all primitive collections.
    /// Derived classes should release their own GPU resources before calling base.Dispose().
    /// </summary>
    public override void Dispose()
    {
        _primitives.Clear();
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();
        _transparentInstanceOrder.Clear();

        if (_instanceBuffer.Buffer.Handle != 0)
        {
            Device.ResourceManager.DestroyBuffer(_instanceBuffer);
            _instanceBuffer = default;
        }
    }
}
