// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Materials;   // KnownChannel
using SharpGLTF.Schema2;
// Avoid ambiguity with Microsoft.Maui.Controls.Image: in this file, Image always means the glTF image type.
using Image = SharpGLTF.Schema2.Image;

namespace Engine.Panels;

/// <summary>Coastal rock instance. Like Mountain, it inherits MeshInstanceTransform and only adds Yaw and Animation.</summary>
internal class Rock : MeshInstanceTransform
{
    /// <summary>Yaw angle around the Y axis in radians. Robots.Update converts it to a Rotation quaternion.</summary>
    internal float Yaw { get; set; }

    /// <summary>Animation clip name, matched against InstancedModel.AnimationNames. Null, empty, or missing names fall back to the default clip.</summary>
    internal string? Animation { get; set; }
}

/// <summary>
/// Coastal rock panel. At runtime it extracts 15 rock meshes from Rocks.glb, bakes node-chain transforms,
/// recenters them to the origin, and assigns one Surface to one InstancedMesh3D for GPU instancing. The asset
/// contains 5 large, 5 medium, and 5 small variants, ordered by Mesh.LogicalIndex. Each placed rock chooses
/// a variant, yaw, and scale, then straddles the shoreline so part of it sits in sand or grass and part of it reaches into the water.
///
/// Materials use standard PBR with baseColor, metallicRoughness, normal, and occlusion textures. Texture data is decoded
/// into in-memory pixel sources without writing files, and AO and MR reuse the same decoded source when the asset points both slots
/// at one image. Since the asset has no TANGENT data, baking generates tangents with Lengyel so normal maps still work.
///
/// Loading follows the BaseApp queue contract: Load extracts in the background, Update harvests on the main thread, and child controls
/// then become ready progressively. Placement is limited to the east shoreline and keeps the center corridor open so sunrise and sunset
/// sightlines are not blocked by rocks.
/// </summary>
// Explicit ILoadable declaration: this panel has background extraction work and can enter the RequestLoad queue.
// All other members come from Panel or BaseControl.
internal class Rocks : Panel, ILoadable
{
    // -- Shoreline placement parameters for the east-coast opening only. --
    const int Count = 64;              // Total placement draws before corridor filtering.
    const float CoastHalf = 75f;       // Half-width of the grass tile, so the east coast lies at x=+75 with z in [-75,75].
    const float StraddleJitter = 6f;   // Cross-shore offset jitter in meters along the +X outward normal.
    const float SinkMin = 0.05f;       // Minimum sink ratio times instance height, so lower edges stay buried.
    const float SinkRange = 0.15f;     // Additional randomized sink ratio range.
    const int Seed = 0x0C0A2026;       // Fixed seed so the layout stays reproducible.

    // Shared mountain-foot and center-corridor boundary with Mountains and Beach.
    // Leaving |PosZ| < 55 empty keeps a clear water sightline for sunrise and sunset along the +/-X axis.
    const float CorridorClearance = 55f;

    // Three scale ranges with uniform scaling through Width=Height=Depth=s.
    // The template bounds are about 1.5x2.6x1.5 units, producing roughly 4-5m tall large rocks,
    // 2.5-3.5m medium rocks, and 1.5-2.5m small rocks.
    static readonly (float Min, float Max)[] ScaleRanges = { (2.6f, 3.4f), (1.5f, 2.1f), (0.9f, 1.4f) };

    // Weighted size-class draw using rng.Next(10): 0-2 large, 3-6 medium, 7-9 small, giving a natural distribution.
    const int LargeThreshold = 3;
    const int MediumThreshold = 7;

    const string GlbPath = "Assets/Rocks.glb";

    /// <summary><c>groups[i]</c> maps to <c>rockFields[i]</c>: all instances using the i-th rock variant, fixed once during construction.</summary>
    List<Rock>[] groups = new List<Rock>[15];

    /// <summary>Template controls for rock variants. Each rock gets one InstancedMesh3D with one Surface, created only after extraction succeeds.</summary>
    internal InstancedMesh3D[] rockFields;

    /// <summary>Queued Load result written on a background thread and harvested by Update on the main thread; null means not finished yet or already failed.</summary>
    volatile Extraction extraction;

    internal Rocks()
    {
        groups[0] = new List<Rock>
        {
            new Rock()
            {
                PosX = 75,
                PosY = -1,
                PosZ = 64,
                Width = 5,
                Height = 10,
                Depth = 5
            },
            new Rock()
            {
                PosX = 75,
                PosY = -1,
                PosZ = 3,
                Width = 5,
                Height = 10,
                Depth = 5
            }
        };

        groups[1] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 61,
                Width = 3,
                Height = 5,
                Depth = 3
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 0,
                Width = 3,
                Height = 5,
                Depth = 3
            }
        };

        groups[2] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 58,
                Width = 3,
                Height = 5,
                Depth = 3
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -3,
                Width = 3,
                Height = 5,
                Depth = 3
            }
        };

        groups[3] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 55.5f,
                Width = 3,
                Height = 5,
                Depth = 3
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -6f,
                Width = 3,
                Height = 5,
                Depth = 3
            }
        };

        groups[4] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 53f,
                Width = 3,
                Height = 5,
                Depth = 3
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -9,
                Width = 3,
                Height = 5,
                Depth = 3
            }
        };

        groups[5] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 50f,
                Width = 5,
                Height = 5,
                Depth = 4
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -13,
                Width = 5,
                Height = 5,
                Depth = 4
            }
        };

        groups[6] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 46.5f,
                Width = 5,
                Height = 4,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -18,
                Width = 5,
                Height = 4,
                Depth = 5
            }
        };

        groups[7] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 42,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -23,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        groups[8] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 37,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -28,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        groups[9] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 33,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -33,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        groups[10] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 28,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -38,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        groups[11] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 23,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -43.5f,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        groups[12] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 18,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -49,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        groups[13] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 13,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -54,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        groups[14] = new List<Rock>
        {
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = 8,
                Width = 5,
                Height = 5,
                Depth = 5
            },
            new Rock()
            {
                PosX = 74,
                PosY = 1,
                PosZ = -59,
                Width = 5,
                Height = 5,
                Depth = 5
            }
        };

        //for (int i = 0; i < groups.Length; i++)
        //    groups[i] = new List<MeshInstanceTransform>();

        //var rng = new Random(Seed);
        //float perimeter = CoastHalf * 2f;   // East edge only: 150m.

        //for (int i = 0; i < Count; i++)
        //{
        //    // Spread instances by arc length with jitter so they do not look evenly queued.
        //    // Class, variant, scale, yaw, shoreline offset, and sink are randomized independently for layering.
        //    // Draw every random value before corridor filtering so later random sequences remain identical for the same seed.
        //    float posZ = -CoastHalf + (i + rng.NextSingle()) / Count * perimeter;

        //    int classRoll = rng.Next(10);
        //    int rockClass = classRoll < LargeThreshold ? 0 : classRoll < MediumThreshold ? 1 : 2;
        //    int variant = rockClass * 5 + rng.Next(5);

        //    var scaleRange = ScaleRanges[rockClass];
        //    float scale = scaleRange.Min + rng.NextSingle() * (scaleRange.Max - scaleRange.Min);
        //    float yaw = rng.NextSingle() * MathF.Tau;
        //    float off = (rng.NextSingle() * 2f - 1f) * StraddleJitter;
        //    float sink = SinkMin + rng.NextSingle() * SinkRange;

        //    // Skip the center corridor according to CorridorClearance.
        //    if (MathF.Abs(posZ) < CorridorClearance)
        //        continue;

        //    // Unified placement convention: the anchor is the template-bounds center. Under uniform scaling,
        //    // Height = scale and PosY = -sink * Height, so the lower edge sinks into sand or grass while the upper edge stays exposed.
        //    groups[variant].Add(new MeshInstanceTransform
        //    {
        //        PosX = CoastHalf + off,
        //        PosY = -sink * scale,
        //        PosZ = posZ,
        //        Width = scale,
        //        Height = scale,
        //        Depth = scale,
        //        Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f),
        //    });
        //}
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);

        // Extraction is driven by the load queue. No controls exist before it finishes, and after failure the panel stays empty.
        if (extraction == null)
            return result;

        // Harvest on the main thread by building variant controls and calling AddControl so children load progressively.
        if (rockFields == null)
        {
            if (IsDisposed)
                return result;

            Build(extraction);
        }

        // Instance sets are static after construction, so each frame only synchronizes matrices and materials.
        for (int i = 0; i < rockFields.Length; i++)
        {
            if (rockFields[i].Update(time))
                result = true;
        }

        return result;
    }

    /// <summary>
    /// Queue-driven Load entry point. AddPanel triggers RequestLoad, then Load extracts the glb into 15 variant geometries
    /// plus per-variant four-slot textures on a background thread. Returning true lets BaseApp mark the panel ready,
    /// while control creation is still harvested later on the main thread from Update.
    /// </summary>
    public override async Task<bool> Load()
    {
        try
        {
            extraction = await Task.Run(ExtractAsync);
        }
        catch (Exception ex)
        {
            App.Instance.AddLog(LogType.Error, $"Rocks ExtractAsync failed: {ex.GetBaseException()}");
            return false;
        }

        return !IsDisposed;
    }

    /// <summary>Builds N InstancedMesh3D controls on the main thread, each with one Surface using centered vertices and per-variant four-slot texture pixel sources.</summary>
    void Build(Extraction extraction)
    {
        int variantCount = Math.Min(groups.Length, extraction.Geometries.Length);
        if (variantCount == 0)
        {
            rockFields = Array.Empty<InstancedMesh3D>();   // Prevent Update from harvesting the same empty result repeatedly.
            return;
        }

        // If the asset exposes fewer variants than expected, merge the extra groups into the last available variant.
        for (int i = variantCount; i < groups.Length; i++)
        {
            groups[variantCount - 1].AddRange(groups[i]);
            groups[i].Clear();
        }

        rockFields = new InstancedMesh3D[variantCount];

        for (int i = 0; i < variantCount; i++)
        {
            var geometry = extraction.Geometries[i];

            var field = new InstancedMesh3D()
            {
                Name = $"rock{i + 1}",
                // Keep CastShadows at the default true: these are near-field solid rocks, not thin shoreline shells.
            };

            field.Surfaces.Add(new Surface()
            {
                Vertices = geometry.Vertices,
                Indices = geometry.Indices,
                TextureOverride = extraction.BaseColor[i],
                MetallicRoughnessTextureOverride = extraction.MetallicRoughness[i],
                NormalTextureOverride = extraction.Normal[i],
                OcclusionTextureOverride = extraction.Occlusion[i],
                MetallicFactor = 1f,          // With an MR texture, the factor multiplies the texture channels, so use the glTF default of 1.
                RoughnessFactor = 1f,
                Unlit = false,                // Use PBR lighting.
            });

            // Instances were already grouped by variant during construction, so attach them once and keep the layout static.
            for (int j = 0; j < groups[i].Count; j++)
                field.Instances.Add(groups[i][j]);

            AddControl(field);
            rockFields[i] = field;

            field.Highlight = new Highlight { Style = HighlightStyle.Wireframe };

            App.Instance.picker.InstancedTargets.Add(field);

            App.Instance.collider.InstancedObstacles.Add(field);
        }
    }

    // -- Background extraction: glb -> 15 variant geometries plus per-variant four-slot textures. --

    /// <summary>Extraction result: 15 centered variant geometries plus in-memory pixel caches for per-variant textures.</summary>
    class Extraction
    {
        internal (Vertex[] Vertices, ushort[] Indices)[] Geometries;
        internal TextureUpdateSource[] BaseColor;
        internal TextureUpdateSource[] MetallicRoughness;
        internal TextureUpdateSource[] Normal;
        internal TextureUpdateSource[] Occlusion;
    }

    static async Task<Extraction> ExtractAsync()
    {
        var model = await GLTFInstance.LoadGlbAsync(GlbPath);

        var extraction = new Extraction();

        // Geometry: every mesh-bearing node is one rock variant. Mesh.LogicalIndex order maps directly to
        // large, medium, and small groups, while the object-node names do not sort into that same order.
        var meshNodes = GLTFInstance.GetMeshNodes(model);

        int variantCount = meshNodes.Count;
        extraction.Geometries = new (Vertex[] Vertices, ushort[] Indices)[variantCount];
        extraction.BaseColor = new TextureUpdateSource[variantCount];
        extraction.MetallicRoughness = new TextureUpdateSource[variantCount];
        extraction.Normal = new TextureUpdateSource[variantCount];
        extraction.Occlusion = new TextureUpdateSource[variantCount];

        // Decode each image source only once. In this asset AO and MR share one image, so deduplicate by Image reference.
        var savedImages = new Dictionary<Image, TextureUpdateSource>();

        for (int i = 0; i < variantCount; i++)
        {
            var node = meshNodes[i];
            extraction.Geometries[i] = GLTFInstance.BakeMeshNode(node, generateTangents: true);   // The asset has no TANGENT data, so generate tangents while baking.
            await ExtractVariantTextures(node.Mesh.Primitives[0].Material, i, extraction, savedImages);
        }

        return extraction;
    }

    /// <summary>Finds the four texture slots from one variant material and decodes them into in-memory pixel caches, deduplicating shared image references and reusing one source when AO and MR point at the same image.</summary>
    static async Task ExtractVariantTextures(Material material, int variant, Extraction extraction, Dictionary<Image, TextureUpdateSource> savedImages)
    {
        var baseColorImage = GLTFInstance.FindChannelImage(material, KnownChannel.BaseColor);
        var metallicRoughnessImage = GLTFInstance.FindChannelImage(material, KnownChannel.MetallicRoughness);
        var normalImage = GLTFInstance.FindChannelImage(material, KnownChannel.Normal);
        var occlusionImage = GLTFInstance.FindChannelImage(material, KnownChannel.Occlusion);

        extraction.BaseColor[variant] = await GLTFInstance.ExtractEmbeddedImageAsync(baseColorImage, savedImages);
        extraction.MetallicRoughness[variant] = await GLTFInstance.ExtractEmbeddedImageAsync(metallicRoughnessImage, savedImages);
        extraction.Normal[variant] = await GLTFInstance.ExtractEmbeddedImageAsync(normalImage, savedImages);
        // When AO and MR share one image source, reuse the MR pixel source directly: AO reads the R channel while MR reads G and B.
        extraction.Occlusion[variant] = occlusionImage == metallicRoughnessImage
            ? extraction.MetallicRoughness[variant]
            : await GLTFInstance.ExtractEmbeddedImageAsync(occlusionImage, savedImages);
    }
}
