// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Holds the full PDF graphics state for one level of the q/Q stack.
    /// </summary>
    internal sealed class GraphicsState
    {
        // ---- Current Transformation Matrix [a b c d e f] ----
        // x' = a*x + c*y + e
        // y' = b*x + d*y + f
        public float[] CTM = { 1, 0, 0, 1, 0, 0 };

        // ---- Color ----
        public SKColor FillColor   = SKColors.Black;
        public SKColor StrokeColor = SKColors.Black;

        // ---- Line Style ----
        // LineCap / LineJoin / TextRenderMode below are the raw PDF operand values.  They stay
        // `int` because the operands arrive as numbers and a malformed file may set anything;
        // read them through the Tetitulz.Pdf.Core.Constants.PdfLineCap / PdfLineJoin /
        // TextRenderMode enums (cast at the use site) rather than comparing bare literals.
        public float LineWidth  = 1f;
        public int   LineCap    = (int)Core.Constants.PdfLineCap.Butt;
        public int   LineJoin   = (int)Core.Constants.PdfLineJoin.Miter;
        public float MiterLimit = 10f;   // PDF §8.4.3.5 initial value
        public float[] DashPattern = null;
        public float   DashPhase   = 0f;

        // ---- Text State ----
        public string FontResourceName = null;
        public float  FontSize         = 12f;
        public float  CharSpacing      = 0f;
        public float  WordSpacing      = 0f;
        public float  HorizScaling     = 1f;   // stored as fraction (100 → 1.0)
        public float  Leading          = 0f;
        public int    TextRenderMode   = (int)Core.Constants.TextRenderMode.Fill;
        public float  TextRise         = 0f;

        // ---- Transparency / Blend ----
        public float  FillAlpha   = 1f;    // non-stroking alpha (ca in ExtGState)
        public float  StrokeAlpha = 1f;    // stroking alpha (CA in ExtGState)
        // PDF blend mode name (BM in ExtGState).
        public string BlendMode   = Core.Constants.PdfBlendModes.Normal;

        // ---- Active color spaces (for sc/SC dispatch) ----
        public string FillColorSpace   = Core.Constants.PdfColorSpaceNames.DeviceGray;
        public string StrokeColorSpace = Core.Constants.PdfColorSpaceNames.DeviceGray;

        // ---- Separation/DeviceN color space arrays ----
        // Stores the full color space array (e.g. [/Separation /MyInk /DeviceRGB fn]) so
        // that OpSc can evaluate the tint transform function.
        public PdfArray FillColorSpaceArray   = null;
        public PdfArray StrokeColorSpaceArray = null;

        // ---- Pattern state ----
        public bool   FillIsPattern    = false;
        public bool   StrokeIsPattern  = false;

        /// <summary>
        /// The matrix a pattern's <c>/Matrix</c> composes with: pattern space maps to the default
        /// coordinate space of the content stream the pattern is used in (§8.7.3.1) — the page's
        /// initial CTM, a form's CTM at its <c>Do</c> with its <c>/Matrix</c>, an annotation
        /// appearance's, a Type 3 glyph's, a pattern cell's or a soft mask group's own. Null is the
        /// identity. Never mutated in place: a frame assigns a new array, so a clone's shared
        /// reference is safe.
        /// </summary>
        public float[] PatternBase = null;

        /// <summary>The pattern base, the identity when none was set.</summary>
        public float[] PatternBaseOrIdentity => PatternBase ?? new float[] { 1, 0, 0, 1, 0, 0 };
        /// <summary>
        /// The pattern names selected by <c>scn</c> and <c>SCN</c> — <b>one per paint side</b>,
        /// as §8.4.1 Table 52 has them and as <c>SvgGs.PatternNameFill</c>/<c>PatternNameStroke</c>
        /// already did.
        /// </summary>
        /// <remarks>
        /// <para>This was a <b>single</b> <c>CurrentPatternName</c> that <c>OpSc</c> overwrote
        /// whatever the <c>stroke</c> flag said, so
        /// <c>/Pattern cs /P1 scn /Pattern CS /P2 SCN 0 0 100 100 re B</c> painted the <i>fill</i>
        /// with <c>P2</c> — the stroke's pattern — while the SVG writer painted P1 and P2 as the
        /// file asked. The colour operators have kept fill and stroke apart since the beginning;
        /// only the pattern name was shared, which is why nothing else in the state looked wrong.
        /// </para>
        /// </remarks>
        public string FillPatternName   = null;
        public string StrokePatternName = null;

        // ---- Rendering intent (RI in ExtGState, ri operator) ----
        // Stored for completeness; SkiaSharp does not expose ICC rendering intent.
        public string RenderingIntent = "RelativeColorimetric"; // PDF default
        // /UseBlackPtComp (ISO 32000-2 §8.6.5.9): off only after /OFF; /Default is on here.
        public bool BlackPointCompensation = true;

        /// <summary>The intent ICC colour is converted at — <see cref="RenderingIntent"/> with this state's compensation.</summary>
        internal IccRenderingIntent Intent => IccRenderingIntents.Of(RenderingIntent, BlackPointCompensation);

        // ---- AIS: Alpha is Shape (ExtGState /AIS) ----
        // When true, the soft mask's alpha channel represents shape (not opacity).
        // Affects how soft masks are composited in transparency groups.
        public bool AlphaIsShape = false;

        // ---- TK: Text Knockout (ExtGState /TK) ----
        // When true, glyphs in a transparency group do not composite with each other.
        public bool TextKnockout = true; // PDF default is true

        // ---- TR/TR2: Transfer functions (ExtGState /TR, /TR2) ----
        // Null means identity (no remapping).  May be a PdfDictionary (single function
        // applied to all components), a PdfArray of per-component functions, or a
        // PdfName "Identity"/"Default".
        public PdfObject TransferFunction = null;

        // Precomputed transfer-function LUTs: [0]=R, [1]=G, [2]=B, each a byte[256] (or null
        // for an identity channel); the whole array is null when no transfer function is set.
        // Built when /TR is set and applied to fill/stroke colours at paint time.  Treated as
        // immutable once built (replaced wholesale, never mutated in place), so the shallow
        // MemberwiseClone reference-copy is safe across q/Q.
        public byte[][] TransferLut = null;

        // ---- Overprint simulation ----
        public bool OverprintFill   = false;
        public bool OverprintStroke = false;
        public int  OverprintMode   = 0;   // OPM

        // The fill/stroke colour's DeviceCMYK components, or null when the colour was NOT set in
        // DeviceCMYK. Overprint has to know *which colorants the source specifies*, and that is
        // exactly what converting to sRGB throws away: `1 0 0 rg` and `0 1 1 0 k` land on the same
        // SKColor, but only the latter may leave the backdrop's cyan and black alone. §11.7.4.2
        // restricts nonzero overprint mode (OPM 1) to DeviceCMYK, so a null here means
        // "not eligible" rather than "unknown". Cleared by every non-CMYK colour operator.
        // Arrays are replaced wholesale, never mutated, so the shallow q/Q clone stays safe.
        public float[] FillCmyk   = null;
        public float[] StrokeCmyk = null;

        // The fill/stroke colour's /Separation or /DeviceN colorant tints, keyed by colorant name,
        // or null when the colour was not set in one of those spaces. The spot counterpart of
        // FillCmyk above, and it lives here for the same reason: the tint transform runs at colour-
        // set time, so by the time anything paints, "which named inks did the file ask for, and how
        // much of each" has already been converted away into one sRGB value.
        //
        // Fields on the state, not dictionaries on the interpreter: that is the difference between a
        // value q/Q restores and one it does not. A spot tint set inside a q…Q surviving the Q, and no
        // device colour operator clearing it, every later fill on the page would deposit ink for a
        // colorant it did not
        // use. The map came back naming inks the artwork never touched.
        //
        // Replaced wholesale and never mutated in place, so the shallow MemberwiseClone below is
        // safe — the same contract FillCmyk and TransferLut carry.
        public System.Collections.Generic.IReadOnlyDictionary<string, float> FillSepTints   = null;
        public System.Collections.Generic.IReadOnlyDictionary<string, float> StrokeSepTints = null;

        // Which DeviceCMYK colorants an overprinting paint WRITES, as a bit set over
        // OverprintCompositor's channel bits — or 0, meaning "not a colorant set: apply the
        // DeviceCMYK rule", under which OPM 1 preserves every zero component.
        //
        // Whether the current colour was set in DeviceGray itself (g, G, or sc in a bare
        // /DeviceGray space) — not an /ICCBased grey, which FillColorSpace cannot tell apart since it
        // holds the device family. A separation through an output profile keeps DeviceGray on the
        // black plate alone (DeviceGray to K) and converts everything else. Cleared by
        // every colour operator (ClearSepTints) and set again by the ones above; true initially,
        // because the initial colour is DeviceGray black.
        public bool FillDeviceGray   = true;
        public bool StrokeDeviceGray = true;

        // Whether the current colour was set in DeviceRGB itself (rg, RG, or sc in a bare /DeviceRGB
        // space) — the one colour §10.3.4's black generation and undercolour removal convert to the
        // process inks. Cleared and set with FillDeviceGray, by the same operators.
        public bool FillDeviceRgb   = false;
        public bool StrokeDeviceRgb = false;

        // /BG, /BG2, /UCR, /UCR2 (§10.3.4), or null for the device's own conversion. Replaced
        // wholesale, never mutated, so the shallow MemberwiseClone stays safe.
        internal BlackGeneration BlackGeneration = null;

        // The current clip as a PATH, in Y-down page-point space, maintained only while a host has
        // attached an InkAccumulator. Skia hands back a clip's bounds and never its shape, so ink
        // coverage would otherwise have to report a circular clip as its square.
        //
        // It lives on the graphics state because the clip's lifetime IS the state stack's — q saves
        // it and Q restores it, with no second save stack to keep in step with the canvas's own.
        // NOT owned by the state: after a q the same SKPath is referenced by both frames, exactly
        // like ActiveSoftMask below, so PdfRenderContext owns the lifetime and frees them together.
        public SkiaSharp.SKPath InkClipPath = null;

        // ---- Soft mask ----
        // A graphics-state parameter like any other (§8.4.2 Table 52, "soft mask"), so q saves it
        // and Q restores it — see Clone below.
        //
        // NOT owned by the GraphicsState: after a q the same SKBitmap is referenced by both the
        // saved frame and the live one, so no single frame may dispose it. PdfRenderContext owns
        // the lifetime (SetSoftMask / ClearSoftMask / PopState / Dispose), releasing a bitmap only
        // once no frame anywhere on the stack still points at it.
        public SKBitmap ActiveSoftMask  = null;
        public bool     SoftMaskIsAlpha = true;

        /// <summary>
        /// Device-pixel position of <see cref="ActiveSoftMask"/>'s top-left corner.
        /// </summary>
        /// <remarks>
        /// The mask is rendered only over the group's <c>/BBox</c>, which is usually a small part of
        /// the page — so its position has to travel with it. Copied by the
        /// <c>MemberwiseClone</c> in <see cref="Clone"/> like the bitmap reference it belongs to.
        /// <para><b>Every consumer must honour this.</b> Missing it in one of the three
        /// (<c>DrawWithSoftMask</c>'s composite, its device-bounds intersection, and the mask
        /// shader's local matrix) misaligns the mask rather than failing, which is invisible
        /// without a fixture whose mask covers only part of the page —
        /// <c>SoftMaskAlignmentTests</c> is that fixture.</para>
        /// </remarks>
        public int SoftMaskOffsetX = 0;
        public int SoftMaskOffsetY = 0;

        // ---- Text Matrices (not saved/restored with q/Q per spec, but tracked here) ----
        // These are owned by the text state, not by the graphics state stack,
        // but we keep them here so the interpreter can access them uniformly.
        public float[] Tm  = { 1, 0, 0, 1, 0, 0 };  // text matrix
        public float[] Tlm = { 1, 0, 0, 1, 0, 0 };  // text line matrix

        public GraphicsState Clone()
        {
            var c = (GraphicsState)MemberwiseClone();
            c.CTM  = (float[])CTM.Clone();
            // Tm and Tlm are intentionally NOT deep-copied here.
            // Per PDF spec §9.3.1 the text matrices are NOT part of the saved/restored
            // graphics state.  PdfRenderContext.PopState() preserves the live values.
            if (DashPattern != null) c.DashPattern = (float[])DashPattern.Clone();
            // FillColorSpaceArray and StrokeColorSpaceArray are PdfArray references that
            // are treated as immutable once set (we never mutate a PdfArray in place).
            // A shallow reference copy via MemberwiseClone is therefore safe; both the
            // parent and cloned state share the same PdfArray instance, which is fine
            // because the interpreter only replaces the reference (assigns a new PdfArray)
            // and never modifies the array contents.
            // ActiveSoftMask IS inherited by the clone (the MemberwiseClone reference copy above).
            //
            // Not nulled: §11.6.5.2 describes how a soft mask is *derived from a transparency group*,
            // and says nothing about q/Q. §8.4.2 Table 52 lists "soft mask" among the graphics-state
            // parameters, so `q` saves it and `Q` restores it exactly like the CTM or the fill
            // colour. Nulling it would mean `Q` restored
            // "no mask" and PopState then disposed the live one, so
            //   /GS0 gs  q … Q  0 0 200 200 re f
            // lost the mask for everything after the Q. The SVG renderer's MemberwiseClone had
            // always kept it, which is why the two renderers disagreed here.
            //
            // The bitmap is now shared by parent and clone, so neither may dispose it —
            // PdfRenderContext.ReleaseSoftMaskIfUnreferenced is the single release point.
            return c;
        }

        // ---- Matrix helpers ----

        /// <summary>
        /// Composes A * B in PDF row-vector convention (A applied first), returning a new
        /// matrix — forwards to <see cref="PdfMatrix.Compose"/>, the one implementation.
        ///
        /// <para>The public signature stays because callers outside Rendering use it.</para>
        /// </summary>
        public static float[] Compose(float[] A, float[] B) => PdfMatrix.Compose(A, B);

        /// <summary>Transforms a point through a PDF matrix — <see cref="PdfMatrix.Transform"/>.</summary>
        public static (float x, float y) Transform(float[] m, float x, float y)
            => PdfMatrix.Transform(m, x, y);

        /// <summary>Converts a PDF matrix [a b c d e f] to an SKMatrix.</summary>
        public static SKMatrix ToSKMatrix(float[] m)
            => new SKMatrix
            {
                ScaleX = m[0], SkewY  = m[1],
                SkewX  = m[2], ScaleY = m[3],
                TransX = m[4], TransY = m[5],
                Persp2 = 1f
            };
    }
}
