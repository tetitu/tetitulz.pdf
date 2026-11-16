// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>
    /// What a JPEG frame says its image dictionary must be when it is embedded as <c>DCTDecode</c>:
    /// the device colour space its component count denotes, and the <c>/Decode</c> array an Adobe
    /// four-component frame needs.
    /// </summary>
    /// <remarks>
    /// <para><b>Derived from the codestream, never from the caller.</b> A dictionary that contradicts
    /// the bytes it describes is reported as damaged by a conforming reader and is invisible in this
    /// library's own renderer, which trusts the codestream.</para>
    ///
    /// <para><b>A four-component frame with no Adobe APP14 marker is refused rather than guessed
    /// at.</b> Adobe writes CMYK components inverted and nothing else in the codestream says which way
    /// round the values are, so a wrong guess yields a colour-negative image that still opens.</para>
    ///
    /// <para>The one rule for every writer that embeds a JPEG: Base's image writers and SVG
    /// converter, and Builder, which may not reference Base.</para>
    /// </remarks>
    public static class JpegColorSpace
    {
        /// <summary>
        /// The colour-space name a JPEG frame denotes — <c>DeviceGray</c>, <c>DeviceRGB</c>, or
        /// <c>DeviceCMYK</c> for an Adobe four-component frame — or <c>null</c> with
        /// <paramref name="reason"/> saying why it is refused.
        /// </summary>
        public static string Of(RasterImageInfo frame, out string reason)
        {
            reason = null;
            switch (frame.Components)
            {
                case PdfColorSpaceNames.GrayComponents:
                    return PdfColorSpaceNames.DeviceGray;
                case PdfColorSpaceNames.RgbComponents:
                    return PdfColorSpaceNames.DeviceRGB;
                case PdfColorSpaceNames.CmykComponents:
                    if (frame.HasAdobeMarker) return PdfColorSpaceNames.DeviceCMYK;
                    reason = "a four-component JPEG without an Adobe APP14 marker has no identifiable " +
                             "colour space: the sample polarity cannot be determined from the codestream, " +
                             "and guessing it would embed an inverted image";
                    return null;
                default:
                    reason = $"a JPEG with {frame.Components} components is not supported; only 1 " +
                             "(greyscale), 3 (YCbCr → RGB) and 4 (Adobe CMYK / YCCK) are";
                    return null;
            }
        }

        /// <summary>
        /// The <c>/Decode</c> array the embedded frame needs: <c>[1 0 1 0 1 0 1 0]</c> for Adobe's
        /// inverted CMYK, else <c>null</c>. Ask it only of a frame <see cref="Of"/> accepts.
        /// </summary>
        public static double[] DecodeFor(RasterImageInfo frame)
            => frame.Components == PdfColorSpaceNames.CmykComponents && frame.HasAdobeMarker
                ? new double[] { 1, 0, 1, 0, 1, 0, 1, 0 }
                : null;
    }
}
