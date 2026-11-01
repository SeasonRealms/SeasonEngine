// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P2 水面材质的 GLSL 变体（Vulkan / Linux / Android），与 <see cref="WaterShaders"/> 的 HLSL 同语义。
///
/// 与原版的差别只在语法层面，三处按 Vulkan 的规则保留：
/// <list type="bullet">
/// <item>帧缓冲按 <c>vec4</c> 数组读（<c>std430</c>），与 HLSL 的 <c>Load4(i*16)</c> 是同一份内存；</item>
/// <item>glslang 的简化 C 接口只认 <c>main</c>，所以顶点与片元是两份独立源码，各自拼同一段 <c>CoreGlsl</c>；</item>
/// <item>深度用 <c>texelFetch</c> 而不是 <c>texture</c>：软件深度测试是逐像素比较，任何滤波都会把邻像素的
/// 深度混进来，让水面边缘出现一圈错误的遮挡。</item>
/// </list>
/// </summary>
public static partial class WaterShaders
{
    /// <summary>
    /// 帧缓冲与采样源的访问器，顶点与片元共用。<c>WaterTransform</c> 保持 HLSL 的行向量写法
    /// （<c>p.x*F(i)+...</c>），因为 <see cref="WaterMath"/> 把 <see cref="System.Numerics.Matrix4x4"/>
    /// 按行抄进缓冲，GLSL 里按 <c>vec4</c> 索引取到的正是同一批行。
    /// </summary>
    const string CoreGlsl = """
layout(std430,set=0,binding=0) readonly buffer WaterFrame { vec4 waterF[]; };
layout(set=0,binding=1) uniform sampler2D sceneDepth;
layout(set=0,binding=2) uniform sampler2D refractionSource;
layout(set=0,binding=3) uniform sampler2D waterNormalTexture;
layout(set=0,binding=4) uniform sampler2D waterSkyView;
// P3 Hi-Z 金字塔的四个级别（1/2 ~ 1/16）；data[12].x 为 0 时一张都不会被采样。
layout(set=0,binding=5) uniform sampler2D waterHz0;
layout(set=0,binding=6) uniform sampler2D waterHz1;
layout(set=0,binding=7) uniform sampler2D waterHz2;
layout(set=0,binding=8) uniform sampler2D waterHz3;
layout(push_constant) uniform WaterParams { uvec4 waterParams; };

vec4 WaterF(int i) { return waterF[i]; }
vec4 WaterTransform(vec4 p,int i) { return p.x*WaterF(i)+p.y*WaterF(i+1)+p.z*WaterF(i+2)+p.w*WaterF(i+3); }
int WaterOffset(uint index) { return 16+int(index)*16; }
""";

    /// <summary>
    /// P4 顶点端：分块网格展开 + Gerstner 位移。与 HLSL 版逐句同序；唯一的语法差异是
    /// <c>gl_InstanceIndex</c> 而不是 <c>SV_InstanceID</c>，以及 <c>out</c> 参数写在形参表里。
    /// </summary>
    public const string VertexGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) out vec3 vWorld;
layout(location=1) flat out uint vSlot;
layout(location=2) out vec3 vNormal;

// 六个二维角点，按 quad 的两个三角形排列。单一出口，与 HLSL 版同理。
vec2 WaterCorner(uint id) {
    vec2 corner = vec2(0.0,0.0);
    if (id == 1u) corner = vec2(1.0,0.0);
    else if (id == 2u) corner = vec2(1.0,1.0);
    else if (id == 3u) corner = vec2(0.0,0.0);
    else if (id == 4u) corner = vec2(1.0,1.0);
    else if (id == 5u) corner = vec2(0.0,1.0);
    return corner;
}

// tile 内的 [0,1] 网格坐标 → 方形的 [-0.5,0.5] 平面坐标。
// 除数钳到 1：整数除零在 GLSL 与 WGSL 都是未定义值，且驱动无法证明根常量非零。
vec2 WaterLocalUv(uint id,uint tile,uint tilesX,uint tilesZ,uint subdiv) {
    if (subdiv == 0u) return WaterCorner(id)-0.5;
    uint nx = max(tilesX,1u);
    uint nz = max(tilesZ,1u);
    uint sd = max(subdiv,1u);
    uint quad = id/6u;
    vec2 corner = WaterCorner(id%6u);
    vec2 quadUv = (vec2(float(quad%sd),float(quad/sd))+corner)/float(sd);
    vec2 tileXy = vec2(float(tile%nx),float(tile/nx));
    return (tileXy+quadUv)/vec2(float(nx),float(nz))-0.5;
}

// Gerstner 位移与切向量，参数在 water + 8 .. + 11（振幅, 波数, 方向.x, 方向.z）。
// 相速度由深水色散 ω = sqrt(g·k) 现算，帧缓冲里只存波数与 water + 13.x 的速度倍率。
// 水平位移贡献 k·A·sin（横向压缩），高度贡献 k·A·cos（竖直倾斜）；混用会让波峰与波谷的法线反号。
vec3 WaterGerstner(vec2 p,float t,int offset,out vec3 tangent,out vec3 binormal) {
    vec3 displacement = vec3(0.0);
    tangent = vec3(1.0,0.0,0.0);
    binormal = vec3(0.0,0.0,1.0);
    float choppiness = WaterF(offset+12).w;
    float speedScale = WaterF(offset+13).x;
    for (uint i=0u;i<4u;i++) {
        vec4 wave = WaterF(offset+8+int(i));
        float amplitude = wave.x;
        float k = wave.y;
        // 风浪关闭时四位全 0：k 为 0 跳过，顶点与法线保持恒等值。
        if (k > 0.0) {
            vec2 dir = wave.zw;
            float omega = sqrt(9.81*k)*speedScale;
            float phase = k*dot(dir,p)-omega*t;
            float s = sin(phase);
            float c = cos(phase);
            float q = choppiness*amplitude;
            displacement.x += q*dir.x*c;
            displacement.y += amplitude*s;
            displacement.z += q*dir.y*c;
            float ks = k*amplitude*s;
            float kc = k*amplitude*c;
            tangent += vec3(-choppiness*ks*dir.x*dir.x,kc*dir.x,-choppiness*ks*dir.x*dir.y);
            binormal += vec3(-choppiness*ks*dir.x*dir.y,kc*dir.y,-choppiness*ks*dir.y*dir.y);
        }
    }
    return displacement;
}

// 实例号即 tile 索引；槽位仍走 push constant（WebGPU 没有，那边只能挤进实例号）。
void main() {
    uint slot = waterParams.x;
    int offset = WaterOffset(slot);
    vec2 uv = WaterLocalUv(uint(gl_VertexIndex),uint(gl_InstanceIndex),
        waterParams.y,waterParams.z,waterParams.w);
    vec4 world = WaterTransform(vec4(uv.x,0.0,uv.y,1.0),offset);

    // 位移在模型→世界之后做：Gerstner 是世界 XZ 平面上的行波，放进模型空间会跟着矩形转。
    vec3 tangent, binormal;
    world.xyz += WaterGerstner(world.xz,WaterF(10).z,offset,tangent,binormal);

    gl_Position = WaterTransform(world,4);
    vWorld = world.xyz;
    // cross(binormal,tangent)：未扰动时 (0,0,1)×(1,0,0) = (0,1,0)，正好朝上。
    vNormal = normalize(cross(binormal,tangent));
    vSlot = slot;
}
""";

    /// <summary>
    /// 法线扰动、折射、吸收、Fresnel 与雨涟漪的求值。与 HLSL 版逐句同序，唯一的语法差异是
    /// <c>atan2(y,x)</c> 在 GLSL 里写作 <c>atan(y,x)</c>，以及 <c>frac</c> 对应 <c>fract</c>。
    /// </summary>
    public const string PixelGlsl = "#version 450\n" + CoreGlsl + """

layout(location=0) in vec3 vWorld;
layout(location=1) flat in uint vSlot;
layout(location=2) in vec3 vNormal;
layout(location=0) out vec4 outColor;

vec3 WaterSampleSky(vec3 direction) {
    vec2 uv = vec2(atan(direction.x,-direction.z)*0.15915494309+0.5,
        0.5-0.5*sign(direction.y)*sqrt(abs(direction.y)));
    return textureLod(waterSkyView,uv,0.0).rgb;
}

// 高度场梯度（米/米），系数 = 波数 × 振幅，叠加不到 0.07，法线只倾 3~4°。
// 量纲不能省：直接返回 d·cos（幅值 ~1）会让法线倾到 15° 以上，俯视时一半像素的
// 法线背对视线，facing 被 clamp 到 0、Fresnel 饱和到 1，水面退化成硬边镜面斑块。
vec2 WaterWave(vec2 p,float t,float speed) {
    vec2 d1 = vec2(0.86,0.51);
    vec2 d2 = vec2(-0.42,0.91);
    float k1 = dot(p,d1)*2.4+t*speed*6.2831853;
    float k2 = dot(p,d2)*3.7-t*speed*5.1;
    return d1*cos(k1)*(2.4*0.016)+d2*cos(k2)*(3.7*0.009);
}

// 雨涟漪同样是梯度量纲：环频率 26/m、振幅 0.0025m，单环梯度约 0.065。
vec2 WaterRipple(vec2 p,float t) {
    vec2 acc = vec2(0.0);
    for (uint i=0u;i<3u;i++) {
        vec2 c = vec2(sin(float(i)*12.9898)*7.0,cos(float(i)*78.233)*7.0);
        vec2 d = p-c;
        float r = length(d);
        float phase = fract(t*1.7+float(i)*0.37);
        float wave = sin(r*26.0-phase*40.0)*clamp(1.0-phase,0.0,1.0);
        acc += d/max(r,1e-4)*wave;
    }
    return acc*(26.0*0.0025);
}

// 视空间前向深度（LH + [0,1]），与主着色器 / GTAO / SsrEffect 的重建公式同源。
float WaterLinear(float d) {
    float nearV = WaterF(12).z, farV = WaterF(12).w;
    return nearV*farV/(farV-d*(farV-nearV));
}

float WaterHiZ(vec2 uv,int level) {
    if (level <= 0) return textureLod(waterHz0,uv,0.0).r;
    if (level == 1) return textureLod(waterHz1,uv,0.0).r;
    if (level == 2) return textureLod(waterHz2,uv,0.0).r;
    return textureLod(waterHz3,uv,0.0).r;
}

// 屏幕空间反射：沿反射方向在屏幕空间步进，用 Hi-Z 金字塔整块跳过空区域，
// 命中点取折射快照（开画前的不透明场景色）。rgb = 辐射，a = 置信度（0 = 没命中）。
vec4 WaterTraceReflection(vec3 origin,vec3 direction,vec2 size) {
    const int coarseSteps = 24;
    const int refineSteps = 6;
    float maxDistance = WaterF(13).x;
    float thickness = WaterF(13).y;
    vec4 result = vec4(0.0);
    vec4 clip0 = WaterTransform(vec4(origin,1.0),4);
    vec4 clip1 = WaterTransform(vec4(origin+direction*maxDistance,1.0),4);

    // 射线走到相机背后时屏幕空间没有信息可用：沿原方向缩到相机近平面之前。
    float endT = 1.0;
    if (clip1.w <= 0.001) {
        float denom = clip1.w-clip0.w;
        endT = denom < -0.0001 ? clamp(-clip0.w/denom,0.0,1.0) : 0.0;
        clip1 = WaterTransform(vec4(origin+direction*(maxDistance*endT),1.0),4);
    }

    if (clip0.w > 0.001 && clip1.w > 0.001 && endT > 0.001) {
        vec2 uv0 = clip0.xy/clip0.w*vec2(0.5,-0.5)+0.5;
        vec2 uv1 = clip1.xy/clip1.w*vec2(0.5,-0.5)+0.5;
        float z0 = clip0.z/clip0.w;
        float z1 = clip1.z/clip1.w;
        vec2 delta = uv1-uv0;

        // 每步跨多少像素决定查哪一级金字塔：短射线用细级，否则粗块永远命中不了。
        float pixels = length(delta*size)/float(coarseSteps);
        int level = 3;
        if (pixels < 12.0) level = 2;
        if (pixels < 6.0) level = 1;
        if (pixels < 3.0) level = 0;

        vec2 coarse = uv0;
        float coarseZ = z0;
        for (int i=1;i<=coarseSteps;i++) {
            float t = float(i)/float(coarseSteps);
            vec2 next = uv0+delta*t;
            float nextZ = mix(z0,z1,t);
            // 出屏即终止：屏幕外的信息不存在，继续走只会采到钳制后的边缘像素。
            if (next.x < 0.0 || next.x > 1.0 || next.y < 0.0 || next.y > 1.0) break;
            if (nextZ < WaterHiZ(next,level)) {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // 越过了：在（上一节点, 当前节点）内逐点逼近真实交点。
            for (int j=1;j<=refineSteps;j++) {
                float tj = float(j)/float(refineSteps);
                vec2 suv = mix(coarse,next,tj);
                float rayZ = mix(coarseZ,nextZ,tj);
                ivec2 coord = clamp(ivec2(suv*size),ivec2(0),ivec2(int(uint(size.x))-1,int(uint(size.y))-1));
                float sceneD = texelFetch(sceneDepth,coord,0).r;
                if (sceneD >= 1.0) continue;
                // 厚度判定放在线性深度上：NDC 深度越远越挤，同一阈值在近处和远处差出几个数量级。
                float rayView = WaterLinear(rayZ);
                float sceneView = WaterLinear(sceneD);
                if (rayView >= sceneView && rayView-sceneView <= thickness) {
                    vec2 hitUv = clamp(suv,vec2(0.0),vec2(1.0));
                    // 屏幕边缘淡出：那里的采样是外推出来的，硬切会在画面边缘拉出一条亮边。
                    float edge = min(min(hitUv.x,1.0-hitUv.x),min(hitUv.y,1.0-hitUv.y));
                    float fade = clamp(edge/0.06,0.0,1.0)*clamp(1.0-t,0.0,1.0);
                    result = vec4(textureLod(refractionSource,hitUv,0.0).rgb,fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

void main() {
    int offset = WaterOffset(vSlot);
    vec4 material0 = WaterF(offset+4);
    vec4 material1 = WaterF(offset+5);
    vec4 material2 = WaterF(offset+6);
    vec4 material3 = WaterF(offset+7);

    vec3 camera = WaterF(8).xyz;
    vec4 screen = WaterF(9);
    vec4 misc = WaterF(10);
    vec4 sun = WaterF(11);
    float time = misc.z;

    vec2 uv = gl_FragCoord.xy*screen.zw;
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float sceneDepthValue = texelFetch(sceneDepth,pixel,0).r;

    if (gl_FragCoord.z > sceneDepthValue+1e-6) discard;

    vec4 ndc = vec4(uv*vec2(2,-2)+vec2(-1,1),sceneDepthValue,1.0);
    vec4 scenePosition = ndc.x*WaterF(0)+ndc.y*WaterF(1)+ndc.z*WaterF(2)+ndc.w*WaterF(3);
    float thickness = max(length(scenePosition.xyz/scenePosition.w-vWorld),0.0);

    vec2 planar = vWorld.xz;
    vec2 slope = WaterWave(planar,time,material2.x);
    if (material3.x > 0.5) {
        vec2 tiled = planar*material1.w;
        vec2 a = textureLod(waterNormalTexture,tiled+time*material2.x*vec2(0.7,0.3),0.0).rg*2.0-1.0;
        vec2 b = textureLod(waterNormalTexture,tiled*1.37-time*material2.x*vec2(0.4,0.8),0.0).rg*2.0-1.0;
        slope = (a+b)*0.5;
    }
    if (misc.y > 0.5) slope += WaterRipple(planar*2.0,time)*material2.y;
    slope *= material1.z;
    // P4：细节斜率叠加到 Gerstner 的解析法线上。Gerstner 关闭时 vNormal 恒为 (0,1,0)，
    // 这里退化成 P3 的 normalize(vec3(slope.x,1,slope.y))。
    vec3 normal = normalize(vNormal+vec3(slope.x,0.0,slope.y));

    vec3 view = normalize(camera-vWorld);
    float facing = clamp(dot(normal,view),0.0,1.0);
    float fresnel = material2.z+(1.0-material2.z)*pow(1.0-facing,5.0);

    // 折射偏移的单位是像素（后面乘 1/宽高换成 UV），600 把梯度折成像素尺度。
    vec2 offsetUv = slope*material1.y*600.0;
    vec2 refractedUv = clamp(uv+offsetUv*screen.zw,vec2(0.0),vec2(1.0));
    vec3 refracted = textureLod(refractionSource,refractedUv,0.0).rgb;

    vec3 transmittance = exp(-material1.x*thickness*vec3(1.35,0.85,0.55));
    // 水体是被照亮的散射介质：水色是反照率，必须乘光照，否则夜里场景全暗之后水池会自己发光。
    // 用池底快照的亮度当日光代理：夜里趋近 0，白天饱和到 1。
    float daylight = clamp(dot(refracted,vec3(0.2126,0.7152,0.0722))*3.0,0.0,1.0);
    vec3 body = refracted*transmittance+material0.rgb*(1.0-transmittance)*daylight;

    // 反射：先试屏幕空间反射（Hi-Z 加速），没命中再退回程序化天空。
    // 射线用未钳制的方向：水面往下看时反射方向指向水底，天空 LUT 那里没有信息。
    vec3 reflectDirection = reflect(-view,normal);
    // 天空采样前把地平线以下钳回地平线：LUT 下半球是近地雾霾（夜里几乎全黑），法线稍一倾斜就会打进去。
    vec3 skyDirection = vec3(reflectDirection.x,max(reflectDirection.y,0.0),reflectDirection.z);
    // 兑底色同样乘 daylight：拿不到天空 LUT 时，这个常量不该在夜里继续发光。
    vec3 reflection = misc.w > 0.5 ? WaterSampleSky(skyDirection) : vec3(0.35,0.45,0.62)*daylight;
    if (WaterF(12).x > 0.5) {
        vec4 traced = WaterTraceReflection(vWorld,reflectDirection,screen.xy);
        reflection = mix(reflection,traced.rgb,clamp(traced.a*WaterF(12).y,0.0,1.0));
    }
    vec3 toSun = normalize(-sun.xyz);
    vec3 halfVector = normalize(view+toSun);
    float shininess = 2.0/max(material2.w*material2.w,1e-3);
    reflection += sun.w*pow(clamp(dot(normal,halfVector),0.0,1.0),shininess)*vec3(1.0,0.95,0.85)*0.5;

    vec3 color = mix(body,reflection,fresnel);
    float thicknessAlpha = 1.0-exp(-thickness*0.9);
    float alpha = clamp(max(mix(material0.a,1.0,fresnel),thicknessAlpha*0.9),0.0,1.0);
    outColor = vec4(color,alpha);
}
""";
}
