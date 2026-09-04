// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using Season.Fonts;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal texture aligned one to one with DX12 DXTexture and Vulkan Texture:
///   - creates an IMTLTexture using RGBA8Unorm, mip=1, and StorageMode=Private
///   - actual pixel copies are submitted in one batch by TextureUploadBatch through a BlitCommandEncoder
///   - Metal does not need layout transitions, and StorageMode.Private becomes fragment-sampleable after BlitEncoder upload
///   - UploadFenceValue stores the monotonic upload command counter for CPU-side validation only, while the main path uses cmd.WaitUntilCompleted
/// </summary>
internal sealed class Texture : IDisposable
{
    public string Name = string.Empty;

    public bool Ready;

    /// <summary>Monotonic completion value allocated by CommandQueue.RegisterSignal. Zero means no wait is needed anymore.</summary>
    public ulong UploadFenceValue;

    public IMTLTexture Image = null!;

    public uint Width;

    public uint Height;

    public MTLPixelFormat Format = MTLPixelFormat.RGBA8Unorm;

    /// <summary>Raw RGBA8 pixel data. It can be discarded immediately after TextureUploadBatch copies it into the staging buffer.</summary>
    public byte[]? ImageData;

    int _refCount = 1;

    public int RefCount => _refCount;

    public void AddRef() => Interlocked.Increment(ref _refCount);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0) Dispose();
    }

    void ProcessImageResult(INativeImageDecoder imageResult)
    {
        Width = (uint)imageResult.Width;
        Height = (uint)imageResult.Height;
        ImageData = imageResult.PixelSpan.ToArray();

        Image = Device.ResourceManager.CreateTexture2D((int)Width, (int)Height, Format);
        Device.TextureUploadBatch.AddTextureUpload(this);
    }

    internal Texture(INativeImageDecoder imageResult)
    {
        ProcessImageResult(imageResult);
    }

    internal Texture(string name, SharpGLTF.Schema2.Image? image)
    {
        Name = name;
        INativeImageDecoder imageResult;

        if (name is "White")
        {
            imageResult = new NativeImageData(1, 1, new byte[] { 255, 255, 255, 255 });
        }
        else if (image != null)
        {
            using Stream stream = image.Content.Open();
            imageResult = ImageUtils.GetImageFromStream(stream, null);
        }
        else
        {
            using Stream stream = File.Open(name, FileMode.Open);
            imageResult = ImageUtils.GetImageFromStream(stream, null);
        }

        ProcessImageResult(imageResult);
    }

    internal static Texture GetOrCreate(string name, SharpGLTF.Schema2.Image? image)
    {
        if (Device.DictionaryTexture.TryGetValue(name, out var texture))
        {
            texture.AddRef();
            return texture;
        }

        texture = new Texture(name, image);
        Device.DictionaryTexture[name] = texture;
        return texture;
    }

    /// <summary>Create a new texture directly from decoded pixels without joining the global cache. The caller owns the lifetime.</summary>
    internal static Texture CreateFromDecoder(INativeImageDecoder decoder)
    {
        return new Texture(decoder);
    }

    /// <summary>
    /// Update texture pixel content in place, and the size must match the current GPU texture.
    /// Reuses the same MTLTexture without allocating new GPU memory.
    /// </summary>
    public void UploadPixels(ReadOnlySpan<byte> rgbaPixels)
    {
        int expectedSize = (int)(Width * Height * 4);
        if (rgbaPixels.Length != expectedSize)
            throw new ArgumentException(
                $"Pixel data size mismatch. Expected {expectedSize} bytes for {Width}×{Height}, got {rgbaPixels.Length}.");

        var mtlDevice = Device.MtlDevice;
        var staging = mtlDevice.CreateBuffer((nuint)expectedSize, MTLResourceOptions.StorageModeShared)
            ?? throw new Exception("staging IMTLBuffer.CreateBuffer failed");

        try
        {
            // Copy pixels into the staging buffer through an intermediate array to avoid pointer manipulation.
            var pixels = rgbaPixels.ToArray();
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, staging.Contents, expectedSize);

            // Blit copy
            var cmd = Device.GraphicsQueue.CreateCommandBuffer();
            var blit = cmd.CreateBlitCommandEncoder(new MTLBlitPassDescriptor())
                ?? throw new Exception("CreateBlitCommandEncoder failed");

            blit.CopyFromBuffer(
                staging,
                0,
                (nuint)(Width * 4),
                (nuint)(Width * Height * 4),
                new MTLSize((nint)Width, (nint)Height, 1),
                Image,
                0,
                0,
                new MTLOrigin(0, 0, 0));

            blit.EndEncoding();
            cmd.Commit();
            cmd.WaitUntilCompleted();
        }
        finally
        {
            staging.Dispose();
        }
    }

    public void Dispose()
    {
        if (Image != null)
        {
            Image.Dispose();
            Image = null!;
        }

        if (!string.IsNullOrEmpty(Name))
            Device.DictionaryTexture.Remove(Name);
    }

    /// <summary>
    /// Create an empty atlas or storage texture with no initial pixel data.
    /// Used for dynamic atlases in GlyphAtlasManager and for 1-6 compute storage, including 2-1 bloom-chain RGBA16Float HDR intermediate textures.
    /// </summary>
    internal static Texture CreateEmpty(uint width, uint height, string name,
        MTLPixelFormat format = MTLPixelFormat.RGBA8Unorm)
    {
        var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(
            format, (nuint)width, (nuint)height, false);
        desc.Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite;
        desc.StorageMode = MTLStorageMode.Private;

        var image = Device.MtlDevice.CreateTexture(desc)
            ?? throw new Exception("MTLDevice.CreateTexture failed for empty atlas");

        return new Texture
        {
            Width = width,
            Height = height,
            Format = format,
            Name = name,
            Image = image
        };
    }

    /// <summary>
    /// 1-8 format intent to concrete Metal format.
    /// This is the single source of truth shared by both 2D and 3D creation paths,
    /// preventing the same intent from mapping to different concrete formats in different places.
    /// On this backend, Metal is the only one among the four backends where all five intents have native equivalents with zero fallback.
    /// R16Float, R8Unorm, and RG16Float all support ShaderWrite plus linear filtering on every GPU family that supports Metal.
    /// </summary>
    internal static MTLPixelFormat MapComputeFormat(Season.Rendering.ComputeStorageFormat format) => format switch
    {
        Season.Rendering.ComputeStorageFormat.Rgba16Float => MTLPixelFormat.RGBA16Float,
        Season.Rendering.ComputeStorageFormat.R16Float => MTLPixelFormat.R16Float,
        Season.Rendering.ComputeStorageFormat.R8Unorm => MTLPixelFormat.R8Unorm,
        Season.Rendering.ComputeStorageFormat.Rg16Float => MTLPixelFormat.RG16Float,
        _ => MTLPixelFormat.RGBA8Unorm,
    };

    /// <summary>
    /// Recreate the IMTLTexture in place to match a new size.
    /// Metal retained references keep the old object alive while command buffers are still in flight,
    /// so immediate Dispose is safe.
    /// The C# object identity stays unchanged, so Sprite2D AddRef references and DictionaryMtlTexture keys remain valid.
    /// </summary>
    internal void Recreate(uint width, uint height)
    {
        Image?.Dispose();
        Width = width;
        Height = height;
        var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(
            Format, (nuint)width, (nuint)height, false);
        desc.Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite;
        desc.StorageMode = MTLStorageMode.Private;
        Image = Device.MtlDevice.CreateTexture(desc)
            ?? throw new Exception($"MTLDevice.CreateTexture failed for resize '{Name}'");
        Ready = true;
    }

    /// <summary>
    /// Incrementally upload dirty sub-rectangles by copying the specified atlas regions into the GPU texture.
    /// The texture must have been created by CreateEmpty and must match sourceWidth and sourceHeight.
    /// </summary>
    public void UploadSubRects(byte[] rgbaPixels, int sourceWidth, int sourceHeight, AtlasUploadRect[] dirtyRects)
    {
        if (dirtyRects == null || dirtyRects.Length == 0)
            return;

        int expectedSize = (int)(Width * Height * 4);
        if (Width != (uint)sourceWidth || Height != (uint)sourceHeight)
            throw new ArgumentException(
                $"Atlas size mismatch. Expected {Width}×{Height}, got {sourceWidth}×{sourceHeight}.");
        if (rgbaPixels.Length != expectedSize)
            throw new ArgumentException(
                $"Pixel data size mismatch. Expected {expectedSize} bytes, got {rgbaPixels.Length}.");

        var mtlDevice = Device.MtlDevice;
        var staging = mtlDevice.CreateBuffer((nuint)expectedSize, MTLResourceOptions.StorageModeShared)
            ?? throw new Exception("staging IMTLBuffer.CreateBuffer failed");

        try
        {
            // Copy the full atlas pixel buffer into the staging buffer.
            System.Runtime.InteropServices.Marshal.Copy(rgbaPixels, 0, staging.Contents, expectedSize);

            var cmd = Device.GraphicsQueue.CreateCommandBuffer();
            var blit = cmd.CreateBlitCommandEncoder(new MTLBlitPassDescriptor())
                ?? throw new Exception("CreateBlitCommandEncoder failed");

            nuint bytesPerRow = (nuint)(sourceWidth * 4);
            nuint bytesPerImage = (nuint)(sourceWidth * sourceHeight * 4);
            int bpr = sourceWidth * 4;

            for (int i = 0; i < dirtyRects.Length; i++)
            {
                var rect = dirtyRects[i];
                blit.CopyFromBuffer(
                    staging,
                    (nuint)(rect.Y * bpr + rect.X * 4),
                    bytesPerRow,
                    bytesPerImage,
                    new MTLSize((nint)rect.Width, (nint)rect.Height, 1),
                    Image,
                    0,
                    0,
                    new MTLOrigin(rect.X, rect.Y, 0));
            }

            blit.EndEncoding();
            cmd.Commit();
            cmd.WaitUntilCompleted();
        }
        finally
        {
            staging.Dispose();
        }
    }

    /// <summary>
    /// Parameterless constructor used by CreateEmpty so it can skip the texture-creation logic in ProcessImageResult.
    /// </summary>
    Texture() { }
}
