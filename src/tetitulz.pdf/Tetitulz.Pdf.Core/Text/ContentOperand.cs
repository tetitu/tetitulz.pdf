// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Single source of truth for coercing PDF content-stream operands to CLR
    /// values, for every content-stream consumer (the SkiaSharp interpreter, the
    /// SVG renderer, the graphics extractor). Centralising them here keeps the
    /// coercion rules
    /// (out-of-range index → default, non-number → default) in one place.
    /// </summary>
    internal static class ContentOperand
    {
        /// <summary>Operand <paramref name="idx"/> as float, or 0 if missing / not a number.</summary>
        public static float Float(ContentStreamOperation op, int idx)
        {
            if (op == null || idx < 0 || idx >= op.Operands.Length) return 0f;
            return (float)((op.Operands[idx] as PdfNumber)?.DoubleValue ?? 0.0);
        }

        /// <summary>Operand <paramref name="idx"/> as int, or 0 if missing / not a number.</summary>
        public static int Int(ContentStreamOperation op, int idx)
        {
            if (op == null || idx < 0 || idx >= op.Operands.Length) return 0;
            return (op.Operands[idx] as PdfNumber)?.IntValue ?? 0;
        }

        /// <summary>Operand <paramref name="idx"/> as long, or 0 if missing / not a number.</summary>
        public static long Long(ContentStreamOperation op, int idx)
        {
            if (op == null || idx < 0 || idx >= op.Operands.Length) return 0;
            return (op.Operands[idx] as PdfNumber)?.LongValue ?? 0;
        }

        /// <summary>Operand <paramref name="idx"/> as a name value (without the leading '/'), or null.</summary>
        public static string Name(ContentStreamOperation op, int idx)
        {
            if (op == null || idx < 0 || idx >= op.Operands.Length) return null;
            return (op.Operands[idx] as PdfName)?.Value;
        }

        /// <summary>Raw bytes of a string object, or an empty array if it is not a string.</summary>
        public static byte[] StringBytes(PdfObject obj)
            => obj is PdfString ps ? ps.RawBytes : System.Array.Empty<byte>();

        /// <summary>Element <paramref name="idx"/> of an array as float, or 0 if out of range / not a number.</summary>
        public static float ArrayFloat(PdfArray arr, int idx)
            => arr != null && idx >= 0 && idx < arr.Count
                ? (float)((arr[idx] as PdfNumber)?.DoubleValue ?? 0.0)
                : 0f;

        /// <summary>
        /// The text segments a <c>Tj</c> / <c>'</c> / <c>"</c> operator shows: at most one element,
        /// the string's raw bytes.
        /// </summary>
        /// <remarks>
        /// Paired with <see cref="ShowTextArraySegments"/>, this is the <c>TJ</c>/<c>Tj</c> operand
        /// model both whole-document rewriters walk: a list whose elements are either
        /// <c>byte[]</c> (a string to show) or <c>float</c> (a position adjustment in thousandths
        /// of an em). Returning <c>object</c> is what lets one walk handle both operators.
        /// <para>It existed twice, under two names -- <c>PdfRedactionEngine.GetStringSegs</c> and
        /// <c>PdfTemplateEngine.Segs</c> -- differing only by a capacity hint, which cannot change
        /// a result. That is the same shape this type was created for: "six copies once existed
        /// under six different names".</para>
        /// </remarks>
        public static List<object> ShowTextSegments(ContentStreamOperation op, int idx)
        {
            var segs = new List<object>(1);
            if (op != null && idx >= 0 && idx < op.Operands.Length && op.Operands[idx] is PdfString s)
                segs.Add(s.RawBytes);
            return segs;
        }

        /// <summary>
        /// The segments a <c>TJ</c> operator shows: each array element as either its raw bytes
        /// (<see cref="PdfString"/>) or its value as a float (<see cref="PdfNumber"/>).
        /// </summary>
        /// <remarks>
        /// <b>Any other element type is dropped silently</b>, which both copies did and which is
        /// right for a rewriter: §9.4.3 admits only strings and numbers in a <c>TJ</c> array, and a
        /// rewriter that invented a segment for a malformed element would move every glyph after
        /// it. The advance is unchanged because a dropped element contributes none.
        /// </remarks>
        public static List<object> ShowTextArraySegments(ContentStreamOperation op)
        {
            var segs = new List<object>();
            if (op != null && op.Operands.Length >= 1 && op.Operands[0] is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is PdfString s) segs.Add(s.RawBytes);
                    else if (arr[i] is PdfNumber n) segs.Add((float)n.DoubleValue);
                }
            }
            return segs;
        }

        /// <summary>
        /// Reads the <b>last</b> <paramref name="count"/> operands as floats into
        /// <paramref name="destination"/>, reporting whether all of them were numbers.
        /// </summary>
        /// <remarks>
        /// <b>Trailing, not leading, and that is the rule rather than a convenience.</b> A
        /// content stream may leave operands on the stack that an operator does not consume —
        /// §7.8.2 says a conforming reader takes the operands an operator needs and ignores the
        /// rest — so <c>1 2 3 4 5 6 7 Tm</c> is the six values nearest the operator, not the first
        /// six.
        ///
        /// <para>All-or-nothing: one non-number and nothing is written, so a caller's state is
        /// never left half-updated from a malformed operator. Two content-stream walks in
        /// <c>Accessibility</c> spelled this out identically; it belongs here, with the other
        /// operand coercions, for the reason this type exists.</para>
        /// </remarks>
        /// <param name="destination">Receives the values; must hold at least <paramref name="count"/>.</param>
        /// <param name="operands">The operator's operand list.</param>
        /// <param name="count">How many trailing operands to read.</param>
        public static bool TryTrailingNumbers(
            IReadOnlyList<PdfObject> operands, int count, float[] destination)
        {
            if (operands == null || destination == null) return false;
            if (count < 0 || destination.Length < count || operands.Count < count) return false;

            int start = operands.Count - count;
            for (int i = 0; i < count; i++)
            {
                if (!(operands[start + i] is PdfNumber number)) return false;
                destination[i] = (float)number.DoubleValue;
            }
            return true;
        }
    }
}
