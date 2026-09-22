// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Fonts;

// Shared types, platform-independent.

internal readonly record struct GlyphAtlasKey(int FontSize, int CodePoint, Font? Font = null);

internal readonly struct GlyphAtlasEntry
{
    public readonly GlyphAtlasKey Key;
    public readonly GlyphMetrics GlyphMetrics;
    public readonly float PixelRange;
    public readonly int X;
    public readonly int Y;
    public readonly int Width;
    public readonly int Height;
    public readonly float SourceX;
    public readonly float SourceY;
    public readonly float SourceWidth;
    public readonly float SourceHeight;
    public readonly int AtlasVersion;

    public GlyphAtlasEntry(
        GlyphAtlasKey key,
        GlyphMetrics glyphMetrics,
        float pixelRange,
        int x,
        int y,
        int width,
        int height,
        float sourceX,
        float sourceY,
        float sourceWidth,
        float sourceHeight,
        int atlasVersion)
    {
        Key = key;
        GlyphMetrics = glyphMetrics;
        PixelRange = pixelRange;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        SourceX = sourceX;
        SourceY = sourceY;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        AtlasVersion = atlasVersion;
    }
}

/// <summary>Dirty rectangle within the atlas, used for incremental uploads.</summary>
internal readonly struct AtlasUploadRect
{
    public readonly int X;
    public readonly int Y;
    public readonly int Width;
    public readonly int Height;

    public AtlasUploadRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}

// Generic shared glyph-atlas manager.

/// <summary>
/// Platform-independent glyph atlas manager.
/// Responsible for MTSDF glyph rasterization, shelf packing, and dirty-rectangle tracking and merging.
/// Platform-specific operations, such as texture creation, pixel upload, and frame-index retrieval,
/// are injected through delegates.
/// </summary>
/// <typeparam name="TTexture">Platform texture type, such as DXTexture, VkTexture, MTLTexture, or WgpuTexture.</typeparam>
internal sealed class GlyphAtlasManager<TTexture> : IDisposable
{
    // Merge-strategy constants.
    const int MergeGapTolerance = 2;
    const int MinDirtyRectCountForMerge = 4;
    const float MergeAreaGrowthLimit = 1.35f;
    const float TotalMergeAreaGrowthLimit = 1.2f;

    readonly object _sync = new();
    readonly object _stableSync = new();
    readonly List<GlyphAtlasManager<TTexture>> _stablePages = new();
    // Stable pages are cached by font identity and do not overwrite UVs already referenced by frames; explicit failure occurs when budget is reached.
    const int MaxStablePages = 16;
    readonly Dictionary<GlyphAtlasKey, GlyphAtlasEntry> _entries = new();
    readonly byte[] _pixels;
    readonly int _atlasWidth;
    readonly int _atlasHeight;
    readonly int _padding;
    readonly List<AtlasUploadRect> _dirtyRects = new();

    // Platform-specific delegates.
    readonly Func<int, int, TTexture> _createAtlasTexture;
    readonly Action<TTexture, byte[]> _uploadFullPixels;
    readonly Action<TTexture, byte[], int, int, AtlasUploadRect[]> _uploadSubRects;
    readonly Func<uint> _getCurrentFrameIndex;

    TTexture _atlasTexture = default!;
    bool _atlasTextureCreated;
    int _cursorX;
    int _cursorY;
    int _rowHeight;
    int _version = 1;
    bool _dirty;
    bool _fullAtlasDirty = true;
    uint _lastFlushFrame = uint.MaxValue;

    /// <summary>
    /// Creates a glyph atlas manager.
    /// </summary>
    /// <param name="atlasWidth">Atlas width in pixels.</param>
    /// <param name="atlasHeight">Atlas height in pixels.</param>
    /// <param name="createAtlasTexture">Factory method used to create the empty atlas texture.</param>
    /// <param name="uploadFullPixels">Uploads all pixels to the atlas texture as an RGBA8 byte array.</param>
    /// <param name="uploadSubRects">Uploads pixels for specified rectangular subregions to the atlas texture as an RGBA8 byte array.</param>
    /// <param name="getCurrentFrameIndex">Retrieves the current frame index, used for per-frame throttling so flush is not repeated within the same frame.</param>
    /// <param name="padding">Glyph spacing in pixels. Defaults to 2.</param>
    public GlyphAtlasManager(
        int atlasWidth,
        int atlasHeight,
        Func<int, int, TTexture> createAtlasTexture,
        Action<TTexture, byte[]> uploadFullPixels,
        Action<TTexture, byte[], int, int, AtlasUploadRect[]> uploadSubRects,
        Func<uint> getCurrentFrameIndex,
        int padding = 2)
    {
        _atlasWidth = atlasWidth;
        _atlasHeight = atlasHeight;
        _padding = Math.Max(1, padding);
        _pixels = new byte[_atlasWidth * _atlasHeight * 4];
        _createAtlasTexture = createAtlasTexture;
        _uploadFullPixels = uploadFullPixels;
        _uploadSubRects = uploadSubRects;
        _getCurrentFrameIndex = getCurrentFrameIndex;
        ResetPackingState();
    }

    /// <summary>Atlas texture, created lazily. The first access invokes <see cref="_createAtlasTexture"/>.</summary>
    public TTexture AtlasTexture
    {
        get
        {
            lock (_sync)
            {
                if (!_atlasTextureCreated)
                {
                    _atlasTexture = _createAtlasTexture(_atlasWidth, _atlasHeight);
                    _atlasTextureCreated = true;
                }
                return _atlasTexture;
            }
        }
    }

    /// <summary>Current atlas version number, used for glyph synchronization checks such as NeedsGlyphSync.</summary>
    public int Version
    {
        get { lock (_sync) { return _version; } }
    }

    /// <summary>
    /// Ensures that the specified glyph exists in the atlas. If it does not, it is rasterized and packed.
    /// </summary>
    /// <returns>True when the glyph is available and <c>entry</c> contains valid coordinates and metrics.</returns>
    public bool TryEnsureGlyph(int fontSize, int codePoint, out GlyphAtlasEntry entry)
        => TryEnsureGlyphCore(null, fontSize, codePoint, true, out entry, out _);

    bool TryEnsureGlyphCore(Font? explicitFont, int fontSize, int codePoint, bool allowReset,
        out GlyphAtlasEntry entry, out bool full)
    {
        full = false;
        var key = new GlyphAtlasKey(fontSize, codePoint, explicitFont);

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out entry))
                return true;
        }

        // MSDF rasterization runs outside the lock because it is pure CPU work and does not mutate shared state.
        // This avoids blocking the render thread for a long time inside FlushPendingUploadsOnRenderThread.
        (byte[] bytes, GlyphMetrics glyphMetrics, float pixelRange, int textureWidth, int textureHeight) glyphResult = default;
        bool found = false;

        if (explicitFont != null)
        {
            glyphResult = explicitFont.CreateMsdfGlyph(fontSize, codePoint);
            found = glyphResult.bytes != null;
        }
        else
        {
            foreach (var font in Season.Fonts.Font.Instance)
            {
                glyphResult = font.CreateMsdfGlyph(fontSize, codePoint);
                if (glyphResult.bytes is not null)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            entry = default;
            return false;
        }

        lock (_sync)
        {
            _ = AtlasTexture;

            // Double-check: another thread may have inserted it while rasterization was running.
            if (_entries.TryGetValue(key, out entry))
                return true;

            // Shelf packing.
            if (!TryAllocate(glyphResult.textureWidth, glyphResult.textureHeight, out int x, out int y))
            {
                if (!allowReset)
                {
                    full = true;
                    entry = default;
                    return false;
                }
                ResetAtlas();

                if (!TryAllocate(glyphResult.textureWidth, glyphResult.textureHeight, out x, out y))
                {
                    entry = default;
                    return false;
                }
            }

            BlitGlyph(glyphResult.bytes, glyphResult.textureWidth, glyphResult.textureHeight, x, y);

            entry = new GlyphAtlasEntry(
                key,
                glyphResult.glyphMetrics,
                glyphResult.pixelRange,
                x,
                y,
                glyphResult.textureWidth,
                glyphResult.textureHeight,
                x + (glyphResult.glyphMetrics.HasAtlasBounds ? glyphResult.glyphMetrics.AtlasSourceX : 0f),
                y + (glyphResult.glyphMetrics.HasAtlasBounds ? glyphResult.glyphMetrics.AtlasSourceY : 0f),
                glyphResult.glyphMetrics.HasAtlasBounds ? glyphResult.glyphMetrics.AtlasSourceWidth : glyphResult.textureWidth,
                glyphResult.glyphMetrics.HasAtlasBounds ? glyphResult.glyphMetrics.AtlasSourceHeight : glyphResult.textureHeight,
                _version);

            _entries[key] = entry;
            DumpAtlasIfNeeded(fontSize, codePoint);
            _dirty = true;
            return true;
        }
    }

    /// <summary>
    /// Flushes dirty regions to the GPU on the render thread.
    /// Includes built-in per-frame throttling so multiple calls within the same frame execute only once.
    /// </summary>
    public void FlushPendingUploadsOnRenderThread(bool force = false)
    {
        // Lock-free fast path: if there is no dirty data or this frame has already flushed, return immediately.
        // This avoids the render thread blocking on lock(_sync) while consumer-side MSDF rasterization is running.
        if (!_dirty)
            return;

        uint currentFrame = _getCurrentFrameIndex();
        if (!force && _lastFlushFrame == currentFrame)
            return;

        lock (_sync)
        {
            // Double-check: reads performed outside the lock may now be stale.
            if (!_dirty || (!force && _lastFlushFrame == currentFrame))
                return;

            if (_fullAtlasDirty)
            {
                _uploadFullPixels(_atlasTexture, _pixels);
            }
            else if (_dirtyRects.Count > 0)
            {
                var uploadRects = MergeDirtyRects();
                _uploadSubRects(_atlasTexture, _pixels, _atlasWidth, _atlasHeight,
                    uploadRects.ToArray());
            }

            _dirty = false;
            _fullAtlasDirty = false;
            _dirtyRects.Clear();
            _lastFlushFrame = currentFrame;
        }
    }

    /// <summary>Clears the atlas and resets packing state, for example when a font-size change forces a rebuild.</summary>
    public void ResetAtlas()
    {
        lock (_sync)
        {
            Array.Clear(_pixels);
            _entries.Clear();
            _version++;
            _dirty = true;
            _fullAtlasDirty = true;
            _dirtyRects.Clear();
            _lastFlushFrame = uint.MaxValue;
            ResetPackingState();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _entries.Clear();
            _dirtyRects.Clear();
        }
    }

    /// <summary>
    /// Explicit font stable paging entry, reusing the same packer and upload path. The single-page version semantics for regular Texts remain unchanged.
    /// When a page is full, it expands rather than resetting the atlas, ensuring that glyphs recorded in a frame are not overwritten by subsequent glyphs.
    /// </summary>
    internal bool TryEnsureStableGlyph(Font font, int size, int codePoint, out GlyphAtlasEntry entry, out TTexture texture)
    {
        lock (_stableSync)
        {
            var key = new GlyphAtlasKey(size, codePoint, font);
            foreach (var page in _stablePages)
            {
                if (page._entries.TryGetValue(key, out entry))
                {
                    texture = page.AtlasTexture;
                    return true;
                }
            }

            bool full = true;
            if (_stablePages.Count > 0)
            {
                var last = _stablePages[^1];
                if (last.TryEnsureGlyphCore(font, size, codePoint, false, out entry, out full))
                {
                    texture = last.AtlasTexture;
                    return true;
                }
            }
            if (full)
            {
                if (_stablePages.Count >= MaxStablePages)
                    throw new InvalidOperationException("The real-time glyph atlas has reached the stable page budget.");
                var page = new GlyphAtlasManager<TTexture>(_atlasWidth, _atlasHeight, _createAtlasTexture,
                    _uploadFullPixels, _uploadSubRects, _getCurrentFrameIndex, _padding);
                _stablePages.Add(page);
                if (page.TryEnsureGlyphCore(font, size, codePoint, false, out entry, out full))
                {
                    texture = page.AtlasTexture;
                    return true;
                }
                if (full) throw new InvalidOperationException("A single glyph exceeds the size of the atlas page.");
            }
            entry = default;
            texture = default!;
            return false;
        }
    }

    /// <summary>Only called during the frame preparation phase; The cyclic FrameIndex is not a frame number and cannot be used to miss new glyph shapes that appear after multiple frames.</summary>
    internal void FlushStablePages()
    {
        lock (_stableSync)
            foreach (var page in _stablePages) page.FlushPendingUploadsOnRenderThread(force: true);
    }

    /// <summary>Texture is recycled by the platform; The caller must ensure GPU idle or use delayed release.</summary>
    internal void DisposeStablePages(Action<TTexture> release)
    {
        lock (_stableSync)
        {
            foreach (var page in _stablePages)
            {
                if (page._atlasTextureCreated) release(page._atlasTexture);
                page.Dispose();
            }
            _stablePages.Clear();
        }
    }

    // Private methods: shelf packing.

    bool TryAllocate(int glyphWidth, int glyphHeight, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (glyphWidth <= 0 || glyphHeight <= 0
            || glyphWidth + 2 * _padding > _atlasWidth || glyphHeight + 2 * _padding > _atlasHeight)
            return false;

        // Line-wrap detection.
        if (_cursorX + glyphWidth + _padding > _atlasWidth)
        {
            _cursorX = _padding;
            _cursorY += _rowHeight + _padding;
            _rowHeight = 0;
        }

        if (_cursorY + glyphHeight + _padding > _atlasHeight)
            return false;

        x = _cursorX;
        y = _cursorY;

        _cursorX += glyphWidth + _padding;
        _rowHeight = Math.Max(_rowHeight, glyphHeight);
        return true;
    }

    void BlitGlyph(byte[] glyphBytes, int glyphWidth, int glyphHeight, int dstX, int dstY)
    {
        int srcRowBytes = glyphWidth * 4;

        for (int row = 0; row < glyphHeight; row++)
        {
            int srcOffset = row * srcRowBytes;
            int dstOffset = ((dstY + row) * _atlasWidth + dstX) * 4;
            Buffer.BlockCopy(glyphBytes, srcOffset, _pixels, dstOffset, srcRowBytes);
        }

        if (!_fullAtlasDirty)
            _dirtyRects.Add(new AtlasUploadRect(dstX, dstY, glyphWidth, glyphHeight));
    }

    void ResetPackingState()
    {
        _cursorX = _padding;
        _cursorY = _padding;
        _rowHeight = 0;
    }

    void DumpAtlasIfNeeded(int fontSize, int codePoint)
    {
        if (!MsdfDiagnostics.TryRegisterDump("atlas", fontSize, codePoint))
            return;

        string codePointLabel = MsdfDiagnostics.DescribeCodePoint(codePoint);
        // Png save temporarily removed (SharpMSDF Png dependency removed).
        DeviceServices.BaseApp.AddLog(LogType.Texts, $"{DateTime.UtcNow} MsdfGlyphDump codePoint={codePointLabel}, fontSize={fontSize}, atlasSize={_atlasWidth}x{_atlasHeight}, entries={_entries.Count}, version={_version}");
    }

    // Private methods: dirty-rectangle merging.

    List<AtlasUploadRect> MergeDirtyRects()
    {
        if (_dirtyRects.Count < MinDirtyRectCountForMerge)
            return _dirtyRects;

        int originalTotalArea = CalculateTotalArea(_dirtyRects);
        var orderedRects = new List<AtlasUploadRect>(_dirtyRects);
        orderedRects.Sort(static (left, right) =>
        {
            int compare = left.Y.CompareTo(right.Y);
            if (compare != 0) return compare;

            compare = left.Height.CompareTo(right.Height);
            if (compare != 0) return compare;

            return left.X.CompareTo(right.X);
        });

        var mergedRects = new List<AtlasUploadRect>(orderedRects.Count);
        var current = orderedRects[0];

        for (int i = 1; i < orderedRects.Count; i++)
        {
            var next = orderedRects[i];

            if (CanMergeHorizontally(current, next))
                current = MergeRectPair(current, next);
            else
            {
                mergedRects.Add(current);
                current = next;
            }
        }

        mergedRects.Add(current);
        int mergedTotalArea = CalculateTotalArea(mergedRects);

        if (mergedRects.Count >= _dirtyRects.Count)
            return _dirtyRects;

        if (mergedTotalArea > (int)MathF.Ceiling(originalTotalArea * TotalMergeAreaGrowthLimit))
            return _dirtyRects;

        return mergedRects;
    }

    static bool CanMergeHorizontally(AtlasUploadRect left, AtlasUploadRect right)
    {
        if (left.Y != right.Y || left.Height != right.Height)
            return false;

        int leftRight = left.X + left.Width;
        if (right.X > leftRight + MergeGapTolerance)
            return false;

        var merged = MergeRectPair(left, right);
        int mergedArea = merged.Width * merged.Height;
        int originalArea = (left.Width * left.Height) + (right.Width * right.Height);

        return mergedArea <= (int)MathF.Ceiling(originalArea * MergeAreaGrowthLimit);
    }

    static AtlasUploadRect MergeRectPair(AtlasUploadRect left, AtlasUploadRect right)
    {
        int x = Math.Min(left.X, right.X);
        int y = Math.Min(left.Y, right.Y);
        int rightEdge = Math.Max(left.X + left.Width, right.X + right.Width);
        int bottomEdge = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new AtlasUploadRect(x, y, rightEdge - x, bottomEdge - y);
    }

    static int CalculateTotalArea(List<AtlasUploadRect> rects)
    {
        int totalArea = 0;
        for (int i = 0; i < rects.Count; i++)
            totalArea += rects[i].Width * rects[i].Height;
        return totalArea;
    }
}
