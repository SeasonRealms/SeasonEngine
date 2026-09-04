// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Minimal P/Invoke bindings for the glslang 16.x C interface, glslang_c_interface.h.
/// Exposes only the functions actually needed by ShaderCompiler and does not provide higher-level wrappers.
///
/// Deployment locations:
/// - Android: Platforms/Android/libs/arm64-v8a/libglslang.so, the CI static-merged artifact with libc++ embedded statically
/// - Linux:   Platforms/Linux/runtimes/linux-{x64,arm64}/native/libglslang.so
///
/// Note:
/// glslang_default_resource comes from the glslang-default-resource-limits target
/// and is merged into libglslang.so during the CI --whole-archive step, so it is resolvable at runtime.
/// </summary>
internal static unsafe class Glslang
{
    public const string LibName = "glslang";

    // NOTE: DllImport resolution for "glslang" on Linux is handled by
    // LinuxNativeLibraryResolver (Platforms/Linux/), which is the single
    // resolver registered for the Season assembly via [ModuleInitializer].
    // On Android the runtime's JNI loader resolves libglslang.so via
    // nativeLibraryDirectories, so no resolver hook is needed there.
    // .NET only allows ONE SetDllImportResolver per assembly; this class
    // must NOT register its own.

    // ---- Process-level initialization ----

    [DllImport(LibName, EntryPoint = "glslang_initialize_process", CallingConvention = CallingConvention.Cdecl)]
    public static extern int InitializeProcess();

    [DllImport(LibName, EntryPoint = "glslang_finalize_process", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FinalizeProcess();

    // ---- Shader ----

    [DllImport(LibName, EntryPoint = "glslang_shader_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ShaderCreate(GlslangInput* input);

    [DllImport(LibName, EntryPoint = "glslang_shader_delete", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ShaderDelete(IntPtr shader);

    [DllImport(LibName, EntryPoint = "glslang_shader_preprocess", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ShaderPreprocess(IntPtr shader, GlslangInput* input);

    [DllImport(LibName, EntryPoint = "glslang_shader_parse", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ShaderParse(IntPtr shader, GlslangInput* input);

    [DllImport(LibName, EntryPoint = "glslang_shader_get_info_log", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ShaderGetInfoLog(IntPtr shader);

    [DllImport(LibName, EntryPoint = "glslang_shader_get_info_debug_log", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ShaderGetInfoDebugLog(IntPtr shader);

    // ---- Program ----

    [DllImport(LibName, EntryPoint = "glslang_program_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ProgramCreate();

    [DllImport(LibName, EntryPoint = "glslang_program_delete", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProgramDelete(IntPtr program);

    [DllImport(LibName, EntryPoint = "glslang_program_add_shader", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProgramAddShader(IntPtr program, IntPtr shader);

    [DllImport(LibName, EntryPoint = "glslang_program_link", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ProgramLink(IntPtr program, int messages);

    [DllImport(LibName, EntryPoint = "glslang_program_SPIRV_generate", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProgramSpirvGenerate(IntPtr program, int stage);

    [DllImport(LibName, EntryPoint = "glslang_program_SPIRV_get_size", CallingConvention = CallingConvention.Cdecl)]
    public static extern UIntPtr ProgramSpirvGetSize(IntPtr program);

    [DllImport(LibName, EntryPoint = "glslang_program_SPIRV_get", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProgramSpirvGet(IntPtr program, uint* outBuffer);

    [DllImport(LibName, EntryPoint = "glslang_program_get_info_log", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ProgramGetInfoLog(IntPtr program);

    // ---- Default Resource (TBuiltInResource) ----

    [DllImport(LibName, EntryPoint = "glslang_default_resource", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr DefaultResource();
}

/// <summary>
/// Field-by-field mirror of glslang_input_t, where field order is layout-sensitive.
/// In C, callbacks is an inlined struct passed by value with three function pointers.
/// It is expanded here into three IntPtr fields to preserve the exact layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GlslangInput
{
    public GlslangSource Language;
    public GlslangStage Stage;
    public GlslangClient Client;
    public int ClientVersion;            // GlslangTargetClientVersion.*
    public GlslangTargetLanguage TargetLanguage;
    public int TargetLanguageVersion;    // GlslangTargetLanguageVersion.*
    public IntPtr Code;                  // const char* (NUL-terminated UTF-8)
    public int DefaultVersion;
    public GlslangProfile DefaultProfile;
    public int ForceDefaultVersionAndProfile;
    public int ForwardCompatible;
    public int Messages;                 // GlslangMessages.*
    public IntPtr Resource;              // const glslang_resource_t* from DefaultResource()

    // glslang_include_callbacks_t, inlined by value
    public IntPtr CallbackIncludeLocal;
    public IntPtr CallbackIncludeSystem;
    public IntPtr CallbackFreeIncludeResult;

    public IntPtr CallbacksCtx;
}

internal enum GlslangSource : int
{
    None = 0,
    Glsl = 1,
    Hlsl = 2,
}

internal enum GlslangStage : int
{
    Vertex = 0,
    TessControl = 1,
    TessEvaluation = 2,
    Geometry = 3,
    Fragment = 4,
    Compute = 5,
}

internal enum GlslangClient : int
{
    None = 0,
    Vulkan = 1,
    OpenGL = 2,
}

internal enum GlslangTargetLanguage : int
{
    None = 0,
    Spv = 1,
}

internal static class GlslangTargetClientVersion
{
    public const int Vulkan10 = 1 << 22;                 // 0x00400000
    public const int Vulkan11 = (1 << 22) | (1 << 12);   // 0x00401000
    public const int Vulkan12 = (1 << 22) | (2 << 12);   // 0x00402000
    public const int Vulkan13 = (1 << 22) | (3 << 12);   // 0x00403000
}

internal static class GlslangTargetLanguageVersion
{
    public const int Spv10 = 1 << 16;                    // 0x00010000
    public const int Spv11 = (1 << 16) | (1 << 8);       // 0x00010100
    public const int Spv12 = (1 << 16) | (2 << 8);       // 0x00010200
    public const int Spv13 = (1 << 16) | (3 << 8);       // 0x00010300
    public const int Spv14 = (1 << 16) | (4 << 8);       // 0x00010400
    public const int Spv15 = (1 << 16) | (5 << 8);       // 0x00010500
    public const int Spv16 = (1 << 16) | (6 << 8);       // 0x00010600
}

internal enum GlslangProfile : int
{
    None = 1 << 0,           // GLSLANG_NO_PROFILE
    Core = 1 << 1,
    Compatibility = 1 << 2,
    Es = 1 << 3,
}

internal static class GlslangMessages
{
    public const int Default = 0;
    public const int RelaxedErrors = 1 << 0;
    public const int SuppressWarnings = 1 << 1;
    public const int SpvRules = 1 << 3;
    public const int VulkanRules = 1 << 4;
    public const int DebugInfo = 1 << 10;
    public const int Enhanced = 1 << 15;
}
