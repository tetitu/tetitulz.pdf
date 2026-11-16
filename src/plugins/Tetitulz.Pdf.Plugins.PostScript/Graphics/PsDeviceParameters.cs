// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// The device-dependent parameters of the graphics state (PLRM §4.2, Table 4.2) that a PDF
    /// <c>/ExtGState</c> carries beside the transfer function (ISO 32000-1 Table 58): overprint, black
    /// generation, undercolour removal, flatness, smoothness and stroke adjustment. Immutable, compared
    /// by value — a function by the sampled object it is — so the emitter writes a <c>gs</c> only where
    /// the state the stream holds differs.
    /// </summary>
    /// <param name="Overprint"><c>setoverprint</c>: PDF's <c>/OP</c> and <c>/op</c> alike, PostScript having one parameter for both.</param>
    /// <param name="BlackGeneration"><c>setblackgeneration</c>'s procedure sampled as a function, or null for the device's own.</param>
    /// <param name="UnderColorRemoval"><c>setundercolorremoval</c>'s, sampled over −1 to 1, or null for the device's own.</param>
    /// <param name="Flatness"><c>setflat</c>.</param>
    /// <param name="Smoothness"><c>setsmoothness</c>.</param>
    /// <param name="StrokeAdjust"><c>setstrokeadjust</c>.</param>
    internal sealed record PsDeviceParameters(
        bool Overprint,
        PdfObject BlackGeneration,
        PdfObject UnderColorRemoval,
        double Flatness,
        double Smoothness,
        bool StrokeAdjust)
    {
        /// <summary>
        /// What a page starts with and nothing has written: no overprint, the device's black generation
        /// and undercolour removal, flatness 1, smoothness 0, no stroke adjustment — the values the
        /// <c>current*</c> operators answer before any <c>set*</c>.
        /// </summary>
        public static readonly PsDeviceParameters Initial = new(false, null, null, 1.0, 0.0, false);
    }
}
