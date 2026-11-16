// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// A PDF affine matrix — <c>[a b c d e f]</c>, the six operands of <c>cm</c> and <c>Tm</c>.
    ///
    /// <para>Two identical copies of this lived as private nested structs inside
    /// <c>ContentStreamTagger</c> and <c>PdfReadingOrderAnalyzer</c>, differing only in their
    /// members' accessibility keywords and in one extra property. Both walk content streams and
    /// both need the same maths; one implementation makes a change to the multiplication order
    /// impossible to apply to one and not the other.</para>
    ///
    /// <para><b>It is deliberately NOT merged into <c>Core.Text.Matrix3x3</c>.</b> That type is
    /// <c>double</c> where this is <c>float</c>, so adopting it would change the precision of every
    /// geometric comparison in reading-order banding and MCID placement — a real behaviour change,
    /// in the service of removing a duplication that has never produced a defect. It is also
    /// <c>internal</c> to Core, so reaching it would need an <c>InternalsVisibleTo</c> edge. Two
    /// affine matrices in the tree is a cost worth paying; two in one assembly was not.</para>
    /// </summary>
    internal readonly struct AffineMatrix
    {
        internal readonly float A, B, C, D, E, F;

        internal AffineMatrix(float a, float b, float c, float d, float e, float f)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }

        internal static AffineMatrix Identity => new AffineMatrix(1f, 0f, 0f, 1f, 0f, 0f);

        internal static AffineMatrix Translate(float tx, float ty)
            => new AffineMatrix(1f, 0f, 0f, 1f, tx, ty);

        /// <summary>Returns <c>m × n</c> — apply <paramref name="m"/> first, then <paramref name="n"/>.</summary>
        internal static AffineMatrix Multiply(AffineMatrix m, AffineMatrix n) => new AffineMatrix(
            (m.A * n.A) + (m.B * n.C),
            (m.A * n.B) + (m.B * n.D),
            (m.C * n.A) + (m.D * n.C),
            (m.C * n.B) + (m.D * n.D),
            (m.E * n.A) + (m.F * n.C) + n.E,
            (m.E * n.B) + (m.F * n.D) + n.F);

        /// <summary>Length of the transformed unit-Y vector — the factor by which type is scaled vertically.</summary>
        internal float VerticalScale => (float)Math.Sqrt((C * C) + (D * D));
    }
}
