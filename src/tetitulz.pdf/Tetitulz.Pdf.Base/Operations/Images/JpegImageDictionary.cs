// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>
    /// What a JPEG codestream says its image dictionary must be: its frame, read by Core's one
    /// marker walk, and the colour space and <c>/Decode</c> array Core's one rule
    /// (<see cref="JpegColorSpace"/>) derives from it.
    /// </summary>
    /// <remarks>
    /// <para><b>Shared because three writers embed a JPEG</b> — <c>PdfImageInserter</c>,
    /// <c>PdfImageToPdf</c>, and <c>ReplacementImage.Jpeg</c> — and each needs the frame, the
    /// dictionary entries and a refusal's reason from one call.</para>
    /// </remarks>
    internal static class JpegImageDictionary
    {
        /// <summary>
        /// Reads <paramref name="jpegBytes"/> and reports the frame plus the dictionary entries it
        /// requires.
        /// </summary>
        /// <param name="jpegBytes">The JPEG codestream.</param>
        /// <param name="frame">The frame header the dimensions come from.</param>
        /// <param name="colorSpace">The colour-space name for <c>/ColorSpace</c>.</param>
        /// <param name="decode">The <c>/Decode</c> array, or <c>null</c> when none is needed.</param>
        /// <param name="error">Why the JPEG cannot be embedded, when the result is false.</param>
        /// <returns>False when the codestream cannot be embedded as-is.</returns>
        internal static bool TryDescribe(
            byte[] jpegBytes, out RasterImageInfo frame,
            out string colorSpace, out double[] decode, out string error)
        {
            frame = default;
            colorSpace = null;
            decode = null;
            error = null;

            if (jpegBytes == null) { error = "no JPEG bytes were supplied"; return false; }

            if (!RasterImageInspector.TryInspectJpeg(jpegBytes, out frame))
            {
                error = "Not a readable JPEG: no Start-Of-Frame marker with valid dimensions was found.";
                return false;
            }

            colorSpace = JpegColorSpace.Of(frame, out error);
            if (colorSpace == null) return false;
            decode = JpegColorSpace.DecodeFor(frame);
            return true;
        }
    }
}
