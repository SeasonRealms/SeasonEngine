// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// P2 水面材质的 HLSL 变体。其余三端（GLSL / MSL / WGSL）按后端顺序各自实现同一套语义，
/// 帧缓冲布局以 <see cref="WaterMath"/> 为准 —— 这里只做「读布局 + 求值」，不再定义布局。
///
/// 与 D1 贴花的两处结构性差异，决定了这份源码的形状：
/// <list type="bullet">
/// <item>贴花是屏幕空间体积（36 顶点投影盒），水面是真实几何（6 顶点水平矩形），
/// 所以顶点端要真的算一套裁剪空间坐标，而不只是一个代理三角形；</item>
/// <item>贴花只读深度、靠固定功能混合写回颜色；水面还要<b>读</b>场景色做折射，
/// 因此输入里多一张 <c>RefractionSource</c>（开画前的场景色快照），本 Pass 的颜色附件是另一张。</item>
/// </list>
///
/// 遮挡不靠深度附件（本 Pass 不挂），而是着色器里的软件深度测试：水面片元的裁剪空间 z
/// 比场景深度更远时直接 <c>discard</c>。这样深度目标可以整帧保持「只读采样」状态，
/// 池沿、石头、玩家挡住的半格水面会自动消失，也不需要为水面准备单独的 DSV。
/// </summary>
public static partial class WaterShaders
{
    /// <summary>
    /// 帧头与水面数组的访问器，顶点与像素着色器共用。拆开拼会让两个入口对同一份布局的解读出现分歧，
    /// 与 <see cref="DecalShaders.FrameHlsl"/> 的处理一致。
    /// </summary>
    public const string FrameHlsl = """
ByteAddressBuffer waterFrame : register(t0);
Texture2D<float> sceneDepth : register(t1);
Texture2D<float4> refractionSource : register(t2);
Texture2D<float4> waterNormalTexture : register(t3);
Texture2D<float4> waterSkyView : register(t4);
// P3 Hi-Z 金字塔的四个级别（1/2 ~ 1/16）。名字与 <see cref="WaterEffect"/> 的开关解耦：
// 未启用时 data[12].x 为 0，这里四张一张都不会被采样。
Texture2D<float> waterHz0 : register(t5);
Texture2D<float> waterHz1 : register(t6);
Texture2D<float> waterHz2 : register(t7);
Texture2D<float> waterHz3 : register(t8);
SamplerState waterLinearClamp : register(s0);
cbuffer WaterParams : register(b0) { uint4 waterParams; };

float4 WaterF(uint i) { return asfloat(waterFrame.Load4(i * 16)); }
uint WaterOffset(uint index) { return 16u + index * 16u; }

// 与 GLSL/MSL/WGSL 的 WaterTransform 逐字同形：p 当行向量，按行点乘。
// 这里不能写成 mul(M, p)：HLSL 的 mul 是「矩阵乘列向量」，而 WaterMath 是按 System.Numerics 的
// 行序把矩阵抄进缓冲的。两者一凑，平移那一整行被甩掉，顶点先落回原点附近、再被同样的错乘
// 放进裁剪空间，画面表现就是水面铺成一大片伸到池子外面的三角形。
float4 WaterTransform(float4 p, uint offset)
{
    return p.x * WaterF(offset) + p.y * WaterF(offset + 1) + p.z * WaterF(offset + 2) + p.w * WaterF(offset + 3);
}

struct WaterVertex
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    // P4：顶点位移后的解析法线（世界空间）。Gerstner 关闭时它恒为 (0,1,0)，
    // 像素端拼上细节斜率后与 P3 的平整水面逐像素一致。
    float3 normal : TEXCOORD1;
    nointerpolation uint slot : TEXCOORD2;
};
""";

    /// <summary>
    /// P4 顶点端：分块网格展开 + Gerstner 位移。
    ///
    /// 两套几何共用一条路径。不分块时（<c>subdiv == 0</c>）顶点数就是 6、实例数是 1，
    /// 展开出来就是 P3 的两三角形矩形；分块时顶点数是 <c>subdiv²×6</c>、实例数是 tile 总数，
    /// 每个实例画一块，块内是 subdiv×subdiv 个四边形。世界矩阵来自共享层，两套路径都不需要顶点缓冲。
    /// </summary>
    public const string VertexHlsl = """
// 六个二维角点，按 quad 的两个三角形排列。不分块时直接当单位平面的位置，
// 分块时当第 (qx,qz) 个四边形的相对角点。
// 单一出口：fxc 的流分析对「多 return 的函数 + 参数范围未知」会保守判定为未初始化（X4000），
// 直接调用时会被内联掩盖，一旦被另一层函数套住就会报出来。
float2 WaterCorner(uint id)
{
    float2 corner = float2(0.0f, 0.0f);
    if (id == 1u) corner = float2(1.0f, 0.0f);
    else if (id == 2u) corner = float2(1.0f, 1.0f);
    else if (id == 3u) corner = float2(0.0f, 0.0f);
    else if (id == 4u) corner = float2(1.0f, 1.0f);
    else if (id == 5u) corner = float2(0.0f, 1.0f);
    return corner;
}

// tile 内的 [0,1] 网格坐标 → 方形的 [-0.5,0.5] 平面坐标。
// tilesX/tilesZ/subdiv 三份与 WaterMath.Tiling 给出的值逐位相同，否则 tile 会重叠或漏缝。
float2 WaterLocalUv(uint id, uint tile, uint tilesX, uint tilesZ, uint subdiv)
{
    if (subdiv == 0u) return WaterCorner(id) - 0.5f;
    // 除数钳到 1：这三个值来自根常量，流分析无法证明它们非零，直接除会被 fxc 判成「可能未初始化」；
    // 而 WGSL 里整数除零本身就是未定义值，四端都需要这一行。实际上 WaterMath.Tiling
    // 保证进到这个分支时三者都 >= 1，钳制只是把已经成立的前提写给编译器看。
    uint nx = max(tilesX, 1u);
    uint nz = max(tilesZ, 1u);
    uint sd = max(subdiv, 1u);
    uint quad = id / 6u;
    float2 corner = WaterCorner(id % 6u);
    float2 quadUv = (float2(quad % sd, quad / sd) + corner) / (float)sd;
    float2 tileXy = float2(tile % nx, tile / nx);
    return (tileXy + quadUv) / float2(nx, nz) - 0.5f;
}

// Gerstner 位移与切向量。四个分量的参数打包在 water + 8 .. + 11（振幅, 波数, 方向.x, 方向.z）。
//
// 相速度不单独传：深水色散 ω = sqrt(g·k) 由波数唯一确定，这里现算。于是「速度」只有两个来源：
// 波数 k（由风速经波长谱得出）与 water + 13.x 的倍率，作者层调一个数就能整片海加减速。
//
// 返回位移（x/z 是水平轨道，y 是高度），tangent/binormal 是位移后表面的偏导。
// 注意两项用的是不同的三角函数：水平位移贡献 k·A·sin（横向压缩），高度贡献 k·A·cos（竖直倾斜）。
// 混用会让法线在波峰与波谷处反号，水面出现沿波向的暗条纹。
float3 WaterGerstner(float2 p, float t, uint offset, out float3 tangent, out float3 binormal)
{
    float3 displacement = float3(0.0f, 0.0f, 0.0f);
    tangent = float3(1.0f, 0.0f, 0.0f);
    binormal = float3(0.0f, 0.0f, 1.0f);
    float choppiness = WaterF(offset + 12u).w;
    float speedScale = WaterF(offset + 13u).x;
    [unroll]
    for (uint i = 0u; i < 4u; i++)
    {
        float4 wave = WaterF(offset + 8u + i);
        float amplitude = wave.x;
        float k = wave.y;
        // 风浪关闭时 BuildWaves 把四位清 0：k 为 0 直接跳过，顶点与法线都保持恒等值。
        // 不用 continue：这份循环会被 fxc 完全展开，而展开后的 continue 在部分版本上会误报流分析。
        if (k > 0.0f)
        {
            float2 dir = float2(wave.z, wave.w);
            float omega = sqrt(9.81f * k) * speedScale;
            float phase = k * dot(dir, p) - omega * t;
            float s = sin(phase);
            float c = cos(phase);
            float q = choppiness * amplitude;
            displacement.x += q * dir.x * c;
            displacement.y += amplitude * s;
            displacement.z += q * dir.y * c;
            float ks = k * amplitude * s;
            float kc = k * amplitude * c;
            tangent += float3(-choppiness * ks * dir.x * dir.x, kc * dir.x, -choppiness * ks * dir.x * dir.y);
            binormal += float3(-choppiness * ks * dir.x * dir.y, kc * dir.y, -choppiness * ks * dir.y * dir.y);
        }
    }
    return displacement;
}

// 实例号即 tile 索引：分块时一次 DrawInstanced 画出整张 tile 表。
// 槽位走根常量而不是实例号，因为 <see cref="WaterMath.MaxTilesPerWater"/> 只有 WebGPU 需要拿来当编码基数。
WaterVertex VSMain(uint id : SV_VertexID, uint tile : SV_InstanceID)
{
    WaterVertex o;
    uint slot = waterParams.x;
    uint offset = WaterOffset(slot);
    float2 uv = WaterLocalUv(id, tile, waterParams.y, waterParams.z, waterParams.w);
    float4 world = WaterTransform(float4(uv.x, 0.0f, uv.y, 1.0f), offset);

    // 位移在模型→世界之后做：Gerstner 是定义在世界 XZ 平面上的行波，
    // 放进模型空间的话，矩形一旋转波浪方向就会跟着车头转。
    float3 tangent, binormal;
    world.xyz += WaterGerstner(world.xz, WaterF(10u).z, offset, tangent, binormal);

    o.position = WaterTransform(world, 4u);
    o.world = world.xyz;
    // cross(binormal, tangent) 而不是反过来：未扰动时 (0,0,1)×(1,0,0) = (0,1,0)，正好朝上。
    o.normal = normalize(cross(binormal, tangent));
    o.slot = slot;
    return o;
}
""";

    /// <summary>
    /// 法线扰动、折射、吸收、Fresnel 与雨涟漪的求值。
    ///
    /// 顺序不可交换：先用（扰动的）法线做软件深度判定的补偿没有意义，所以深度测试用未扰动的片元位置；
    /// 折射与吸收都依赖厚度的同一个定义 —— 「水面到场景表面沿视线的距离」，由反 VP 重建后一次求出，
    /// 不能再各自线性化一遍，否则两条衰减会在掠射角对不上。
    /// </summary>
    public const string PixelHlsl = """
float3 WaterSampleSky(float3 direction)
{
    // 与主着色器的 SampleSkyViewRadiance 逐字同参：u = atan2(x,-z)/2pi+0.5，v = 0.5-0.5*sign(y)*sqrt(|y|)。
    // 只在 LOD 0 采样：LUT 没有 mip，接缝两侧的导数本来就没有意义。
    float2 uv = float2(atan2(direction.x, -direction.z) * 0.15915494309f + 0.5f,
        0.5f - 0.5f * sign(direction.y) * sqrt(abs(direction.y)));
    return waterSkyView.SampleLevel(waterLinearClamp, uv, 0.0f).rgb;
}

// 程序化水波：两列不同方向的正弦叠加。第二列频率更高、相位反向推进，
// 因此两列不会以同一速度平移，视觉上不会出现「整块贴图在滑」的廉价感。
//
// 返回的是高度场的**梯度**（米/米），不是归一化的方向向量：系数 = 波数 × 振幅，
// 两列分别是 2.4×0.016 与 3.7×0.009，叠加不到 0.07，法线只倾 3~4°（微风）。
// 这个量纲不能省：早先版本直接返回 d·cos，幅值接近 1，法线会倾到 15° 以上；
// 俯视时总有接近一半的像素法线背对视线，facing 被 saturate 到 0、Fresnel 饱和到 1，
// 水面在那一片退化成完全不透明的镜面反射块（叠加两列波的拍频后就是规则的黑圆斑）。
float2 WaterWave(float2 p, float t, float speed)
{
    float2 d1 = float2(0.86f, 0.51f);
    float2 d2 = float2(-0.42f, 0.91f);
    float k1 = dot(p, d1) * 2.4f + t * speed * 6.2831853f;
    float k2 = dot(p, d2) * 3.7f - t * speed * 5.1f;
    return d1 * cos(k1) * (2.4f * 0.016f) + d2 * cos(k2) * (3.7f * 0.009f);
}

// 雨涟漪：三组同心环，相位按索引错开，环心固定所以水面不会整体漂移。
// 同样是梯度量纲：环的空间频率 26/m、振幅 0.0025m，单环梯度约 0.065，
// 三环叠加后与风力项同量级，雨天才不会突然跳成另一套斜率尺度。
float2 WaterRipple(float2 p, float t)
{
    float2 acc = float2(0.0f, 0.0f);
    for (uint i = 0u; i < 3u; i++)
    {
        float2 c = float2(sin(float(i) * 12.9898f) * 7.0f, cos(float(i) * 78.233f) * 7.0f);
        float2 d = p - c;
        float r = length(d);
        float phase = frac(t * 1.7f + float(i) * 0.37f);
        float wave = sin(r * 26.0f - phase * 40.0f) * saturate(1.0f - phase);
        acc += d / max(r, 1e-4f) * wave;
    }
    return acc * (26.0f * 0.0025f);
}

// 视空间前向深度（LH + [0,1]），与主着色器 / GTAO / SsrEffect 的重建公式同源。
// 变量不能叫 near / far：它们是 fxc 的保留字（旧式裁剪平面语义），流分析会错报未初始化。
float WaterLinear(float d)
{
    float nearPlane = WaterF(12).z, farPlane = WaterF(12).w;
    return nearPlane * farPlane / (farPlane - d * (farPlane - nearPlane));
}

// 单一出口 + 显式 .x：fxc 4.x 对「模板化 SampleLevel 返回值隐式转 float + 多 return」的
// 流分析会错报 X4000（use of potentially uninitialized variable）。写成一个赋值链就没这条路径。
float WaterHiZ(float2 uv, int level)
{
    float value;
    if (level <= 0) value = waterHz0.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    else if (level == 1) value = waterHz1.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    else if (level == 2) value = waterHz2.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    else value = waterHz3.SampleLevel(waterLinearClamp, uv, 0.0f).x;
    return value;
}

// 屏幕空间反射：从水面沿反射方向在屏幕空间步进，用 Hi-Z 金字塔整块跳过空区域，
// 命中点取折射快照（开画前的场景色）—— 它正好是「水面之外的不透明场景」，
// 也避免了与颜色附件同一资源不能既读又写的矛盾。
// 返回 rgb = 反射辐射，a = 置信度（0 = 没命中，调用方退回天空）。
float4 WaterTraceReflection(float3 origin, float3 direction, float2 size)
{
    const int coarseSteps = 24;
    const int refineSteps = 6;
    float maxDistance = WaterF(13).x;
    float thickness = WaterF(13).y;
    float4 result = float4(0.0f, 0.0f, 0.0f, 0.0f);
    float4 clip0 = WaterTransform(float4(origin, 1.0f), 4u);
    float4 clip1 = WaterTransform(float4(origin + direction * maxDistance, 1.0f), 4u);

    // 射线走到相机背后时屏幕空间没有信息可用：沿原方向缩到相机近平面之前。
    float endT = 1.0f;
    if (clip1.w <= 0.001f)
    {
        float denom = clip1.w - clip0.w;
        endT = denom < -0.0001f ? saturate(-clip0.w / denom) : 0.0f;
        clip1 = WaterTransform(float4(origin + direction * (maxDistance * endT), 1.0f), 4u);
    }

    if (clip0.w > 0.001f && clip1.w > 0.001f && endT > 0.001f)
    {
        float2 uv0 = clip0.xy / clip0.w * float2(0.5f, -0.5f) + 0.5f;
        float2 uv1 = clip1.xy / clip1.w * float2(0.5f, -0.5f) + 0.5f;
        float z0 = clip0.z / clip0.w;
        float z1 = clip1.z / clip1.w;
        float2 delta = uv1 - uv0;

        // 每步跨多少像素决定查哪一级金字塔：短射线用细级，否则粗块永远命中不了。
        float pixels = length(delta * size) / (float)coarseSteps;
        int level = 3;
        if (pixels < 12.0f) level = 2;
        if (pixels < 6.0f) level = 1;
        if (pixels < 3.0f) level = 0;

        float2 coarse = uv0;
        float coarseZ = z0;
        [loop]
        for (int i = 1; i <= coarseSteps; i++)
        {
            float t = (float)i / (float)coarseSteps;
            float2 next = uv0 + delta * t;
            float nextZ = lerp(z0, z1, t);
            // 出屏即终止：屏幕外的信息不存在，继续走只会采到钳制后的边缘像素。
            if (next.x < 0.0f || next.x > 1.0f || next.y < 0.0f || next.y > 1.0f) break;
            // 射线深度还没越过这一块的最近表面：整块跳过。
            if (nextZ < WaterHiZ(next, level))
            {
                coarse = next;
                coarseZ = nextZ;
                continue;
            }
            // 越过了：在（上一节点, 当前节点）内逐点逼近真实交点。
            [loop]
            for (int j = 1; j <= refineSteps; j++)
            {
                float tj = (float)j / (float)refineSteps;
                float2 suv = lerp(coarse, next, tj);
                float rayZ = lerp(coarseZ, nextZ, tj);
                int2 coord = clamp(int2(suv * size), int2(0, 0), int2((int)size.x - 1, (int)size.y - 1));
                float sceneD = sceneDepth.Load(int3(coord, 0));
                if (sceneD >= 1.0f) continue;
                // 厚度判定放在线性深度上：NDC 深度越远越挤，同一个阈值在近处和远处差出几个数量级。
                float rayView = WaterLinear(rayZ);
                float sceneView = WaterLinear(sceneD);
                if (rayView >= sceneView && rayView - sceneView <= thickness)
                {
                    float2 hitUv = saturate(suv);
                    // 屏幕边缘淡出：那里的采样是外推出来的，硬切会在画面边缘拉出一条亮边。
                    float edge = min(min(hitUv.x, 1.0f - hitUv.x), min(hitUv.y, 1.0f - hitUv.y));
                    float fade = saturate(edge / 0.06f) * saturate(1.0f - t);
                    result = float4(refractionSource.SampleLevel(waterLinearClamp, hitUv, 0.0f).rgb, fade);
                    break;
                }
            }
            break;
        }
    }
    return result;
}

float4 PSMain(WaterVertex input) : SV_TARGET
{
    uint offset = WaterOffset(input.slot);
    float4 material0 = WaterF(offset + 4u);   // tint.rgb, baseOpacity
    float4 material1 = WaterF(offset + 5u);   // absorption, refraction, normalStrength, normalTiling
    float4 material2 = WaterF(offset + 6u);   // scrollSpeed, rippleStrength, fresnelF0, roughness
    float4 material3 = WaterF(offset + 7u);   // hasNormalTexture, sortOrder, uvOrigin.x, uvOrigin.z

    float3 camera = WaterF(8).xyz;
    float4 screen = WaterF(9);                // width, height, 1/width, 1/height
    float4 misc = WaterF(10);                 // count, raining, time, skyAvailable
    float4 sun = WaterF(11);                  // sunDirection.xyz, sunIntensity
    float time = misc.z;

    float2 uv = input.position.xy * screen.zw;
    float sceneDepthValue = sceneDepth.Load(int3(input.position.xy, 0));

    // 软件深度测试：本 Pass 没有深度附件，遮挡完全靠这一句。偏置用来吃掉水面与池底共面时的抖动。
    if (input.position.z > sceneDepthValue + 1e-6f) discard;

    // 厚度：从水面沿视线走到场景表面。深度为远平面时（天空、无表面）自然得到一个很大的值，
    // 吸收项因此饱和到水色本身，不需要额外分支。
    float4 ndc = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), sceneDepthValue, 1.0f);
    float4 scenePosition = ndc.x * WaterF(0) + ndc.y * WaterF(1) + ndc.z * WaterF(2) + ndc.w * WaterF(3);
    float thickness = max(length(scenePosition.xyz / scenePosition.w - input.world), 0.0f);

    // 切线空间斜率 → 世界法线。水面恒为 +Y 朝上，切线基就是 (1,0,0)/(0,0,1)，所以没有 TBN 矩阵。
    float2 planar = input.world.xz;
    float2 slope = WaterWave(planar, time, material2.x);
    if (material3.x > 0.5f)
    {
        float2 tiled = planar * material1.w;
        float2 a = waterNormalTexture.SampleLevel(waterLinearClamp, tiled + time * material2.x * float2(0.7f, 0.3f), 0.0f).rg * 2.0f - 1.0f;
        float2 b = waterNormalTexture.SampleLevel(waterLinearClamp, tiled * 1.37f - time * material2.x * float2(0.4f, 0.8f), 0.0f).rg * 2.0f - 1.0f;
        slope = (a + b) * 0.5f;
    }
    if (misc.y > 0.5f) slope += WaterRipple(planar * 2.0f, time) * material2.y;
    slope *= material1.z;
    // P4：细节斜率叠加到 Gerstner 的解析法线上，而不是从零重建。
    // 顶点位移已经把大尺度浪形写成真实几何，它的法线是解析求导得来的，比任何高度场差分都稳；
    // 细节项（小波长正弦 / 法线贴图 / 雨涟漪）负责 3~4° 的粗糙度，量纲与顶点法线的 xz 分量同量级，
    // 直接相加不会把法线推歪。Gerstner 关闭时 input.normal 恒为 (0,1,0)，这里退化成 P3 的 normalize(slope.x,1,slope.y)。
    float3 normal = normalize(input.normal + float3(slope.x, 0.0f, slope.y));

    float3 view = normalize(camera - input.world);
    float facing = saturate(dot(normal, view));
    float fresnel = material2.z + (1.0f - material2.z) * pow(1.0f - facing, 5.0f);

    // 折射：按斜率在屏幕上偏移采样，offset 的单位就是像素（后面乘 1/宽高换成 UV）。
    // 600 是把「梯度」折成像素尺度的常数：默认梯度 0.07 与 RefractionStrength 0.035 相乘约 1.5 像素。
    // 名字不能叫 offset：本函数的槽位偏移已经占了那个名字，HLSL 的函数体是单一作用域。
    float2 refractionOffset = slope * material1.y * 600.0f;
    float2 refractedUv = saturate(uv + refractionOffset * screen.zw);
    float3 refracted = refractionSource.SampleLevel(waterLinearClamp, refractedUv, 0.0f).rgb;

    // 吸收：红光衰减最快，所以厚处偏青蓝。三项指数分别对应 R/G/B 的吸收系数。
    float3 transmittance = exp(-material1.x * thickness * float3(1.35f, 0.85f, 0.55f));
    // 水体是被照亮的散射介质，水色不能自带绝对亮度：tint 是「水体反照率」，必须乘光照。
    // 之前它被直接加进 HDR，相机一到夜里、场景全暗之后池子会自己发光（自动曝光会把它再放大一次）。
    // 这里用池底快照的亮度当日光代理：夜里趋近 0，水色随之熄灭；白天饱和到 1，外观与原来一致。
    // 代理放在快照而不是常量，是为了顺便跟上局部光照（池底的焦散、火炬、闪电都会照亮水体）。
    float daylight = saturate(dot(refracted, float3(0.2126f, 0.7152f, 0.0722f)) * 3.0f);
    float3 body = refracted * transmittance + material0.rgb * (1.0f - transmittance) * daylight;

    // 反射：先试屏幕空间反射（Hi-Z 加速），没命中再退回程序化天空。
    // 射线用未钳制的方向：水面往下看时反射方向指向水底，天空 LUT 那里没有信息。
    float3 reflectDirection = reflect(-view, normal);
    // 天空采样前把地平线以下的部分钳回地平线：LUT 的下半球存的是近地雾霾（夜里几乎全黑），
    // 而俯视水面时法线只要有一点点倾斜就会把反射打到那里。不钳的话，法线扰动的极值点会
    // 整片变成暗斑，呈现为规则排列的黑圆点，而不是天空。
    float3 skyDirection = float3(reflectDirection.x, max(reflectDirection.y, 0.0f), reflectDirection.z);
    // 兑底色同样乘 daylight：StaticCube 档拿不到天空 LUT 时，这个常量不该在夜里继续发光。
    float3 reflection = misc.w > 0.5f ? WaterSampleSky(skyDirection) : float3(0.35f, 0.45f, 0.62f) * daylight;
    if (WaterF(12).x > 0.5f)
    {
        float4 traced = WaterTraceReflection(input.world, reflectDirection, screen.xy);
        reflection = lerp(reflection, traced.rgb, saturate(traced.a * WaterF(12).y));
    }
    // 太阳高光：sun.xyz 是「射向表面」的传播方向，指向太阳的向量要取反。
    float3 toSun = normalize(-sun.xyz);
    float3 halfVector = normalize(view + toSun);
    float shininess = 2.0f / max(material2.w * material2.w, 1e-3f);
    reflection += sun.w * pow(saturate(dot(normal, halfVector)), shininess) * float3(1.0f, 0.95f, 0.85f) * 0.5f;

    float3 color = lerp(body, reflection, fresnel);
    // 体积厚度会自己把水变得不透：薄处才看得到水底，厚处只剩水色。
    float thicknessAlpha = 1.0f - exp(-thickness * 0.9f);
    float alpha = saturate(max(lerp(material0.a, 1.0f, fresnel), thicknessAlpha * 0.9f));
    return float4(color, alpha);
}
""";
}
