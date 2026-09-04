// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using Image = SharpGLTF.Schema2.Image;

namespace Engine.Panels;

/// <summary>
/// Bottom-toolbar painting panel. The picker chooses a source glb asset, the asset is split at runtime
/// into one Mesh3D preview per mesh through the shared GLTFInstance pipeline, and those previews are laid
/// out in a horizontal row to the right of the select button.
///
/// Two display rules matter here. First, previews are not occluded by the toolbar border because the border
/// uses the Transparent path without writing depth, then the preview meshes render afterward through Opaque or Fade
/// and overwrite the toolbar pixels by normal depth testing. Second, previews stay visually screen-anchored even
/// though Mesh3D is world-space only: each frame their target pixel positions are projected back to camera rays,
/// then the meshes are placed at a fixed distance and scaled from the world-space cell width. That keeps them stable
/// on screen without adding a dedicated screen-space mesh mode.
/// </summary>
internal class Painting : Panel
{
    public override bool MouseOver
    {
        get
        {
            return border.MouseOver;
        }
    }

    SimplePicker simplePicker;

    Shape border;

    Sprite2D select;

    Input modelSize;

    List<Mesh3D> mesh3Ds = new List<Mesh3D>();

    Mesh3D _ghost, _ghostSource;   // Placement preview mesh and its source preview item; rebuild when selection changes.

    int _placedSerial;             // Running suffix for placed-instance names so logs and texture registration stay distinct.

    MovePanel movePanel;

    // -- Screen-anchoring parameters for the 3D preview row. --
    const float PreviewDistance = 3f;   // Fixed distance along the camera ray for preview placement.
    const float PreviewFill = 0.9f;     // Fraction of the world-space cell width occupied by the preview's longest side.
    const float PreviewSpacing = 1.6f;  // Center-to-center spacing multiplier between neighboring preview cells.

    Task<Extraction> loadTask;

    // Extraction cache for the current preview row. Texture sources must be preserved here because preview
    // Surface TextureOverride slots are consumed and cleared during Load, so placed meshes cannot clone them back out later.
    Extraction _extraction;

    // Preview item to extraction index mapping. BuildMeshes may skip empty geometry, so mesh3Ds indices do not always match extraction indices.
    readonly Dictionary<Mesh3D, int> _extractionIndex = new Dictionary<Mesh3D, int>();

    internal Painting()
    {
        RenderDomain = RenderDomain.Overlay;

        border = new Shape()
        {
            RenderDomain = RenderDomain.Scene,
            Type = ShapeType.Dot,
            Alpha = 0.7f,
            Color = Season.Basic.Colors.White,
            Height = 100
        };
        AddControl(border);

        select = new Sprite2D()
        {
            Name = @"Assets/Arrow.png",
            Clock = 180,
            OnClick = () =>
            {
                var sources = new List<Season.Entities.EData>()
                {
                    new Season.Entities.EData()
                    {
                        Key = "background_mountains.glb",
                        Title = "background_mountains.glb"
                    },
                    new Season.Entities.EData()
                    {
                        Key = "Rocks.glb",
                        Title = "Rocks.glb"
                    }
                };

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        var picked = simplePicker.Results?.Count > 0 ? simplePicker.Results[0] : null;

                        if (picked != null)
                        {
                            // Extract on a background thread, then let the next main-thread Update harvest the result and build controls.
                            // This follows the same contract as Mountains and Rocks, and the latest click wins.
                            loadTask = Task.Run(() => ExtractAsync(picked.Key));
                        }

                        simplePicker.OnClose?.Invoke();
                    },
                    OnClose = () =>
                    {
                        RemovePanel(simplePicker);
                        simplePicker = null;
                    }
                };
                AddPanel(simplePicker);
            }
        };
        AddControl(select);

        modelSize = new Input()
        {
            Text = "1",
            Alignment = Season.Controls.TextAlignment.Center,
            OnAction = async () =>
            {
                var result = await DeviceServices.Dialog.ShowKeyboard("Model size".Translate(), "", new string[] { "OK".Translate(), "Cancel".Translate() }, modelSize.Text);

                if (result is not null && float.TryParse(result, out float size))
                {
                    modelSize.Text = size.ToString();
                }
            }
        };
        AddPanel(modelSize);

        movePanel = new MovePanel()
        {
            MoveType = MoveType.X,
            Color = Season.Basic.Colors.LightBlack,
            DisplayLine = true,
            EnableStartMoving = true,
            EnableEndMoving = true
        };
        AddPanel(movePanel);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);

        if (simplePicker != null)
        {
            if (simplePicker.Update(time, alpha: alpha, posX: (int)select.PosX, posY: (int)select.PosY + 50))
            {
                result = true;
            }
        }

        // Once background extraction finishes, harvest it on the main thread by building Mesh3D controls and calling AddControl.
        if (loadTask != null && loadTask.IsCompleted)
        {
            if (loadTask.IsCompletedSuccessfully)
                BuildMeshes(loadTask.Result);
            else
                App.Instance.AddLog(LogType.Error, $"Painting ExtractAsync failed: {loadTask.Exception?.GetBaseException()}");

            loadTask = null;
        }

        var size = 70;

        border.Update(time, posY: App.Instance.ExtendResolution.Y - border.Height, width: App.Instance.ExtendResolution.X);

        select.Color = select.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.Black;
        if (select.Update(time, posX: size, posY: border.PosY + (border.Height - select.Height) / 2, width: size, height: size))
        {
            result = true;
        }

        modelSize.Color = modelSize.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.Black;
        if (modelSize.Update(time, posX: App.Instance.ExtendResolution.X - size - size, posY: border.PosY + (border.Height - modelSize.Height) / 2 + 10, width: size, height: size))
        {
            result = true;
        }

        movePanel.PosX = select.PosX + (select.Width ?? 0f);
        movePanel.PosY = border.PosY;
        movePanel.Width = modelSize.PosX - movePanel.PosX;
        movePanel.Height = border.Height;
        // Scrollable width equals the full preview-row width, with a 1000 minimum so the row remains scrollable.
        movePanel.SizeX = Math.Max(1000, 30 + size + (mesh3Ds.Count - 1) * size * PreviewSpacing) + size / 2;
        movePanel.SizeY = (movePanel.Height ?? 0) + size; // (SourcesView.Count > 0 ? sourcesImages[SourcesView.Count - 1].PosY - sourcesImages[0].PosY : 0) + 150;
        if (movePanel.SizeX < movePanel.Width)
        {
            movePanel.SizeX = movePanel.Width ?? 0;
        }
        if (movePanel.Update(time))
        {
            //return true;
        }

        // -- Mesh3D preview row: laid out to the right of select and screen-anchored as described above. --
        float slotY = border.PosY + (border.Height ?? 0f) / 2f;
        float rowX = select.PosX + (select.Width ?? 0f); // Row start, flush with the right edge of select.

        var camera = App.Instance.Camera;
        float resX = App.Instance.ExtendResolution.X;
        float resY = App.Instance.ExtendResolution.Y;

        // Use a ray pair each frame to estimate the world-space width of one preview cell at PreviewDistance.
        float cellWorld = 0f;
        if (Season.Rendering.Picking.ScreenPointToRay(rowX, slotY, camera, resX, resY, out var originL, out var dirL)
            && Season.Rendering.Picking.ScreenPointToRay(rowX + size, slotY, camera, resX, resY, out var originR, out var dirR))
        {
            cellWorld = Vector3.Distance(originL + dirL * PreviewDistance, originR + dirR * PreviewDistance);
        }

        for (var i = 0; i < mesh3Ds.Count; i++)
        {
            var mesh3D = mesh3Ds[i];

            if (mesh3D == null)
                continue;

            mesh3D.Highlight.Outline = false;

            var mesh3DX = rowX + ((float)i + 0.5f) * (float)size * PreviewSpacing - movePanel.Scroll;

            if (mesh3D.Ready
                && cellWorld > 0f
                && Season.Rendering.Picking.ScreenPointToRay(mesh3DX, slotY, camera, resX, resY, out var origin, out var dir))
            {
                var pos = origin + dir * PreviewDistance;
                mesh3D.PosX = pos.X;
                mesh3D.PosY = pos.Y;
                mesh3D.PosZ = pos.Z;

                // Uniform scaling: fit the longest side into the world-space cell width times the fill ratio.
                var local = mesh3D.LocalSize;
                float maxLocal = MathF.Max(local.X, MathF.Max(local.Y, local.Z));
                if (maxLocal > 1e-6f)
                {
                    float s = cellWorld * PreviewFill / maxLocal;
                    mesh3D.Width = local.X * s;
                    mesh3D.Height = local.Y * s;
                    mesh3D.Depth = local.Z * s;
                }

                mesh3D.Rotation = Quaternion.CreateFromYawPitchRoll(time * 0.5f, 0f, 0f);   // Slow turntable preview around the bounds center.

                if (mesh3DX < movePanel.PosX + size / 2)
                {
                    mesh3D.Alpha = 0f;
                }
                else if (mesh3DX > movePanel.PosX + movePanel.Width - size / 2)
                {
                    mesh3D.Alpha = 0f;
                }
                else
                {
                    mesh3D.Alpha = 1f;   // Show only after the first valid anchoring, avoiding a flash at the world origin.

                    if (mesh3DX - size / 2 < TouchService.PoX && TouchService.PoX < mesh3DX + size / 2
    && slotY - size / 2 < TouchService.PoY && TouchService.PoY < slotY + size / 2)
                    {
                        mesh3D.MouseOver = true;
                    }
                    else
                    {
                        mesh3D.MouseOver = false;
                    }

                    if (mesh3D.MouseOver || mesh3D.Selected)
                    {
                        mesh3D.Highlight.Outline = true;
                    }
                    else
                    {

                    }
                }
            }

            if (mesh3D.Update(time))
            {
                result = true;
            }
        }

        if (border.MouseOver && mesh3Ds.All(me => !me.MouseOver) && TouchService.IsReleased)
        {
            mesh3Ds.ForEach(me => me.Selected = false);
        }

        // -- Placement flow: selected preview item -> hovering ghost -> click or tap to place into App.meshes. --
        if (UpdatePlacement(time))
        {
            result = true;
        }

        return result;
    }

    // -- Background extraction: glb -> per-mesh geometry plus four texture slots through the shared GLTFInstance pipeline. --

    class Extraction
    {
        internal string Stem;   // Asset stem without extension, used only for control naming.
        internal (Vertex[] Vertices, ushort[] Indices)[] Geometries;
        internal TextureUpdateSource[] BaseColor;
        internal TextureUpdateSource[] MetallicRoughness;
        internal TextureUpdateSource[] Normal;
        internal TextureUpdateSource[] Occlusion;
    }

    static async Task<Extraction> ExtractAsync(string key)
    {
        var model = await GLTFInstance.LoadGlbAsync($"Assets/{key}");

        var meshNodes = GLTFInstance.GetMeshNodes(model);

        var extraction = new Extraction
        {
            Stem = Path.GetFileNameWithoutExtension(key),
            Geometries = new (Vertex[] Vertices, ushort[] Indices)[meshNodes.Count],
            BaseColor = new TextureUpdateSource[meshNodes.Count],
            MetallicRoughness = new TextureUpdateSource[meshNodes.Count],
            Normal = new TextureUpdateSource[meshNodes.Count],
            Occlusion = new TextureUpdateSource[meshNodes.Count],
        };

        var savedImages = new Dictionary<Image, TextureUpdateSource>();

        for (int i = 0; i < meshNodes.Count; i++)
        {
            var node = meshNodes[i];
            var primitive = node.Mesh.Primitives[0];

            // If the asset already has TANGENT data, baking only flips handedness in W; otherwise, such as Rocks.glb, generate tangents with Lengyel.
            bool hasTangents = primitive.VertexAccessors.ContainsKey("TANGENT");
            extraction.Geometries[i] = GLTFInstance.BakeMeshNode(node, generateTangents: !hasTangents);

            await ExtractMeshTextures(primitive.Material, i, extraction, savedImages);
        }

        return extraction;
    }

    /// <summary>Locates the four texture slots from a mesh material and decodes them into in-memory pixel sources, deduplicating shared image references and reusing the same source when AO and MR come from one image.</summary>
    static async Task ExtractMeshTextures(Material material, int index, Extraction extraction, Dictionary<Image, TextureUpdateSource> savedImages)
    {
        // Standard PBR assets store the base color in BaseColor, while specGloss assets such as background_mountains.glb fall back to Diffuse.
        var baseColorImage = GLTFInstance.FindChannelImage(material, KnownChannel.BaseColor)
                          ?? GLTFInstance.FindChannelImage(material, KnownChannel.Diffuse);
        var metallicRoughnessImage = GLTFInstance.FindChannelImage(material, KnownChannel.MetallicRoughness);
        var normalImage = GLTFInstance.FindChannelImage(material, KnownChannel.Normal);
        var occlusionImage = GLTFInstance.FindChannelImage(material, KnownChannel.Occlusion);

        extraction.BaseColor[index] = await GLTFInstance.ExtractEmbeddedImageAsync(baseColorImage, savedImages);
        extraction.MetallicRoughness[index] = await GLTFInstance.ExtractEmbeddedImageAsync(metallicRoughnessImage, savedImages);
        extraction.Normal[index] = await GLTFInstance.ExtractEmbeddedImageAsync(normalImage, savedImages);
        extraction.Occlusion[index] = occlusionImage == metallicRoughnessImage
            ? extraction.MetallicRoughness[index]
            : await GLTFInstance.ExtractEmbeddedImageAsync(occlusionImage, savedImages);
    }

    /// <summary>Harvests extraction results on the main thread by removing the previous preview row, creating one Mesh3D per mesh, and calling AddControl for each.</summary>
    void BuildMeshes(Extraction extraction)
    {
        _extraction = extraction;        // Preserve texture sources here because preview slots are consumed during Load and placed meshes must restore them from this cache.
        _extractionIndex.Clear();

        for (int i = mesh3Ds.Count - 1; i >= 0; i--)
            RemoveControl(mesh3Ds[i]);   // RemoveControl includes Dispose, letting platform resources be reclaimed by (Name, ID).
        mesh3Ds.Clear();

        for (int i = 0; i < extraction.Geometries.Length; i++)
        {
            var geometry = extraction.Geometries[i];
            if (geometry.Vertices == null || geometry.Vertices.Length == 0)
                continue;

            var mesh = new Mesh3D()
            {
                RenderDomain = RenderDomain.Scene,
                Name = $"painting_{extraction.Stem}_{i}",
                // Preview meshes do not cast shadows so toolbar thumbnails do not contaminate CSM or GI.
                CastShadows = false,
                // Keep hidden until the first valid screen anchoring to avoid a world-origin flash.
                Alpha = 0f
            };

            mesh.OnClick = () =>
            {
                var pre = mesh.Selected;

                foreach (var mesh3D in mesh3Ds)
                {
                    mesh3D.Selected = false;
                }

                mesh.Selected = !pre;
            };

            mesh.Surfaces.Add(new Surface()
            {
                Vertices = geometry.Vertices,
                Indices = geometry.Indices,
                TextureOverride = extraction.BaseColor[i],
                MetallicRoughnessTextureOverride = extraction.MetallicRoughness[i],
                NormalTextureOverride = extraction.Normal[i],
                OcclusionTextureOverride = extraction.Occlusion[i],
                MetallicFactor = 1f,      // With an MR texture, the factor multiplies the texture channels, so use the glTF default of 1.
                RoughnessFactor = 1f,
                Unlit = false,            // Use PBR lighting so previews match the look of placed scene entities.
            });

            mesh3Ds.Add(mesh);
            _extractionIndex[mesh] = i;
            AddControl(mesh);   // Triggers RequestLoad so GPU resources are built asynchronously, then shown once ready and anchored in Update.
        }
    }

    // -- Placement: hover-following ghost plus click or tap to commit. --

    /// <summary>
    /// Placement driver. When selection changes it rebuilds the ghost mesh. During mouse hover with no button held,
    /// the ghost follows the pointer ray and snaps to the current hit point at alpha 0.6. When TouchService.IsReleased
    /// fires, one placement is committed at the pointer location and the release is consumed so ObjectPicker does not
    /// also treat the same click as a scene-selection action. The ghost is hidden when the pointer is unavailable,
    /// over the bottom bar, or while the picker is open.
    /// </summary>
    bool UpdatePlacement(float time)
    {
        var app = App.Instance;

        // Selected preview item. Preview OnClick already sets Selected, but geometry cannot be used until the preview is ready.
        Mesh3D selected = null;
        for (int i = 0; i < mesh3Ds.Count; i++)
        {
            if (mesh3Ds[i] != null && mesh3Ds[i].Selected && mesh3Ds[i].Ready)
            {
                selected = mesh3Ds[i];
                break;
            }
        }

        // Rebuild the ghost whenever the selected source changes, including after a placement clears selection.
        if (!ReferenceEquals(_ghostSource, selected))
        {
            if (_ghost != null)
            {
                RemoveControl(_ghost);   // Includes Dispose, so platform resources are reclaimed by (Name, ID).
                _ghost = null;
            }

            _ghostSource = selected;

            if (selected != null)
            {
                _ghost = BuildPlacementMesh(selected, ghost: true);
                AddControl(_ghost);   // Triggers RequestLoad for asynchronous GPU resource creation.
            }
        }

        if (_ghost == null || _ghostSource == null)
            return false;

        // Pointer validity: disallow placement when the pointer is missing, over the bottom bar, or while the picker is open.
        var pointerX = TouchService.PoX ?? 0;
        var pointerY = TouchService.PoY ?? 0;
        bool pointerValid = TouchService.PoX != null && TouchService.PoY != null;
        bool overBar = pointerValid && pointerY >= border.PosY;

        bool rayValid = false;
        Vector3 rayOrigin = default, rayDirection = default;
        if (pointerValid && !overBar && simplePicker == null
            && Season.Rendering.Picking.ScreenPointToRay(pointerX, pointerY, app.Camera,
                app.ExtendResolution.X, app.ExtendResolution.Y, out rayOrigin, out rayDirection))
        {
            rayValid = true;
        }

        // Hover follow: move the ghost with the hit point while the mouse moves freely; do not follow during a touch hold.
        if (rayValid && !TouchService.IsDown)
        {
            if (TryFindPlacement(rayOrigin, rayDirection, out var point))
            {
                _ghost.PosX = point.X;
                _ghost.PosY = point.Y + (float)(_ghost.Height ?? 0f) * 0.5f;   // Snap by lifting the center anchor half a height so the bottom rests on the surface.
                _ghost.PosZ = point.Z;
                _ghost.Alpha = 0.6f;   // Semi-transparent preview.
            }
            else
            {
                _ghost.Alpha = 0f;
            }
        }
        else
        {
            _ghost.Alpha = 0f;
        }

        var result = false;

        // Commit placement on click or tap. Clone the entity, snap it to the hit point, add it to App.meshes,
        // register it with ObjectPicker, and clear the preview selection so placement does not repeat continuously.
        if (rayValid && TouchService.IsReleased)
        {
            TouchService.IsReleased = false;   // Consume the release so ObjectPicker does not also react to the same click.

            if (TryFindPlacement(rayOrigin, rayDirection, out var point))
                CommitPlacement(point);

            result = true;
        }

        _ghost.Update(time);

        return result;
    }

    /// <summary>
    /// Builds a placement mesh from a preview item for either the ghost or the final placed entity.
    /// Geometry and material factors are shallow-cloned from the preview Surface, while texture sources
    /// are restored from the extraction cache because preview TextureOverride slots have already been consumed
    /// and cleared during preview loading. Size is re-normalized so the longest side becomes one unit,
    /// since the tiny preview-row scaling is not reused for scene placement. Ghost meshes stay semi-transparent
    /// and do not cast shadows, while placed entities do.
    /// </summary>
    Mesh3D BuildPlacementMesh(Mesh3D source, bool ghost)
    {
        var mesh = new Mesh3D()
        {
            Highlight = new Highlight() { Style = HighlightStyle.Wireframe },
            RenderDomain = RenderDomain.Scene,
            Name = ghost ? $"painting_ghost_{source.Name}" : $"painting_placed_{source.Name}_{++_placedSerial}",
            CastShadows = !ghost,   // Ghost previews do not cast shadows; placed entities do.
            Alpha = 0f,             // Stay hidden until snapped, preventing a world-origin flash.
            Rotation = Quaternion.Identity,   // Placement meshes are static; they do not inherit the preview turntable rotation.
        };

        // Restore texture sources from the extraction cache because the preview Surface slots were consumed and cleared during Load.
        int extractionIndex = -1;
        bool hasSources = _extraction != null && _extractionIndex.TryGetValue(source, out extractionIndex);
        for (int i = 0; i < source.Surfaces.Count; i++)
        {
            var surface = CloneSurface(source.Surfaces[i]);
            if (hasSources && extractionIndex < _extraction.BaseColor.Length)
            {
                surface.TextureOverride = _extraction.BaseColor[extractionIndex];
                surface.MetallicRoughnessTextureOverride = _extraction.MetallicRoughness[extractionIndex];
                surface.NormalTextureOverride = _extraction.Normal[extractionIndex];
                surface.OcclusionTextureOverride = _extraction.Occlusion[extractionIndex];
            }
            mesh.Surfaces.Add(surface);
        }

        var size = 1f;
        float.TryParse(modelSize.Text, out size);

        ApplyUnitSize(mesh, source, size);

        return mesh;
    }

    /// <summary>Normalizes placement-mesh size so the longest side becomes one world unit while preserving aspect ratio.</summary>
    static void ApplyUnitSize(Mesh3D target, Mesh3D source, float size)
    {
        var local = source.LocalSize;
        float maxLocal = MathF.Max(local.X, MathF.Max(local.Y, local.Z));
        if (maxLocal <= 1e-6f)
            return;

        float s = size / maxLocal;
        target.Width = local.X * s;
        target.Height = local.Y * s;
        target.Depth = local.Z * s;
    }

    /// <summary>Shallow-clones a Surface while sharing geometry and texture-source references. If the source TextureOverride slots were already consumed and cleared during Load, the clone inherits those empty slots; BuildPlacementMesh later restores them from the extraction cache.</summary>
    static Surface CloneSurface(Surface source) => new Surface()
    {
        Vertices = source.Vertices,
        Indices = source.Indices,
        TextureOverride = source.TextureOverride,
        MetallicRoughnessTextureOverride = source.MetallicRoughnessTextureOverride,
        NormalTextureOverride = source.NormalTextureOverride,
        OcclusionTextureOverride = source.OcclusionTextureOverride,
        EmissiveTextureOverride = source.EmissiveTextureOverride,
        BaseColor = source.BaseColor,
        MetallicFactor = source.MetallicFactor,
        RoughnessFactor = source.RoughnessFactor,
        EmissiveFactor = source.EmissiveFactor,
        Alpha = source.Alpha,
        Mode = source.Mode,
        AlphaCutoff = source.AlphaCutoff,
        DoubleSided = source.DoubleSided,
        Unlit = source.Unlit,
    };

    /// <summary>
    /// Placement hit test. Intersect the pointer ray against scene geometry and take the nearest hit among
    /// the mountain ring, shoreline rocks, already placed entities, and the grass surface. If nothing is hit,
    /// fall back to the Y=0 plane outside the grass region. Distances use the same world-space metric as ObjectPicker
    /// so results are comparable across controls and instances.
    /// </summary>
    bool TryFindPlacement(Vector3 origin, Vector3 direction, out Vector3 point)
    {
        point = default;
        float best = float.MaxValue;
        bool hit = false;

        void Consider(float distance)
        {
            if (distance < best)
            {
                best = distance;
                hit = true;
            }
        }

        var app = App.Instance;

        if (app.mountains?.mountainFields != null)
        {
            for (int i = 0; i < app.mountains.mountainFields.Length; i++)
            {
                var field = app.mountains.mountainFields[i];
                if (field != null && field.TryPickInstanceSurface(origin, direction, out _, out var d))
                    Consider(d);
            }
        }

        if (app.rocks?.rockFields != null)
        {
            for (int i = 0; i < app.rocks.rockFields.Length; i++)
            {
                var field = app.rocks.rockFields[i];
                if (field != null && field.TryPickInstanceSurface(origin, direction, out _, out var d))
                    Consider(d);
            }
        }

        for (int i = 0; i < app.meshes.Count; i++)
        {
            var mesh = app.meshes[i];
            if (mesh != null && !mesh.IsDisposed && mesh.TryPickSurface(origin, direction, out var d))
                Consider(d);
        }

        // Treat the grass surface as a first-class candidate. When the cursor visually points at grass,
        // its hit point is closer than the buried mountain slope underneath, so the visible surface naturally wins.
        var grass = app.ground?.grass;
        if (grass != null && grass.TryPickSurface(origin, direction, out var gd))
            Consider(gd);

        // If no scene geometry is hit, fall back to the Y=0 plane, which covers areas such as sea or far background.
        if (!hit && direction.Y < -1e-6f)
        {
            float t = -origin.Y / direction.Y;
            if (t >= 0f)
                Consider(t);
        }

        if (!hit)
            return false;

        point = origin + direction * best;
        return true;
    }

    /// <summary>
    /// Commits one placement by cloning the selected entity, snapping it to the hit point with its bottom face on the surface,
    /// adding it to App.meshes so App.Update drives it, and selecting it through ObjectPicker so the property board can edit
    /// PosX, PosY, PosZ, Width, Height, and Depth immediately. Selection is then cleared so one preview click creates only one placement.
    /// </summary>
    void CommitPlacement(Vector3 point)
    {
        var placed = BuildPlacementMesh(_ghostSource, ghost: false);
        placed.Alpha = 1f;

        placed.PosX = point.X;
        placed.PosY = point.Y + (float)(placed.Height ?? 0f) * 0.5f;   // Snap by lifting the center anchor half a height so the bottom rests on the surface.
        placed.PosZ = point.Z;

        var app = App.Instance;
        app.meshes.Add(placed);
        app.AddControl(placed);   // Triggers RequestLoad for asynchronous GPU resource creation.

        // Register and lock selection so the property panel is ready for immediate editing.
        app.picker?.Select(placed);

        // Clear the preview selection after placement so the ghost is destroyed on the next frame and placement does not chain.
        for (int i = 0; i < mesh3Ds.Count; i++)
        {
            if (mesh3Ds[i] != null)
                mesh3Ds[i].Selected = false;
        }
    }
}
