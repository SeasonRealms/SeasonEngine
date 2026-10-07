// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

public static class ImageUtils
{
    public static string[] Extensions = new string[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    public static bool CreateImageExist(string name)
    {
        return name is "Dot" or "Square" or "Circle" or "RoundRect" or "RectFrame" or "Gradual" or "GradualCircle";
    }

    /// <summary>
    /// Creates a procedural shape texture from the ShapeType enum.
    /// </summary>
    public static INativeImageDecoder CreateShapeImage(Season.Controls.ShapeType type, int width, int height, int? border = null)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        return type switch
        {
            // Dot and Square share the same semantics: a solid filled rectangle.
            // Dot must still produce a full-size image, otherwise WebGPU writeTexture
            // may receive too little data and leave the GPU texture invalid.
            Season.Controls.ShapeType.Dot => new NativeImageData(1, 1, new byte[] { 255, 255, 255, 255 }),
            Season.Controls.ShapeType.Square => CreateSquareImage(width, height),
            Season.Controls.ShapeType.Circle => CreateImageEllipse(width, height, false),
            Season.Controls.ShapeType.RoundRect => CreateImageRoundedRectangle(width, height),
            Season.Controls.ShapeType.RectFrame => CreateImageRectFrame(width, height, border ?? 1),
            Season.Controls.ShapeType.Gradual => CreateImageGradual(width, height),
            Season.Controls.ShapeType.GradualCircle => CreateImageGradualCircle(width, height),
            _ => new NativeImageData(1, 1, new byte[] { 255, 255, 255, 255 })
        };
    }

    static INativeImageDecoder CreateSquareImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }
        return new NativeImageData(width, height, pixels);
    }

    public static INativeImageDecoder CreateImage(string name, int? width = null, int? height = null)
    {
        INativeImageDecoder imageResult = null;

        if (name is "Dot" or "Square")
        {
            imageResult = new NativeImageData(1, 1, new byte[] { 255, 255, 255, 255 });
        }
        else if (name is "Square")
        {
            imageResult = new NativeImageData(
                width is null ? 1 : (int)width,
                height is null ? 1 : (int)height,
                new byte[] { 255, 255, 255, 255 } // RGBA
            );
        }
        else if (name is "Circle")
        {
            int ellipseWidth = Math.Max(1, width ?? height ?? 100);
            int ellipseHeight = Math.Max(1, height ?? width ?? 100);
            imageResult = CreateImageEllipse(ellipseWidth, ellipseHeight, false);
        }
        else if (name is "RoundRect")
        {
            int rectWidth = Math.Max(1, width ?? height ?? 160);
            int rectHeight = Math.Max(1, height ?? width ?? 80);
            imageResult = CreateImageRoundedRectangle(rectWidth, rectHeight);
        }
        else if (name is "RectFrame")
        {
            int frameWidth = Math.Max(1, width ?? height ?? 160);
            int frameHeight = Math.Max(1, height ?? width ?? 80);
            imageResult = CreateImageRectFrame(frameWidth, frameHeight, 1);
        }
        else if (name is "Gradual")
        {
            int gradualWidth = Math.Max(1, width ?? height ?? 50);
            int gradualHeight = Math.Max(1, height ?? width ?? 50);
            imageResult = CreateImageGradual(gradualWidth, gradualHeight);
        }
        else if (name is "GradualCircle")
        {
            int gradualCircleWidth = Math.Max(1, width ?? height ?? 50);
            int gradualCircleHeight = Math.Max(1, height ?? width ?? 50);
            imageResult = CreateImageGradualCircle(gradualCircleWidth, gradualCircleHeight);
        }

        return imageResult;
    }

    public static INativeImageDecoder CreateImageCircle(int radius)
    {
        int diameter = Math.Max(1, radius * 2);
        return CreateImageEllipse(diameter, diameter, false);
    }

    public static INativeImageDecoder CreateImageEllipse(int width, int height, bool drawBorder)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        byte[] imageData = new byte[width * height * 4];
        float centerX = width / 2.0f;
        float centerY = height / 2.0f;
        float radiusX = width / 2.0f;
        float radiusY = height / 2.0f;
        float borderThickness = Math.Max(1.0f, Math.Min(width, height) * 0.02f);
        float innerRadiusX = Math.Max(0.0f, radiusX - borderThickness);
        float innerRadiusY = Math.Max(0.0f, radiusY - borderThickness);

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int pixelIndex = (row * width + col) * 4;

                float normalizedX = (col + 0.5f - centerX) / radiusX;
                float normalizedY = (row + 0.5f - centerY) / radiusY;
                bool isInsideEllipse = (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1.0f;
                bool isInsideInnerEllipse = false;

                if (drawBorder && innerRadiusX > 0.0f && innerRadiusY > 0.0f)
                {
                    float innerNormalizedX = (col + 0.5f - centerX) / innerRadiusX;
                    float innerNormalizedY = (row + 0.5f - centerY) / innerRadiusY;
                    isInsideInnerEllipse = (innerNormalizedX * innerNormalizedX) + (innerNormalizedY * innerNormalizedY) <= 1.0f;
                }

                if (!isInsideEllipse)
                {
                    imageData[pixelIndex] = 255;     // R
                    imageData[pixelIndex + 1] = 255; // G
                    imageData[pixelIndex + 2] = 255; // B
                    imageData[pixelIndex + 3] = 0;   // A
                }
                else if (!drawBorder)
                {
                    imageData[pixelIndex] = 255;     // R
                    imageData[pixelIndex + 1] = 255; // G
                    imageData[pixelIndex + 2] = 255; // B
                    imageData[pixelIndex + 3] = 255; // A
                }
                else if (!isInsideInnerEllipse)
                {
                    imageData[pixelIndex] = 255;     // R
                    imageData[pixelIndex + 1] = 255; // G
                    imageData[pixelIndex + 2] = 255; // B
                    imageData[pixelIndex + 3] = 255; // A
                }
                else
                {
                    imageData[pixelIndex] = 255;     // R
                    imageData[pixelIndex + 1] = 255; // G
                    imageData[pixelIndex + 2] = 255; // B
                    imageData[pixelIndex + 3] = 0;   // A
                }
            }
        }

        return new NativeImageData(width, height, imageData);
    }

    public static INativeImageDecoder CreateImageRoundedRectangle(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        byte[] imageData = new byte[width * height * 4];
        float radius = Math.Max(1.0f, Math.Min(width, height) * 0.2f);
        float left = radius;
        float right = width - radius;
        float top = radius;
        float bottom = height - radius;

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int pixelIndex = (row * width + col) * 4;
                float sampleX = col + 0.5f;
                float sampleY = row + 0.5f;

                float nearestX = Math.Clamp(sampleX, left, right);
                float nearestY = Math.Clamp(sampleY, top, bottom);
                float deltaX = sampleX - nearestX;
                float deltaY = sampleY - nearestY;
                bool isInsideRoundedRect = (deltaX * deltaX) + (deltaY * deltaY) <= radius * radius;

                imageData[pixelIndex] = 255;     // R
                imageData[pixelIndex + 1] = 255; // G
                imageData[pixelIndex + 2] = 255; // B
                imageData[pixelIndex + 3] = isInsideRoundedRect ? (byte)255 : (byte)0; // A
            }
        }

        return new NativeImageData(width, height, imageData);
    }

    /// <summary>
    /// Rectangle frame texture: the outer border thickness is opaque and the inside is fully transparent.
    /// border is clamped to the range [1, min(width, height) / 2].
    /// </summary>
    public static INativeImageDecoder CreateImageRectFrame(int width, int height, int border)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int b = Math.Clamp(border, 1, Math.Min(width, height) / 2);

        byte[] imageData = new byte[width * height * 4];

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int pixelIndex = (row * width + col) * 4;
                bool inFrame = col < b || col >= width - b || row < b || row >= height - b;

                imageData[pixelIndex] = 255;     // R
                imageData[pixelIndex + 1] = 255; // G
                imageData[pixelIndex + 2] = 255; // B
                imageData[pixelIndex + 3] = inFrame ? (byte)255 : (byte)0; // A
            }
        }

        return new NativeImageData(width, height, imageData);
    }

    public static INativeImageDecoder CreateImageGradual(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        byte[] imageData = new byte[width * height * 4]; // RGBA format.

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int pixelIndex = (row * width + col) * 4;

                // Compute alpha as a gradient from top 0 to bottom 255.
                byte alpha = (byte)(255 * row / height);

                // White background with an alpha gradient.
                imageData[pixelIndex] = 255;     // R
                imageData[pixelIndex + 1] = 255; // G
                imageData[pixelIndex + 2] = 255; // B
                imageData[pixelIndex + 3] = alpha; // A
            }
        }

        return new NativeImageData(width, height, imageData);
    }

    public static INativeImageDecoder CreateImageGradualCircle(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        byte[] imageData = new byte[width * height * 4]; // RGBA format.

        // Compute the center point.
        float centerX = width / 2.0f;
        float centerY = height / 2.0f;

        // Compute the maximum radius using the smaller dimension as the baseline.
        float maxRadius = Math.Min(width, height) / 2.0f;

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int pixelIndex = (row * width + col) * 4;

                // Compute the distance from the current pixel to the center.
                float distance = (float)Math.Sqrt(
                    Math.Pow(col - centerX, 2) +
                    Math.Pow(row - centerY, 2)
                );

                // If the distance exceeds the maximum radius, the pixel is fully transparent.
                if (distance > maxRadius)
                {
                    imageData[pixelIndex] = 255;   // R
                    imageData[pixelIndex + 1] = 255; // G
                    imageData[pixelIndex + 2] = 255; // B
                    imageData[pixelIndex + 3] = 0; // A, fully transparent.
                }
                else
                {
                    // Compute gradient alpha: the farther from the center, the higher the transparency.
                    float alphaFactor = 1.0f - (distance / maxRadius);

                    // Compute the radial gradient contribution.
                    float radialAlpha = alphaFactor * 255;

                    // Also add a gradient that fades from the center outward.
                    float distanceFromCenterNormalized = distance / maxRadius;
                    float additionalGradient = (1.0f - distanceFromCenterNormalized) * 255;

                    // Combine the two gradient contributions.
                    byte alpha = (byte)Math.Min(radialAlpha, additionalGradient);

                    // White circle with an alpha gradient.
                    imageData[pixelIndex] = 255;     // R
                    imageData[pixelIndex + 1] = 255; // G
                    imageData[pixelIndex + 2] = 255; // B
                    imageData[pixelIndex + 3] = alpha; // A
                }
            }
        }

        return new NativeImageData(width, height, imageData);
    }

    public static INativeImageDecoder GetImageFromStream(Stream stream, string ext)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead)
            throw new ArgumentException("Stream is not readable.", nameof(stream));

        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        var iNativeImageDecoder = DeviceServices.Image.GetImageFromStream(stream, ext);

        // Select the appropriate decoder by file extension.
        //ext = (ext ?? "").ToLower().Trim();
        //if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" ||
        //    ext == ".tga" || ext == ".psd" || ext == ".gif" || ext == ".hdr")

        return iNativeImageDecoder;
    }

    public static async Task<INativeImageDecoder> GetImageFromStreamAsync(Stream stream, string ext)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead)
            throw new ArgumentException("Stream is not readable.", nameof(stream));

        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        return await DeviceServices.Image.GetImageFromStreamAsync(stream, ext);
    }

    /// <summary>
    /// Saves an INativeImageDecoder as JPEG bytes.
    /// The input is adapted automatically for both RGBA and RGB layouts based on data length.
    /// </summary>
    /// <param name="image">Image data in RGBA or RGB format.</param>
    /// <param name="quality">JPEG quality from 1 to 100. Defaults to 90.</param>
    /// <returns>JPEG file bytes.</returns>
    public static byte[] SaveImage(INativeImageDecoder image, Season.Basic.ImageFormat imageFormat, int quality = 90)
    {
        var data = image.PixelSpan;
        byte[] rgb;
        int pixelCount = image.Width * image.Height;

        if (data.Length == pixelCount * 3)
        {
            // The input is already RGB, so use it directly.
            rgb = data.ToArray();
        }
        else if (data.Length == pixelCount * 4)
        {
            // Convert RGBA to RGB.
            rgb = new byte[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
            {
                int src = i * 4;
                int dst = i * 3;
                rgb[dst] = data[src];
                rgb[dst + 1] = data[src + 1];
                rgb[dst + 2] = data[src + 2];
            }
        }
        else
        {
            throw new NotSupportedException(
                $"SaveAsJpeg does not support image data length {data.Length}. " +
                $"Pixel count: {pixelCount}, expected {pixelCount * 3} (RGB) or {pixelCount * 4} (RGBA).");
        }

        var bytes = DeviceServices.Image.SaveImage(image, imageFormat, quality);

        return bytes;
    }

    public static Task<byte[]> SaveImageAsync(INativeImageDecoder image, Season.Basic.ImageFormat imageFormat, int quality = 90)
    {
        var data = image.PixelSpan;
        int pixelCount = image.Width * image.Height;

        if (data.Length != pixelCount * 3 && data.Length != pixelCount * 4)
        {
            throw new NotSupportedException(
                $"SaveAsJpeg does not support image data length {data.Length}. " +
                $"Pixel count: {pixelCount}, expected {pixelCount * 3} (RGB) or {pixelCount * 4} (RGBA).");
        }

        return DeviceServices.Image.SaveImageAsync(image, imageFormat, quality);
    }

    // ==================== CPU pixel transforms ====================
    // Decoder-in, decoder-out transforms shared by downstream image pipelines (photo editing,
    // circle masks, thumbnail resize). They all operate on straight RGBA8 rows and never touch
    // a GPU texture, so every platform runs the identical algorithm.

    /// <summary>Copies the source rows into a tightly packed RGBA8 buffer (row stride = Width * 4).</summary>
    static byte[] CopyPixels(INativeImageDecoder source)
    {
        int rowBytes = checked(source.Width * 4);
        var pixels = new byte[checked(rowBytes * source.Height)];
        for (int y = 0; y < source.Height; y++)
            source.PixelSpan.Slice(y * source.Stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        return pixels;
    }

    /// <summary>Crops the source to the given pixel rectangle.</summary>
    public static INativeImageDecoder Crop(INativeImageDecoder source, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (x < 0 || y < 0 || width <= 0 || height <= 0
            || (long)x + width > source.Width || (long)y + height > source.Height)
            throw new ArgumentOutOfRangeException(nameof(width));

        var pixels = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++)
            source.PixelSpan.Slice((y + row) * source.Stride + x * 4, width * 4)
                .CopyTo(pixels.AsSpan(row * width * 4));
        return new NativeImageData(width, height, pixels);
    }

    /// <summary>Horizontal mirror (equivalent to RotateNoneFlipX).</summary>
    public static INativeImageDecoder MirrorHorizontal(INativeImageDecoder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] src = CopyPixels(source);
        int width = source.Width, height = source.Height;
        var pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            var row = src.AsSpan(y * width * 4, width * 4);
            var dst = pixels.AsSpan(y * width * 4, width * 4);
            for (int x = 0; x < width; x++)
                row.Slice((width - 1 - x) * 4, 4).CopyTo(dst.Slice(x * 4, 4));
        }
        return new NativeImageData(width, height, pixels);
    }

    /// <summary>Clockwise rotation in 90-degree steps (rotate: 0-3), matching RotateFlipType.Rotate90/180/270.</summary>
    public static INativeImageDecoder Rotate90(INativeImageDecoder source, int rotate)
    {
        ArgumentNullException.ThrowIfNull(source);
        rotate = ((rotate % 4) + 4) % 4;
        byte[] src = CopyPixels(source);
        int width = source.Width, height = source.Height;
        int outW = rotate % 2 == 0 ? width : height;
        int outH = rotate % 2 == 0 ? height : width;
        var pixels = new byte[checked(outW * outH * 4)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int dx, dy;
                if (rotate == 0) { dx = x; dy = y; }
                else if (rotate == 1) { dx = height - 1 - y; dy = x; }
                else if (rotate == 2) { dx = width - 1 - x; dy = height - 1 - y; }
                else { dx = y; dy = width - 1 - x; }
                src.AsSpan((y * width + x) * 4, 4).CopyTo(pixels.AsSpan((dy * outW + dx) * 4, 4));
            }
        return new NativeImageData(outW, outH, pixels);
    }

    /// <summary>Bilinear CPU resize. sameRatio keeps the aspect ratio, fitting inside the target box.</summary>
    public static INativeImageDecoder ResizeBilinear(INativeImageDecoder source, int targetWidth, int targetHeight, bool sameRatio)
    {
        ArgumentNullException.ThrowIfNull(source);
        int outW, outH;
        if (sameRatio)
        {
            float scaleWidth = (float)targetWidth / source.Width;
            float scaleHeight = (float)targetHeight / source.Height;
            float scale = Math.Min(scaleWidth, scaleHeight);
            outW = Math.Max(1, (int)Math.Round(source.Width * scale));
            outH = Math.Max(1, (int)Math.Round(source.Height * scale));
        }
        else
        {
            outW = Math.Max(1, targetWidth);
            outH = Math.Max(1, targetHeight);
        }

        var src = source.PixelSpan;
        int stride = source.Stride;
        int inW = source.Width, inH = source.Height;
        var pixels = new byte[checked(outW * outH * 4)];
        float sx = (float)inW / outW;
        float sy = (float)inH / outH;
        for (int y = 0; y < outH; y++)
        {
            float fy = Math.Min((y + 0.5f) * sy - 0.5f, inH - 1);
            if (fy < 0) fy = 0;
            int y0 = (int)fy;
            int y1 = Math.Min(y0 + 1, inH - 1);
            float wy = fy - y0;
            for (int x = 0; x < outW; x++)
            {
                float fx = Math.Min((x + 0.5f) * sx - 0.5f, inW - 1);
                if (fx < 0) fx = 0;
                int x0 = (int)fx;
                int x1 = Math.Min(x0 + 1, inW - 1);
                float wx = fx - x0;
                int o = (y * outW + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float v00 = src[y0 * stride + x0 * 4 + c];
                    float v10 = src[y0 * stride + x1 * 4 + c];
                    float v01 = src[y1 * stride + x0 * 4 + c];
                    float v11 = src[y1 * stride + x1 * 4 + c];
                    float v = (v00 * (1 - wx) + v10 * wx) * (1 - wy) + (v01 * (1 - wx) + v11 * wx) * wy;
                    pixels[o + c] = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
                }
            }
        }
        return new NativeImageData(outW, outH, pixels);
    }

    /// <summary>Flattens straight alpha onto the given background color (photo pipelines use white).</summary>
    public static INativeImageDecoder FlattenAlpha(INativeImageDecoder source,
        byte red = 255, byte green = 255, byte blue = 255)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] pixels = CopyPixels(source);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a == 255) continue;
            pixels[i] = (byte)((pixels[i] * a + red * (255 - a) + 127) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * a + green * (255 - a) + 127) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * a + blue * (255 - a) + 127) / 255);
            pixels[i + 3] = 255;
        }
        return new NativeImageData(source.Width, source.Height, pixels);
    }

    /// <summary>Clears every pixel outside the inscribed circle (centered at Width/2, Height/2, radius = Width/2).</summary>
    public static INativeImageDecoder MaskCircle(INativeImageDecoder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] pixels = CopyPixels(source);
        int width = source.Width, height = source.Height;
        int cx = width / 2, cy = height / 2;
        long radius2 = (long)cx * cx;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if ((long)(x - cx) * (x - cx) + (long)(y - cy) * (y - cy) > radius2)
                    pixels.AsSpan((y * width + x) * 4, 4).Clear();
        return new NativeImageData(width, height, pixels);
    }

    /// <summary>
    /// Solid circle of diameter radius * 2. The RGB channels are expanded by 255 / alpha so that
    /// a premultiplied-looking source color stores as a straight-alpha pixel.
    /// </summary>
    public static INativeImageDecoder CreateImageCircle(int radius, byte red, byte green, byte blue, byte alpha)
    {
        int size = checked(radius * 2);
        var pixels = new byte[checked(size * size * 4)];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if ((long)(x - radius) * (x - radius) + (long)(y - radius) * (y - radius) <= (long)radius * radius)
                {
                    int offset = (y * size + x) * 4;
                    if (alpha > 0)
                    {
                        pixels[offset] = (byte)(red * 255 / alpha);
                        pixels[offset + 1] = (byte)(green * 255 / alpha);
                        pixels[offset + 2] = (byte)(blue * 255 / alpha);
                    }
                    pixels[offset + 3] = alpha;
                }
        return new NativeImageData(size, size, pixels);
    }
}
