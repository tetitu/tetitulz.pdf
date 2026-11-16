// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Rebuilds a <c>TJ</c> array while dropping some of its glyphs, compensating for each dropped
    /// run with a kern element so everything after it stays where it was.
    /// </summary>
    /// <remarks>
    /// <para><b>The two operations that must DESTROY content shared this and did not.</b>
    /// <see cref="PdfRedactionEngine"/> and <see cref="PdfTemplateEngine"/> each carried the same
    /// pair of local functions — a hex-string flush and a removed-advance flush — identical down to
    /// the <c>1e-6f</c> epsilons and the NaN/Infinity guard, differing only in local names. The
    /// loops around them differ genuinely (redaction measures each glyph against the redaction
    /// rectangles; templating consumes a precomputed drop list), so only the accumulator is here.
    /// They already share <see cref="PdfFullRewriter"/> for the rewrite itself.</para>
    ///
    /// <para><b>The number format is a parameter because the two really do differ.</b> Templating
    /// emits <c>0.####</c> and redaction <c>0.######</c>. Neither is wrong — more precision is the
    /// safer choice where a mis-placed kern could shift surviving text over content that was meant
    /// to be removed — so the difference is preserved rather than unified. That is the same rule the
    /// seven <c>Fmt</c> helpers in this assembly follow: they all reach
    /// <c>PdfRealFormat.Format</c> and each names its own precision.</para>
    ///
    /// <para><b>Why the compensation is a kern and not a re-measure.</b> A <c>TJ</c> number is a
    /// displacement in thousandths of an em, scaled by the font size and horizontal scale — so
    /// removing glyphs whose total advance was <c>a</c> points needs an element of
    /// <c>-a · 1000 / (Tfs · Th)</c>. With either factor at zero that is undefined, and the element
    /// is omitted: a text run scaled to nothing has no visible position to preserve.</para>
    /// </remarks>
    internal sealed class ShowTextArrayBuilder
    {
        private readonly List<string> _elements = new List<string>();
        private readonly List<byte> _kept = new List<byte>();
        private readonly float _fontSize;
        private readonly float _horizScale;
        private readonly string _numberFormat;
        private float _removedAdvance;

        /// <param name="fontSize">The <c>Tfs</c> in force at this operator.</param>
        /// <param name="horizScale">The <c>Th</c> in force at this operator, as a factor.</param>
        /// <param name="numberFormat">
        /// The format each emitted number takes, passed to <c>PdfRealFormat.Format</c>. See the
        /// remarks: the two callers deliberately differ.
        /// </param>
        internal ShowTextArrayBuilder(float fontSize, float horizScale, string numberFormat)
        {
            _fontSize = fontSize;
            _horizScale = horizScale;
            _numberFormat = numberFormat;
        }

        /// <summary>Keeps one byte of the original string.</summary>
        internal void Keep(byte b)
        {
            FlushRemoved();
            _kept.Add(b);
        }

        /// <summary>
        /// Keeps <paramref name="length"/> bytes starting at <paramref name="start"/>, clamped to
        /// the source. <b>A multi-byte code's bytes travel together</b>: keeping half of one leaves
        /// the survivor decoding to a character the author never wrote.
        /// </summary>
        internal void Keep(byte[] source, int start, int length)
        {
            FlushRemoved();
            for (int k = 0; k < length && start + k < source.Length; k++)
                _kept.Add(source[start + k]);
        }

        /// <summary>
        /// Drops a glyph whose advance was <paramref name="advance"/> points, to be compensated by
        /// the next flush.
        /// </summary>
        internal void Drop(float advance)
        {
            FlushKept();
            _removedAdvance += advance;
        }

        /// <summary>Passes an original <c>TJ</c> kern element through unchanged.</summary>
        internal void Kern(float kern)
        {
            FlushKept();
            FlushRemoved();
            _elements.Add(Format(kern));
        }

        /// <summary>Flushes what is pending and returns the rebuilt elements, in order.</summary>
        internal IReadOnlyList<string> Build()
        {
            FlushKept();
            FlushRemoved();
            return _elements;
        }

        private void FlushKept()
        {
            if (_kept.Count == 0) return;
            var hex = new StringBuilder(_kept.Count * 2 + 2);
            hex.Append('<');
            foreach (byte b in _kept) hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            hex.Append('>');
            _elements.Add(hex.ToString());
            _kept.Clear();
        }

        private void FlushRemoved()
        {
            if (Math.Abs(_removedAdvance) < 1e-6f) return;

            float denom = _fontSize * _horizScale;
            if (Math.Abs(denom) > 1e-6f)
            {
                float n = -_removedAdvance * (float)PdfUnits.GlyphSpaceUnitsPerEm / denom;
                if (!float.IsNaN(n) && !float.IsInfinity(n)) _elements.Add(Format(n));
            }
            _removedAdvance = 0f;
        }

        private string Format(float v) => PdfRealFormat.Format(v, _numberFormat);
    }
}
