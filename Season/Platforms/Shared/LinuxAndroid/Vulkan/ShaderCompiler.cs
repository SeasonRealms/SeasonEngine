// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Equivalent to DX12 ShaderCompiler:
/// GLSL source strings are compiled to SPIR-V byte arrays at runtime.
/// Nothing is precompiled to .spv. All GLSL source is inlined in VKPipeline.CreatePipelineState,
/// following the same style as inline DX HLSL.
/// The compiled result is then wrapped into a VkShaderModule by Vk.CreateShaderModule.
///
/// Backend: the glslang 16.x C interface, replacing Silk.NET.Shaderc.
/// Note:
/// the simplified glslang C API does not support custom entry-point names and always uses "main".
/// All shaders in this project use "main", so the entryPoint parameter is kept only for compatibility.
///
/// Compiled SPIR-V is cached by content, matching MTLShaderCompiler on Metal and ShaderCompiler on DX12.
/// Pipeline.Init and BlitPipeline.Init bake several PSO variants whose GLSL differs only in injected defines,
/// so many requests resolve to identical source and would otherwise pay for a full glslang
/// parse/link/SPIR-V pass each time, right inside the pre-first-frame window.
/// The VkShaderModule lifetime is deliberately left untouched: modules stay cheap per-pipeline objects that
/// callers still destroy after pipeline creation, and only the expensive glslang step is shared.
/// </summary>
internal static unsafe class ShaderCompiler
{
    static readonly object s_initLock = new();
    static bool s_initialized;

    static readonly object _cacheLock = new();

    /// <summary>The key covers every input that can change the emitted SPIR-V.</summary>
    static readonly Dictionary<(string Source, ShaderStageFlags Stage, bool Debug), byte[]> _spirvCache = new();

    /// <summary>glslang invocations that actually ran, in other words cache misses.</summary>
    public static int CompileCount { get; private set; }

    /// <summary>Requests answered from the SPIR-V cache without invoking glslang.</summary>
    public static int CacheHitCount { get; private set; }

    static void EnsureInitialized()
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;
            // glslang_initialize_process returns non-zero on success.
            if (Glslang.InitializeProcess() == 0)
                throw new Exception("glslang_initialize_process failed");
            s_initialized = true;
        }
    }

    /// <summary>
    /// Compile GLSL source into SPIR-V.
    /// </summary>
    /// <param name="glslSource">GLSL source code, including the #version 460 header.</param>
    /// <param name="stage">Shader stage, vertex, fragment, and so on.</param>
    /// <param name="entryPoint">Entry-point name. The simplified glslang API only supports "main", so this parameter is retained for compatibility.</param>
    /// <param name="fileName">Logical file name used only for error diagnostics. The simplified glslang API does not consume it.</param>
    /// <param name="debug">When DEBUG=true, include DebugInfo.</param>
    /// <returns>SPIR-V byte array, 4-byte aligned. The array is owned by the cache, so callers must treat it as read-only.</returns>
    public static byte[] CompileGlsl(
        string glslSource,
        ShaderStageFlags stage,
        string entryPoint = "main",
        string fileName = "inline.glsl",
        bool debug = false)
    {
        // entryPoint and fileName stay out of the key on purpose: the simplified glslang C API always compiles
        // "main" and consumes fileName only for diagnostics, so neither can change the emitted SPIR-V.
        var key = (glslSource, stage, debug);

        lock (_cacheLock)
        {
            if (_spirvCache.TryGetValue(key, out var cached))
            {
                CacheHitCount++;
                return cached;
            }

            var spirv = CompileGlslUncached(glslSource, stage, debug);
            _spirvCache[key] = spirv;
            CompileCount++;
            return spirv;
        }
    }

    static byte[] CompileGlslUncached(string glslSource, ShaderStageFlags stage, bool debug)
    {
        EnsureInitialized();

        var glslangStage = MapStage(stage);

        // glslang requires input.code to be NUL-terminated UTF-8.
        var srcUtf8 = Encoding.UTF8.GetBytes(glslSource + "\0");

        int messages = GlslangMessages.SpvRules | GlslangMessages.VulkanRules;
        if (debug) messages |= GlslangMessages.DebugInfo;

        fixed (byte* pSrc = srcUtf8)
        {
            var input = new GlslangInput
            {
                Language = GlslangSource.Glsl,
                Stage = glslangStage,
                Client = GlslangClient.Vulkan,
                ClientVersion = GlslangTargetClientVersion.Vulkan12,
                TargetLanguage = GlslangTargetLanguage.Spv,
                TargetLanguageVersion = GlslangTargetLanguageVersion.Spv15,
                Code = (IntPtr)pSrc,
                DefaultVersion = 460,
                DefaultProfile = GlslangProfile.None,
                ForceDefaultVersionAndProfile = 0,
                ForwardCompatible = 0,
                Messages = messages,
                Resource = Glslang.DefaultResource(),
                CallbackIncludeLocal = IntPtr.Zero,
                CallbackIncludeSystem = IntPtr.Zero,
                CallbackFreeIncludeResult = IntPtr.Zero,
                CallbacksCtx = IntPtr.Zero,
            };

            var shader = Glslang.ShaderCreate(&input);
            if (shader == IntPtr.Zero)
                throw new Exception($"glslang_shader_create returned null [{stage}]");

            try
            {
                if (Glslang.ShaderPreprocess(shader, &input) == 0)
                    throw BuildShaderError("preprocess", shader, stage);

                if (Glslang.ShaderParse(shader, &input) == 0)
                    throw BuildShaderError("parse", shader, stage);

                var program = Glslang.ProgramCreate();
                if (program == IntPtr.Zero)
                    throw new Exception($"glslang_program_create returned null [{stage}]");

                try
                {
                    Glslang.ProgramAddShader(program, shader);

                    if (Glslang.ProgramLink(program, messages) == 0)
                        throw BuildProgramError("link", program, stage);

                    Glslang.ProgramSpirvGenerate(program, (int)glslangStage);

                    var wordCount = (int)(uint)Glslang.ProgramSpirvGetSize(program);
                    if (wordCount <= 0)
                        throw new Exception($"glslang produced empty SPIR-V [{stage}]");

                    var byteCount = wordCount * sizeof(uint);
                    var spirv = new byte[byteCount];
                    fixed (byte* pSpv = spirv)
                        Glslang.ProgramSpirvGet(program, (uint*)pSpv);

                    return spirv;
                }
                finally
                {
                    Glslang.ProgramDelete(program);
                }
            }
            finally
            {
                Glslang.ShaderDelete(shader);
            }
        }
    }

    /// <summary>
    /// Compile GLSL and directly create a VkShaderModule.
    /// This is the common entry used by VKPipeline.
    /// </summary>
    public static ShaderModule CreateShaderModule(
        Vk vk,
        Silk.NET.Vulkan.Device device,
        string glslSource,
        ShaderStageFlags stage,
        string entryPoint = "main",
        string fileName = "inline.glsl",
        bool debug = false)
    {
        var spirv = CompileGlsl(glslSource, stage, entryPoint, fileName, debug);

        fixed (byte* pCode = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)pCode
            };

            if (vk.CreateShaderModule(device, in info, null, out var module) != Result.Success)
                throw new Exception($"vkCreateShaderModule failed [{stage}]");
            return module;
        }
    }

    static GlslangStage MapStage(ShaderStageFlags stage) => stage switch
    {
        ShaderStageFlags.VertexBit => GlslangStage.Vertex,
        ShaderStageFlags.FragmentBit => GlslangStage.Fragment,
        ShaderStageFlags.GeometryBit => GlslangStage.Geometry,
        ShaderStageFlags.TessellationControlBit => GlslangStage.TessControl,
        ShaderStageFlags.TessellationEvaluationBit => GlslangStage.TessEvaluation,
        ShaderStageFlags.ComputeBit => GlslangStage.Compute,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), $"Unsupported shader stage: {stage}")
    };

    static Exception BuildShaderError(string phase, IntPtr shader, ShaderStageFlags stage)
    {
        var info = Marshal.PtrToStringUTF8(Glslang.ShaderGetInfoLog(shader)) ?? string.Empty;
        var debug = Marshal.PtrToStringUTF8(Glslang.ShaderGetInfoDebugLog(shader)) ?? string.Empty;
        return new Exception($"glslang shader {phase} failed [{stage}]\n{info}\n{debug}");
    }

    static Exception BuildProgramError(string phase, IntPtr program, ShaderStageFlags stage)
    {
        var info = Marshal.PtrToStringUTF8(Glslang.ProgramGetInfoLog(program)) ?? string.Empty;
        return new Exception($"glslang program {phase} failed [{stage}]\n{info}");
    }
}
