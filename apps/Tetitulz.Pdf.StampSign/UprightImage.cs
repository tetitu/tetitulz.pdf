// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>
    /// How a picture is drawn upright on a page that is shown turned: the matrix that puts the
    /// picture's unit square over a rectangle of the page with its top where the viewer shows the
    /// top. One rule for every app that stamps a picture on a page.
    /// </summary>
    internal static class UprightImage
    {
        /// <summary>
        /// The <c>cm</c> matrix that draws an image's unit square over the rectangle from
        /// (<paramref name="left"/>, <paramref name="bottom"/>) to (<paramref name="right"/>,
        /// <paramref name="top"/>), in the page's own coordinates, upright as a page turned by
        /// <paramref name="rotation"/> (its <c>/Rotate</c>: 0, 90, 180 or 270) is shown: the image's
        /// bottom-left, bottom-right and top-left corners where the viewer shows them.
        /// </summary>
        public static double[] Matrix(double left, double bottom, double right, double top, int rotation)
        {
            double blX, blY, brX, brY, tlX, tlY;
            switch (rotation)
            {
                case 90: blX = right; blY = bottom; brX = right; brY = top; tlX = left; tlY = bottom; break;
                case 180: blX = right; blY = top; brX = left; brY = top; tlX = right; tlY = bottom; break;
                case 270: blX = left; blY = top; brX = left; brY = bottom; tlX = right; tlY = top; break;
                default: blX = left; blY = bottom; brX = right; brY = bottom; tlX = left; tlY = top; break;
            }
            return new[] { brX - blX, brY - blY, tlX - blX, tlY - blY, blX, blY };
        }
    }
}
