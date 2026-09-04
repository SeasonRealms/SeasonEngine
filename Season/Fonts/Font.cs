// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Typography.OpenFont;
using NativeMsdfGen = Season.MSDF.MsdfGen;

namespace Season.Fonts;

public struct FontMetrics
{
    public int Ascent;
    public int Descent;
    public int LineGap;
    public float LineHeight;
}

public struct GlyphMetrics
{
    public int Width;

    public int Height;

    public int X0;

    public int Y0;

    public int X1;

    public int Y1;

    /// <summary>
    /// Layout advance width in pixels, derived from the font advance width and used
    /// to advance the text-layout cursor.
    /// This differs from visual-bounds Width, which is used for texture rendering size.
    /// </summary>
    public float AdvanceWidth;

    /// <summary>
    /// Official SharpMSDF glyph-quad plane bounds, already converted to pixel units
    /// for the current font size.
    /// Used to place MSDF glyphs according to the official geometric contract,
    /// instead of reverse-inferring them from GlyphMetrics, Y0, and PixelRange.
    /// </summary>
    public bool HasPlaneBounds;
    public float PlaneLeft;
    public float PlaneBottom;
    public float PlaneRight;
    public float PlaneTop;

    /// <summary>
    /// Official SharpMSDF quad atlas bounds, already converted into the flipped texture coordinate system.
    /// They represent the actual sub-rectangle inside the glyph box that should be sampled.
    /// </summary>
    public bool HasAtlasBounds;
    public float AtlasSourceX;
    public float AtlasSourceY;
    public float AtlasSourceWidth;
    public float AtlasSourceHeight;
}

public class Font
{
    // ═══════════════════════════════════════════════════════════════
    // Performance optimization level 1 - parameter downgrade (2026-07-12)
    //   PixelRange:           8 -> 4   (border halved, texture area reduced to roughly 1/2 to 1/4)
    //   MinMsdfGlyphScale:   64 -> 32  (minimum supersampled resolution halved)
    //   MsdfOversampleFactor: 2 -> 1.5 (font-size scaling factor reduced by 25%)
    //   OverlapSupport:      enabled on demand only for glyphs with detected overlapping contours,
    //                        consistent with the official msdfgen default strategy:
    //                        no orientContours preprocessing, relying on the overlap combiner
    //                        to handle native non-zero winding
    //   ErrorCorrection:     EDGE_PRIORITY -> DISABLED (skips the full-image second pass)
    // Expected overall speedup: 40-60%.
    // To restore quality, simply revert the parameters above to their original values.
    // ═══════════════════════════════════════════════════════════════
    public const float PixelRange = 4f;
    const float MinMsdfGlyphScale = 32f;
    const float MsdfOversampleFactor = 1.5f;

    public static bool UseNativeMsdfgenBackend = false;

    public static List<Font> Instance = new List<Season.Fonts.Font>();

    public static Dictionary<int, GlyphMetrics> DictionaryFontGlyphMetrics = new Dictionary<int, GlyphMetrics>();

    public static Dictionary<int, float> DictionaryFontGlyphPixelRanges = new Dictionary<int, float>();
    public static Dictionary<(int FontSize, int CodePoint), GlyphMetrics> DictionaryFontGlyphLayoutMetrics = new Dictionary<(int FontSize, int CodePoint), GlyphMetrics>();

    public Typeface Typeface;

    public FontMetrics FontMetrics;
    readonly string _fileName;
    readonly byte[] _fontBytes;

    // ═══════════════════════════════════════════════════════════════
    // Performance optimization level 2 - Shape cache + simplified coloring (2026-07-12)
    //   _shapeCache:         caches processed Shape instances by glyphIndex, so later calls skip Steps 1-3
    //   EdgeColoringSimple:  replaces EdgeColoringInkTrap and avoids EstimateEdgeLength sampling
    // First-call speedup: about 15%. Subsequent calls for the same glyph: about 80%.
    // To restore quality, remove _shapeCache and switch Simple back to InkTrap.
    // ═══════════════════════════════════════════════════════════════
    /// <summary>Caches the Shape for each glyphIndex after Build, Normalize, and EdgeColoring, avoiding repeated reconstruction.</summary>
    private readonly Dictionary<int, Season.MSDF.Shape> _shapeCache = new();

    /// <summary>
    /// Cheap overlapping-contour detection using an O(n^2) bounding-box test.
    /// Nested holes, with reversed winding plus bounding-box containment, can be handled correctly by the simple combiner
    /// and do not require overlap support.
    /// Only contour pairs with intersecting bounding boxes but no containment, or same-direction containment,
    /// need OverlapSupport to be enabled.
    /// </summary>
    static bool HasOverlappingContours(Season.MSDF.Shape shape)
    {
        int n = shape.Contours.Count;
        if (n < 2)
            return false;

        var l = new double[n]; var b = new double[n];
        var r = new double[n]; var t = new double[n];
        var w = new int[n];
        for (int i = 0; i < n; i++)
        {
            l[i] = double.PositiveInfinity; b[i] = double.PositiveInfinity;
            r[i] = double.NegativeInfinity; t[i] = double.NegativeInfinity;
            shape.Contours[i].Bound(ref l[i], ref b[i], ref r[i], ref t[i]);
            w[i] = shape.Contours[i].Winding();
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (r[i] < l[j] || r[j] < l[i] || t[i] < b[j] || t[j] < b[i])
                    continue; // Bounding boxes do not intersect.
                bool contained =
                    (l[i] <= l[j] && r[i] >= r[j] && b[i] <= b[j] && t[i] >= t[j]) ||
                    (l[j] <= l[i] && r[j] >= r[i] && b[j] <= b[i] && t[j] <= t[i]);
                if (!contained || w[i] == w[j])
                    return true;
            }
        }
        return false;
    }

    public Font(string fileName, float size)
        : this(fileName, StorageService.LoadBytes(fileName), size)  //DroidSans.ttf
    {
    }

    Font(string fileName, byte[] fontBytes, float size)
    {
        _fileName = fileName;
        _fontBytes = fontBytes;

        using (var stream = new MemoryStream(_fontBytes, writable: false))
        {
            Typeface = new OpenFontReader().Read(stream);
        }

        float scale = size / (float)Typeface.UnitsPerEm;
        int ascent = (int)(Typeface.Ascender * scale);
        int descent = (int)(Typeface.Descender * scale);
        int lineGap = (int)(Typeface.LineGap * scale);
        float lineHeight = ascent + Math.Abs(descent) + lineGap;

        FontMetrics = new FontMetrics
        {
            Ascent = ascent,
            Descent = descent,
            LineGap = lineGap,
            LineHeight = lineHeight
        };
    }

    /// <summary>
    /// Creates a font asynchronously. On Web, the font file is downloaded dynamically through LoadFileAsync,
    /// so no preload is required.
    /// On other platforms, this is equivalent to the synchronous constructor.
    /// </summary>
    public static async Task<Font> CreateAsync(string fileName, float size)
    {
        var fontBytes = await StorageService.LoadBytesAsync(fileName);

        return new Font(fileName, fontBytes, size);
    }

    public bool TryGetGlyphLayoutMetrics(int fontSize, int codePoint, out GlyphMetrics glyphMetrics, out float pixelRange)
    {
        glyphMetrics = default;
        pixelRange = PixelRange;

        if (Typeface == null)
            return false;

        var cacheKey = (fontSize, codePoint);
        if (DictionaryFontGlyphLayoutMetrics.TryGetValue(cacheKey, out glyphMetrics))
            return true;

        try
        {
            float metricsScale = fontSize / (float)Typeface.UnitsPerEm;
            ushort glyphIndex = Typeface.GetGlyphIndex(codePoint);
            if (glyphIndex == 0 && codePoint != 0)
                return false;

            var glyphObj = Typeface.GetGlyph(glyphIndex);
            if (glyphObj == null)
                return false;

            glyphMetrics.HasPlaneBounds = false;
            glyphMetrics.HasAtlasBounds = false;
            glyphMetrics.AdvanceWidth = Typeface.GetAdvanceWidthFromGlyphIndex(glyphIndex) * metricsScale;

            int x0 = (int)(glyphObj.MinX * metricsScale);
            int y0 = -(int)(glyphObj.MaxY * metricsScale);
            int x1 = (int)(glyphObj.MaxX * metricsScale);
            int y1 = -(int)(glyphObj.MinY * metricsScale);

            glyphMetrics.Width = Math.Max(0, x1 - x0);
            glyphMetrics.Height = Math.Max(0, y1 - y0);
            glyphMetrics.X0 = x0;
            glyphMetrics.Y0 = y0;
            glyphMetrics.X1 = x1;
            glyphMetrics.Y1 = y1;

            DictionaryFontGlyphLayoutMetrics[cacheKey] = glyphMetrics;
            return glyphMetrics.AdvanceWidth > 0f || glyphMetrics.Width > 0 || glyphMetrics.Height > 0;
        }
        catch
        {
            glyphMetrics = default;
            return false;
        }
    }

    public (byte[] colorBuffer, GlyphMetrics glyphMetrics, float pixelRange, int textureWidth, int textureHeight) CreateMsdfGlyph(int fontSize, int codePoint)
    {
        const float angleThreshold = 3f;
        const int effectAmount = 1;

        byte[] colorBuffer = null;
        GlyphMetrics glyphMetrics = new GlyphMetrics();
        int textureWidth = 0;
        int textureHeight = 0;

        if (Typeface == null)
            return new(colorBuffer, glyphMetrics, PixelRange, textureWidth, textureHeight);

        try
        {
            float metricsScale = fontSize / (float)Typeface.UnitsPerEm;
            float msdfGlyphScale = MathF.Max(MinMsdfGlyphScale, fontSize * MsdfOversampleFactor);
            float[] rawData;
            double boundsL;
            double boundsB;
            double boundsR;
            double boundsT;
            ushort glyphIndex;
            int width;
            int height;

            glyphMetrics.HasPlaneBounds = false;
            glyphMetrics.HasAtlasBounds = false;

            if (UseNativeMsdfgenBackend)
            {
                try
                {
                    MsdfPipelineCompareDiagnostics.PrepareNativeEnvironment();
                    var nativeOptions = new NativeMsdfGen.MsdfGenGlyphOptions
                    {
                        CoordinateScaling = NativeMsdfGen.MsdfGenFontCoordinateScaling.EmNormalized,
                        GlyphScale = msdfGlyphScale,
                        PixelRange = PixelRange,
                        AngleThreshold = angleThreshold,
                        ColoringSeed = 0,
                        ColoringStrategy = NativeMsdfGen.MsdfGenColoringStrategy.InkTrap,
                        OverlapSupport = true,
                        ErrorCorrection = new MSDF.ErrorCorrectionConfig()
                    };

                    var nativeResult = NativeMsdfGen.MsdfGenGenerator.GenerateGlyph(_fontBytes, codePoint, nativeOptions);
                    glyphIndex = nativeResult.GlyphIndex <= ushort.MaxValue ? (ushort)nativeResult.GlyphIndex : (ushort)0;
                    glyphMetrics.AdvanceWidth = (float)(nativeResult.Advance * fontSize);
                    boundsL = nativeResult.BoundsL;
                    boundsB = nativeResult.BoundsB;
                    boundsR = nativeResult.BoundsR;
                    boundsT = nativeResult.BoundsT;
                    width = nativeResult.Width;
                    height = nativeResult.Height;
                    rawData = nativeResult.Pixels;

                    int x0 = (int)(boundsL * fontSize);
                    int y0 = -(int)(boundsT * fontSize);
                    int x1 = (int)(boundsR * fontSize);
                    int y1 = -(int)(boundsB * fontSize);
                    glyphMetrics.Width = (x1 - x0) + effectAmount * 2;
                    glyphMetrics.Height = (y1 - y0) + effectAmount * 2;
                    glyphMetrics.X0 = x0;
                    glyphMetrics.Y0 = y0;
                    glyphMetrics.X1 = x1;
                    glyphMetrics.Y1 = y1;
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} MsdfNative NativeGenerate U+{codePoint:X} {ex}");
                    return new(colorBuffer, glyphMetrics, PixelRange, 0, 0);
                }
            }
            else
            {
                glyphIndex = Typeface.GetGlyphIndex(codePoint);
                if (glyphIndex == 0 && codePoint != 0)
                    return new(colorBuffer, glyphMetrics, PixelRange, textureWidth, textureHeight);

                // Some fonts may return a glyphIndex but still provide no outline data usable for MSDF.
                // This path must return safely so the caller can continue trying the next font.
                var glyphObj = Typeface.GetGlyph(glyphIndex);
                if (glyphObj == null || glyphObj.EndPoints == null || glyphObj.GlyphPoints == null)
                    return new(colorBuffer, glyphMetrics, PixelRange, textureWidth, textureHeight);

                int x0 = (int)(glyphObj.MinX * metricsScale);
                int y0 = -(int)(glyphObj.MaxY * metricsScale);  // Flip Y: OpenFont Y-up -> screen Y-down
                int x1 = (int)(glyphObj.MaxX * metricsScale);
                int y1 = -(int)(glyphObj.MinY * metricsScale);
                glyphMetrics.Width = (x1 - x0) + effectAmount * 2;
                glyphMetrics.Height = (y1 - y0) + effectAmount * 2;
                glyphMetrics.X0 = x0;
                glyphMetrics.Y0 = y0;
                glyphMetrics.X1 = x1;
                glyphMetrics.Y1 = y1;
                glyphMetrics.AdvanceWidth = Typeface.GetAdvanceWidthFromGlyphIndex(glyphIndex) * metricsScale;
                //bool shouldCompareLog = MsdfPipelineCompareDiagnostics.ShouldLogCodePoint(codePoint);
                //if (shouldCompareLog)
                //{
                //    MsdfPipelineCompareDiagnostics.LogRequest(
                //        backend: "managed",
                //        fontFileName: _fileName,
                //        fontSize: fontSize,
                //        codePoint: codePoint,
                //        glyphIndex: glyphIndex,
                //        unitsPerEm: Typeface.UnitsPerEm,
                //        fontBytes: _fontBytes,
                //        glyphScale: msdfGlyphScale,
                //        pixelRange: PixelRange,
                //        angleThreshold: angleThreshold,
                //        coloringSeed: 0,
                //        coloringStrategy: "InkTrap",
                //        overlapSupport: true,
                //        coordinateScaling: "EmNormalized");
                //}

                // ── Steps 1–3: Build / Normalize / EdgeColor (cached per glyphIndex) ──
                if (!_shapeCache.TryGetValue(glyphIndex, out var shape))
                {
                    try
                    {
                        var builder = new MSDF.GlyphShapeBuilder();
                        float geometryScale = 1.0f / Typeface.UnitsPerEm;
                        builder.Read(glyphObj.GlyphPoints, glyphObj.EndPoints, geometryScale);
                        shape = builder.Shape;
                    }
                    catch (Exception ex)
                    {
                        DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} MsdfStep1 ShapeBuild U+{codePoint:X} {ex}");
                        return new(colorBuffer, glyphMetrics, PixelRange, 0, 0);
                    }

                    if (shape.Contours.Count == 0)
                        return new(colorBuffer, glyphMetrics, PixelRange, textureWidth, textureHeight);

                    try
                    {
                        // Do not call OrientContours(): its even-odd assumption can misclassify winding direction
                        // for overlapping contours, which legitimately appear in CJK glyphs from Noto Sans SC/TC variable fonts.
                        // TrueType and CFF outlines already carry correct non-zero winding, so only global sign normalization is applied:
                        // if the largest contour in a non-conforming font has negative winding, flip all contours together
                        // to preserve relative direction without breaking overlaps.
                        if (shape.Contours.Count > 0)
                        {
                            int biggest = 0;
                            double biggestArea = -1;
                            for (int ci = 0; ci < shape.Contours.Count; ci++)
                            {
                                double xl = double.PositiveInfinity, yb = double.PositiveInfinity;
                                double xr = double.NegativeInfinity, yt = double.NegativeInfinity;
                                shape.Contours[ci].Bound(ref xl, ref yb, ref xr, ref yt);
                                double area = (xr - xl) * (yt - yb);
                                if (area > biggestArea) { biggestArea = area; biggest = ci; }
                            }
                            if (shape.Contours[biggest].Winding() < 0)
                                foreach (var c in shape.Contours)
                                    c.Reverse();
                        }
                    }
                    catch (Exception ex)
                    {
                        DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} MsdfStep2 ShapeBuild U+{codePoint:X} OrientNorm cnt={shape.Contours.Count}: {ex}");
                        return new(colorBuffer, glyphMetrics, PixelRange, 0, 0);
                    }

                    try
                    {
                        shape.Normalize();
                    }
                    catch (Exception ex)
                    {
                        DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} MsdfStep2 U+{codePoint:X} OrientNorm cnt={shape.Contours.Count}: {ex}");
                        return new(colorBuffer, glyphMetrics, PixelRange, 0, 0);
                    }

                    try
                    {
                        // Level-2 optimization: use Simple instead of InkTrap and skip
                        // the four EstimateEdgeLength samples per edge.
                        MSDF.EdgeColoring.EdgeColoringSimple(shape, angleThreshold, 0);
                    }
                    catch (Exception ex)
                    {
                        DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} MsdfStep3 U+{codePoint:X} EdgeColoring cnt={shape.Contours.Count}: {ex}");
                        return new(colorBuffer, glyphMetrics, PixelRange, 0, 0);
                    }

                    _shapeCache[glyphIndex] = shape;
                }

                // Compute pixel-space bitmap dimensions from shape bounds with border padding.
                var bounds = shape.GetBounds();
                boundsL = bounds.L;
                boundsB = bounds.B;
                boundsR = bounds.R;
                boundsT = bounds.T;
                double border = PixelRange / msdfGlyphScale;
                double scale = msdfGlyphScale;
                width = (int)Math.Ceiling((boundsR - boundsL + 2.0 * border) * scale);
                height = (int)Math.Ceiling((boundsT - boundsB + 2.0 * border) * scale);
                //if (shouldCompareLog)
                //{
                //    var projectionTranslate = new SeasonMSDF.Vector2(border - boundsL, border - boundsB);
                //    MsdfPipelineCompareDiagnostics.LogProjectionStage(
                //        backend: "managed",
                //        stage: "S05.BoundsProjection",
                //        codePoint: codePoint,
                //        glyphIndex: glyphIndex,
                //        bounds: bounds,
                //        border: border,
                //        scale: scale,
                //        translate: projectionTranslate,
                //        range: PixelRange / msdfGlyphScale,
                //        width: width,
                //        height: height,
                //        advance: glyphMetrics.AdvanceWidth);
                //}

                if (width <= 0 || height <= 0)
                    return new(colorBuffer, glyphMetrics, PixelRange, textureWidth, textureHeight);

                // ── Step 4: Generate MTSDF ──
                try
                {
                    var projection = new MSDF.Projection(
                        new Season.MSDF.Vector2(scale, scale),
                        new Season.MSDF.Vector2(border - boundsL, border - boundsB));
                    var range = new Season.MSDF.Range(PixelRange / msdfGlyphScale);
                    // Level-1 optimization: ErrorCorrection disabled, and OverlapSupport enabled per glyph only when needed.
                    // Glyphs without overlapping contours take the fast path, while overlapping glyphs enable the overlap combiner.
                    var config = new MSDF.MSDFGeneratorConfig()
                    {
                        OverlapSupport = HasOverlappingContours(shape),
                        ErrorCorrection = new MSDF.ErrorCorrectionConfig(
                            mode: MSDF.ErrorCorrectionConfig.Mode.DISABLED)
                    };

                    var bitmap = new MSDF.Bitmap(width, height, 4);
                    // Level-3 optimization: parallelize row-by-row distance-field generation.
                    // With ErrorCorrection disabled, this path is thread-safe.
                    MSDF.Msdfgen.GenerateMTSDFParallel(bitmap.Section(), shape, projection, range, config);
                    rawData = bitmap.GetRawData();
                    //if (shouldCompareLog)
                    //    MsdfPipelineCompareDiagnostics.LogBitmapStage("managed", "S06.GenerateMTSDF", codePoint, glyphIndex, rawData, width, height, 4, bitmap.YOrientation);
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} MsdfStep4 U+{codePoint:X} GenerateMTSDF dims={width}x{height}: {ex}");
                    return new(colorBuffer, glyphMetrics, PixelRange, 0, 0);
                }
            }

            //StorageService.Log("MsdfDims", $"U+{codePoint:X} bounds=({boundsL:F4},{boundsB:F4},{boundsR:F4},{boundsT:F4}) border={PixelRange / msdfGlyphScale:F4} msdfScale={msdfGlyphScale:F1} dims={width}x{height} fontSize={fontSize} advance={glyphMetrics.AdvanceWidth:F1}");

            if (width <= 0 || height <= 0)
                return new(colorBuffer, glyphMetrics, PixelRange, textureWidth, textureHeight);

            textureWidth = width;
            textureHeight = height;

            // Convert float[] → byte[] RGBA for GPU texture upload.
            colorBuffer = new byte[width * height * 4];
            for (int i = 0; i < rawData.Length; i++)
            {
                float v = rawData[i] * 255f;
                colorBuffer[i] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
            }

            // Flip Y: SeasonMSDF generates Y-up (bottom-row-first), texture expects Y-down (top-row-first).
            colorBuffer = FlipMtsdfBitmapY(colorBuffer, width, height);

            // ── Compute PlaneBounds from shape bounds ──
            // Shape bounds (L,B,R,T) are in EM units. Border is PixelRange/msdfGlyphScale EM units.
            // Multiply by fontSize to convert to screen-pixel units (pre-Scale).
            // This enables the useOfficialMsdfLayout path in Texts.Position(), which correctly
            // handles the MTSDF border vs advance-width relationship.
            {
                double planeBorder = PixelRange / msdfGlyphScale;
                glyphMetrics.PlaneLeft = (float)((boundsL - planeBorder) * fontSize);
                glyphMetrics.PlaneBottom = (float)((boundsB - planeBorder) * fontSize);
                glyphMetrics.PlaneRight = (float)((boundsR + planeBorder) * fontSize);
                glyphMetrics.PlaneTop = (float)((boundsT + planeBorder) * fontSize);
                glyphMetrics.HasPlaneBounds = true;

                glyphMetrics.AtlasSourceX = 0;
                glyphMetrics.AtlasSourceY = 0;
                glyphMetrics.AtlasSourceWidth = width;
                glyphMetrics.AtlasSourceHeight = height;
                glyphMetrics.HasAtlasBounds = true;

                //StorageService.Log("MsdfPlane", $"U+{codePoint:X} fontSize={fontSize} msdfScale={msdfGlyphScale:F1} plane=({glyphMetrics.PlaneLeft:F1},{glyphMetrics.PlaneBottom:F1},{glyphMetrics.PlaneRight:F1},{glyphMetrics.PlaneTop:F1}) advance={glyphMetrics.AdvanceWidth:F1}");
            }

            //MsdfDiagnostics.DumpGlyphIfNeeded(_fileName, fontSize, codePoint, glyphIndex, msdfGlyphScale, PixelRange,
            //    glyphMetrics, width, height, rawData);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} CreateMsdfGlyph U+{codePoint:X} {ex}");
            return new(colorBuffer, glyphMetrics, PixelRange, 0, 0);
        }

        return new(colorBuffer, glyphMetrics, PixelRange, textureWidth, textureHeight);
    }

    /// <summary>
    /// Generates a 4-channel RGBA texture for MTSDF, already including true-distance alpha.
    /// Only a Y-axis flip is required: SharpMSDF uses mathematical Y-up, while DirectX textures use Y-down.
    /// </summary>
    static byte[] FlipMtsdfBitmapY(byte[] rgbaSource, int width, int height)
    {
        int rowBytes = width * 4;
        byte[] flipped = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            int srcRow = (height - 1 - y) * rowBytes;
            int dstRow = y * rowBytes;
            Buffer.BlockCopy(rgbaSource, srcRow, flipped, dstRow, rowBytes);
        }

        return flipped;
    }

}

internal static class MsdfPipelineCompareDiagnostics
{
    const string BackendManaged = "managed";
    const string LogDirectoryEnvVar = "SEASON_MSDF_COMPARE_LOG_DIR";
    static readonly object Sync = new();
    static readonly HashSet<int> TargetCodePoints = new()
    {
        0x4E2D, // Zhong
        0x534E, // Hua
        0x6574, // Zheng
        0x6700, // Zui
        0x53D7, // Shou
        0x675F, // Shu
        0x8FC7, // Guo
        0x004C, // L
        0x0031, // 1
        0x0032, // 2
        0x0030  // 0
    };

    public static void PrepareNativeEnvironment()
    {
        Directory.CreateDirectory(GetLogDirectory());
        Environment.SetEnvironmentVariable(LogDirectoryEnvVar, GetLogDirectory());
    }

    public static bool ShouldLogCodePoint(int codePoint) => TargetCodePoints.Contains(codePoint);

    public static void LogRequest(
        string backend,
        string fontFileName,
        int fontSize,
        int codePoint,
        int glyphIndex,
        int unitsPerEm,
        byte[] fontBytes,
        float glyphScale,
        float pixelRange,
        float angleThreshold,
        ulong coloringSeed,
        string coloringStrategy,
        bool overlapSupport,
        string coordinateScaling)
    {
        if (!ShouldLogCodePoint(codePoint))
            return;

        AppendLine(
            CreatePrefix("request", "S00.Request", backend, codePoint, glyphIndex)
            + $"|font={Escape(Path.GetFileName(fontFileName))}"
            + $"|font_size={fontSize}"
            + $"|units_per_em={unitsPerEm}"
            + $"|font_bytes={fontBytes.Length}"
            + $"|font_hash={ComputeByteHash(fontBytes)}"
            + $"|glyph_scale={FormatDouble(glyphScale)}"
            + $"|pixel_range={FormatDouble(pixelRange)}"
            + $"|angle_threshold={FormatDouble(angleThreshold)}"
            + $"|coloring_seed={coloringSeed}"
            + $"|coloring_strategy={Escape(coloringStrategy)}"
            + $"|overlap_support={(overlapSupport ? 1 : 0)}"
            + $"|coord_scaling={Escape(coordinateScaling)}");
    }

    public static void LogShapeStage(string backend, string stage, int codePoint, int glyphIndex, Season.MSDF.Shape shape)
    {
        if (!ShouldLogCodePoint(codePoint))
            return;

        var bounds = shape.GetBounds();
        string[] windingValues = new string[shape.Contours.Count];
        for (int i = 0; i < shape.Contours.Count; i++)
            windingValues[i] = shape.Contours[i].Winding().ToString(CultureInfo.InvariantCulture);

        AppendLine(
            CreatePrefix("shape", stage, backend, codePoint, glyphIndex)
            + $"|contours={shape.Contours.Count}"
            + $"|edges={shape.EdgeCount()}"
            + $"|bounds={FormatBounds(bounds.L, bounds.B, bounds.R, bounds.T)}"
            + $"|y_axis={FormatYAxis(shape.GetYAxisOrientation())}"
            + $"|winding={string.Join(",", windingValues)}"
            + $"|shape_hash={ComputeShapeHash(shape)}");

        for (int contourIndex = 0; contourIndex < shape.Contours.Count; contourIndex++)
        {
            var contour = shape.Contours[contourIndex];
            AppendLine(
                CreatePrefix("contour", stage, backend, codePoint, glyphIndex)
                + $"|contour={contourIndex}"
                + $"|winding={contour.Winding()}"
                + $"|edges={contour.Edges.Count}");

            for (int edgeIndex = 0; edgeIndex < contour.Edges.Count; edgeIndex++)
            {
                var segment = contour.Edges[edgeIndex].EdgeSegment;
                if (segment == null)
                    continue;
                AppendLine(
                    CreatePrefix("edge", stage, backend, codePoint, glyphIndex)
                    + $"|contour={contourIndex}"
                    + $"|edge={edgeIndex}"
                    + $"|type={segment.Type()}"
                    + $"|color={(int)segment.Color}"
                    + $"|points={FormatControlPoints(segment.ControlPoints())}");
            }
        }
    }

    public static void LogProjectionStage(
        string backend,
        string stage,
        int codePoint,
        int glyphIndex,
        Season.MSDF.Shape.Bounds bounds,
        double border,
        double scale,
        Season.MSDF.Vector2 translate,
        double range,
        int width,
        int height,
        float advance)
    {
        if (!ShouldLogCodePoint(codePoint))
            return;

        AppendLine(
            CreatePrefix("projection", stage, backend, codePoint, glyphIndex)
            + $"|bounds={FormatBounds(bounds.L, bounds.B, bounds.R, bounds.T)}"
            + $"|border={FormatDouble(border)}"
            + $"|scale={FormatDouble(scale)}"
            + $"|translate={FormatVector(translate)}"
            + $"|range={FormatDouble(range)}"
            + $"|width={width}"
            + $"|height={height}"
            + $"|advance={FormatDouble(advance)}");
    }

    public static void LogBitmapStage(
        string backend,
        string stage,
        int codePoint,
        int glyphIndex,
        float[] rawData,
        int width,
        int height,
        int channels,
        Season.MSDF.YAxisOrientation orientation)
    {
        if (!ShouldLogCodePoint(codePoint) || rawData.Length == 0)
            return;

        Span<double> minValues = stackalloc double[channels];
        Span<double> maxValues = stackalloc double[channels];
        Span<double> sumValues = stackalloc double[channels];
        for (int c = 0; c < channels; c++)
        {
            minValues[c] = double.PositiveInfinity;
            maxValues[c] = double.NegativeInfinity;
            sumValues[c] = 0;
        }

        int pixelCount = width * height;
        for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            int baseIndex = pixelIndex * channels;
            for (int c = 0; c < channels; c++)
            {
                double value = rawData[baseIndex + c];
                if (value < minValues[c])
                    minValues[c] = value;
                if (value > maxValues[c])
                    maxValues[c] = value;
                sumValues[c] += value;
            }
        }

        AppendLine(
            CreatePrefix("bitmap", stage, backend, codePoint, glyphIndex)
            + $"|width={width}"
            + $"|height={height}"
            + $"|channels={channels}"
            + $"|y_axis={FormatYAxis(orientation)}"
            + $"|min={FormatChannelValues(minValues)}"
            + $"|max={FormatChannelValues(maxValues)}"
            + $"|mean={FormatChannelAverages(sumValues, pixelCount)}"
            + $"|bitmap_hash={ComputeFloatHash(rawData)}");

        var samples = GetSamplePoints(width, height);
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            (int x, int y) = samples[sampleIndex];
            int baseIndex = (y * width + x) * channels;
            AppendLine(
                CreatePrefix("sample", stage, backend, codePoint, glyphIndex)
                + $"|sample={sampleIndex}"
                + $"|x={x}"
                + $"|y={y}"
                + $"|values={FormatRawPixel(rawData, baseIndex, channels)}");
        }
    }

    static string ComputeShapeHash(Season.MSDF.Shape shape)
    {
        StringBuilder builder = new();
        builder.Append("y_axis=").Append(FormatYAxis(shape.GetYAxisOrientation()));
        builder.Append(";contours=").Append(shape.Contours.Count.ToString(CultureInfo.InvariantCulture));
        var bounds = shape.GetBounds();
        builder.Append(";bounds=").Append(FormatBounds(bounds.L, bounds.B, bounds.R, bounds.T));
        for (int contourIndex = 0; contourIndex < shape.Contours.Count; contourIndex++)
        {
            var contour = shape.Contours[contourIndex];
            builder.Append(";c").Append(contourIndex.ToString(CultureInfo.InvariantCulture));
            builder.Append(":w=").Append(contour.Winding().ToString(CultureInfo.InvariantCulture));
            builder.Append(":e=").Append(contour.Edges.Count.ToString(CultureInfo.InvariantCulture));
            for (int edgeIndex = 0; edgeIndex < contour.Edges.Count; edgeIndex++)
            {
                MSDF.EdgeSegment? segment = contour.Edges[edgeIndex].EdgeSegment;
                if (segment == null)
                    continue;
                builder.Append(";e").Append(edgeIndex.ToString(CultureInfo.InvariantCulture));
                builder.Append(":t=").Append(segment.Type().ToString(CultureInfo.InvariantCulture));
                builder.Append(":c=").Append(((int)segment.Color).ToString(CultureInfo.InvariantCulture));
                builder.Append(":p=").Append(FormatControlPoints(segment.ControlPoints()));
            }
        }
        return ComputeByteHash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    static string ComputeFloatHash(float[] rawData)
    {
        ulong hash = 14695981039346656037UL;
        foreach (float value in rawData)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            hash ^= bits & 0xFF;
            hash *= 1099511628211UL;
            hash ^= (bits >> 8) & 0xFF;
            hash *= 1099511628211UL;
            hash ^= (bits >> 16) & 0xFF;
            hash *= 1099511628211UL;
            hash ^= (bits >> 24) & 0xFF;
            hash *= 1099511628211UL;
        }
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    static string ComputeByteHash(byte[] bytes)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    static string CreatePrefix(string kind, string stage, string backend, int codePoint, int glyphIndex) =>
        $"kind={kind}|stage={stage}|backend={backend}|codepoint=U+{codePoint:X4}|glyph_index={glyphIndex}";

    static string GetLogDirectory() =>
        StorageService.SubPath(StorageService.DirectoryBase, "MsdfDebug/PipelineCompare");

    static void AppendLine(string line)
    {
        string logPath = Path.Combine(GetLogDirectory(), $"{BackendManaged}-log.txt");
        lock (Sync)
            File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
    }

    static string Escape(string value) => value.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");

    static string FormatBounds(double l, double b, double r, double t) =>
        $"{FormatDouble(l)},{FormatDouble(b)},{FormatDouble(r)},{FormatDouble(t)}";

    static string FormatChannelValues(ReadOnlySpan<double> values)
    {
        StringBuilder builder = new();
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(FormatDouble(values[i]));
        }
        return builder.ToString();
    }

    static string FormatChannelAverages(ReadOnlySpan<double> sums, int pixelCount)
    {
        StringBuilder builder = new();
        for (int i = 0; i < sums.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(FormatDouble(sums[i] / pixelCount));
        }
        return builder.ToString();
    }

    static string FormatControlPoints(Season.MSDF.Vector2[] points)
    {
        StringBuilder builder = new();
        for (int i = 0; i < points.Length; i++)
        {
            if (i > 0)
                builder.Append(';');
            builder.Append(FormatVector(points[i]));
        }
        return builder.ToString();
    }

    static string FormatRawPixel(float[] rawData, int baseIndex, int channels)
    {
        StringBuilder builder = new();
        for (int i = 0; i < channels; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(FormatDouble(rawData[baseIndex + i]));
        }
        return builder.ToString();
    }

    static string FormatVector(Season.MSDF.Vector2 vector) =>
        $"{FormatDouble(vector.X)},{FormatDouble(vector.Y)}";

    static string FormatYAxis(Season.MSDF.YAxisOrientation orientation) =>
        orientation == Season.MSDF.YAxisOrientation.Y_UPWARD ? "Y_UPWARD" : "Y_DOWNWARD";

    static string FormatDouble(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    static (int x, int y)[] GetSamplePoints(int width, int height)
    {
        int maxX = Math.Max(0, width - 1);
        int maxY = Math.Max(0, height - 1);
        return new (int x, int y)[]
        {
            (0, 0),
            (width / 2, height / 2),
            (maxX, maxY),
            (width / 4, height / 4),
            ((maxX * 3) / 4, (maxY * 3) / 4),
            (maxX, 0),
            (0, maxY)
        };
    }
}

internal static class MsdfDiagnostics
{
    static readonly object Sync = new();
    static readonly HashSet<string> DumpedKeys = new();
    static readonly HashSet<int> TargetCodePoints = CreateTargetCodePoints("ABCDEFG1234567");

    public static bool Enabled = true;

    static HashSet<int> CreateTargetCodePoints(string chars)
    {
        HashSet<int> set = new();
        foreach (char c in chars)
            set.Add(c);
        return set;
    }

    public static bool ShouldDumpCodePoint(int codePoint) => Enabled && TargetCodePoints.Contains(codePoint);

    public static bool TryRegisterDump(string kind, int fontSize, int codePoint)
    {
        if (!ShouldDumpCodePoint(codePoint))
            return false;

        string key = $"{kind}:{fontSize}:{codePoint}";
        lock (Sync)
            return DumpedKeys.Add(key);
    }

    public static string GetFullPath(string relativePath)
    {
        string fullPath = StorageService.SubPath(StorageService.DirectoryBase, $"MsdfDebug/{relativePath}");
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        return fullPath;
    }

    public static string DescribeCodePoint(int codePoint)
    {
        string text = char.ConvertFromUtf32(codePoint);
        string safe = text.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
        return $"U+{codePoint:X4}-{safe}";
    }

    public static void DumpGlyphIfNeeded(
        string fontFileName,
        int fontSize,
        int codePoint,
        ushort glyphIndex,
        float msdfGlyphScale,
        float pixelRange,
        GlyphMetrics glyphMetrics,
        int width,
        int height,
        float[] rawData)
    {
        if (!TryRegisterDump("glyph", fontSize, codePoint))
            return;

        string codePointLabel = DescribeCodePoint(codePoint);
        string previewPath = GetFullPath($"glyphs/{codePointLabel}-size{fontSize}-preview.png");
        DumpPreview(rawData, width, height, pixelRange, previewPath);

        //DeviceServices.BaseApp.AddLog($"{DateTime.UtcNow} MsdfGlyphDump font={fontFileName}, codePoint={codePointLabel}, glyphIndex={glyphIndex}, fontSize={fontSize}, msdfGlyphScale={msdfGlyphScale:F2}, pixelRange={pixelRange:F2}, box={width}x{height}, advance={glyphMetrics.AdvanceWidth:F3}, preview={previewPath}");
    }

    static void DumpPreview(float[] rawData, int width, int height, float pixelRange, string previewPath)
    {
        var sdfBitmap = new MSDF.Bitmap(width, height, 4);
        var sdfData = sdfBitmap.GetRawData();
        Array.Copy(rawData, sdfData, rawData.Length);

        var preview = new MSDF.Bitmap(width, height, 1);
        MSDF.RenderSDF.Render4To1(preview.Section(), sdfBitmap.Section(), pixelRange);

        // Png save temporarily removed (SharpMSDF Png dependency removed).
    }
}
