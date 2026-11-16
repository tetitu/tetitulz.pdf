// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>
    /// A page as it is shown — after its <c>/Rotate</c> — against the page's own coordinates: one
    /// rule for every app that places something where the user sees it.
    /// </summary>
    internal static class ShownPage
    {
        /// <summary>
        /// A shown point — <paramref name="u"/> from the shown page's left edge,
        /// <paramref name="v"/> down from its top — in the page's own coordinates (the origin
        /// bottom-left, y upwards), for a page whose box runs from (<paramref name="left"/>,
        /// <paramref name="bottom"/>) to (<paramref name="right"/>, <paramref name="top"/>) and is
        /// shown turned by <paramref name="rotation"/> (0, 90, 180 or 270, clockwise).
        /// </summary>
        public static (double X, double Y) ToPage(double left, double bottom, double right, double top, int rotation, double u, double v)
        {
            switch (rotation)
            {
                case 90: return (left + v, bottom + u);
                case 180: return (right - u, bottom + v);
                case 270: return (right - v, top - u);
                default: return (left + u, top - v);
            }
        }
    }
}
