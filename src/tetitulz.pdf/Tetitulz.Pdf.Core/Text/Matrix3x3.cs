// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Text
{
    internal struct Matrix3x3
    {
        public double A, B, C, D, E, F;
        // [A B 0]
        // [C D 0]
        // [E F 1]

        public Matrix3x3(double a, double b, double c, double d, double e, double f)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }

        public static Matrix3x3 Identity => new Matrix3x3(1, 0, 0, 1, 0, 0);

        public static Matrix3x3 Multiply(Matrix3x3 m1, Matrix3x3 m2)
        {
            return new Matrix3x3(
                m1.A * m2.A + m1.B * m2.C,
                m1.A * m2.B + m1.B * m2.D,
                m1.C * m2.A + m1.D * m2.C,
                m1.C * m2.B + m1.D * m2.D,
                m1.E * m2.A + m1.F * m2.C + m2.E,
                m1.E * m2.B + m1.F * m2.D + m2.F
            );
        }

        public double TranslateX => E;
        public double TranslateY => F;
    }
}
