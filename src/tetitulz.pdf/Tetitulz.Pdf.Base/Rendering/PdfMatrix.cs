// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The PDF affine matrix (§8.3.3) — six numbers <c>[a b c d e f]</c> in row-vector convention,
    /// and the one composition rule for the whole assembly.
    ///
    /// <para><b>One <see cref="Compose"/></b>, for <c>GraphicsState</c>, <c>PdfRedactionEngine</c>,
    /// <c>PdfTemplateEngine</c> and <c>PdfStructuredTextExtractor</c>. Byte-identical copies are
    /// correct only until one is fixed and the others are not; a comment reading "matches …Compose"
    /// is the tell. Agreement today is not the bar; being structurally unable to disagree is.</para>
    ///
    /// <para><b>Argument order is the thing to get right.</b> <c>Compose(A, B)</c> is "A applied
    /// first, then B" — the row-vector product <c>A × B</c> — which is what a content stream's
    /// <c>cm</c> means when it concatenates onto the CTM. Reversing it produces a matrix that is
    /// still well-formed, still finite, and places everything in the wrong spot.</para>
    ///
    /// <para><b>No static fields</b>, only pure functions, so this is a leaf by construction: the
    /// operations side and the rendering side both call it, and neither picks up a
    /// type-initialization edge from the other (see <c>SvgSyntax</c> for why that matters here).</para>
    /// </summary>
    internal static class PdfMatrix
    {
        /// <summary>The identity, as a fresh array — callers mutate what they are given.</summary>
        internal static float[] Identity() => new float[] { 1, 0, 0, 1, 0, 0 };

        /// <summary>Inverse of a 2-D affine [a b c d e f], or null when singular.</summary>
        internal static float[] Invert(float[] m) => Invert(m, 1e-9f);

        /// <summary>
        /// <see cref="Invert(float[])"/> under the caller's own singularity rule: null when the
        /// determinant is 0 or its magnitude below <paramref name="minDeterminant"/>. The renderers
        /// take 1e-9, below which a <c>float</c> inverse is no use to a draw; the SVG converter takes
        /// 0, since its <c>viewBox</c> may map a coordinate system a million units wide onto ten and
        /// must still invert. Like the other members here, no finiteness guard: the callers differ.
        /// </summary>
        internal static float[] Invert(float[] m, float minDeterminant)
        {
            float det = m[0] * m[3] - m[1] * m[2];
            if (System.Math.Abs(det) < minDeterminant || det == 0) return null;
            float id = 1f / det;
            float a =  m[3] * id, b = -m[1] * id, c = -m[2] * id, d = m[0] * id;
            float e = -(m[4] * a + m[5] * c);
            float f = -(m[4] * b + m[5] * d);
            return new[] { a, b, c, d, e, f };
        }

        /// <summary>
        /// Composes <paramref name="a"/> then <paramref name="b"/> (row-vector <c>A × B</c>),
        /// returning a new array.
        /// </summary>
        internal static float[] Compose(float[] a, float[] b)
        {
            var result = new float[6];
            ComposeInto(a, b, result);
            return result;
        }

        /// <summary>
        /// <see cref="Compose"/> into <paramref name="into"/>, which may be <paramref name="a"/> or
        /// <paramref name="b"/> itself (every operand is read before any is written) — for a caller
        /// composing once a glyph, where a new array each time was most of what it allocated.
        /// </summary>
        internal static void ComposeInto(float[] a, float[] b, float[] into)
        {
            float a1 = a[0], b1 = a[1], c1 = a[2], d1 = a[3], e1 = a[4], f1 = a[5];
            float a2 = b[0], b2 = b[1], c2 = b[2], d2 = b[3], e2 = b[4], f2 = b[5];
            into[0] = a1 * a2 + b1 * c2;      into[1] = a1 * b2 + b1 * d2;
            into[2] = c1 * a2 + d1 * c2;      into[3] = c1 * b2 + d1 * d2;
            into[4] = e1 * a2 + f1 * c2 + e2; into[5] = e1 * b2 + f1 * d2 + f2;
        }

        /// <summary>
        /// The matrix's uniform scale factor: the geometric mean of its two axis lengths
        /// (§8.4.3.2's "the scale factor the CTM applies").
        /// </summary>
        /// <remarks>
        /// <para><b>Raw on purpose — no guard.</b> The raster path and the SVG writer both need this
        /// number and they guard it <i>differently</i>, deliberately: the raster callers each apply
        /// their own degenerate-CTM rule (the file's own comment records two of them and says
        /// collapsing them would change what a zero-scale CTM renders), while the SVG writer folds
        /// a single "non-finite or non-positive becomes 1" into its accessor because its callers
        /// divide by the result and an infinity would reach the markup. Sharing the arithmetic and
        /// leaving the guards where they are keeps both behaviours and removes the copy.</para>
        /// </remarks>
        internal static float UniformScaleRaw(float[] m)
        {
            float ax = (float)System.Math.Sqrt((double)m[0] * m[0] + (double)m[1] * m[1]);
            float ay = (float)System.Math.Sqrt((double)m[2] * m[2] + (double)m[3] * m[3]);
            return (float)System.Math.Sqrt((double)ax * ay);
        }

        /// <summary>Transforms a point through <paramref name="m"/>.</summary>
        internal static (float x, float y) Transform(float[] m, float x, float y)
            => (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]);

        /// <summary>
        /// The axis-aligned bounds of the axis-aligned box <c>(x0,y0)-(x1,y1)</c> after mapping
        /// through <paramref name="m"/> — all four corners, because a rotated or skewed matrix
        /// turns the box into a parallelogram whose extent no two opposite corners describe.
        /// </summary>
        /// <remarks>
        /// <para><b>No finiteness guard, deliberately, and the callers differ on it.</b>
        /// <c>ContentStreamInterpreter</c> tests the result and declines the layer;
        /// <c>PdfPageRenderer</c> lets it through to a scale division that its own
        /// <c>MatrixGuard</c> covers; <c>PdfRedactionEngine.CornersAabb</c> needs a NaN <i>quad</i>
        /// back and says in its remarks why seeding from the first mapped corner — this shape —
        /// is not sufficient for it. Folding any one of those policies in here would change the
        /// other two, so the arithmetic is shared and the guards stay at the call sites, exactly as
        /// <see cref="UniformScaleRaw"/> above does.</para>
        ///
        /// <para>That also explains why <c>CornersAabb</c> is <b>not</b> a caller: it takes four
        /// arbitrary points rather than a box, and returns the refusal in-band. It is a different
        /// function that looks like this one.</para>
        /// </remarks>
        internal static void TransformedBounds(
            float[] m, float x0, float y0, float x1, float y1,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            var (cx0, cy0) = Transform(m, x0, y0);
            var (cx1, cy1) = Transform(m, x1, y0);
            var (cx2, cy2) = Transform(m, x1, y1);
            var (cx3, cy3) = Transform(m, x0, y1);

            minX = System.Math.Min(System.Math.Min(cx0, cx1), System.Math.Min(cx2, cx3));
            maxX = System.Math.Max(System.Math.Max(cx0, cx1), System.Math.Max(cx2, cx3));
            minY = System.Math.Min(System.Math.Min(cy0, cy1), System.Math.Min(cy2, cy3));
            maxY = System.Math.Max(System.Math.Max(cy0, cy1), System.Math.Max(cy2, cy3));
        }
    }
}
