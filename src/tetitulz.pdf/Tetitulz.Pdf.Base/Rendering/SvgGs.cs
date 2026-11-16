// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using HashSetInt = System.Collections.Generic.HashSet<int>;
using SKPath = SkiaSharp.SKPath;
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>The SVG writer's graphics state — the fields <c>q</c>/<c>Q</c> save and restore.</summary>
    internal sealed class SvgGs
    {
        public float[] CTM  = { 1, 0, 0, 1, 0, 0 };
        public float[] Tm   = { 1, 0, 0, 1, 0, 0 };
        public float[] Tlm  = { 1, 0, 0, 1, 0, 0 };

        public string FillColor   = "rgb(0,0,0)";
        public string StrokeColor = "rgb(0,0,0)";
        public float  FillAlpha   = 1f;
        public float  StrokeAlpha = 1f;
        public float  LineWidth   = 1f;
        public int    LineCap     = (int)PdfLineCap.Butt;
        public int    LineJoin    = (int)PdfLineJoin.Miter;
        public float  MiterLimit  = SvgSyntax.DefaultMiterLimit;
        // The dash array as PDF gave it — user-space lengths, unscaled and unformatted. It used
        // to be stored pre-rendered as a "6,3" string, which is why nothing could apply the CTM
        // scale or normalise the phase against the array's effective period: by the time the
        // stroke was emitted, the numbers were gone. Never mutated in place (OpDash/OpGs assign
        // a fresh array), so SvgGs.Clone's shallow copy of the reference is safe.
        public float[] DashPattern = null;
        public float   DashPhase   = 0f;

        public string FontName   = null;
        public float  FontSize   = 12f;
        public float  CharSpacing = 0f;
        public float  WordSpacing = 0f;
        public float  HorizScale  = 1f;
        public float  Leading     = 0f;
        public float  TextRise    = 0f;
        // Held as int (not the enum) because content streams may set Tr to an
        // out-of-range value; the paint/clip decode happens in DrawTextBytes.
        public int    TextRenderMode = (int)Tetitulz.Pdf.Core.Constants.TextRenderMode.Fill;

        // Color space names (used by sc/SC to interpret operands)
        public string ColorSpaceFill   = PdfColorSpaceNames.DeviceRGB;
        public string ColorSpaceStroke = PdfColorSpaceNames.DeviceRGB;

        // Full colour space arrays for complex spaces (Lab, ICCBased, Indexed,
        // Separation, DeviceN).  null when the space is a simple device name.
        public PdfArray ColorSpaceArrayFill   = null;
        public PdfArray ColorSpaceArrayStroke = null;

        // Active SVG blend mode (from ExtGState /BM)
        public string BlendMode = null;

        // Active SVG soft mask id (from ExtGState /SMask)
        public string SvgMaskId = null;

        // Active pattern names from scn/SCN (empty = no pattern)
        public string PatternNameFill   = null;
        public string PatternNameStroke = null;

        // Whether the current colour space is /Pattern. Distinct from the name above, which is
        // null both before an `scn` names a pattern and when the space is not Pattern at all —
        // and §8.6.8 Table 74 makes the first of those "paint nothing", not "paint the colour
        // that happened to be current". Mirrors GraphicsState.FillIsPattern / StrokeIsPattern.
        public bool FillIsPattern   = false;
        public bool StrokeIsPattern = false;

        /// <summary>
        /// The matrix a pattern's <c>/Matrix</c> composes with — pattern space maps to the default
        /// coordinate space of the content stream the pattern is used in (§8.7.3.1): the page's
        /// (the identity, the root group carrying the page), a form's CTM at its <c>Do</c> with its
        /// <c>/Matrix</c>, a Type 3 glyph's, a pattern cell's or a mask group's. Null is the identity;
        /// never mutated in place, so the shallow clone is safe. Mirrors
        /// <c>GraphicsState.PatternBase</c>.
        /// </summary>
        public float[] PatternBase = null;

        // Number of <g clip-path="..."> groups opened in this q/Q scope
        public int ClipGroupCount = 0;

        // Number of <g data-mc="..."> groups opened in this q/Q scope
        public int MarkedContentGroupCount = 0;

        // ExtGState parity — stored so soft masks and transfer-aware paths can read
        // the same state the raster renderer does.  Most fields have no SVG analogue
        // but are kept for completeness.
        public bool      OverprintFill    = false;
        public bool      OverprintStroke  = false;
        public int       OverprintMode    = 0;
        // Whether the current colour was specified in DeviceCMYK — the fact conversion to an
        // sRGB string throws away, and the one overprint simulation depends on.
        public bool      FillIsCmyk       = false;
        public bool      StrokeIsCmyk     = false;
        // The colorants an overprinting paint writes, as OverprintCompositor channel bits, or 0 for
        // "the DeviceCMYK rule" — the same field the raster GraphicsState carries, for the same
        // reason: §11.7.4.5's colorant-set rule ignores /OPM and the DeviceCMYK rule does not.
        public int       FillOverprintChannels   = 0;
        public int       StrokeOverprintChannels = 0;
        // A /Separation or /DeviceN naming a spot ink — CMYK-less, and reported by name.
        public bool      FillIsSpotColorant      = false;
        public bool      StrokeIsSpotColorant    = false;
        public bool      AlphaIsShape     = false;
        public bool      TextKnockout     = true;
        public string    RenderingIntent  = "RelativeColorimetric";
        public bool      BlackPointCompensation = true;
        public float     Flatness         = 1f;
        public bool      StrokeAdjustment = false;
        // Transfer function (/TR or /TR2) — stored as the raw dict/array/name so
        // image emission can feed it into `PdfFunctionEvaluator`.  Null = identity.
        public PdfObject TransferFunction = null;
        // The same function as a per-channel LUT, for fill and stroke colours. It exists
        // separately from TransferFunction because the image path wants the function object and
        // the paint path wants the table, and it exists AT ALL because /TR applies to paint as well
        // as images: a page setting `1 − t` and filling a red rectangle renders cyan, and must
        // export cyan. Built by TransferFunctionLut, which the raster path also calls, so the two
        // cannot disagree about what the function evaluates to. Null = identity (no remap).
        // Never mutated in place — OpGs assigns a fresh table — so Clone's shallow copy is safe.
        public byte[][]  TransferLut = null;

        public SvgGs Clone()
        {
            var c = (SvgGs)MemberwiseClone();
            c.CTM = (float[])CTM.Clone();
            c.Tm  = (float[])Tm.Clone();
            c.Tlm = (float[])Tlm.Clone();
            return c;
        }
    }
}
