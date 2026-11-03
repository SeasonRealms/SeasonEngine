// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// GLSL variant of the D1 screen-space decal (Vulkan / Linux / Android), semantically identical to the HLSL of <see cref="DecalShaders"/>.
///
/// The differences from the original are only at the syntax level, and three places that "look like typos" are kept as-is per Vulkan's rules:
/// <list type="bullet">
/// <item>The frame buffer is read as a <c>vec4</c> array rather than unpacked by byte offset: the layout of <see cref="DecalMath"/> is inherently float4 values stored contiguously, and
/// indexing <c>std430</c>'s <c>vec4[]</c> directly refers to the same memory as HLSL's <c>Load4(i*16)</c>;</item>
/// <item>glslang's simplified C interface only recognizes <c>main</c>, so "writing roughness" and "not writing roughness" must be two separate sources,
/// with <see cref="PixelCoreGlsl"/> concatenating the same evaluation function to avoid the two entry points diverging on how they read the layout;</item>
/// <item>Depth uses <c>texelFetch</c> instead of <c>texture</c>: the decal is a per-pixel projection, and any filtering would mix the depth at the decal's edge
/// into neighboring pixels, producing a ring of erroneous occlusion at the box boundary.</item>
/// </list>
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// Accessors for the frame buffer and pixel size, shared by the vertex and pixel stages. <c>DecalTransform</c> keeps HLSL's row-vector form
    /// (<c>p.x*F(i)+...</c>), because <see cref="DecalMath"/> copies <see cref="System.Numerics.Matrix4x4"/>
    /// into the buffer row by row, and indexing by <c>vec4</c> in GLSL retrieves exactly the same rows.
    /// </summary>
    const string CoreGlsl = """
layout(std430,set=0,binding=0) readonly buffer DecalFrame { vec4 decalF[]; };
layout(set=0,binding=1) uniform sampler2D sceneDepth;
layout(set=1,binding=0) uniform sampler2D decalTexture;
layout(push_constant) uniform DecalParams { uvec4 decalParams; };

vec4 DecalF(int i) { return decalF[i]; }
vec4 DecalTransform(vec4 p,int i) { return p.x*DecalF(i)+p.y*DecalF(i+1)+p.z*DecalF(i+2)+p.w*DecalF(i+3); }
int DecalOffset(uint index) { return 16+int(index)*12; }
vec2 DecalSize() { return DecalF(9).xy; }
""";

    /// <summary>
    /// The 36 vertices of the projection box. The bit decomposition of the face index is word-for-word identical to the HLSL version: all six faces are unified as "counter-clockwise when viewed from outside the box",
    /// and the backend then pairs this with <c>CullMode.Front</c> (keeping only back faces), hitting each pixel exactly once.
    /// </summary>
    public const string VertexGlsl = "#version 450\n" + CoreGlsl + """

void main() {
    uint id=uint(gl_VertexIndex);
    uint face=id/6u, t=id-face*6u;
    uint tri=t/3u, c=t-tri*3u;
    uint k=c==0u?0u:(c==1u?(tri==0u?1u:2u):(tri==0u?2u:3u));
    uint axis=face>>1u, side=face&1u;
    uint u=((k^(k>>1u))&1u)^(side^1u);
    uint v=(k>>1u)&1u;
    float sa=side!=0u?0.5:-0.5, su=u!=0u?0.5:-0.5, sv=v!=0u?0.5:-0.5;
    vec3 p=axis==0u?vec3(sa,su,sv):(axis==1u?vec3(sv,sa,su):vec3(su,sv,sa));
    int offset=DecalOffset(decalParams.x);
    gl_Position=DecalTransform(DecalTransform(vec4(p,1),offset+4),4);
}
""";

    /// <summary>
    /// Depth reconstruction + decal evaluation. The semantics correspond one-to-one to the same-named HLSL function, and the output convention is also the same:
    /// Modulate outputs "the factor multiplied into the target", Additive outputs an emissive color, and both are applied via fixed-function blending.
    /// <para>
    /// <c>gl_FragCoord</c> still uses a top-left origin with Y pointing down under a negative-height viewport, the same convention as DX's <c>SV_Position</c>,
    /// so the conversion from screen coordinates to NDC keeps <c>vec2(2,-2)</c> unchanged.
    /// </para>
    /// </summary>
    const string PixelCoreGlsl = "#version 450\n" + CoreGlsl + """

void DecalEval(out vec4 color,out float coverage,out float roughness,out float roughWeight) {
    uint index=decalParams.x;
    int offset=DecalOffset(index);
    float depth=texelFetch(sceneDepth,ivec2(gl_FragCoord.xy),0).x;
    vec4 clip=vec4(gl_FragCoord.xy/DecalSize()*vec2(2,-2)+vec2(-1,1),depth,1);
    vec4 w=DecalTransform(clip,0);
    vec3 world=w.xyz/w.w;
    vec3 local=DecalTransform(vec4(world,1),offset).xyz;
    vec4 tint=DecalF(offset+8), shape=DecalF(offset+9), wrap=DecalF(offset+10);
    vec2 uv=local.xz*0.5+0.5;
    vec2 uvDx=dFdx(uv), uvDy=dFdy(uv);
    vec3 n=normalize(cross(dFdx(world),dFdy(world))+vec3(1e-8));
    vec3 axis=normalize(DecalF(offset+5).xyz);
    float shade=0.0; vec3 albedo=tint.rgb;
    if(depth<1.0 && all(lessThanEqual(abs(local),vec3(1.0)))) {
        // Feathering: the fraction of shape.y inward from the box edge; grazing: fade out when the cosine of the angle between the normal and the projection axis drops below 1-shape.z.
        float edge=shape.y>0? clamp((1.0-max(abs(local.x),abs(local.z)))/max(shape.y,1e-3),0.0,1.0) : 1.0;
        float grazing=shape.z>0? smoothstep(1.0-shape.z,1.0,abs(dot(n,axis))) : 1.0;
        float alpha=1.0;
        if(wrap.w>0.5) {
            vec4 texel=textureGrad(decalTexture,uv,uvDx,uvDy);
            albedo=albedo*texel.rgb; alpha=texel.a;
        } else {
            // Procedural pattern when there is no texture: used to verify depth reconstruction; if the pattern follows the terrain relief, the reconstruction is correct.
            float checker=mod(floor(uv.x*4.0)+floor(uv.y*4.0),2.0)<0.5?1.0:0.45;
            albedo=albedo*checker*(0.55+0.45*sin(length(local.xz)*18.0));
        }
        shade=clamp(edge*edge*(3.0-2.0*edge),0.0,1.0)*grazing*alpha;
    }
    coverage=shade*tint.w;
    // A decal that does not modify roughness must set the source alpha of RT1 to 0: RT1 blending is mix(dst, src, src.a),
    // so a non-zero source alpha would needlessly pull regions that "did not request a roughness change" toward src.
    roughWeight=wrap.y>0.5?coverage:0.0;
    roughness=wrap.y>0.5?wrap.x:0.0;
    color=shape.x<0.5 ? vec4(mix(vec3(1.0),albedo,coverage),1.0) : vec4(albedo*coverage*shape.w,0.0);
}
""";

    /// <summary>
    /// Pixel entry point that writes only SceneColor.
    /// The fixed-function blend for Modulate is <c>DST_COLOR / ZERO</c>, and for Additive it is <c>ONE / ONE</c>;
    /// both keep the destination value in the alpha channel (<c>ONE / ZERO</c>), so the decal does not smear SceneColor's alpha to 1.
    /// </summary>
    public const string PixelGlsl = PixelCoreGlsl + """

layout(location=0) out vec4 outColor;

void main() {
    float coverage, roughness, roughWeight;
    DecalEval(outColor,coverage,roughness,roughWeight);
}
""";

    /// <summary>
    /// s9: pixel entry point that also writes roughness. The normal attachment only enables the B channel in its write mask, so here we only need to send the target roughness to .z,
    /// while the octahedral normal (.xy) and reflection weight (.w) keep their original values from the attachment, and the blend factor interpolates on the B channel using <c>roughWeight</c>.
    /// </summary>
    public const string PixelRoughnessGlsl = PixelCoreGlsl + """

layout(location=0) out vec4 outColor;
layout(location=1) out vec4 outNormal;

void main() {
    float coverage, roughness, roughWeight;
    DecalEval(outColor,coverage,roughness,roughWeight);
    outNormal=vec4(0.0,0.0,roughness,roughWeight);
}
""";
}
