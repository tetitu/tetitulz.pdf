// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// The PDF values the plugin's resources are built of, in one place: a number array, a matrix as
    /// its six numbers, and the unit ranges of an n-component space (<c>[0 1 0 1 …]</c>). The colour
    /// spaces, the shadings, the patterns and the forms each carried a copy.
    /// </summary>
    internal static class PdfValues
    {
        /// <summary>An array of <paramref name="values"/>.</summary>
        public static PdfArray Numbers(double[] values)
        {
            var array = new PdfArray();
            foreach (double v in values) array.Add(new PdfNumber(v));
            return array;
        }

        /// <summary><paramref name="m"/> as <c>[a b c d tx ty]</c>.</summary>
        public static PdfArray Matrix(PsMatrix m) => Numbers(new[] { m.A, m.B, m.C, m.D, m.Tx, m.Ty });

        /// <summary><c>[0 1 0 1 …]</c>, <paramref name="n"/> pairs.</summary>
        public static double[] UnitRanges(int n)
        {
            var range = new double[2 * n];
            for (int k = 0; k < n; k++) range[2 * k + 1] = 1;
            return range;
        }
    }
}
