// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 1-7 contract clause 3: cube-map face order, always +X,-X,+Y,-Y,+Z,-Z, identical face by face across all four backends
/// and naturally aligned with D3D12 subresource order, Vulkan arrayLayer order, Metal slice order, and the WebGPU layer order.
/// In-face orientation follows the standard D3D/GL convention:
///   +X: u→-Z, v↓→-Y; -X: u→+Z, v↓→-Y; +Y: u→+X, v↓→+Z;
///   -Y: u→+X, v↓→-Z; +Z: u→+X, v↓→-Y; -Z: u→-X, v↓→-Y.
/// The six existing Mesh3D skybox textures in the Sample already satisfy this convention by geometric placement, mapping to
/// rt=+X, lf=-X, up=+Y, dn=-Y, bk=+Z, and ft=-Z.
/// Note that the names bk/ft run opposite to the axis sign:
/// the default camera stands on +Z and looks toward the origin, so the "front" face is the one at z=-h.
/// </summary>
public enum CubeFace
{
    PositiveX = 0,
    NegativeX = 1,
    PositiveY = 2,
    NegativeY = 3,
    PositiveZ = 4,
    NegativeZ = 5,
}

/// <summary>
/// 1-7 contract clause 2: whitelist of cube-map pixel formats, following the same narrowing strategy used by ComputeStorageFormat.
/// At present, only Rgba8Unorm is implemented because the current source assets are six-face PNG images and the decoder contract is always RGBA8.
/// Rgba16Float is reserved for future .hdr or equirectangular input; backends throw NotSupported when asked for an unimplemented format.
/// </summary>
public enum TextureCubeFormat
{
    Rgba8Unorm = 0,
    Rgba16Float = 1,
}

/// <summary>
/// 1-7 contract clause 1: cube-map resource handle.
/// This is a shared-layer type independent from Controls.TextureType, because that type expresses UI texture semantics rather than GPU dimensionality and is not being expanded.
/// This type only carries "name + edge length + format + ready flag".
/// Native resources are registered by each backend into its own cube dictionary under <see cref="Name"/>, following the same name-as-handle convention used for 1-6 storage textures.
/// Later systems such as 2-4 DDGI can reference sky radiance by that name alone, with no cross-layer handle passing.
/// The cube has a single mip and six equal square faces, as required by contract clause 2.
/// </summary>
public sealed class TextureCube
{
    /// <summary>Registration name in the backend texture dictionary. This is the unique identifier referenced by DDGI and other downstream systems.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Edge length of one face. All six faces are equal squares.</summary>
    public int Size { get; init; }

    /// <summary>Pixel format, restricted by the whitelist from contract clause 2.</summary>
    public TextureCubeFormat Format { get; init; } = TextureCubeFormat.Rgba8Unorm;

    /// <summary>True once the native resource has been created and all six faces have been uploaded. When false, downstream users always fall back to a 1×1 black dummy cube.</summary>
    public bool Ready;
}

/// <summary>
/// 1-7 contract clause 5: environment-lighting mode.
/// Modes are mutually exclusive and may be switched at runtime without rebuilding resources, making A/B comparisons straightforward.
/// On the diffuse side, the choice is always one-or-the-other, never additive:
/// Off uses the constant ambient term SceneLightParams.Ambient, while Diffuse and DiffuseSpecular use SH9 environment diffuse lighting.
/// They must never be added together, or the overall environment light would be doubled.
/// </summary>
public enum EnvironmentLightingMode
{
    /// <summary>Disables environment-map lighting. Diffuse falls back to the constant Ambient path from 1-2, with no specular reflection. Visuals remain pixel-identical to the pre-1-7 path.</summary>
    Off,

    /// <summary>Diffuse-only environment lighting. SH9 irradiance replaces the constant Ambient term, with no specular reflection.</summary>
    Diffuse,

    /// <summary>Diffuse plus specular environment lighting. Adds a radiance-cube LOD0 specular term on top of Diffuse, per contract clause 6.</summary>
    DiffuseSpecular,
}

/// <summary>
/// 1-7 environment map: owner of one radiance cube plus its SH9 irradiance, serving as the shared layer's single source of truth.
/// The App assigns it to <see cref="Season.Basic.BaseApp.SceneEnvironment"/>, and each backend's SetLighting injects it through <see cref="Apply"/> into the tail of the lighting UBO
/// as EnvParams + IrradianceSH9, per contract clause 4.
/// Starting from 2-4 Step 0, it also owns <see cref="RadianceSH9"/>, used as sky radiance for DDGI ray misses.
/// That data does not go into the lighting UBO; DDGI carries it through its own parameter storage buffer.
///
/// Deliberate scope limits for 1-7, which implements only the necessary subset and leaves the rest for later:
/// there is no HDR/equirectangular decode, no mip chain, no GGX prefilter or BRDF LUT, no cube array, and no runtime texture swapping.
/// The specular path always samples LOD0 and applies a (1-roughness)² mask, per contract clause 6, while rough-surface energy is carried by SH9 diffuse lighting.
/// </summary>
public sealed class EnvironmentMap
{
    /// <summary>Number of cube-map faces, always 6.</summary>
    public const int FaceCount = 6;

    /// <summary>Number of SH9 coefficients. Bands l=0..2 yield 9 coefficients in total.</summary>
    public const int Sh9Count = 9;

    /// <summary>Radiance cube handle. Null means creation failed or the current backend does not support it, and the full pipeline falls back to constant Ambient.</summary>
    public TextureCube? Radiance;

    /// <summary>Environment-lighting mode, per contract clause 5, switchable at runtime. The default Off mode preserves existing visuals when this type is introduced.</summary>
    public EnvironmentLightingMode Mode = EnvironmentLightingMode.Off;

    /// <summary>Specular reflection intensity multiplier applied to cube samples. 1.0 means unchanged. This is a runtime knob.</summary>
    public float SkyIntensity = 1f;

    /// <summary>Environment diffuse intensity multiplier applied to SH9 irradiance.
    /// Also acts as a color-bias compensation knob for the simplifying assumption that face pixels are treated as linear, as described in contract clause 7.</summary>
    public float DiffuseIntensity = 1f;

    /// <summary>
    /// SH9 irradiance coefficients, xyz=RGB and w reserved as 0.
    /// They are computed once on the CPU with Lambert convolution coefficients A_l already pre-multiplied,
    /// so the shader only performs a 9-term linear combination, per contract clause 7.
    /// <see cref="LoadFromFacesAsync"/> fills them through <c>ProjectIrradianceSH9</c> after creating the cube and before releasing the decoders, in Step A2.
    /// Until that method runs, the array stays all zero, corresponding to DC=0, and <see cref="SphericalHarmonicsReady"/> stays false.
    /// </summary>
    public readonly Vector4[] IrradianceSH9 = new Vector4[Sh9Count];

    /// <summary>
    /// 2-4 Step 0: SH9 **radiance** coefficients, xyz=RGB with w reserved as 0.
    /// Given a direction ω, they reconstruct incident sky radiance L(ω) rather than Lambert-convolved irradiance.
    /// They use the same basis and ordering as <see cref="IrradianceSH9">IrradianceSH9</see>, namely the same 9 polynomials
    /// 1, y, z, x, xy, yz, 3z²-1, xz, x²-y², so the evaluation side can directly reuse the same combination shape as <c>EvaluateIrradianceSH9</c>.
    ///
    /// Their purpose is the sky-background radiance used when a DDGI probe ray **misses**.
    /// The reason not to sample the radiance cube directly is that doing so would require a new cube binding type for compute and end-to-end support on all four backends, including the web JS side,
    /// whereas 9 float4 values can simply ride inside DDGI's parameter storage buffer with zero infrastructure changes.
    ///
    /// Notes:
    /// - Second-order SH reconstruction can ring due to Gibbs behavior, producing negative values under high-contrast skies, so consumers must apply max(0, ·).
    /// - These values are derived from the same accumulated quantities as irradiance, see <see cref="DeriveRadianceSH9"/>, so they are naturally source-consistent with it;
    ///   they also inherit the same simplifying assumption from contract clause 7 that face pixels are treated as linear.
    /// - Intensity is multiplied later on the consumer side by <see cref="SkyIntensity"/>, the same knob used by the specular path, so this array is not pre-scaled.
    /// - Readiness matches <see cref="SphericalHarmonicsReady"/>. When not ready, the array remains all zero.
    /// </summary>
    public readonly Vector4[] RadianceSH9 = new Vector4[Sh9Count];

    /// <summary>Whether SH9 data has been computed. When false, even Mode=Diffuse falls back to constant Ambient to avoid a black result.</summary>
    public bool SphericalHarmonicsReady;

    /// <summary>Whether the radiance cube is available, meaning the native resource is ready.</summary>
    public bool Ready => Radiance is { Ready: true };

    /// <summary>Cube registration name in the backend texture dictionary, used by DDGI and other downstream systems. Null when not ready.</summary>
    public string? RadianceName => Ready ? Radiance!.Name : null;

    /// <summary>
    /// Creates a radiance cube from six face textures, which must follow the <see cref="CubeFace"/> declaration order:
    /// +X,-X,+Y,-Y,+Z,-Z.
    /// Decoding uses the existing shared-layer path through <c>DeviceServices.Core.LoadFileAsync</c> and <c>ImageUtils.GetImageFromStreamAsync</c>,
    /// shared by all four backends with a decoder contract fixed at RGBA8, so this method requires no new asset-format support.
    /// All six faces must have the same size and be square. Any failure returns null and logs the issue, degrading gracefully back to constant Ambient.
    /// </summary>
    public static async Task<EnvironmentMap?> LoadFromFacesAsync(
        string name, string[] facePaths, TextureCubeFormat format = TextureCubeFormat.Rgba8Unorm)
    {
        if (facePaths is null || facePaths.Length != FaceCount)
            throw new ArgumentException($"EnvironmentMap requires exactly {FaceCount} face textures, ordered as +X,-X,+Y,-Y,+Z,-Z.", nameof(facePaths));

        var graphics = Season.Basic.Graphics.Instance;
        if (graphics is null || !graphics.TextureCubeSupported)
        {
            DeviceServices.BaseApp?.AddLog(LogType.GI, $"{DateTime.UtcNow} [EnvironmentMap] '{name}' skipped: TextureCube is not wired up on this backend yet.");
            return null;
        }

        var faces = new Season.Basic.INativeImageDecoder?[FaceCount];

        try
        {
            for (int i = 0; i < FaceCount; i++)
            {
                var path = facePaths[i];
                var ext = System.IO.Path.GetExtension(path);

                using var stream = await Season.Basic.DeviceServices.Core.LoadFileAsync(path);
                if (stream is null)
                {
                    DeviceServices.BaseApp?.AddLog(LogType.GI, $"{DateTime.UtcNow} [EnvironmentMap] '{name}' failed to read face {(CubeFace)i}: {path}");
                    return null;
                }

                faces[i] = await Season.Models.ImageUtils.GetImageFromStreamAsync(stream, ext);
                if (faces[i] is null)
                {
                    DeviceServices.BaseApp?.AddLog(LogType.GI, $"{DateTime.UtcNow} [EnvironmentMap] '{name}' failed to decode face {(CubeFace)i}: {path}");
                    return null;
                }
            }

            int size = faces[0]!.Width;
            for (int i = 0; i < FaceCount; i++)
            {
                var f = faces[i]!;
                if (f.Width != f.Height || f.Width != size)
                {
                    DeviceServices.BaseApp?.AddLog(LogType.GI, $"{DateTime.UtcNow} [EnvironmentMap] '{name}' face size mismatch: face {(CubeFace)i} is {f.Width}×{f.Height}, expected a {size}×{size} square.");
                    return null;
                }
            }

            // This method must be callable from a background thread because the call site must not block the first frame, as noted below in the App-side comments.
            // Creating the cube allocates images, submits transfer work, and waits for the queue to become idle.
            // ResizeSemaphore is already this project's mutual-exclusion gate for "GPU resource work executed on a background thread",
            // as described in its summary and in the matching use inside BaseApp.StartConsumerLoop, so the same gate is reused instead of inventing new synchronization.
            // Only the GPU section is wrapped by the semaphore. The decode above and the SH9 projection below are pure CPU work, and holding the gate for them would only slow control loading.
            TextureCube? cube;
            await Season.Basic.BaseApp.ResizeSemaphore.WaitAsync();
            try
            {
                cube = graphics.CreateTextureCube(name, size, format, faces!);
            }
            finally
            {
                Season.Basic.BaseApp.ResizeSemaphore.Release();
            }

            if (cube is null)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [EnvironmentMap] '{name}' failed to create cube. See backend logs.");
                return null;
            }

            var env = new EnvironmentMap { Radiance = cube };

            // Step A2: SH9 projection.
            // It uses the same decoded pixel batch as the cube, so it must complete before the decoders are released in finally.
            // Face sizes have already been validated above, so the projection itself cannot fail.
            // This is pure CPU work and happens once. The log includes timing and DC for validation,
            // where DC is the solid-angle-weighted average sky color and can be compared directly against the scale of constant Ambient.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ProjectIrradianceSH9(faces!, size, env.IrradianceSH9);
            DeriveRadianceSH9(env.IrradianceSH9, env.RadianceSH9);
            env.SphericalHarmonicsReady = true;
            watch.Stop();

            var dc = env.IrradianceSH9[0];
            DeviceServices.BaseApp?.AddLog(LogType.GI, $"{DateTime.UtcNow} [EnvironmentMap] '{name}' SH9 ready: projection of {size}×{size}×6 took {watch.Elapsed.TotalMilliseconds:F1}ms, DC=({dc.X:F3}, {dc.Y:F3}, {dc.Z:F3}).");

            return env;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [EnvironmentMap] '{name}' load exception: {ex.Message}");
            return null;
        }
        finally
        {
            for (int i = 0; i < FaceCount; i++)
                faces[i]?.Dispose();
        }
    }

    /// <summary>
    /// Per-face texel-direction basis.
    /// This is the vectorized form of the in-face orientation table from contract clause 3, moving the per-texel face switch outside the inner loop.
    /// dir(s,t) = Normal + s·AxisU + t·AxisV, where s/t ∈ [-1,1] are face-local texel-center coordinates and positive t points downward along v.
    /// The vector is intentionally unnormalized, with |dir| = √(s²+t²+1), sharing the same denominator as the solid-angle formula.
    /// </summary>
    readonly struct CubeFaceBasis
    {
        public readonly Vector3 Normal;
        public readonly Vector3 AxisU;
        public readonly Vector3 AxisV;

        public CubeFaceBasis(Vector3 normal, Vector3 axisU, Vector3 axisV)
        {
            Normal = normal;
            AxisU = axisU;
            AxisV = axisV;
        }
    }

    /// <summary>Face order matches <see cref="CubeFace"/>. The row comments are the contract-clause-3 in-face orientation table written directly into code.</summary>
    static readonly CubeFaceBasis[] FaceBasis =
    {
        new(new Vector3(+1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, -1, 0)),   // +X: u→-Z, v↓→-Y
        new(new Vector3(-1, 0, 0), new Vector3(0, 0, +1), new Vector3(0, -1, 0)),   // -X: u→+Z, v↓→-Y
        new(new Vector3(0, +1, 0), new Vector3(+1, 0, 0), new Vector3(0, 0, +1)),   // +Y: u→+X, v↓→+Z
        new(new Vector3(0, -1, 0), new Vector3(+1, 0, 0), new Vector3(0, 0, -1)),   // -Y: u→+X, v↓→-Z
        new(new Vector3(0, 0, +1), new Vector3(+1, 0, 0), new Vector3(0, -1, 0)),   // +Z: u→+X, v↓→-Y
        new(new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, -1, 0)),   // -Z: u→-X, v↓→-Y
    };

    /// <summary>
    /// Contract clause 7 / Step A2: CPU-side SH9 irradiance projection.
    /// This is pure CPU work, computed once, and consumes no GPU resources.
    ///
    /// Division of labor with the shader:
    /// the 9 coefficients produced here are already pre-multiplied by the Lambert convolution coefficients A_l, by basis normalization k_i, and by 1/π.
    /// That means <c>EvaluateIrradianceSH9</c> only needs a 9-term polynomial linear combination, and its result is E(n)/π.
    /// It can therefore be multiplied directly by albedo as a replacement for constant Ambient. The two share the same units, which is why contract clause 5's "choose one rather than add both" rule holds.
    /// Basis-function ordering matches the shader term by term: 1, y, z, x, xy, yz, 3z²-1, xz, x²-y².
    /// The pre-multiplied factor W = A_l·k_i²/π has a closed form and has been checked term by term against the Ramamoorthi &amp; Hanrahan irradiance formula c1..c5:
    /// l0=1/4π, l1=1/2π, xy|yz|xz=15/16π, 3z²-1=5/64π, x²-y²=15/64π.
    ///
    /// Energy sanity check for regression:
    /// a completely white environment with radiance≡1 yields DC=1.0 and zero for the other 8 terms.
    /// In the general case, DC is exactly the solid-angle-weighted average color across the six faces, i.e. the "average sky brightness".
    ///
    /// Simplifying assumption from contract clause 7:
    /// face pixels are treated as linear, with no sRGB→linear decode.
    /// This is intentionally consistent with the specular path, where the cube is created as R8G8B8A8_UNORM rather than _SRGB and the sampled values are likewise encoded values.
    /// That keeps diffuse and reflection sourced from the same representation, while any overall brightness bias is compensated by <see cref="DiffuseIntensity"/>.
    /// </summary>
    static void ProjectIrradianceSH9(Season.Basic.INativeImageDecoder[] faces, int size, Vector4[] result)
    {
        // Σ radiance·P_i(ω)·dω, where P_i are the unnormalized basis functions listed above and xyz carries RGB.
        Span<Vector3> sum = stackalloc Vector3[Sh9Count];
        float omegaSum = 0f;

        float texel = 2f / size;
        const float inv255 = 1f / 255f;

        for (int f = 0; f < FaceCount; f++)
        {
            var decoder = faces[f];
            var pixels = decoder.PixelSpan;
            int stride = decoder.Stride;              // Decoder stride may include padding, so rows must advance by this exact amount.
            ref readonly var basis = ref FaceBasis[f];

            for (int y = 0; y < size; y++)
            {
                float t = (y + 0.5f) * texel - 1f;    // Texel center mapped into [-1,1].
                int row = y * stride;

                for (int x = 0; x < size; x++)
                {
                    float s = (x + 0.5f) * texel - 1f;

                    // Cube-texel solid angle dω = ds·dt/(s²+t²+1)^{3/2}; the six faces always sum to 4π.
                    // The same d2 also provides the direction-normalization factor 1/|dir|.
                    float d2 = s * s + t * t + 1f;
                    float invLen = 1f / MathF.Sqrt(d2);
                    float dOmega = texel * texel * invLen / d2;

                    var dir = basis.Normal + basis.AxisU * s + basis.AxisV * t;
                    float nx = dir.X * invLen, ny = dir.Y * invLen, nz = dir.Z * invLen;

                    int p = row + x * 4;              // RGBA8, per the INativeImageDecoder contract.
                    var radiance = new Vector3(pixels[p], pixels[p + 1], pixels[p + 2]) * (inv255 * dOmega);

                    sum[0] += radiance;
                    sum[1] += radiance * ny;
                    sum[2] += radiance * nz;
                    sum[3] += radiance * nx;
                    sum[4] += radiance * (nx * ny);
                    sum[5] += radiance * (ny * nz);
                    sum[6] += radiance * (3f * nz * nz - 1f);
                    sum[7] += radiance * (nx * nz);
                    sum[8] += radiance * (nx * nx - ny * ny);

                    omegaSum += dOmega;
                }
            }
        }

        // Numerical normalization: rescale the measured accumulated solid angle to 4π to cancel the error from texel-center discretization,
        // making "white environment → DC=1.0" hold exactly at any face resolution.
        float norm = omegaSum > 0f ? 4f * MathF.PI / omegaSum : 0f;

        const float invPi = 1f / MathF.PI;
        float wL0 = 0.25f * invPi;              // 1/4π
        float wL1 = 0.5f * invPi;               // 1/2π
        float wL2 = 15f / 16f * invPi;          // xy, yz, xz
        float wL2z = 5f / 64f * invPi;          // 3z²-1
        float wL2d = 15f / 64f * invPi;         // x²-y²

        Span<float> scale = stackalloc float[Sh9Count]
        {
            wL0, wL1, wL1, wL1, wL2, wL2, wL2z, wL2, wL2d
        };

        for (int i = 0; i < Sh9Count; i++)
            result[i] = new Vector4(sum[i] * (norm * scale[i]), 0f);
    }

    /// <summary>
    /// 2-4 Step 0: derives radiance coefficients directly from irradiance coefficients. See <see cref="RadianceSH9"/>.
    ///
    /// No second projection pass is needed.
    /// Both use the same accumulated quantities sum[i] = ∫L·P_i dω and differ only in the final constant multiplier:
    /// radiance reconstruction uses k_i² as its weight, since L(ω) = Σ (k_i·sum[i])·Y_i = Σ k_i²·sum[i]·P_i,
    /// while irradiance uses k_i²·A_l/π because Lambert convolution adds A_l and 1/π.
    /// The ratio is therefore always π/A_l, with A_l = {π, 2π/3, π/4}, giving per-band ratios {1, 3/2, 4}.
    ///
    /// Term-by-term alignment for regression:
    /// ProjectIrradianceSH9 gives wL0=1/4π × 1 = 1/4π = k_00²;
    /// wL1=1/2π × 3/2 = 3/4π = k_1m²;
    /// wL2=15/16π × 4 = 15/4π = k_2² for xy|yz|xz;
    /// wL2z=5/64π × 4 = 5/16π;
    /// wL2d=15/64π × 4 = 15/16π.
    ///
    /// Energy sanity check:
    /// a white environment with radiance≡1 still yields DC=1.0 because the l=0 ratio is 1, and all other 8 terms remain 0.
    /// </summary>
    static void DeriveRadianceSH9(Vector4[] irradiance, Vector4[] result)
    {
        // Term order matches IrradianceSH9 exactly: 1 | y, z, x | xy, yz, 3z²-1, xz, x²-y².
        Span<float> ratio = stackalloc float[Sh9Count]
        {
            1f, 1.5f, 1.5f, 1.5f, 4f, 4f, 4f, 4f, 4f
        };

        for (int i = 0; i < Sh9Count; i++)
            result[i] = irradiance[i] * ratio[i];
    }

    /// <summary>
    /// Contract clause 4: writes environment parameters into the tail of the lighting UBO.
    /// Called only from each backend's SetLighting, following the same rule as hdrExposure and VelocityParams, so direct writes from the App side have no effect.
    /// EnvParams: x=specular intensity, y=diffuse intensity, z=diffuse switch (use SH9 when &gt;0.5, otherwise constant Ambient),
    /// w=specular switch (enable the LOD0 specular term when &gt;0.5).
    /// All zeros means a full fallback to 1-2 behavior.
    /// </summary>
    public void Apply(ref SceneLightParams lightParams)
    {
        bool useDiffuse = Mode != EnvironmentLightingMode.Off && SphericalHarmonicsReady;
        bool useSpecular = Mode == EnvironmentLightingMode.DiffuseSpecular && Ready;

        lightParams.EnvParams = new Vector4(
            SkyIntensity,
            DiffuseIntensity,
            useDiffuse ? 1f : 0f,
            useSpecular ? 1f : 0f);

        if (useDiffuse)
        {
            for (int i = 0; i < Sh9Count; i++)
                lightParams.IrradianceSH9[i] = IrradianceSH9[i];
        }
    }
}
