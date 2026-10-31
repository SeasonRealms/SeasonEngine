// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// D1 屏幕空间贴花的 HLSL 变体。其余三端（GLSL / MSL / WGSL）按后端顺序各自实现同一套语义，
/// 帧缓冲布局以 <see cref="DecalMath"/> 为准 —— 这里只做「读布局」的部分，不再定义布局。
/// </summary>
public static partial class DecalShaders
{
    /// <summary>
    /// 帧头与贴花数组的访问器，顶点与像素着色器共用。
    ///
    /// 顶点端需要世界→局部以外的两套矩阵，像素端需要反投影矩阵与像素尺寸，所以这份前缀必须同时
    /// 拼进 VS 与 PS 的源码；拆开拼会让两个入口对同一份布局的解读出现分歧。
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
    /// 把投影盒的 36 个顶点按 <c>SV_VertexID</c> 算出来，不依赖顶点缓冲。
    ///
    /// 六个面统一成「从盒外看逆时针」，因此 <c>CullMode.Front</c>（只留背面）在每个像素上恰好命中一次 ——
    /// 这是整个方案不用模板、不用逐 draw 排序的前提。面索引的位拆分见下面的注释，改任何一处都要同步改绕序推导。
    /// </summary>
    public const string VertexHlsl = """
float4 VSMain(uint id:SV_VertexID):SV_Position {
    // 每面 6 个顶点：两条三角形 (0,1,2)+(0,2,3)，再按 k 还原成四角编号。
    uint face=id/6u, t=id-face*6u;
    uint tri=t/3u, c=t-tri*3u;
    uint k=c==0u?0u:(c==1u?(tri==0u?1u:2u):(tri==0u?2u:3u));
    // face>>1 = 主轴（0=x,1=y,2=z），face&1 = 正负侧。副轴固定取 (a+1)%3 / (a+2)%3，
    // 这组排列的叉积正好是 +e_a，所以负侧必须把 u 取反才能保持「盒外逆时针」。
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
    /// 深度重建 + 贴花求值。要点：
    /// <list type="bullet">
    /// <item>UV 来自盒内归一化坐标，所以贴花是「沿着盒的局部 Y 投影」的，与表面朝向无关；</item>
    /// <item>导数一律在统一控制流里取（uv 与 world 都先算好），分支里的导数在 quad 边界是未定义行为；</item>
    /// <item>采样用 <c>SampleGrad</c>：盒的背面代理三角形比像素足迹大得多，隐式导数会在盒轮廓上取到盒外的像素；</item>
    /// <item>掠射淡出用深度导出的法线，不依赖 SceneNormal。贴花在写粗糙度时把 SceneNormal 挂在颜色附件上，
    /// 同一个资源不能同时是可采样源，所以这条路径不能改成读 SceneNormal。</item>
    /// </list>
    ///
    /// 输出约定：Modulate 输出「乘到目标上的因子」（1 处不改变），Additive 输出自发光颜色。
    /// 两者都靠固定功能的混合完成，像素着色器不读 SceneColor，因此 SceneColor 不需要同时是可采样资源。
    ///
    /// 帧缓冲字段：<c>wrap = (目标粗糙度, 是否改写粗糙度, 排序键, 是否有贴图)</c>，
    /// <c>tint.w</c> 是不透明度，<c>shape.zw</c> 是羽化与掠射阈值。
    /// </summary>
    public const string PixelHlsl = """
void DecalEval(float4 position,out float4 color,out float coverage,out float roughness) {
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
        // 羽化：盒边界向内 shape.y 的比例；掠射：法线与投影轴夹角的余弦低于 1-shape.z 就淡出。
        float edge=shape.y>0? saturate((1.0-max(abs(local.x),abs(local.z)))/max(shape.y,1e-3)) : 1.0;
        float grazing=shape.z>0? smoothstep(1.0-shape.z,1.0,abs(dot(n,axis))) : 1.0;
        float alpha=1.0;
        if(wrap.w>0.5) {
            float4 texel=decalTexture.SampleGrad(decalLinearClamp,uv,uvDx,uvDy);
            albedo=albedo*texel.rgb; alpha=texel.a;
        } else {
            // 无纹理时的程序化图案：验证深度重建用，图案跟着地形起伏就说明重建对了。
            float checker=fmod(floor(uv.x*4)+floor(uv.y*4),2.0)<0.5?1.0:0.45;
            albedo=albedo*checker*(0.55+0.45*sin(length(local.xz)*18.0));
        }
        shade=saturate(edge*edge*(3.0-2.0*edge))*grazing*alpha;
    }
    coverage=shade*tint.w;
    roughness=wrap.y>0.5?wrap.x:0.0;
    color=shape.x<0.5
        ? float4(lerp(1.0,albedo,coverage),1)
        : float4(albedo*coverage*shape.w,0);
}

float4 PSMain(float4 position:SV_Position):SV_Target {
    float4 color; float coverage, roughness;
    DecalEval(position,color,coverage,roughness);
    return color;
}

// s9：法线目标只开 B 通道写掩码，所以这里只需要把目标粗糙度送到 .z，
// 八面体法线（.xy）与金属度（.w）由附件里的原值保留。混合用 coverage 在 B 通道上插值。
struct PSRoughnessOut { float4 color:SV_Target0; float4 normal:SV_Target1; };

PSRoughnessOut PSMainRoughness(float4 position:SV_Position) {
    PSRoughnessOut o; float coverage, roughness;
    DecalEval(position,o.color,coverage,roughness);
    o.normal=float4(0,0,roughness,coverage);
    return o;
}
""";
}
