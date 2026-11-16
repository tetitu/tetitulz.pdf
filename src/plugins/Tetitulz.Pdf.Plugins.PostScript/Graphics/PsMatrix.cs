// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// A PostScript transformation matrix: the six-element <c>[a b c d tx ty]</c> form both
    /// PostScript and PDF use for the affine matrix
    /// <c>| a b 0 |</c> / <c>| c d 0 |</c> / <c>| tx ty 1 |</c>.
    ///
    /// <para><b>Double, not float.</b> The library's convention is <c>float</c> for PDF user-space
    /// coordinates, and this deliberately departs from it: <c>setmatrix</c> is an <i>absolute</i>
    /// operation that has to be emitted as a relative <c>cm</c>, which means composing with an
    /// inverse. Accumulated single-precision error in a chain of inverses is visible as drift in the
    /// output, and the values never reach the PDF as doubles anyway —
    /// <c>PdfContentStreamBuilder.FormatNumber</c> rounds them to six decimals on the way out.</para>
    ///
    /// <para>A readonly struct: the graphics state stack copies matrices constantly, and every
    /// operation produces a new one rather than mutating.</para>
    /// </summary>
    internal readonly struct PsMatrix : IEquatable<PsMatrix>
    {
        public readonly double A, B, C, D, Tx, Ty;

        public PsMatrix(double a, double b, double c, double d, double tx, double ty)
        {
            A = a; B = b; C = c; D = d; Tx = tx; Ty = ty;
        }

        public static readonly PsMatrix Identity = new(1, 0, 0, 1, 0, 0);

        public static PsMatrix Translate(double tx, double ty) => new(1, 0, 0, 1, tx, ty);
        public static PsMatrix Scale(double sx, double sy)      => new(sx, 0, 0, sy, 0, 0);

        public static PsMatrix Rotate(double degrees)
        {
            double r = degrees * Math.PI / 180.0;
            double cos = Math.Cos(r), sin = Math.Sin(r);
            return new PsMatrix(cos, sin, -sin, cos, 0, 0);
        }

        /// <summary>
        /// <c>this × other</c> — apply <c>this</c> first, then <c>other</c>. That order is what
        /// PostScript's <c>concat</c> and PDF's <c>cm</c> both mean: the new matrix is prepended to
        /// the CTM, so it applies to coordinates before the existing transform does.
        /// </summary>
        public PsMatrix Concat(in PsMatrix other) => new(
            A * other.A + B * other.C,
            A * other.B + B * other.D,
            C * other.A + D * other.C,
            C * other.B + D * other.D,
            Tx * other.A + Ty * other.C + other.Tx,
            Tx * other.B + Ty * other.D + other.Ty);

        public double Determinant => A * D - B * C;

        /// <summary>
        /// True when the matrix collapses the plane to a line or a point, so nothing drawn under it
        /// would be visible. Emitting such a <c>cm</c> makes every later operator invisible in most
        /// readers, which is why the operators refuse rather than pass it through.
        /// </summary>
        public bool IsSingular => Math.Abs(Determinant) < PsLimits.MinMatrixDeterminant;

        /// <summary>
        /// True when every component <b>and</b> the determinant is a real number.
        ///
        /// <para><b>This is a separate predicate from <see cref="IsSingular"/> because
        /// <c>Math.Abs(NaN) &lt; x</c> and <c>Math.Abs(∞) &lt; x</c> are both <c>false</c>.</b> A
        /// non-finite matrix therefore passed the singular check that exists to refuse exactly this
        /// — and then <c>ContentStreamEmitter.F</c> substituted <c>0</c> for each non-finite
        /// component at the write boundary, so <c>1e200 1e200 mul dup scale</c> emitted
        /// <c>0 0 0 0 0 0 cm</c>: a genuinely singular PDF matrix that collapses every later path,
        /// image and text run to a point. The only diagnostic said a coordinate had been replaced,
        /// not that the transform had blanked the rest of the stream.</para>
        ///
        /// <para>The determinant is tested as well as the components, because <c>A</c> and <c>D</c>
        /// can each be finite while <c>A * D</c> overflows — which makes <see cref="Invert"/>
        /// return an all-zero matrix rather than null, since <c>1.0 / ∞</c> is <c>0</c>.</para>
        /// </summary>
        public bool IsFinite =>
            PsNumeric.IsFinite(A) && PsNumeric.IsFinite(B) && PsNumeric.IsFinite(C) &&
            PsNumeric.IsFinite(D) && PsNumeric.IsFinite(Tx) && PsNumeric.IsFinite(Ty) &&
            PsNumeric.IsFinite(Determinant);

        /// <summary>The inverse, or null when <see cref="IsSingular"/> or not <see cref="IsFinite"/>.</summary>
        public PsMatrix? Invert()
        {
            double det = Determinant;
            if (!IsFinite || Math.Abs(det) < PsLimits.MinMatrixDeterminant) return null;

            double inv = 1.0 / det;
            return new PsMatrix(
                 D * inv,
                -B * inv,
                -C * inv,
                 A * inv,
                 (C * Ty - D * Tx) * inv,
                 (B * Tx - A * Ty) * inv);
        }

        /// <summary>Transforms a point, translation included (<c>transform</c>).</summary>
        public (double X, double Y) Apply(double x, double y) =>
            (A * x + C * y + Tx, B * x + D * y + Ty);

        /// <summary>
        /// Transforms a <b>distance</b> — translation excluded (<c>dtransform</c>). A line width or
        /// a dash length is a distance, not a position, so translating it would be wrong.
        /// </summary>
        public (double X, double Y) ApplyDelta(double dx, double dy) =>
            (A * dx + C * dy, B * dx + D * dy);

        public double[] ToArray() => new[] { A, B, C, D, Tx, Ty };

        /// <summary>
        /// The six numbers of a PostScript matrix operand, or false when the array is not one.
        ///
        /// <para><b>One implementation</b>, for <c>PsGraphicsOps</c>, <c>PsImageOps</c>,
        /// <c>PsTextOps</c> and <c>PsType3Font.ReadMatrix</c> — the last falls back instead of
        /// throwing, by a parameter. Private copies under different names drift, as six copies of
        /// the content operand coercions would (<c>ContentOperand</c>).</para>
        ///
        /// <para>The <b>error policy stays at the call site</b>, which is the reason this returns a
        /// bool rather than throwing. An operator handed a bad matrix must raise a catchable
        /// PostScript error; a font <i>dictionary</i> carrying a malformed <c>/FontMatrix</c> must
        /// fall back to the default rather than kill the object that defines it. Folding both into
        /// one throwing helper would have forced the second case to catch its own exception.</para>
        /// </summary>
        public static bool TryFromOperands(Interpreter.PsArray a, out PsMatrix matrix)
        {
            matrix = Identity;
            if (a == null || a.Length < 6) return false;

            var v = new double[6];
            for (int k = 0; k < 6; k++)
            {
                if (!a[k].IsNumber) return false;
                v[k] = a[k].Num;
            }

            matrix = new PsMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
            return true;
        }

        /// <summary>
        /// The operand form for an <b>operator</b>: a wrong length is <c>rangecheck</c> and a
        /// non-number is <c>typecheck</c>, both catchable by <c>stopped</c>.
        /// </summary>
        public static PsMatrix FromOperands(Interpreter.PsArray a, string op)
        {
            if (a == null || a.Length < 6)
                throw new Interpreter.PsErrorException(Interpreter.PsErrorKind.RangeCheck, op);

            if (!TryFromOperands(a, out var matrix))
                throw new Interpreter.PsErrorException(Interpreter.PsErrorKind.TypeCheck, op);

            return matrix;
        }

        public static PsMatrix FromArray(double[] m) =>
            m == null || m.Length < 6 ? Identity : new PsMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);

        public bool Equals(PsMatrix o) =>
            A == o.A && B == o.B && C == o.C && D == o.D && Tx == o.Tx && Ty == o.Ty;

        public override bool Equals(object obj) => obj is PsMatrix m && Equals(m);

        // PsHash rather than HashCode.Combine: the latter is .NET Core 2.1+ and this assembly also
        // builds for netstandard2.0. Same six fields as Equals, which is the only property required.
        public override int GetHashCode() =>
            PsHash.Combine(A.GetHashCode(), B.GetHashCode(), C.GetHashCode(),
                           D.GetHashCode(), Tx.GetHashCode(), Ty.GetHashCode());

        public bool IsIdentity => Equals(Identity);
    }
}
