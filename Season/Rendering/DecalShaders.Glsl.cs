// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// D1 屏幕空间贴花的 GLSL 变体（Vulkan / Linux / Android），与 <see cref="DecalShaders"/> 的 HLSL 同语义。
///
/// 与原版的差别只在语法层面，三处「看起来像笔误」的地方按 Vulkan 的规则保留：
/// <list type="bullet">
/// <item>帧缓冲按 <c>vec4</c> 数组读，不再按字节偏移解包：<see cref="DecalMath"/> 的布局本来就是 float4 连续存放，
/// 用 <c>std430</c> 的 <c>vec4[]</c> 直接索引与 HLSL 的 <c>Load4(i*16)</c> 是同一份内存；</item>
/// <item>glslang 的简化 C 接口只认 <c>main</c>，所以「写粗糙度」与「不写粗糙度」必须是两份独立源码，
/// 由 <see cref="PixelCoreGlsl"/> 拼同一份求值函数，避免两个入口对布局的解读分叉；</item>
/// <item>深度用 <c>texelFetch</c> 而不是 <c>texture</c>：贴花是逐像素投影，任何滤波都会把贴花边缘的深度
/// 混进邻像素，让盒边界出现一圈错误的遮挡。</item>
/// </list>
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// 帧缓冲与像素尺寸的访问器，顶点与像素共用。<c>DecalTransform</c> 保持 HLSL 的行向量写法
    /// （<c>p.x*F(i)+...</c>），因为 <see cref="DecalMath"/> 把 <see cref="System.Numerics.Matrix4x4"/>
    /// 按行抄进缓冲，GLSL 里按 <c>vec4</c> 索引取到的正是同一批行。
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
    /// 投影盒的 36 个顶点。面索引的位拆分与 HLSL 版逐字一致：六个面统一成「从盒外看逆时针」，
    /// 后端再配 <c>CullMode.Front</c>（只留背面），每个像素恰好命中一次。
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
    /// 深度重建 + 贴花求值。语义与 HLSL 的同名函数一一对应，输出约定也相同：
    /// Modulate 输出「乘到目标上的因子」，Additive 输出自发光颜色，两者都靠固定功能混合落地。
    /// <para>
    /// <c>gl_FragCoord</c> 在负高度视口下仍然是左上原点、Y 向下，与 DX 的 <c>SV_Position</c> 同一约定，
    /// 所以屏幕坐标到 NDC 的换算保持 <c>vec2(2,-2)</c> 不变。
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
        // 羽化：盒边界向内 shape.y 的比例；掠射：法线与投影轴夹角的余弦低于 1-shape.z 就淡出。
        float edge=shape.y>0? clamp((1.0-max(abs(local.x),abs(local.z)))/max(shape.y,1e-3),0.0,1.0) : 1.0;
        float grazing=shape.z>0? smoothstep(1.0-shape.z,1.0,abs(dot(n,axis))) : 1.0;
        float alpha=1.0;
        if(wrap.w>0.5) {
            vec4 texel=textureGrad(decalTexture,uv,uvDx,uvDy);
            albedo=albedo*texel.rgb; alpha=texel.a;
        } else {
            // 无纹理时的程序化图案：验证深度重建用，图案跟着地形起伏就说明重建对了。
            float checker=mod(floor(uv.x*4.0)+floor(uv.y*4.0),2.0)<0.5?1.0:0.45;
            albedo=albedo*checker*(0.55+0.45*sin(length(local.xz)*18.0));
        }
        shade=clamp(edge*edge*(3.0-2.0*edge),0.0,1.0)*grazing*alpha;
    }
    coverage=shade*tint.w;
    // 不改粗糙度的贴花必须把 RT1 的源 alpha 置 0：RT1 混合是 mix(dst, src, src.a)，
    // 源 alpha 不为 0 就会把“没要求改粗糙度”的区域白白拉向 src。
    roughWeight=wrap.y>0.5?coverage:0.0;
    roughness=wrap.y>0.5?wrap.x:0.0;
    color=shape.x<0.5 ? vec4(mix(vec3(1.0),albedo,coverage),1.0) : vec4(albedo*coverage*shape.w,0.0);
}
""";

    /// <summary>
    /// 只写 SceneColor 的像素入口。
    /// Modulate 的固定功能混合是 <c>DST_COLOR / ZERO</c>，Additive 是 <c>ONE / ONE</c>；
    /// 两者的 alpha 通道都保留目标值（<c>ONE / ZERO</c>），免得贴花把 SceneColor 的 alpha 抹成 1。
    /// </summary>
    public const string PixelGlsl = PixelCoreGlsl + """

layout(location=0) out vec4 outColor;

void main() {
    float coverage, roughness, roughWeight;
    DecalEval(outColor,coverage,roughness,roughWeight);
}
""";

    /// <summary>
    /// s9：同时写粗糙度的像素入口。法线附件只开 B 通道写掩码，所以这里只需要把目标粗糙度送到 .z，
    /// 八面体法线（.xy）与反射权重（.w）由附件里的原值保留，混合因子用 <c>roughWeight</c> 在 B 通道上插值。
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
