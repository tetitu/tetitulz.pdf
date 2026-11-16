// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Finiteness checks for matrix and operator operands, shared by both renderers.
    /// </summary>
    /// <remarks>
    /// <para>A NaN or Infinity operand on <c>cm</c> or <c>Tm</c> permanently poisons the CTM for
    /// the rest of the enclosing <c>q</c>…<c>Q</c> scope, so every later draw in it lands at
    /// non-finite coordinates. The raster interpreter has guarded that for a long time; the SVG
    /// writer did not, and emitted path data reading
    /// <c>d="MInfinity,NaN LInfinity,NaN LInfinity,NaN LInfinity,NaN Z"</c> — markup no viewer
    /// renders, from a file the raster path draws correctly.</para>
    /// <para><b>This is a leaf on purpose.</b> It has no static fields, only pure functions, so it
    /// has no type initializer — the same construction that makes <see cref="SvgSyntax"/> safe to
    /// call from both sides. Pointing the SVG writer at <c>ContentStreamInterpreter</c>'s private
    /// copy instead would be a sideways call between peers, which is what once crashed the Base
    /// test host part-way through a parallel run. Share downward into a leaf, never sideways.</para>
    /// <para><c>float.IsFinite</c> is not available on <c>netstandard2.0</c>, which is why the
    /// predicate is written out.</para>
    /// </remarks>
    internal static class MatrixGuard
    {
        /// <summary>True when <paramref name="v"/> is neither NaN nor an infinity.</summary>
        internal static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>True when every element of <paramref name="m"/> is finite.</summary>
        internal static bool AllFinite(float[] m)
        {
            if (m == null) return false;
            for (int i = 0; i < m.Length; i++)
                if (!IsFinite(m[i])) return false;
            return true;
        }

        /// <summary>
        /// True when a numeric operand of <paramref name="op"/> — or a number inside an array operand,
        /// as <c>TJ</c>'s and <c>d</c>'s are — is not finite once read as a <c>float</c>: a real past
        /// ±3.4×10³⁸ is finite as a <c>double</c> and infinite as the <c>float</c> every operator
        /// reads. Both renderers skip such an operation whole, as they skip a non-finite <c>cm</c>.
        /// </summary>
        /// <remarks>
        /// The raster side mostly drew nothing from such a value; the SVG writer wrote it —
        /// <c>stroke-width="Infinity"</c>, <c>transform="matrix(NaN,…)"</c> — and a viewer drops
        /// the attribute and draws with its default, a one-pixel stroke or an untransformed glyph,
        /// so the two disagreed (GAPS.md §2f). Asked once, at each dispatcher's door.
        /// </remarks>
        internal static bool HasNonFiniteOperand(Core.Text.ContentStreamOperation op)
        {
            var operands = op?.Operands;
            if (operands == null) return false;
            for (int i = 0; i < operands.Length; i++)
            {
                if (operands[i] is Core.Objects.PdfNumber n)
                {
                    if (!IsFinite((float)n.DoubleValue)) return true;
                }
                else if (operands[i] is Core.Objects.PdfArray a)
                {
                    for (int j = 0; j < a.Count; j++)
                        if (a[j] is Core.Objects.PdfNumber e && !IsFinite((float)e.DoubleValue)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A dictionary's number as a <c>float</c>, false when it is absent or not finite as one —
        /// a graphics-state entry so read is ignored, as an operator with such an operand is.
        /// </summary>
        internal static bool TryFinite(Core.Objects.PdfNumber n, out float value)
        {
            value = n == null ? 0f : (float)n.DoubleValue;
            return n != null && IsFinite(value);
        }

        /// <summary>
        /// True when a number in <paramref name="arr"/> (resolved through <paramref name="doc"/>) is not
        /// finite as a <c>float</c>. An object whose geometry — a <c>/Matrix</c>, <c>/BBox</c>,
        /// <c>/Rect</c> or page box — says so is not drawn, in either renderer.
        /// </summary>
        internal static bool HasNonFiniteNumber(Core.Objects.PdfArray arr, Core.Document.PdfDocument doc)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Count; i++)
            {
                var item = doc != null ? doc.ResolveObject(arr[i]) : arr[i];
                if (item is Core.Objects.PdfNumber n && !IsFinite((float)n.DoubleValue)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when a pattern's <c>/Matrix</c>, <c>/BBox</c>, <c>/XStep</c> or <c>/YStep</c> holds a
        /// number that is not finite as a <c>float</c>: such a pattern paints nothing, in either renderer.
        /// </summary>
        internal static bool PatternGeometryNotFinite(Core.Objects.PdfDictionary pattern, Core.Document.PdfDocument doc)
        {
            if (pattern == null) return false;
            Core.Objects.PdfObject Resolved(Core.Objects.PdfName key)
                => doc != null ? doc.ResolveObject(pattern[key]) : pattern[key];
            return HasNonFiniteNumber(Resolved(Core.Objects.PdfName.Matrix) as Core.Objects.PdfArray, doc)
                || HasNonFiniteNumber(Resolved(Core.Objects.PdfName.BBox) as Core.Objects.PdfArray, doc)
                || (Resolved(Core.Objects.PdfName.XStep) is Core.Objects.PdfNumber x && !TryFinite(x, out _))
                || (Resolved(Core.Objects.PdfName.YStep) is Core.Objects.PdfNumber y && !TryFinite(y, out _));
        }

        /// <summary>
        /// <paramref name="advancePts"/> when it is finite, otherwise a half-em fallback derived
        /// from <paramref name="fontSize"/> — half of it when that is finite and positive, and half
        /// a point when it is not.
        /// </summary>
        /// <remarks>
        /// <para>A corrupt charstring (CFF, Type 1 or an OpenType-wrapped CFF) can yield a NaN or
        /// Infinity advance width, and an overflowing <c>Tf</c> size reaches the same place by
        /// multiplication. Composing either into <c>Tm</c> poisons the text matrix for the rest of
        /// the run, so **every later glyph draws at non-finite coordinates and is silently
        /// invisible** — the failure this substitutes a usable advance to avoid.</para>
        ///
        /// <para><b>One rule, both renderers.</b> The raster interpreter and the SVG writer each
        /// carried it; the raster copy already reached <see cref="IsFinite"/> here while the SVG one
        /// open-coded the same predicate twice, so the two could drift on the predicate and on the
        /// fallback independently. It lives in this leaf for the reason the type's own remarks give:
        /// share downward, never sideways between the two writers.</para>
        ///
        /// <para><b>The fallback deliberately does not depend on the glyph.</b> There is no correct
        /// advance to recover — the file did not supply one — so the aim is only that the run keeps
        /// moving and later glyphs stay addressable.</para>
        /// </remarks>
        internal static float SanitizeAdvance(float advancePts, float fontSize)
            => IsFinite(advancePts)
                ? advancePts
                : 0.5f * (IsFinite(fontSize) && fontSize > 0 ? fontSize : 1f);
    }
}
