// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The set of every PDF content-stream operator (ISO 32000-1 Table A.1), and the length of the
    /// longest one — the two facts a greedy splitter needs to un-glue operator tokens that a
    /// producer emitted without separating whitespace (<c>"DoQQ"</c> → <c>Do</c>, <c>Q</c>,
    /// <c>Q</c>).
    ///
    /// <para><b>Why this type exists.</b> Two implementations of that splitter existed, with two
    /// hand-maintained copies of the set: <see cref="ContentStreamParser"/> here in Core, and
    /// <c>Tetitulz.Pdf.Accessibility.Internal.ContentStreamScanner</c>. They had already drifted —
    /// the Core copy was missing <c>ID</c>, which the Accessibility copy had — and **both** were
    /// missing <c>PS</c>, which both renderers dispatch a handler for. Neither copy was ever
    /// compared against <see cref="PdfOperators"/>, so neither gap was visible from inside either
    /// file.</para>
    ///
    /// <para><b>The splitting <i>policy</i> deliberately stays at the call sites.</b> This type
    /// answers "is this token an operator, and how long can one be?" and nothing else. Core's
    /// parser allows a partial split and emits the unrecognised remainder verbatim; the
    /// accessibility scanner is all-or-nothing, because there a partial split would let an
    /// extension operator such as <c>"BTx"</c> masquerade as a <c>BT</c> and the tagger would open
    /// a text object that does not exist. Those are different answers to a different question and
    /// merging them would be wrong — the same shape as
    /// <c>Core.Document.PdfPageRotation</c>'s <c>Reduce</c> / <c>IsMalformed</c> split.</para>
    ///
    /// <para><b>A leaf by construction.</b> Its only state is the set and the length derived from
    /// it, and <see cref="PdfOperators"/> holds nothing but <c>const string</c>s, which are
    /// compile-time and give that type no initializer at all. So nothing here can create the
    /// type-initialization edge between peers that once crashed the Base test host mid-run.</para>
    ///
    /// <para><b>The list is written out rather than reflected over
    /// <see cref="PdfOperators"/>.</b> Reflection would make drift impossible, but a trimmer is
    /// free to remove <c>const</c> fields — they are inlined at every use site — and a trimmed
    /// build would then get an <i>empty</i> operator set and silently stop un-gluing anything. The
    /// drift protection is a test instead: <c>ContentStreamOperatorsTests</c> reflects over
    /// <see cref="PdfOperators"/> and fails if this set does not name every constant. That is the
    /// guard that would have caught <c>ID</c> and <c>PS</c>.</para>
    /// </summary>
    internal static class ContentStreamOperators
    {
        /// <summary>
        /// Every operator token this library recognises, keyed ordinally — the whole of
        /// <see cref="PdfOperators"/>, which is Table A.1 plus two deprecated spellings.
        ///
        /// <para><b>The two deprecated ones are in deliberately.</b> <c>F</c> is the obsolete
        /// spelling of <c>f</c>. <c>PS</c> is §8.10.7's inline PostScript escape, and the comment
        /// this set replaced argued it should be excluded because "it is not in Table A.1 (it was
        /// dropped after PDF 1.1)". Table A.1 membership is not the criterion here: <b>both
        /// renderers dispatch a live handler for <c>PS</c></b>
        /// (<c>ContentStreamInterpreter.Dispatch</c> and <c>SvgContentWriter.Dispatch</c>), so a
        /// splitter that cannot see it hides a glued <c>PS</c> from a handler that exists and
        /// wants it. The question this set answers is "is this a real operator token that
        /// something downstream handles?", not "should a producer still write it?".</para>
        /// </summary>
        internal static readonly HashSet<string> All = new HashSet<string>(System.StringComparer.Ordinal)
        {
            // Graphics state
            PdfOperators.Save, PdfOperators.Restore, PdfOperators.ConcatMatrix,
            PdfOperators.SetLineWidth, PdfOperators.SetLineCap, PdfOperators.SetLineJoin,
            PdfOperators.SetMiterLimit, PdfOperators.SetDash, PdfOperators.SetRenderingIntent,
            PdfOperators.SetFlatness, PdfOperators.SetExtGState,

            // Path construction
            PdfOperators.MoveTo, PdfOperators.LineTo, PdfOperators.CurveTo,
            PdfOperators.CurveToV, PdfOperators.CurveToY, PdfOperators.ClosePath,
            PdfOperators.Rectangle,

            // Path painting
            PdfOperators.Stroke, PdfOperators.CloseStroke,
            PdfOperators.Fill, PdfOperators.FillObsolete, PdfOperators.FillEvenOdd,
            PdfOperators.FillStroke, PdfOperators.FillStrokeEvenOdd,
            PdfOperators.CloseFillStroke, PdfOperators.CloseFillStrokeEvenOdd,
            PdfOperators.EndPath,

            // Clipping
            PdfOperators.Clip, PdfOperators.ClipEvenOdd,

            // Colour
            PdfOperators.SetGrayFill, PdfOperators.SetGrayStroke,
            PdfOperators.SetRgbFill, PdfOperators.SetRgbStroke,
            PdfOperators.SetCmykFill, PdfOperators.SetCmykStroke,
            PdfOperators.SetColorSpaceFill, PdfOperators.SetColorSpaceStroke,
            PdfOperators.SetColorFill, PdfOperators.SetColorFillN,
            PdfOperators.SetColorStroke, PdfOperators.SetColorStrokeN,

            // Text objects, positioning, showing and state
            PdfOperators.BeginText, PdfOperators.EndText,
            PdfOperators.SetFont, PdfOperators.TextMove, PdfOperators.TextMoveSetLeading,
            PdfOperators.SetTextMatrix, PdfOperators.NextLine,
            PdfOperators.ShowText, PdfOperators.ShowTextArray,
            PdfOperators.NextLineShowText, PdfOperators.NextLineSetSpacingShowText,
            PdfOperators.SetCharSpacing, PdfOperators.SetWordSpacing,
            PdfOperators.SetHorizontalScaling, PdfOperators.SetLeading,
            PdfOperators.SetTextRenderMode, PdfOperators.SetTextRise,

            // XObjects, inline images, shading
            PdfOperators.InvokeXObject,
            PdfOperators.BeginInlineImage, PdfOperators.InlineImageData, PdfOperators.EndInlineImage,
            PdfOperators.ShadingFill,

            // Type 3 glyph metrics
            PdfOperators.SetCharWidth, PdfOperators.SetCacheDevice,

            // Compatibility, marked content, and the deprecated PostScript escape
            PdfOperators.BeginCompat, PdfOperators.EndCompat,
            PdfOperators.BeginMarkedContent, PdfOperators.BeginMarkedContentProps,
            PdfOperators.EndMarkedContent,
            PdfOperators.MarkedPoint, PdfOperators.MarkedPointProps,
            PdfOperators.PostScript,
        };

        /// <summary>
        /// Longest operator token in <see cref="All"/>, in characters — the starting length for a
        /// longest-match-wins split. Derived rather than written down, so adding a longer operator
        /// cannot leave a splitter unable to see it. (Table A.1's longest are the three-character
        /// <c>BDC</c>, <c>EMC</c> and <c>SCN</c>.)
        /// </summary>
        internal static readonly int MaxLength = ComputeMaxLength();

        private static int ComputeMaxLength()
        {
            int max = 0;
            foreach (string op in All)
                if (op.Length > max) max = op.Length;
            return max;
        }

        /// <summary>True when <paramref name="token"/> is exactly one Table A.1 operator.</summary>
        internal static bool Contains(string token) => token != null && All.Contains(token);
    }
}
