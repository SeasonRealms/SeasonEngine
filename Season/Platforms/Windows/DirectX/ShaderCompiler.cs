// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Compiles inline HLSL to DXBC at runtime through fxc (D3DCompile) and caches the resulting blobs by
/// content, matching MTLShaderCompiler on Metal and ShaderCompiler on Vulkan.
///
/// Why the cache exists: Pipeline.Init and BlitPipeline.Init bake ~36 graphics PSO variants, but most
/// variants differ only in rasterizer / blend / depth / render-target state and share byte-for-byte
/// identical bytecode. Without caching, the same shader was recompiled six to seven times over, and every
/// duplicate compile landed inside the pre-first-frame window the user sees as a white screen.
///
/// Ownership: the cache keeps one reference per entry and every hand-out adds another, so callers keep the
/// existing contract of releasing the blob once the PSO is created. Entries live for the process lifetime,
/// which is the same trade Metal makes with its IMTLLibrary cache.
/// </summary>
internal static unsafe class ShaderCompiler
{
    static readonly object _cacheLock = new();

    /// <summary>The key covers every input that can change the emitted bytecode.</summary>
    static readonly Dictionary<(string Source, string EntryPoint, string Target, uint Flags), nint> _cache = new();

    static D3DCompiler _compiler;

    /// <summary>fxc invocations that actually ran, in other words cache misses.</summary>
    internal static int CompileCount { get; private set; }

    /// <summary>Requests answered from the blob cache without invoking fxc.</summary>
    internal static int CacheHitCount { get; private set; }

    internal static ID3D10Blob* CompileShaderFromSource(
        string hlslSource,
        string entryPoint,
        string target,
        uint compileFlags = 0)
    {
        var key = (hlslSource, entryPoint, target, compileFlags);

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                var hit = (ID3D10Blob*)cached;
                hit->AddRef();
                CacheHitCount++;
                return hit;
            }

            var compiled = Compile(hlslSource, entryPoint, target, compileFlags);
            _cache[key] = (nint)compiled;
            CompileCount++;

            // One reference stays with the cache, the returned one belongs to the caller.
            compiled->AddRef();
            return compiled;
        }
    }

    static ID3D10Blob* Compile(string hlslSource, string entryPoint, string target, uint compileFlags)
    {
        // 1. Get the D3DCompiler instance, reused across compilations.
        _compiler ??= D3DCompiler.GetApi();

        // 2. Prepare compilation parameters.
        ID3D10Blob* shaderBlob = null;
        ID3D10Blob* errorBlob = null;

        // 3. Convert the C# string to a byte array.
        byte[] sourceBytes = System.Text.Encoding.UTF8.GetBytes(hlslSource);

        // 4. Pin the byte array with GCHandle and marshal the ANSI parameters.
        GCHandle handle = GCHandle.Alloc(sourceBytes, GCHandleType.Pinned);
        IntPtr entryPtr = Marshal.StringToHGlobalAnsi(entryPoint);
        IntPtr targetPtr = Marshal.StringToHGlobalAnsi(target);
        try
        {
            // 5. Create safe pointers.
            nint sourcePtr = handle.AddrOfPinnedObject();

            // 6. Compile the shader.
            HResult result = _compiler.Compile(
        pSrcData: (void*)sourcePtr,
        SrcDataSize: (nuint)sourceBytes.Length,
        pSourceName: (byte*)null, // Explicit type to resolve ambiguity
        pDefines: null,
        pInclude: null,
        pEntrypoint: (byte*)entryPtr,
        pTarget: (byte*)targetPtr,
        Flags1: compileFlags,
        Flags2: 0,
        ppCode: ref shaderBlob,
        ppErrorMsgs: ref errorBlob
    );

            // 7. Check the compilation result. fxc also writes warnings such as X4000 into errorBlob, and this
            // project deliberately treats any diagnostic as fatal so kernel sources stay warning-free.
            if (errorBlob != null)
            {
                // Get the error message.
                string errorMsg = Marshal.PtrToStringAnsi((nint)errorBlob->GetBufferPointer());

                // Release the error blob.
                errorBlob->Release();

                // Throw an exception with detailed information.
                throw new Exception($"Shader compilation failed [{entryPoint} {target}]:\n{errorMsg}");
            }

            // Check whether the result succeeded.
            if (result.IsFailure)
            {
                throw new Exception($"Shader compilation failed [{entryPoint} {target}] with HResult: {result}");
            }

            return shaderBlob;
        }
        finally
        {
            // 8. Release the GCHandle and the marshalled parameter strings.
            if (handle.IsAllocated)
                handle.Free();
            Marshal.FreeHGlobal(entryPtr);
            Marshal.FreeHGlobal(targetPtr);
        }
    }
}
