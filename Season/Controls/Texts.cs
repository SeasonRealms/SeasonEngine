// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

public class Texts : Control, IRenderOrder
{
    struct GlyphLayoutState
    {
        public bool HasGlyph;
        public int PosX;
        public int PosY;
        public int Width;
        public int Height;
        public int OriginWidth;
        public int OriginHeight;
        public int LayoutWidth;
        public float Alpha;
    }

    /// <summary>Layout snapshot at the start of a line, recording the full layout state before
    /// the glyph at <see cref="TexIndex"/> is processed.
    /// Replaying from this point reproduces this line's wrapping decisions exactly, including
    /// whole-word lookahead from <see cref="MeasureLatinWordWidth"/>, so incremental layout matches
    /// a full relayout pixel for pixel.
    /// The trailing fields form a layout signature. If any of them changes, the checkpoint becomes invalid
    /// and a full relayout is required.</summary>
    struct LineCheckpoint
    {
        public bool Valid;
        public int TexIndex;
        public float CursorX;
        public float CursorY;
        public bool HasPre;
        public Tex Pre;
        public bool IsStop;
        public int MaxPosX;
        public int MaxPosY;
        public int LastLineHeight;
        public int MinVisualY;
        public int MaxVisualY;

        public float BasePosX;
        public float BasePosY;
        public int? WidthRequest;
        public int? HeightRequest;
        public Vector2 Scale;
        public int LineHeight;
        public float Alpha;
    }

    /// <summary>CJK punctuation lookup table. These characters are outside the main CJK unified block,
    /// but their line-breaking behavior must still be treated as CJK.
    /// Making this static avoids per-glyph array allocation inside the layout loop.</summary>
    static readonly char[] CjkFormatChars = { '，', '。', '！', '？', '：', '；', '“', '”', '‘', '’', '（', '）', '【', '】', '—', '…', '·', '、' };

    readonly object syncRoot = new();

    internal object SyncRoot => syncRoot;

    bool deferredContentLoadWhileHidden;
    GlyphLayoutState[] glyphLayouts = Array.Empty<GlyphLayoutState>();
    GlyphLayoutState dotLayout;
    bool dotLayoutVisible;
    LineCheckpoint layoutCheckpoint;

    // Incremental append state.
    // appendDirty and ContentDirty are mutually exclusive.
    // MarkContentDirty also discards pending appended data because a full rebuild reparses
    // every glyph from the already merged content.
    bool appendDirty;
    List<Tex> pendingAppendTexs;

    /// <summary>While LoadAppend is performing backend AppendTexts and Texs swapping,
    /// Update() must pause UpdateTexts.
    /// Backend UpdateTexts works by taking a state snapshot and blindly writing it back to the dictionary tail,
    /// while AppendTexts runs on the loader thread and writes the expanded state back without holding SyncRoot.
    /// If they interleave, the stale UpdateTexts snapshot can roll InstanceCount back.
    /// Then Texs completes its swap, the glyph count exceeds buffer capacity, and UpdateTexts hits an array out-of-range.
    /// Setting and clearing this flag both happen under SyncRoot, making them atomic with the
    /// SyncRoot-protected UpdateTexts call in Update, without needing to hold the lock across await.</summary>
    bool appendLoadInProgress;

    /// <summary>BuildTex span-markup parsing depends on absolute indices in the whole string.
    /// During incremental append, the parse source is the already merged full text,
    /// which may not be the same reference passed into Build, so this field explicitly
    /// identifies the current parse source.</summary>
    string parseSource;

    const string SpanMarkupPrefix = "<span";

    /// <summary>Whether span markup has ever appeared in content.
    /// Span color scopes may continue across chunks and their indices are absolute within the full string,
    /// so the tail alone cannot be parsed safely. Once span markup appears, incremental append stays disabled
    /// until the next full content assignment.</summary>
    bool contentHasSpanMarkup;

    string DebugLabel()
    {
        var text = ContentOrigin.NullToString().Replace("\r", "\\r").Replace("\n", "\\n");
        if (text.Length > 24)
            text = text[..24] + "...";
        return text;
    }

    void MarkContentDirty()
    {
        Ready = false;
        ContentDirty = true;
        Changed = true;

        // A full rebuild reparses the entire text, and the pending appended tail is already included in content,
        // so it must be discarded to avoid appending it twice.
        appendDirty = false;
        pendingAppendTexs = null;
        layoutCheckpoint = default;
    }

    void DeferContentLoadWhileHidden()
    {
        deferredContentLoadWhileHidden = true;
        ContentDirty = false;
        Ready = false;
        Changed = false;
    }

    bool showDot;
    public bool ShowDot
    {
        get => showDot;
        set
        {
            if (showDot != value)
            {
                showDot = value;
                MarkContentDirty();
            }
        }
    }

    public bool Translate { get; set; } = true;

    public int Layer { get; set; }

    public int Order { get; set; }

    string content;
    public string Content
    {
        get
        {
            ////if (!Translate || BaseApp.Instance.Words is null || BaseApp.Instance.Words.Lan is null or "" || content.IsNullOrWhiteSpace())
            //if (!Translate || BaseApp.Instance.Words is null || BaseApp.Instance.Settings.Language is null or "" || content.IsNullOrWhiteSpace())
            //{
            //}
            //else
            //{
            //    return WordsUtils.Translate(content);
            //}

            return content;
        }
        set
        {
            if (content != value)
            {
                content = value;
                contentHasSpanMarkup = value != null && value.Contains(SpanMarkupPrefix, StringComparison.Ordinal);
                MarkContentDirty();
            }
        }
    }

    public string ContentOrigin
    {
        get
        {
            return content;
        }
    }

    int? widthRequest;
    public int? WidthRequest
    {
        get => widthRequest;
        set
        {
            if (widthRequest != value)
            {
                widthRequest = value;
                Changed = true;
            }
        }
    }

    int? heightRequest;
    public int? HeightRequest
    {
        get => heightRequest;
        set
        {
            if (heightRequest != value)
            {
                heightRequest = value;
                Changed = true;
            }
        }
    }

    int visualOffsetTop;
    public int VisualOffsetTop
    {
        get => visualOffsetTop;
        private set => visualOffsetTop = value;
    }

    int originWidthValue;
    public int OriginWidth
    {
        get => originWidthValue;
        private set => originWidthValue = value;
    }

    int originHeightValue;
    public int OriginHeight
    {
        get => originHeightValue;
        private set => originHeightValue = value;
    }

    public int LineHeight = 40;

    public float WordsSpace;

    public float EmptySpace = 10;  //for english

    public Tex[] Texs = new Tex[] { };

    public Tex[] TexsLoading = new Tex[] { };

    public Vector2? LastPos;

    // PosX and PosY have been unified into Control under the float-based positioning model,
    // with the same Changed gating behavior, so they are no longer declared here.

    Season.Basic.Color color;
    public Season.Basic.Color Color
    {
        get
        {
            return color;
        }
        set
        {
            if (color != value)
            {
                color = value;
                Changed = true;
            }
        }
    }

    Vector2 scale = Vector2.One;
    public Vector2 Scale
    {
        get
        {
            return scale;
        }
        set
        {
            if (scale != value)
            {
                scale = value;
                Changed = true;
            }
        }
    }

    public TextsType TextsType;

    Tex dot;

    internal ITextureHolder[] textureHoldersLoading;
    internal ITextureHolder[] textureHolders;
    internal ITextureHolder dotTextureHolderLoading;
    internal ITextureHolder dotTextureHolder;
    internal ref Tex _dotRef => ref dot;

    public override string ToString()
    {
        return Content;
    }

    public void Build(string content)
    {
        TexsLoading = BuildRange(content, 0).ToArray();
        glyphLayouts = new GlyphLayoutState[TexsLoading.Length];
        dotLayout = default;
        dotLayoutVisible = false;
        layoutCheckpoint = default;
        appendDirty = false;
        pendingAppendTexs = null;

        if (ShowDot)
        {
            dot = new Tex(TexType.Normal)
            {
                Value = char.ConvertToUtf32(".", 0),
                Alpha = 1
            };
        }
        else
        {
            dot = default;
        }
    }

    /// <summary>Parses the tail of <paramref name="source"/> starting at <paramref name="start"/>,
    /// where start is a character index, into a glyph sequence.
    /// Span-markup parsing depends on absolute indices within the whole string, so the full text must be passed in,
    /// not just the tail substring.</summary>
    List<Tex> BuildRange(string source, int start)
    {
        var previousSource = parseSource;
        parseSource = source;

        try
        {
            var textSpan = source.NullToString().ToArray();

            var index = Math.Clamp(start, 0, textSpan.Length);

            //<span style='color:Red;font-weight:bold;'>T W</span>

            bool span = false;
            int spanTarget = 0;
            Season.Basic.Color? spanColor = null;

            var texList = new List<Tex>();

            while (index < textSpan.Length)
            {
                var codePoint = char.ConvertToUtf32(source, index);

                index += char.IsSurrogatePair(source, index) ? 2 : 1;

                var tex = BuildTex(codePoint, textSpan, ref index, ref span, ref spanColor, ref spanTarget);

                if (tex.HasValue)
                {
                    texList.Add(tex.Value);
                }
            }

            return texList;
        }
        finally
        {
            parseSource = previousSource;
        }
    }

    /// <summary>Incrementally appends a text tail for streaming-output scenarios.
    /// <para><paramref name="content"/> is the newly added fragment for this call, not a full snapshot.
    /// After the call, <see cref="Content"/> becomes the concatenation of the previous content and this fragment.</para>
    /// Internally, only new glyphs are parsed, only new glyphs get atlas entries and holders,
    /// and layout resumes only from the start of the last line, avoiding the full Build/LoadTexts/Position path
    /// that direct assignment to <see cref="Content"/> would trigger.
    /// When safe incremental append is not possible, such as not yet ready, a pending full rebuild already exists,
    /// span markup crosses chunks, or a surrogate pair is split, it automatically falls back to a full rebuild.
    /// The visible result is equivalent to assigning <see cref="Content"/> directly.</summary>
    public void Append(string content)
    {
        if (string.IsNullOrEmpty(content))
            return;

        lock (SyncRoot)
        {
            var existing = this.content.NullToString();

            // Span markup may be split across streamed chunks, so the detection window must cross
            // the concatenation boundary by markup-length minus one.
            if (!contentHasSpanMarkup)
            {
                var boundary = existing.Length > SpanMarkupPrefix.Length - 1
                    ? existing[^(SpanMarkupPrefix.Length - 1)..]
                    : existing;

                if ((boundary + content).Contains(SpanMarkupPrefix, StringComparison.Ordinal))
                    contentHasSpanMarkup = true;
            }

            if (!CanAppendIncrementally(existing, content))
            {
                this.content = existing + content;
                MarkContentDirty();
                return;
            }

            var merged = existing + content;
            this.content = merged;

            var appended = BuildRange(merged, existing.Length);

            if (appended.Count == 0)
            {
                // The tail produced no glyphs, for example pure markup.
                // Content has already been merged, so no rebuild is needed.
                return;
            }

            pendingAppendTexs ??= new List<Tex>();
            pendingAppendTexs.AddRange(appended);
            appendDirty = true;
        }
    }

    /// <summary>Feasibility gate for incremental append.
    /// If any condition fails, it falls back to a full rebuild.
    /// Doing one more full rebuild is preferable to leaving holes where appended glyphs have no holders
    /// or become misaligned.</summary>
    bool CanAppendIncrementally(string existing, string tail)
    {
        if (!DeviceServices.BaseApp.FontsCreated)
            return false;

        // The first load is not finished yet, or a full rebuild is already pending,
        // so there is no stable base state to append onto.
        if (!Ready || ContentDirty || deferredContentLoadWhileHidden)
            return false;

        if (TexsLoading != null && TexsLoading.Length > 0)
            return false;

        if (Texs == null || Texs.Length == 0)
            return false;

        if (textureHolders == null || textureHolders.Length < Texs.Length)
            return false;

        // Span-markup parsing depends on absolute indices within the whole string,
        // and color scopes may continue across chunks, so the tail alone cannot be parsed.
        if (contentHasSpanMarkup)
            return false;

        // A surrogate pair was split across two calls, so the tail cannot be decoded independently.
        if (existing.Length > 0 && char.IsHighSurrogate(existing[^1]))
            return false;

        if (char.IsLowSurrogate(tail[0]) || char.IsHighSurrogate(tail[^1]))
            return false;

        return true;
    }

    public Tex? BuildTex(int codePoint, char[] textSpan, ref int index, ref bool span, ref Season.Basic.Color? spanColor, ref int spanTarget)
    {
        Tex tex;

        var source = parseSource ?? content;

        var spanStart = "<span ";
        var spanMiddle = ">";
        var spanEnd = "</span>";

        // Check whether this is a newline code point.
        bool isNewLine = codePoint == '\n' ||  // Line feed (LF)
                         codePoint == '\r' ||  // Carriage return (CR)
                         codePoint == '\u0085' ||  // Next line (NEL)
                         codePoint == '\u2028' ||  // Line separator
                         codePoint == '\u2029';    // Paragraph separator

        // Check whether this is a whitespace code point.
        bool isWhitespace = codePoint == ' ' ||   // Space
                            codePoint == '\t' ||  // Horizontal tab
                            codePoint == '\v' ||  // Vertical tab
                            codePoint == '\f';    // Form feed

        if (isNewLine)
        {
            tex = new Tex(TexType.NewLine);
        }
        else if (isWhitespace)
        {
            tex = new Tex(TexType.Space);
        }
        else
        {
            var currentIndex = index; // - charsConsumed;

            //if (index < textSpan.Length && textSpan[currentIndex] == '<' && index + spanStart.Length < textSpan.Length && content.Substring(index - charsConsumed, spanStart.Length) == spanStart)
            if (index < textSpan.Length && textSpan[currentIndex] == '<' && index + spanStart.Length < textSpan.Length && source.Substring(index, spanStart.Length) == spanStart)
            {
                var spanStartIndex = currentIndex;

                var spanContentIndex = spanStartIndex + spanStart.Length;

                var spanMiddleIndex = source.IndexOf(spanMiddle, spanContentIndex);

                if (spanMiddleIndex > 0)
                {
                    var spanEndIndex = source.IndexOf(spanEnd, spanMiddleIndex + spanMiddle.Length);

                    if (spanEndIndex > 0)
                    {
                        var spanContent = source.Substring(spanContentIndex, spanMiddleIndex - spanContentIndex);

                        var colorText = "color:";

                        if (spanContent.Contains(colorText))
                        {
                            var spanContentColorStart = spanContent.IndexOf(colorText);

                            var spanContentColorMiddle = spanContentColorStart + colorText.Length;

                            var spanContentColorEnd = spanContent.IndexOf(";", spanContentColorMiddle);

                            if (spanContentColorEnd > 0)
                            {
                                var spanContentColor = spanContent.Substring(spanContentColorMiddle, spanContentColorEnd - spanContentColorMiddle);

                                spanColor = Season.Basic.Colors.FromName(spanContentColor);
                            }
                        }

                        index = spanMiddleIndex + spanMiddle.Length;

                        span = true;
                        spanTarget = spanEndIndex; // + spanEnd.Length;

                        //continue;

                        return null;
                    }
                }
            }

            tex = new Tex(TexType.Normal);

            if (span)
            {
                if (spanColor is null)
                {

                }
                else
                {
                    tex.Color = spanColor;
                }

                if (index == spanTarget)
                {
                    span = false;
                    index = spanTarget + spanEnd.Length;
                }
            }

            tex.Value = codePoint;
        }

        return tex;
    }

    Tex[] GetLayoutTexs()
    {
        if (TexsLoading != null && TexsLoading.Length > 0)
            return TexsLoading;

        return Texs ?? Array.Empty<Tex>();
    }

    bool EnsureGlyphMetricsForLayout(ref Tex tex)
    {
        if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
            return false;

        if (tex.GlyphMetrics.HasPlaneBounds
            || tex.GlyphMetrics.HasAtlasBounds
            || tex.GlyphMetrics.Width > 0
            || tex.GlyphMetrics.Height > 0
            || tex.GlyphMetrics.AdvanceWidth > 0f)
        {
            if (tex.Factor <= 0f)
                tex.Factor = Season.Fonts.Font.PixelRange;
            return true;
        }

        DeviceServices.BaseApp.AddLog(LogType.Texts,
            $"{DateTime.UtcNow} [Texts.Layout] missing-real-metrics text=\"{DebugLabel()}\" codePoint={tex.Value} char=\"{char.ConvertFromUtf32(tex.Value)}\"");
        tex.TexType = TexType.Missing;
        tex.GlyphMetrics = default;
        tex.Factor = Season.Fonts.Font.PixelRange;
        return false;
    }

    bool TryMeasureGlyph(
        ref Tex tex,
        out int drawWidth,
        out int drawHeight,
        out int offsetX,
        out int offsetY,
        out int layoutWidth,
        out int originWidth,
        out int originHeight,
        out int advancePx)
    {
        drawWidth = 0;
        drawHeight = 0;
        offsetX = 0;
        offsetY = 0;
        layoutWidth = 0;
        originWidth = 0;
        originHeight = 0;
        advancePx = 0;

        if (!EnsureGlyphMetricsForLayout(ref tex))
            return false;

        var current = tex;
        advancePx = (int)MathF.Round(current.GlyphMetrics.AdvanceWidth * Scale.X);
        originWidth = Math.Max(0, current.GlyphMetrics.Width);
        originHeight = Math.Max(0, current.GlyphMetrics.Height);
        float baselineY = Season.Fonts.Font.Instance[0].FontMetrics.Ascent * Scale.Y;
        float glyphLeftPx = current.GlyphMetrics.X0 * Scale.X;
        float glyphTopPx = current.GlyphMetrics.Y0 * Scale.Y;
        float glyphRightPx = current.GlyphMetrics.X1 * Scale.X;

        drawWidth = Math.Max(0, (int)MathF.Round(originWidth * Scale.X));
        drawHeight = Math.Max(0, (int)MathF.Round(originHeight * Scale.Y));
        offsetX = (int)MathF.Round(glyphLeftPx);
        offsetY = (int)MathF.Round(baselineY + glyphTopPx);
        layoutWidth = Math.Max(
            advancePx,
            (int)MathF.Round(Math.Max(glyphRightPx, originWidth * Scale.X)));

        return drawWidth > 0 || drawHeight > 0 || layoutWidth > 0 || advancePx > 0;
    }

    float MeasureLatinWordWidth(Tex[] layoutTexs, int startIndex)
    {
        float width = 0f;

        for (var j = startIndex; j < layoutTexs.Length; j++)
        {
            ref var next = ref layoutTexs[j];

            if (next.TexType is TexType.NewLine or TexType.Space || char.IsPunctuation((char)next.Value) || IsCJK(next.Value))
                break;

            if (!EnsureGlyphMetricsForLayout(ref next))
                continue;

            width += next.GlyphMetrics.Width + WordsSpace * Scale.X;
        }

        return width;
    }

    void ApplyLayoutToHolder(ITextureHolder holder, ref Tex tex, in GlyphLayoutState layout)
    {
        if (holder?.Texture == null)
            return;

        holder.Texture.Changed = true;
        holder.Texture.Color = tex.Color ?? Color;
        holder.Texture.Alpha = layout.Alpha;
        holder.Texture.Width = layout.Width;
        holder.Texture.Height = layout.Height;
        holder.Texture.PosX = layout.PosX;
        holder.Texture.PosY = layout.PosY;
    }

    ITextureHolder GetLayoutHolderAt(ITextureHolder[] holders, int index)
    {
        if (holders == null || index < 0 || index >= holders.Length)
            return null;

        return holders[index];
    }

    void ApplyCurrentLayoutToActiveHolders()
    {
        int glyphCount = Math.Min(glyphLayouts.Length, Texs?.Length ?? 0);
        for (int i = 0; i < glyphCount; i++)
        {
            if (!glyphLayouts[i].HasGlyph)
                continue;

            ref var tex = ref Texs[i];
            ApplyLayoutToHolder(GetLayoutHolderAt(textureHolders, i), ref tex, glyphLayouts[i]);
        }

        if (dotLayoutVisible && dotLayout.HasGlyph)
            ApplyLayoutToHolder(dotTextureHolder, ref dot, dotLayout);
    }

    bool HasLayoutSourceForUpdate()
    {
        return Ready && Texs != null && Texs.Length > 0;
    }

    void LogHolderLayoutMismatchIfAny()
    {
        int glyphCount = Math.Min(glyphLayouts.Length, Texs?.Length ?? 0);

        if (glyphCount > 0 && (textureHolders == null || textureHolders.Length < glyphCount))
        {
            DeviceServices.BaseApp.AddLog(LogType.Texts,
                $"{DateTime.UtcNow} [Texts.Load] holder-mismatch text=\"{DebugLabel()}\" holders={(textureHolders?.Length ?? 0)} glyphs={glyphCount}");
            return;
        }

        for (int i = 0; i < glyphCount; i++)
        {
            var layout = glyphLayouts[i];
            if (!layout.HasGlyph)
                continue;

            var holder = GetLayoutHolderAt(textureHolders, i);
            if (holder?.Texture == null)
            {
                DeviceServices.BaseApp.AddLog(LogType.Texts,
                    $"{DateTime.UtcNow} [Texts.Load] holder-mismatch text=\"{DebugLabel()}\" index={i} reason=holder-null");
                return;
            }

            var texture = holder.Texture;
            if (texture.PosX != layout.PosX || texture.PosY != layout.PosY ||
                texture.Width != layout.Width || texture.Height != layout.Height)
            {
                DeviceServices.BaseApp.AddLog(LogType.Texts,
                    $"{DateTime.UtcNow} [Texts.Load] holder-mismatch text=\"{DebugLabel()}\" index={i} " +
                    $"layout=({layout.PosX},{layout.PosY},{layout.Width},{layout.Height}) " +
                    $"holder=({texture.PosX},{texture.PosY},{texture.Width},{texture.Height})");
                return;
            }
        }

        if (dotLayoutVisible && dotLayout.HasGlyph)
        {
            if (dotTextureHolder?.Texture == null)
            {
                DeviceServices.BaseApp.AddLog(LogType.Texts,
                    $"{DateTime.UtcNow} [Texts.Load] holder-mismatch text=\"{DebugLabel()}\" dot=holder-null");
                return;
            }

            var texture = dotTextureHolder.Texture;
            if (texture.PosX != dotLayout.PosX || texture.PosY != dotLayout.PosY ||
                texture.Width != dotLayout.Width || texture.Height != dotLayout.Height)
            {
                DeviceServices.BaseApp.AddLog(LogType.Texts,
                    $"{DateTime.UtcNow} [Texts.Load] holder-mismatch text=\"{DebugLabel()}\" dot " +
                    $"layout=({dotLayout.PosX},{dotLayout.PosY},{dotLayout.Width},{dotLayout.Height}) " +
                    $"holder=({texture.PosX},{texture.PosY},{texture.Width},{texture.Height})");
            }
        }
    }

    internal void InvalidateLayout()
    {
        Changed = true;
    }

    void HideDotHolder()
    {
        if (dotTextureHolder?.Texture != null)
            dotTextureHolder.Texture.Alpha = 0f;
    }

    void Position()
    {
        RunLayout(false);
    }

    /// <summary>Resumes layout from the checkpoint at the start of the last line,
    /// used only by the incremental-append path.
    /// When the checkpoint is invalid, such as no line break having occurred yet or the layout signature changing,
    /// it automatically degrades to a full relayout.</summary>
    void PositionAppended()
    {
        RunLayout(true);
    }

    bool CheckpointMatchesLayout()
    {
        return layoutCheckpoint.Valid
            && layoutCheckpoint.BasePosX == PosX
            && layoutCheckpoint.BasePosY == PosY
            && layoutCheckpoint.WidthRequest == WidthRequest
            && layoutCheckpoint.HeightRequest == HeightRequest
            && layoutCheckpoint.Scale == Scale
            && layoutCheckpoint.LineHeight == LineHeight
            && layoutCheckpoint.Alpha == Alpha;
    }

    void RunLayout(bool resume)
    {
        var layoutTexs = GetLayoutTexs();
        var layoutHolders = ReferenceEquals(layoutTexs, Texs) ? textureHolders : null;

        bool resumed = resume
            && CheckpointMatchesLayout()
            && layoutCheckpoint.TexIndex > 0
            && layoutCheckpoint.TexIndex <= layoutTexs.Length
            && glyphLayouts.Length >= layoutCheckpoint.TexIndex;

        var start = resumed ? layoutCheckpoint.TexIndex : 0;

        if (glyphLayouts.Length != layoutTexs.Length)
        {
            var resized = new GlyphLayoutState[layoutTexs.Length];
            if (start > 0)
                Array.Copy(glyphLayouts, resized, Math.Min(start, Math.Min(glyphLayouts.Length, resized.Length)));
            glyphLayouts = resized;
        }
        else
        {
            Array.Clear(glyphLayouts, start, glyphLayouts.Length - start);
        }

        var cursor = new Vector2(PosX, PosY);
        Tex? pre = null;

        bool isStop = false;
        int maxPosX = 0;
        int maxPosY = (int)cursor.Y;
        int lastLineHeight = 0;
        int minVisualY = int.MaxValue;
        int maxVisualY = int.MinValue;
        var basicOffsetY = 0f;

        if (resumed)
        {
            cursor = new Vector2(layoutCheckpoint.CursorX, layoutCheckpoint.CursorY);
            pre = layoutCheckpoint.HasPre ? layoutCheckpoint.Pre : null;
            isStop = layoutCheckpoint.IsStop;
            maxPosX = layoutCheckpoint.MaxPosX;
            maxPosY = layoutCheckpoint.MaxPosY;
            lastLineHeight = layoutCheckpoint.LastLineHeight;
            minVisualY = layoutCheckpoint.MinVisualY;
            maxVisualY = layoutCheckpoint.MaxVisualY;

            // If truncation had not occurred before the checkpoint, any ellipsis belongs to the relayout range
            // and must be recomputed. Otherwise it was generated before the checkpoint and stays untouched,
            // because the glyphLayouts prefix it depends on is still valid.
            if (!isStop)
            {
                LastPos = null;
                dotLayout = default;
                dotLayoutVisible = false;
                HideDotHolder();
            }
        }
        else
        {
            LastPos = null;
            dotLayout = default;
            dotLayoutVisible = false;
            HideDotHolder();
            layoutCheckpoint = default;
        }

        for (var i = start; i < layoutTexs.Length; i++)
        {
            ref var tex = ref layoutTexs[i];

            // Full state snapshot before this iteration.
            // If a line break occurs at index i, this snapshot becomes the checkpoint for the new line.
            var checkpoint = new LineCheckpoint
            {
                Valid = true,
                TexIndex = i,
                CursorX = cursor.X,
                CursorY = cursor.Y,
                HasPre = pre != null,
                Pre = pre ?? default,
                IsStop = isStop,
                MaxPosX = maxPosX,
                MaxPosY = maxPosY,
                LastLineHeight = lastLineHeight,
                MinVisualY = minVisualY,
                MaxVisualY = maxVisualY,
                BasePosX = PosX,
                BasePosY = PosY,
                WidthRequest = WidthRequest,
                HeightRequest = HeightRequest,
                Scale = Scale,
                LineHeight = LineHeight,
                Alpha = Alpha,
            };
            var lineBreak = false;

            if (tex.TexType is TexType.NewLine)
            {
                cursor.Y += LineHeight * Scale.Y;
                cursor.X = PosX;
                lineBreak = true;
            }
            else if (tex.TexType is TexType.Space)
            {
                cursor.X += EmptySpace * Scale.X;
            }
            else if (tex.TexType is TexType.Missing)
            {
                cursor.X += DeviceServices.BaseApp.FontSize * Scale.X;
            }
            else
            {
                if (!TryMeasureGlyph(ref tex, out int drawWidth, out int drawHeight, out int offsetX, out int offsetY, out int layoutWidthPx, out int originWidth, out int originHeight, out int advancePx))
                {
                    cursor.X += DeviceServices.BaseApp.FontSize * Scale.X;
                    pre = tex;
                    continue;
                }

                tex.Alpha = Alpha;

                int codePoint = tex.Value;
                var isCJK = IsCJK(codePoint);
                if (!isCJK)
                {
                    for (var f = 0; f < CjkFormatChars.Length; f++)
                    {
                        if (CjkFormatChars[f] == codePoint)
                        {
                            isCJK = true;
                            break;
                        }
                    }
                }

                int drawPosX;
                int drawPosY;

                if (pre != null && (((Tex)pre).TexType is TexType.Space || char.IsPunctuation((char)((Tex)pre).Value)))
                {
                    if (isCJK)
                    {
                        var preX = cursor.X + layoutWidthPx;

                        if (WidthRequest != null && preX > PosX + WidthRequest - DeviceServices.BaseApp.FontSize * Scale.X)
                        {
                            cursor.Y += LineHeight * Scale.Y;
                            cursor.X = PosX;
                            lineBreak = true;
                            drawPosX = (int)cursor.X + offsetX;
                            drawPosY = (int)cursor.Y + offsetY;
                            cursor.X = cursor.X + advancePx + WordsSpace * Scale.X;
                            if (!isStop)
                                lastLineHeight = drawHeight;
                        }
                        else
                        {
                            drawPosX = (int)cursor.X + offsetX;
                            drawPosY = (int)cursor.Y + offsetY;
                            cursor.X = cursor.X + advancePx;
                            if (!isStop && lastLineHeight < drawHeight)
                                lastLineHeight = drawHeight;
                        }
                    }
                    else
                    {
                        if (i == layoutTexs.Length - 1)
                        {
                            drawPosX = (int)cursor.X + offsetX;
                            drawPosY = (int)(cursor.Y + basicOffsetY) + offsetY;
                            cursor.X = cursor.X + advancePx;
                        }
                        else
                        {
                            var width = MeasureLatinWordWidth(layoutTexs, i);

                            if (WidthRequest != null && cursor.X + width > PosX + WidthRequest - DeviceServices.BaseApp.FontSize * Scale.X && (HeightRequest is null || HeightRequest > LineHeight))
                            {
                                cursor.Y += LineHeight * Scale.Y;
                                cursor.X = PosX;
                                lineBreak = true;
                                drawPosX = (int)cursor.X + offsetX;
                                drawPosY = (int)(cursor.Y + basicOffsetY) + offsetY;
                                cursor.X = cursor.X + advancePx + WordsSpace * Scale.X;
                                if (!isStop)
                                    lastLineHeight = drawHeight;
                            }
                            else
                            {
                                drawPosX = (int)cursor.X + offsetX;
                                drawPosY = (int)(cursor.Y + basicOffsetY) + offsetY;
                                cursor.X = cursor.X + advancePx + WordsSpace * Scale.X;
                                if (!isStop && lastLineHeight < drawHeight)
                                    lastLineHeight = drawHeight;
                            }
                        }
                    }
                }
                else
                {
                    var preX = cursor.X + layoutWidthPx;

                    if (isCJK)
                    {
                        if (WidthRequest != null && preX + DeviceServices.BaseApp.FontSize * Scale.X > PosX + WidthRequest + 20)
                        {
                            cursor.Y += LineHeight * Scale.Y;
                            cursor.X = PosX;
                            lineBreak = true;
                            drawPosX = (int)cursor.X + offsetX;
                            drawPosY = (int)cursor.Y + offsetY;
                            cursor.X = cursor.X + advancePx + WordsSpace * Scale.X;
                            if (!isStop)
                                lastLineHeight = drawHeight;
                        }
                        else
                        {
                            drawPosX = (int)cursor.X + offsetX;
                            drawPosY = (int)cursor.Y + offsetY;
                            cursor.X = cursor.X + advancePx;
                            if (!isStop && lastLineHeight < drawHeight)
                                lastLineHeight = drawHeight;
                        }
                    }
                    else
                    {
                        drawPosX = (int)cursor.X + offsetX;
                        drawPosY = (int)(cursor.Y + basicOffsetY) + offsetY;

                        if (WidthRequest != null && preX > PosX + WidthRequest)
                        {
                            cursor.Y += LineHeight * Scale.Y;
                            cursor.X = PosX;
                            lineBreak = true;
                        }
                        else
                        {
                            cursor.X = cursor.X + advancePx;
                        }

                        if (!isStop && lastLineHeight < drawHeight)
                            lastLineHeight = drawHeight;
                    }
                }

                var layout = new GlyphLayoutState
                {
                    HasGlyph = true,
                    PosX = drawPosX,
                    PosY = drawPosY,
                    Width = drawWidth,
                    Height = drawHeight,
                    OriginWidth = originWidth,
                    OriginHeight = originHeight,
                    LayoutWidth = layoutWidthPx,
                    Alpha = tex.Alpha
                };

                glyphLayouts[i] = layout;
                ApplyLayoutToHolder(GetLayoutHolderAt(layoutHolders, i), ref tex, layout);

                if (HeightRequest != null && cursor.Y + lastLineHeight - basicOffsetY >= PosY + HeightRequest)
                {
                    isStop = true;
                    layout.Alpha = 0f;
                    glyphLayouts[i] = layout;
                    ApplyLayoutToHolder(GetLayoutHolderAt(layoutHolders, i), ref tex, layout);

                    if (LastPos == null && ShowDot)
                    {
                        for (var j = i - 1; j >= 0; j--)
                        {
                            ref var previousTex = ref layoutTexs[j];
                            if (previousTex.TexType is not TexType.Normal || !glyphLayouts[j].HasGlyph)
                                continue;

                            LastPos = new Vector2(glyphLayouts[j].PosX, glyphLayouts[j].PosY);

                            if (TryMeasureGlyph(ref dot, out int dotWidth, out int dotHeight, out _, out _, out _, out _, out _, out int dotAdvance))
                            {
                                dot.Alpha = previousTex.Alpha;
                                dotLayout = new GlyphLayoutState
                                {
                                    HasGlyph = true,
                                    PosX = glyphLayouts[j].PosX + Math.Max(dotAdvance, (int)(glyphLayouts[j].OriginWidth * Scale.X)),
                                    PosY = glyphLayouts[j].PosY,
                                    Width = dotWidth,
                                    Height = dotHeight,
                                    OriginWidth = Math.Max(0, dot.GlyphMetrics.Width),
                                    OriginHeight = Math.Max(0, dot.GlyphMetrics.Height),
                                    LayoutWidth = Math.Max(dotAdvance, dotWidth),
                                    Alpha = previousTex.Alpha
                                };
                                dotLayoutVisible = true;
                                ApplyLayoutToHolder(dotTextureHolder, ref dot, dotLayout);

                                if (dotLayout.Alpha > 0f)
                                {
                                    if (dotLayout.PosY < minVisualY) minVisualY = dotLayout.PosY;
                                    if (dotLayout.PosY + dotLayout.Height > maxVisualY) maxVisualY = dotLayout.PosY + dotLayout.Height;
                                }
                            }

                            break;
                        }
                    }
                }

                // Visual bounds are accumulated during layout instead of by an O(N) full scan at the end,
                // so they can enter the checkpoint together with the rest of the state and continue across incremental layout.
                var visual = glyphLayouts[i];
                if (visual.HasGlyph && visual.Alpha > 0f)
                {
                    if (visual.PosY < minVisualY) minVisualY = visual.PosY;
                    if (visual.PosY + visual.Height > maxVisualY) maxVisualY = visual.PosY + visual.Height;
                }
            }

            if (lineBreak)
                layoutCheckpoint = checkpoint;

            if (!isStop)
            {
                if (maxPosX < cursor.X)
                    maxPosX = (int)cursor.X;

                if (maxPosY < cursor.Y)
                    maxPosY = (int)cursor.Y;
            }

            pre = tex;
        }

        Width = Math.Max(0, maxPosX - (int)PosX);
        OriginWidth = (int)((float)Width / Scale.X);

        if (minVisualY == int.MaxValue)
        {
            VisualOffsetTop = 0;
            Height = 0;
        }
        else
        {
            VisualOffsetTop = minVisualY - (int)PosY;
            Height = maxVisualY - minVisualY;
        }

        OriginHeight = (int)((float)Height / Scale.Y);
    }

    public bool IsCJK(int codePoint)
    {
        //int codePoint = c;
        // Chinese character range
        if (codePoint >= 0x4E00 && codePoint <= 0x9FFF) return true;
        // Japanese character ranges
        if (codePoint >= 0x3040 && codePoint <= 0x309F) return true;
        if (codePoint >= 0x30A0 && codePoint <= 0x30FF) return true;
        // Korean character range
        if (codePoint >= 0xAC00 && codePoint <= 0xD7FF) return true;
        return false;
    }

    public override async Task<bool> Load()
    {
        if (!DeviceServices.BaseApp.FontsCreated)
        {
            // Fonts are not ready yet, so do not build glyphs and do not mark them Missing.
            // Restore ContentDirty, which Update cleared when queuing.
            // Update's queuing logic is gated by FontsCreated, so the next frame after fonts become ready
            // will retry automatically without causing a retry storm.
            ContentDirty = true;
            return false;
        }

        // If Ready is still true, MarkContentDirty has not happened and this queue request came from Append,
        // so use the incremental path. Update clears ContentDirty when queuing, so it cannot distinguish this case.
        if (Ready && appendDirty)
        {
            if (await LoadAppend())
                return true;

            // Incremental append failed, for example because the backend does not support it,
            // buffer growth failed, or a full rebuild happened meanwhile. Fall back to a full rebuild.
            lock (SyncRoot)
            {
                MarkContentDirty();
            }

            return false;
        }

        // Snapshot the content being loaded this time so the end of the load can decide whether a retry is needed.
        string builtContent;
        bool empty;
        lock (SyncRoot)
        {
            ContentDirty = false;

            // Rebuild unconditionally. Build is the only producer of TexsLoading.
            // If the previous load failed in the backend, for example because LoadTexts returned false,
            // old-content glyphs may still remain in TexsLoading. Reusing them would mean
            // building glyphs from old content while recording builtContent as new content,
            // permanently desynchronizing Texs length from content length. Later appends would then
            // extend from the wrong base and the middle portion of the text could never be recovered.
            Build(Content);

            builtContent = Content;
            empty = TexsLoading == null || TexsLoading.Length == 0;
        }

        // Empty content, such as the initial placeholder of streaming output or text that has been cleared,
        // must not be treated as a load failure.
        // Backend LoadTexts returns false for empty input because there are no instances to build.
        // If Load also returned false, the control would stay in Ready = false and ContentDirty = false,
        // so BaseApp would not retry. Recovery would then rely only on the "Content changed -> full rebuild" path.
        // Instead, enter an empty Ready state directly: zero glyphs, zero size, and no backend resources.
        // Semantically this means the empty text has finished loading, and the next Append will rebuild the full text.
        if (empty)
        {
            lock (SyncRoot)
            {
                // There used to be glyphs and content has now been cleared, so backend instances and holders
                // must both be released. Otherwise old glyphs would remain in GPU state and keep rendering
                // while the control is in an empty Ready state.
                if ((Texs != null && Texs.Length > 0) || textureHolders != null || textureHoldersLoading != null)
                    Graphics.Instance.DisposeTexts(this);

                Texs = new Tex[] { };
                TexsLoading = null;
                glyphLayouts = Array.Empty<GlyphLayoutState>();
                dotLayout = default;
                dotLayoutVisible = false;
                layoutCheckpoint = default;

                Width = 0;
                Height = 0;
                OriginWidth = 0;
                OriginHeight = 0;
                VisualOffsetTop = 0;
            }

            FinishLoad(builtContent, loaded: true);

            return true;
        }

        if (!await Graphics.Instance.LoadTexts(this))
        {
            // The backend rejected this load, for example because the full string has no drawable glyphs
            // such as pure whitespace or line breaks, or because buffer creation failed.
            // ContentDirty was already cleared at the start, so returning false directly would leave the control
            // in the state "Ready = false and not dirty". BaseApp would not retry, and the control would remain blank forever.
            // Let FinishLoad decide uniformly: if content changed and grew during loading, restore ContentDirty and retry;
            // if content did not change, do not retry and avoid a per-frame retry storm for unbuildable input.
            FinishLoad(builtContent, loaded: false);

            return false;
        }

        lock (SyncRoot)
        {
            Texs = TexsLoading;
            TexsLoading = null;

            var textureHoldersPre = textureHolders;
            textureHolders = textureHoldersLoading;
            textureHoldersLoading = null;

            var dotPre = dotTextureHolder;
            dotTextureHolder = dotTextureHolderLoading;
            dotTextureHolderLoading = null;

            if (textureHoldersPre != null)
            {
                Graphics.Instance.DisposeTextureHolders(textureHoldersPre);

                textureHoldersPre = null;
            }

            if (dotPre != null)
            {
                Graphics.Instance.DisposeTextureHolders(new[] { dotPre });

                dotPre = null;
            }

            Position();
            ApplyCurrentLayoutToActiveHolders();
            LogHolderLayoutMismatchIfAny();
        }

        lock (SyncRoot)
        {
            Graphics.Instance.UpdateTexts(this);
        }

        FinishLoad(builtContent, loaded: true);

        return true;
    }

    /// <summary>Finalization for a full load.
    /// Decides how to compensate for content changes that happened during loading,
    /// and hands over into the stable incremental state.
    /// <para>For pure append changes, which are the common streaming case, it converts the change into an incremental-append task
    /// and clears ContentDirty in place.
    /// Content changes here cannot be reduced to ContentDirty alone.
    /// If chunks arrive more frequently than one full-load cycle, the full rebuild keeps extending itself,
    /// because Content is already longer by the end of each round. <see cref="Ready"/> would never return to
    /// "ready and clean", so every <see cref="Append"/> would fall back, each fallback would call
    /// <see cref="MarkContentDirty"/> and set Ready = false, and no characters would appear during the whole stream.
    /// Everything would only pop in after the last rebuild once chunks stop.</para>
    /// Non-append changes, such as assigning a whole new string, restore ContentDirty so the next Update() retries.
    /// <para>When <paramref name="loaded"/> is false, the backend built no instances this round and the caller will return false.
    /// In that state, Ready cannot be set and there is no valid base state for incremental extension,
    /// so the only remaining decision is whether a retry is needed.</para></summary>
    void FinishLoad(string builtContent, bool loaded)
    {
        lock (SyncRoot)
        {
            var contentChanged = content != builtContent;
            var tailQueued = contentChanged && loaded && TryQueueTailAppend(builtContent);

            if (tailQueued)
            {
                // The queued appended tail already covers all content changes that happened during loading,
                // because it was parsed from current under the same lock.
                // ContentDirty set by append fallbacks during loading must therefore be cleared in place.
                // Otherwise Update would always choose the full-rebuild branch first, and each rebuild would again
                // discover that content had grown by the end, so convergence would never happen.
                ContentDirty = false;
            }
            else if (contentChanged)
            {
                ContentDirty = true;
            }
            else if (!ContentDirty)
            {
                // Content did not change, or was only assigned the same value repeatedly, for example every frame.
                // Clear change markers correctly to avoid infinite retries and GPU resource churn.
                Changed = false;
            }

            // BaseApp would normally set Ready only after Load returns.
            // Chunks arriving in that window would see !Ready, fall back to full rebuild,
            // and discard the just-queued appended tail through MarkContentDirty.
            // Under high-frequency chunks this degrades into one full rebuild per chunk,
            // where each round is longer than the previous and the state never stabilizes.
            // Set Ready early under the same lock so the handoff from self-consistent state
            // to incremental append allowed becomes atomically visible to Append.
            // Skip already disposed controls in the same way as BaseApp, because Dispose cleans up their GPU resources.
            if (loaded && !IsDisposed)
                Ready = true;
        }
    }

    /// <summary>Queues the newly appended tail after <paramref name="loaded"/> into the incremental-append queue.
    /// The caller must hold SyncRoot.
    /// Returning false means the current change is not a pure append that can be parsed incrementally with safety,
    /// so the caller must fall back to a full rebuild.
    /// Its gating mirrors <see cref="CanAppendIncrementally"/>: one extra full rebuild is preferable
    /// to leaving holes where appended glyphs have no holders or become misaligned.</summary>
    bool TryQueueTailAppend(string loaded)
    {
        var current = content;
        var loadedText = loaded.NullToString();

        // Span-markup color scopes may continue across chunks, and indices are absolute within the whole string,
        // so the tail alone cannot be parsed.
        if (contentHasSpanMarkup)
            return false;

        // This is not a pure append, for example whole-string reassignment or content shrinking,
        // so only a full rebuild is safe.
        if (current == null || current.Length <= loadedText.Length
            || !current.StartsWith(loadedText, StringComparison.Ordinal))
            return false;

        // The already loaded portion must be self-consistent and must have holders.
        // Otherwise there is no stable base state to extend incrementally.
        if (Texs == null || Texs.Length == 0 || textureHolders == null || textureHolders.Length < Texs.Length)
            return false;

        // A surrogate pair was split across the loaded/new boundary, or the tail ends with a high surrogate,
        // so the tail cannot be decoded independently.
        if (loadedText.Length > 0 && char.IsLowSurrogate(current[loadedText.Length]))
            return false;

        if (char.IsHighSurrogate(current[^1]))
            return false;

        var appended = BuildRange(current, loadedText.Length);

        if (appended.Count == 0)
        {
            // The tail produced no glyphs, for example pure markup.
            // Content is already merged, so no rebuild is needed.
            return true;
        }

        pendingAppendTexs ??= new List<Tex>();
        pendingAppendTexs.AddRange(appended);
        appendDirty = true;

        return true;
    }

    /// <summary>Incremental load path.
    /// Builds atlas entries and holders only for pending appended glyphs, grows GPU buffers without rebuilding them,
    /// and resumes layout only from the start of the last line.
    /// Returns false when incremental append is not feasible, in which case the caller must fall back to a full rebuild.</summary>
    async Task<bool> LoadAppend()
    {
        Tex[] appendTexs;
        int baseCount;

        lock (SyncRoot)
        {
            appendDirty = false;

            if (pendingAppendTexs == null || pendingAppendTexs.Count == 0)
                return true;

            if (Texs == null || Texs.Length == 0 || textureHolders == null || textureHolders.Length < Texs.Length)
                return false;

            appendTexs = pendingAppendTexs.ToArray();
            pendingAppendTexs = null;
            baseCount = Texs.Length;

            // From this point onward, pause UpdateTexts inside Update().
            // AppendTexts runs on the loader thread without holding SyncRoot and writes back expanded backend state.
            // If that interleaves with a blind write-back from a stale UpdateTexts snapshot, InstanceCount can roll back,
            // Texs length then exceeds buffer capacity, and the next UpdateTexts hits an array out-of-range.
            // The flag is set under SyncRoot, making it atomic with SyncRoot-protected UpdateTexts in Update,
            // and remains in effect until Texs swapping is complete.
            appendLoadInProgress = true;
        }

        try
        {
            var appendHolders = new ITextureHolder[appendTexs.Length];

            if (!await Graphics.Instance.AppendTexts(this, appendTexs, appendHolders))
                return false;

            lock (SyncRoot)
            {
                // A full rebuild happened during the append, for example because Content was reassigned as a whole.
                // Abandon this incremental attempt and reclaim the holders just created.
                // Backend state already expanded InstanceCount. The extra slots will be hidden by UpdateTexts,
                // and the next LoadTexts will replace the entire state, so there is no need to roll capacity back here.
                if (!Ready || ContentDirty || Texs == null || Texs.Length != baseCount)
                {
                    Graphics.Instance.DisposeTextureHolders(appendHolders);
                    return false;
                }

                // Texs and textureHolders must grow exactly, because the backend iterates by Texs.Length
                // and cannot tolerate over-allocation.
                var texs = Texs;
                Array.Resize(ref texs, baseCount + appendTexs.Length);
                Array.Copy(appendTexs, 0, texs, baseCount, appendTexs.Length);
                Texs = texs;

                // Holder-array layout must match LoadTexts:
                // [0, Texs.Length) are glyph slots, and ShowDot adds one trailing placeholder slot.
                var hasDotSlot = textureHolders.Length > baseCount;
                var holders = new ITextureHolder[texs.Length + (hasDotSlot ? 1 : 0)];
                Array.Copy(textureHolders, 0, holders, 0, baseCount);
                Array.Copy(appendHolders, 0, holders, baseCount, appendHolders.Length);
                textureHolders = holders;

                PositionAppended();
                ApplyCurrentLayoutToActiveHolders();
                LogHolderLayoutMismatchIfAny();
            }

            lock (SyncRoot)
            {
                Graphics.Instance.UpdateTexts(this);
            }

            return true;
        }
        finally
        {
            lock (SyncRoot)
            {
                appendLoadInProgress = false;
            }
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY);

        if (deferredContentLoadWhileHidden && Alpha > 0f)
        {
            deferredContentLoadWhileHidden = false;
            MarkContentDirty();
        }

        if (DeviceServices.BaseApp.FontsCreated)
        {
            if (ContentDirty)
            {
                if (Alpha <= 0f)
                {
                    DeferContentLoadWhileHidden();
                }
                else
                {
                    if (DeviceServices.BaseApp?.RequestLoad(this) == true)
                        ContentDirty = false;
                }
            }
            else if (appendDirty)
            {
                if (Alpha <= 0f)
                {
                    // Do not perform GPU work while hidden.
                    // Pending appended content has already been merged into Content, so a full rebuild when visible again
                    // is enough to cover it.
                    lock (SyncRoot)
                    {
                        appendDirty = false;
                        pendingAppendTexs = null;
                    }

                    DeferContentLoadWhileHidden();
                }
                else
                {
                    // appendDirty is cleared by LoadAppend under the lock, not when queuing succeeds.
                    // New Append calls may arrive between queueing and execution.
                    // RequestLoad already deduplicates requests, so repeated requests are harmless.
                    DeviceServices.BaseApp?.RequestLoad(this);
                }
            }

            bool canResolveLayout = HasLayoutSourceForUpdate();

            if (!canResolveLayout)
            {
                // Text has not finished loading yet, so Ready is false and layout changes cannot be consumed.
                // Changed must be cleared here. Otherwise it would stay true forever, keeping Panel.Changed true
                // and causing pointless full-scene redraws every frame.
                Changed = false;
            }

            if (Changed && canResolveLayout)
            {
                lock (SyncRoot)
                {
                    Position();
                    Changed = false;
                }
            }

            if (Enable && Alpha > 0 && Ready)
            {
                var visualTop = PosY + VisualOffsetTop;
                MouseOver = PosX < TouchService.PoX && TouchService.PoX < PosX + Width && visualTop < TouchService.PoY && TouchService.PoY < visualTop + Height;
            }
            else
            {
                MouseOver = false;
            }

            if (Ready)
            {
                lock (SyncRoot)
                {
                    // Pause UpdateTexts while append is in progress, to prevent a stale state snapshot
                    // from blindly rolling back the InstanceCount that AppendTexts already expanded.
                    // Since Texs has already grown, that rollback would cause the next UpdateTexts to hit an array out-of-range.
                    // Layout changes accumulated during the pause are rebuilt by the UpdateTexts call at the end of LoadAppend.
                    if (!appendLoadInProgress)
                        Graphics.Instance.UpdateTexts(this);
                }
            }
        }

        return result;
    }

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            if (Ready)
            {
                lock (SyncRoot)
                {
                    Graphics.Instance.DrawTexts(this);
                }

                result = true;
            }
        }

        return result;
    }

    public override void Dispose()
    {
        base.Dispose();

        if (Ready)
        {
            lock (SyncRoot)
            {
                Graphics.Instance.DisposeTexts(this);

                Texs = new Tex[] { };
            }

            Ready = false;
        }
    }
}

public enum TextAlignment
{
    Left,
    Center,
    Right
}

public enum TextsType
{
    Immediately,
    FadeIn
}

public struct Line
{
    public Tex[] Texs { get; set; }
}

public enum TexType
{
    Normal,
    NewLine,
    Space,
    Missing
}

public struct Tex
{
    public TexType TexType;

    public long ID;

    public int Value;

    public Season.Basic.Color? Color;

    public float Time;

    public float Alpha;

    public float Factor;

    public float Factor2;

    public Season.Fonts.GlyphMetrics GlyphMetrics;

    public int AtlasVersion;

    public Tex(TexType texType)
    {
        TexType = texType;

        if (TexType is TexType.NewLine or TexType.Space or TexType.Missing)
        {

        }
        else
        {
            ID = Texture.NextID();
        }
    }
}
