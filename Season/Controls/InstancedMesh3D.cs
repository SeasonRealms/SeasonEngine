// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// GPU instancing control for static meshes.
/// v1 targets only static Surface collections and does not include skinning, animation, or transparent sorting.
/// For the shared framework (instance collection / template box-sphere broad-phase culling / shadow gating / Draw gating),
/// see <see cref="InstancedMesh3DBase"/>.
/// </summary>
public class InstancedMesh3D : InstancedMesh3DBase
{
    public InstancedMesh3D()
    {
        // Name is normalized back to Control.Name (platform dictionaries use (Name, ID) as the key);
        // the default value is only for logs and cache keys, so it may be omitted.
        Name = "InstancedMesh3D";
    }

    /// <summary>Shared geometry and material templates.</summary>
    public List<Surface> Surfaces { get; } = new List<Surface>();

    protected override bool HasContent => Surfaces.Count > 0;

    public override async Task<bool> Load()
    {
        // 1-3: Aggregate the template local bounding box once during loading
        // (contract clause 2, shared across all four backends).
        var bounds = default(Bounds3D);
        bool first = true;
        foreach (var surface in Surfaces)
        {
            if (surface.Vertices == null || surface.Vertices.Length == 0)
                continue;

            var surfaceBounds = Bounds3D.FromVertices(surface.Vertices);
            bounds = first ? surfaceBounds : Bounds3D.Union(bounds, surfaceBounds);
            first = false;
        }
        TemplateLocalBounds = bounds;
        // Unified positioning model: InstancedMesh3D has no animation-driven expansion,
        // so the raw box is the aggregated box itself (anchor/per-axis scaling data source).
        TemplateLocalBoundsRaw = bounds;

        await Graphics.Instance.LoadInstancedMesh3D(this);

        return true;
    }

    public bool Update(float time, float? alpha = null)
    {
        var result = base.Update(time, alpha: alpha);

        if (Ready && HasContent && Enable)
        {
            // Unified positioning model: settle per-instance default dimensions
            // (zero dimension means template local size) before each backend calls
            // UpdateInstancedMesh3D, after matrices have already converged in BuildInstanceMatrix.
            for (int i = 0; i < Instances.Count; i++)
                SettleInstanceDimensions(Instances[i]);

            Graphics.Instance.UpdateInstancedMesh3D(this, time);
        }

        return result;
    }

    /// <summary>
    /// Per-instance surface-accurate picking (v2): broad-phase template-box culling per enabled instance,
    /// followed by per-Surface ray-triangle tests.
    /// Only real triangle surfaces can be hit, so empty template space is not selected by mistake;
    /// overlapping instances are resolved by nearest surface distance.
    /// Falls back to the base-class OBB path when no Surface data is available.
    /// </summary>
    public override bool TryPickInstanceSurface(Vector3 rayOrigin, Vector3 rayDirection, out MeshInstanceTransform hit, out float distance)
    {
        hit = null;
        distance = 0f;

        if (!Ready || !Enable || TemplateLocalBounds.Extents == Vector3.Zero)
            return false;

        bool hasData = false;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < Instances.Count; i++)
        {
            var instance = Instances[i];
            if (!instance.Enable)
                continue;

            var world = BuildInstanceMatrix(instance);
            if (!Picking.RayIntersectsObb(rayOrigin, rayDirection, world, TemplateLocalBounds, out _))
                continue;

            foreach (var surface in Surfaces)
            {
                if (surface.Vertices == null || surface.Vertices.Length < 3
                    || surface.Indices == null || surface.Indices.Length < 3)
                    continue;

                hasData = true;
                if (Picking.RayIntersectsTriangles(rayOrigin, rayDirection, world, surface.Vertices, surface.Indices, out var d)
                    && d < bestDistance)
                {
                    bestDistance = d;
                    hit = instance;
                }
            }
        }

        if (!hasData)
            return base.TryPickInstanceSurface(rayOrigin, rayDirection, out hit, out distance);

        if (hit != null)
            distance = bestDistance;

        return hit != null;
    }

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            Graphics.Instance.DrawInstancedMesh3D(this);

            result = true;
        }

        return result;
    }

    protected override void DrawShadowCore() => Graphics.Instance.DrawInstancedMesh3DShadow(this);

    public override void Dispose()
    {
        base.Dispose();
        Graphics.Instance.DisposeInstancedMesh3D(this);
    }
}
