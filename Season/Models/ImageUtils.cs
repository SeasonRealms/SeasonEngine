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

    //public static void Flip(string source, Season.Basic.SpriteEffects mode)
    //{

    //}

    //public static void Rotate(string source, Basic.RotateMode mode)
    //{

    //}

    //public static void Crop(string source, float posX, float posY, float width, float height)
    //{

    //}

    //public static void Erase(string source, List<BrushPoint> brushPoints)
    //{

    //}

    //public static string EraseAuto(string source, BrushPoint brushPoint)
    //{
    //    return source;
    //}

    //public static ImageResult LimitImageSize(ImageResult imageResult, long length)
    //{
    //    return imageResult;
    //}

    //public static void SaveImageToSource(string source, ImageResult targetImage)
    //{

    //}

    //public static void Round(string source, int? width, int? height, int mode)
    //{

    //}

}

