// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// HLSL variant of the D1 screen-space decal. The other three backends (GLSL / MSL / WGSL) each implement the same semantics in backend order,
/// and the frame buffer layout is governed by <see cref="DecalMath"/> -- here we only do the "reading the layout" part and no longer define the layout.
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// Accessors for the frame header and the decal array, shared by the vertex and pixel shaders.
    ///
    /// The vertex stage needs the two matrices other than world->local, and the pixel stage needs the unprojection matrix and pixel size, so this prefix must be
    /// concatenated into both the VS and PS sources; splitting it apart would make the two entry points disagree on how to read the same layout.
    /// </summary>
    public const string FrameHlsl = """
ByteAddressBuffer decalFrame : register(t0);
Texture2D<float> sceneDepth : register(t1);
Texture2D<float4> decalTexture : register(t2);
SamplerState decalLinearClamp : register(s0);
cbuffer DecalParams : register(b0) { uint4 decalParams; };

float4 DecalF(uint i) { return asfloat(decalFrame.Load4(i*16)); }
float4 DecalTransform(float4 p,uint i) { return p.x*DecalF(i)+p.y*DecalF(i+1)+p.z*DecalF(i+2)+p.w*DecalF(i+3); }
uint DecalOffset(uint index) { return 16u+index*12u; }
float2 DecalSize() { return DecalF(9).xy; }
""";

    /// <summary>
    /// Computes the 36 vertices of the projection box from <c>SV_VertexID</c>, without relying on a vertex buffer.
    ///
    /// All six faces are unified as "counter-clockwise when viewed from outside the box", so <c>CullMode.Front</c> (keeping only back faces) hits each pixel exactly once --
    /// this is the premise of the whole approach requiring neither the stencil nor per-draw sorting. The bit decomposition of the face index is described in the comment below; changing any one place requires updating the winding derivation accordingly.
    /// </summary>
    public const string VertexHlsl = """
float4 VSMain(uint id:SV_VertexID):SV_Position {
    // 6 vertices per face: two triangles (0,1,2)+(0,2,3), then k is mapped back to the corner index.
    uint face=id/6u, t=id-face*6u;
    uint tri=t/3u, c=t-tri*3u;
    uint k=c==0u?0u:(c==1u?(tri==0u?1u:2u):(tri==0u?2u:3u));
    // face>>1 = major axis (0=x,1=y,2=z), face&1 = positive/negative side. The minor axes are fixed to (a+1)%3 / (a+2)%3,
    // and the cross product of this arrangement is exactly +e_a, so the negative side must flip u to preserve "counter-clockwise from outside the box".
    uint axis=face>>1u, side=face&1u;
    uint u=((k^(k>>1u))&1u)^(side^1u);
    uint v=(k>>1u)&1u;
    float sa=side!=0u?0.5:-0.5, su=u!=0u?0.5:-0.5, sv=v!=0u?0.5:-0.5;
    float3 p=axis==0u?float3(sa,su,sv):(axis==1u?float3(sv,sa,su):float3(su,sv,sa));
    uint offset=DecalOffset(decalParams.x);
    return DecalTransform(DecalTransform(float4(p,1),offset+4u),4u);
}
""";

    /// <summary>
    /// Depth reconstruction + decal evaluation. Key points:
    /// <list type="bullet">
    /// <item>UV comes from the box's normalized coordinates, so the decal is "projected along the box's local Y" regardless of surface orientation;</item>
    /// <item>Derivatives are always taken in uniform control flow (both uv and world are computed up front); derivatives inside branches are undefined behavior at quad boundaries;</item>
    /// <item>Sampling uses <c>SampleGrad</c>: the box's back-face proxy triangle is far larger than the pixel footprint, so implicit derivatives would sample pixels outside the box along the box outline;</item>
    /// <item>Grazing fade-out uses the normal derived from depth and does not rely on SceneNormal. When the decal writes roughness, SceneNormal is attached to the color attachment,
    /// and the same resource cannot be a sampling source at the same time, so this path cannot be changed to read SceneNormal.</item>
    /// </list>
    ///
    /// Output convention: Modulate outputs "the factor multiplied into the target" (unchanged at 1), and Additive outputs an emissive color.
    /// Both rely on fixed-function blending to complete, and the pixel shader does not read SceneColor, so SceneColor does not need to be a samplable resource at the same time.
    ///
    /// Frame buffer fields: <c>wrap = (target roughness, whether to rewrite roughness, sort key, whether there is a texture)</c>,
    /// <c>tint.w</c> is opacity, and <c>shape.zw</c> are the feather and grazing thresholds.
    /// </summary>
    public const string PixelHlsl = """
void DecalEval(float4 position,out float4 color,out float coverage,out float roughness,out float roughWeight) {
    uint index=decalParams.x, offset=DecalOffset(index);
    int2 pixel=int2(position.xy);
    float depth=sceneDepth.Load(int3(pixel,0));
    float4 clip=float4(position.xy/DecalSize()*float2(2,-2)+float2(-1,1),depth,1);
    float4 w=DecalTransform(clip,0u);
    float3 world=w.xyz/w.w;
    float3 local=DecalTransform(float4(world,1),offset).xyz;
    float4 tint=DecalF(offset+8u), shape=DecalF(offset+9u), wrap=DecalF(offset+10u);
    float2 uv=local.xz*0.5+0.5;
    float2 uvDx=ddx(uv), uvDy=ddy(uv);
    float3 n=normalize(cross(ddx(world),ddy(world))+1e-8);
    float3 axis=normalize(DecalF(offset+5).xyz);
    float shade=0; float3 albedo=tint.rgb;
    if(depth<1.0 && all(abs(local)<=1.0)) {
        // Feathering: the fraction of shape.y inward from the box edge; grazing: fade out when the cosine of the angle between the normal and the projection axis drops below 1-shape.z.
        float edge=shape.y>0? saturate((1.0-max(abs(local.x),abs(local.z)))/max(shape.y,1e-3)) : 1.0;
        float grazing=shape.z>0? smoothstep(1.0-shape.z,1.0,abs(dot(n,axis))) : 1.0;
        float alpha=1.0;
        if(wrap.w>0.5) {
            float4 texel=decalTexture.SampleGrad(decalLinearClamp,uv,uvDx,uvDy);
            albedo=albedo*texel.rgb; alpha=texel.a;
        } else {
            // Procedural pattern when there is no texture: used to verify depth reconstruction; if the pattern follows the terrain relief, the reconstruction is correct.
            float checker=fmod(floor(uv.x*4)+floor(uv.y*4),2.0)<0.5?1.0:0.45;
            albedo=albedo*checker*(0.55+0.45*sin(length(local.xz)*18.0));
        }
        shade=saturate(edge*edge*(3.0-2.0*edge))*grazing*alpha;
    }
    coverage=shade*tint.w;
    // A decal that does not modify roughness must set the source alpha of RT1 to 0: RT1 blending is lerp(dst, src, src.a),
    // so a non-zero source alpha would needlessly pull regions that "did not request a roughness change" toward src.
    roughWeight=wrap.y>0.5?coverage:0.0;
    roughness=wrap.y>0.5?wrap.x:0.0;
    color=shape.x<0.5
        ? float4(lerp(1.0,albedo,coverage),1)
        : float4(albedo*coverage*shape.w,0);
}

float4 PSMain(float4 position:SV_Position):SV_Target {
    float4 color; float coverage, roughness, roughWeight;
    DecalEval(position,color,coverage,roughness,roughWeight);
    return color;
}

// s9: the normal target only enables the B channel in its write mask, so here we only need to send the target roughness to .z,
// while the octahedral normal (.xy) and reflection weight (.w) keep their original values from the attachment. Blending interpolates on the B channel using roughWeight.
struct PSRoughnessOut { float4 color:SV_Target0; float4 normal:SV_Target1; };

PSRoughnessOut PSMainRoughness(float4 position:SV_Position) {
    PSRoughnessOut o; float coverage, roughness, roughWeight;
    DecalEval(position,o.color,coverage,roughness,roughWeight);
    o.normal=float4(0,0,roughness,roughWeight);
    return o;
}
""";
}
