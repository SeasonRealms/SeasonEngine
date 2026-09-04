// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using System.Runtime.InteropServices;

namespace Season.Platforms.Shared.Apple.Metal;

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
/// Base class for GPU-instanced primitive groups on Metal.
/// It centralizes instance-buffer management, transparent sorting, and three-bucket grouped rendering.
/// Derived types include MTLInstancedModel for glTF template cloning
/// and MTLInstancedMesh3D for generated geometry.
/// </summary>
internal abstract class MTLInstancedPrimitiveGroup : MTLPrimitiveGroup
{
    protected readonly List<PrimitiveData> _primitives = new();
    protected readonly List<PrimitiveData> _opaquePrimitives = new();
    protected readonly List<PrimitiveData> _transparentPrimitives = new();
    protected readonly List<int> _transparentInstanceOrder = new();

    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();
    protected Matrix4x4[] _instanceWorlds = Array.Empty<Matrix4x4>();

    protected IMTLBuffer _instanceBuffer = null!;
    protected int _instanceCapacity;
    protected int _instanceCount;

    /// <summary>Whether any instance enabled Wireframe highlighting in the current frame.
    /// This is written during Update and used as a zero-cost gate during Draw.
    /// Derived Update methods reset it at the start of every frame.</summary>
    protected bool _wireframeActive;

    // === Unified highlighting: per-instance Outline2D state, finalized by derived Update methods through SetOutline2DState ===

    /// <summary>Compressed instance indices that enabled Outline2D in the current frame, rebuilt on every Update.</summary>
    protected readonly List<int> _outline2DInstances = new();

    /// <summary>Per-instance outline colors aligned with the slots in _outline2DInstances, rebuilt every frame so the per-slot mask can fetch colors by slot.</summary>
    protected readonly List<Vector4> _outline2DInstanceColors = new();

    /// <summary>Outline color of the first instance that enabled Outline2D in the current frame, used as the shared color for per-instance masks.</summary>
    protected Vector4 _outline2DInstanceColor;

    /// <summary>Outline width of the first instance that enabled Outline2D in the current frame.</summary>
    protected float _outline2DInstanceWidth;

    /// <summary>Whether host-level Outline2D is active in the current frame. When active, it uses the full mask and ignores the per-instance list.</summary>
    protected bool _outline2DHostActive;

    /// <summary>
    /// Contract clause 8(c) of 2-3: double-buffered instance streams.
    /// The write side at [_instanceWriteIndex] is fully rewritten every frame,
    /// while the opposite side contains the previous-frame per-instance world transforms and morph weights.
    /// That opposite side can be bound directly as the prev source at VS buffer(9),
    /// with no extra CPU copy.
    /// When capacity changes, both sides are rebuilt and cleared together,
    /// where r3.w == 0 acts as the "no history" sentinel.
    /// </summary>
    protected readonly IMTLBuffer[] _instanceBuffers = new IMTLBuffer[2];
    protected int _instanceWriteIndex;

    /// <summary>The previous-instance stream for the current frame.
    /// If no history exists yet, it falls back to the write side so the shader uses current values through the r3.w == 0 sentinel.</summary>
    protected IMTLBuffer PrevInstanceBuffer => _instanceBuffers[_instanceWriteIndex ^ 1] ?? _instanceBuffer;

    protected MTLInstancedPrimitiveGroup(string name)
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

        if (_instanceBuffers[0] != null && _instanceCapacity == count)
            return;

        // On capacity changes, rebuild and clear both sides together.
        // After clearing, r3.w == 0 makes the shader fall back to the current world,
        // preventing stale indices on the old side from generating fake velocity when instance counts change.
        nuint size = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * count);
        for (int i = 0; i < 2; i++)
        {
            _instanceBuffers[i]?.Dispose();
            _instanceBuffers[i] = Device.ResourceManager.CreateBuffer(size);
            unsafe
            {
                new Span<byte>((void*)_instanceBuffers[i].Contents, (int)size).Clear();
            }
        }

        _instanceCapacity = count;
    }

    /// <summary>
    /// Advances the write side of the double-buffered instance stream for 2-3 and uploads current-frame data.
    /// Derived Update methods call this at the end,
    /// after which <see cref="_instanceBuffer"/> points to the current-frame write side
    /// and <see cref="PrevInstanceBuffer"/> points to the previous-frame side.
    /// </summary>
    protected void FlipAndUploadInstanceStream(InstanceTransformData[] data, int count)
    {
        _instanceWriteIndex ^= 1;
        _instanceBuffer = _instanceBuffers[_instanceWriteIndex];
        Device.ResourceManager.UpdateBuffer(_instanceBuffer, new ReadOnlySpan<InstanceTransformData>(data, 0, count));
    }

    public new void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var enc = Device.GraphicsEncoder;
        bool forceFadeByAlpha = _currentAlpha < 1.0f;
        int fi = Device.FrameIndex;

        // 1. Opaque, using the Opaque PSO when overall alpha is at least 1, otherwise using Fade.
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.SetPipeline(enc, forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    _instanceBuffer, 0, (nuint)_instanceCount, 0,
                    PrevInstanceBuffer);
        }

        // 2. Transparent, for true BLEND materials drawn per instance from back to front.
        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            BuildTransparentInstanceOrder(primitive);

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];
                nuint instOffset = (nuint)(Marshal.SizeOf<InstanceTransformData>() * instanceIndex);
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                    enc.SetCullMode(MTLCullMode.Front);
                    Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                        primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                        LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                        MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                        primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                        _instanceBuffer, instOffset, 1, 0,
                        PrevInstanceBuffer);
                }

                Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                enc.SetCullMode(MTLCullMode.Back);
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    _instanceBuffer, instOffset, 1, 0,
                    PrevInstanceBuffer);
            }
        }

        // Unified highlighting for per-instance bounds boxes and wireframe shell boxes.
        // It uses the instances enabled this frame, draws transparent faces in two passes plus opaque edges,
        // and runs after all surfaces have finished.
        if (_boundsActive)
            DrawBoundsBoxes();
        if (_wireframeActive)
            DrawShellBoxes(_instanceBuffer, PrevInstanceBuffer);
    }

    /// <summary>
    /// Instanced shadow rendering for 1-5, drawing all instances in one draw call.
    /// Only opaque buckets are rendered because true BLEND materials do not cast shadows under contract clause 7.
    /// The shadow PSO is already bound by RenderShadowPass, so this path neither switches PSOs nor sorts.
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;

        // When b2 and t5, meaning Metal slots 4 and 5, are identical within the group,
        // bind them only for the first primitive. See CanShareShadowMaterial.
        bool shareMaterial = CanShareShadowMaterial(_opaquePrimitives);
        bool materialBound = false;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.DrawShadowPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi], IdentityBoneBuffers[fi],
                primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                (nuint)primitive.Indices.Length, primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
                _instanceBuffer, 0, (nuint)_instanceCount, 0,
                bindMaterial: !shareMaterial || !materialBound);
            materialBound = true;
        }
    }

    /// <summary>
    /// Phase 4 Outline2D mask rendering.
    /// This is the default implementation for the instanced base class and is reused directly by MTLInstancedMesh3D,
    /// which uses the base-class _instanceBuffer.
    /// Host activation uses the full mask; otherwise rendering is per instance through _outline2DInstances.
    /// Only opaque buckets are drawn.
    /// The mask PSO and DSS are routed by SetPipeline from ActivePassId, mirroring VKInstancedPrimitiveGroup.DrawOutlineMask.
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        Pipeline.SetOutlineMaskColor(enc, _outline2DColor);

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.SetPipeline(enc, PipelineMode.Opaque, primitive.DoubleSided);
            enc.SetDepthStencilState(Pipeline.OutlineMaskDepthState);

            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    _instanceBuffer, 0, (nuint)_instanceCount, 0,
                    PrevInstanceBuffer);
            }
            else
            {
                for (int k = 0; k < _outline2DInstances.Count; k++)
                {
                    int idx = _outline2DInstances[k];
                    if ((uint)idx >= (uint)_instanceCount)
                        continue;
                    // Write this instance's own OutlineColor for the current slot
                    // so the per-slot mask can fetch color by slot.
                    Pipeline.SetOutlineMaskColor(enc, _outline2DInstanceColors[k]);
                    nuint instOffset = (nuint)(Marshal.SizeOf<InstanceTransformData>() * idx);
                    Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                        primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                        LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                        MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                        primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                        _instanceBuffer, instOffset, 1, 0,
                        PrevInstanceBuffer);
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

    /// <summary>Returns the owned primitive collection for base-class operations such as SyncAlpha and ReplaceTextureBySlot.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(_primitives);
    }

    /// <summary>
    /// Releases the instance buffers and clears all primitive collections.
    /// Derived classes should clean up their own GPU resources before calling base.Dispose().
    /// </summary>
    public override void Dispose()
    {
        _primitives.Clear();
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();
        _transparentInstanceOrder.Clear();

        // For 2-3, both sides of the double-buffered instance stream are owned by this class,
        // and _instanceBuffer is only an alias of the current write side.
        for (int i = 0; i < _instanceBuffers.Length; i++)
        {
            _instanceBuffers[i]?.Dispose();
            _instanceBuffers[i] = null!;
        }

        _instanceBuffer = null!;
    }
}
