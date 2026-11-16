// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// ISO 32000-1 §9.4.2 — how the text matrix and the text line matrix move: <c>Tm</c>,
    /// <c>Td</c>, <c>TD</c>, <c>T*</c>, <c>TL</c>, and the next-line advance that <c>'</c> and
    /// <c>"</c> perform before showing.
    ///
    /// <para>Two content-stream walks in this assembly carried this algebra —
    /// <c>ContentStreamTagger.BuildTextObjects</c> and
    /// <c>PdfReadingOrderAnalyzer.ScanTokens</c> — written out identically, case for case.</para>
    /// </summary>
    /// <remarks>
    /// <b>Only the algebra is shared, and the sizing is the point.</b> The two walks look alike and
    /// are not interchangeable: their policies on the SAME malformed input are opposites, and both
    /// are right for their caller. The tagger REFUSES a page whose graphics stack overflows, whose
    /// <c>BT</c> nests, or whose <c>ET</c> has no <c>BT</c> — it is computing byte offsets to
    /// splice, and a wrong offset corrupts the document. The analyzer TOLERATES all three, counts
    /// them and reports once — it is only measuring bands, and a partial measurement is still worth
    /// having. Merging the walks would have to pick one, so the walks stay.
    ///
    /// <para><b>Nor is the state shared.</b> The tagger's graphics state carries a font size; the
    /// analyzer's does not, under a comment saying so deliberately — "nothing here needs it, only
    /// the baseline" — and its switch has no <c>Tf</c> case at all. A shared struct would force the
    /// field on one or make the other layer it back on. So this takes the two matrices and the
    /// leading by <c>ref</c> and leaves every caller its own state type.</para>
    ///
    /// <para><b>Rise is deliberately not handled here</b> for the same reason: it lands in a
    /// different struct on each side, and unlike the matrices it takes part in no algebra.</para>
    ///
    /// <para>No static fields, so no type initializer — a leaf, like
    /// <see cref="AffineMatrix"/> beside it.</para>
    /// </remarks>
    internal static class TextPositioning
    {
        /// <summary>
        /// §9.4.1: <c>BT</c> resets the text matrix and the text line matrix to the identity, and
        /// <b>nothing else</b> — not the leading, not the rise, not the font.
        /// </summary>
        internal static void BeginText(ref AffineMatrix tm, ref AffineMatrix tlm)
        {
            tm = AffineMatrix.Identity;
            tlm = AffineMatrix.Identity;
        }

        /// <summary>
        /// The next-line advance <c>T*</c> performs, and that <c>'</c> and <c>"</c> perform before
        /// showing their string.
        /// </summary>
        /// <remarks>
        /// <c>"</c> also sets word and character spacing, which neither walk tracks; that is why
        /// the two operators share this one call and differ only in what their caller does after.
        /// </remarks>
        internal static void NextLine(ref AffineMatrix tm, ref AffineMatrix tlm, float leading)
        {
            tlm = AffineMatrix.Multiply(AffineMatrix.Translate(0f, -leading), tlm);
            tm = tlm;
        }

        /// <summary>
        /// Applies <paramref name="op"/> when it is one of the text-positioning operators, and
        /// reports whether it was.
        /// </summary>
        /// <remarks>
        /// <b>An operator whose operands do not parse leaves the state untouched</b>, which both
        /// walks already did and which is the conservative reading: a malformed <c>Td</c> must not
        /// move the baseline to somewhere the file never asked for. It still returns
        /// <see langword="true"/> — the operator WAS a positioning operator, it simply had nothing
        /// usable in it, and a caller must not then fall through to some other interpretation.
        /// </remarks>
        /// <param name="scratch">
        /// A caller-owned buffer of at least six floats, so a per-operator walk allocates nothing.
        /// </param>
        /// <param name="op">The text-positioning operator — <c>Td</c>, <c>TD</c>, <c>Tm</c>, <c>T*</c>.</param>
        /// <param name="operands">Its operands, coerced through <c>ContentOperand</c>.</param>
        /// <param name="tm">The text matrix, updated in place.</param>
        /// <param name="tlm">The text line matrix, updated in place.</param>
        /// <param name="leading">
        /// The text leading (<c>TL</c>), read by <c>T*</c> and written by <c>TD</c>.
        /// </param>
        internal static bool TryApply(
            string op,
            IReadOnlyList<PdfObject> operands,
            float[] scratch,
            ref AffineMatrix tm,
            ref AffineMatrix tlm,
            ref float leading)
        {
            switch (op)
            {
                case PdfOperators.SetLeading:
                    if (ContentOperand.TryTrailingNumbers(operands, 1, scratch)) leading = scratch[0];
                    return true;

                case PdfOperators.SetTextMatrix:
                    if (ContentOperand.TryTrailingNumbers(operands, 6, scratch))
                    {
                        tlm = new AffineMatrix(scratch[0], scratch[1], scratch[2],
                                               scratch[3], scratch[4], scratch[5]);
                        tm = tlm;
                    }
                    return true;

                case PdfOperators.TextMove:
                    if (ContentOperand.TryTrailingNumbers(operands, 2, scratch))
                    {
                        tlm = AffineMatrix.Multiply(AffineMatrix.Translate(scratch[0], scratch[1]), tlm);
                        tm = tlm;
                    }
                    return true;

                case PdfOperators.TextMoveSetLeading:
                    // TD is Td with the leading set to the NEGATED ty first (§9.4.2 Table 108).
                    if (ContentOperand.TryTrailingNumbers(operands, 2, scratch))
                    {
                        leading = -scratch[1];
                        tlm = AffineMatrix.Multiply(AffineMatrix.Translate(scratch[0], scratch[1]), tlm);
                        tm = tlm;
                    }
                    return true;

                case PdfOperators.NextLine:
                    NextLine(ref tm, ref tlm, leading);
                    return true;

                default:
                    return false;
            }
        }
    }
}
