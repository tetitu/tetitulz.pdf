// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// PNG decoding for the DOCX image path: 8-bit DeviceRGB samples, <b>keeping</b> any alpha plane
    /// so <see cref="ImageRenderer"/> can write it as a real <c>/SMask</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>This was a second full PNG decoder</b> — chunk walk, zlib, unfiltering, every colour
    /// type at every bit depth — sitting alongside the one in
    /// <c>Builder.Export.BuilderImageDecoder</c>, and each had what the other lacked: this one
    /// honoured a colour-key <c>tRNS</c> from the start, that one supported Adam7. Both files said
    /// so about each other in prose, which by CLAUDE.md's rule is a duplication report rather than
    /// documentation. The union lives in <see cref="PngImageDecoder"/>, and what is here is the two
    /// lines of glue this assembly actually needs.</para>
    ///
    /// <para><b>Alpha is not composited over white here.</b> Flattening would be a fact about these
    /// pages, not about PNG — and wrong wherever a picture overlaps anything, which is where Word puts
    /// logos. When the mask became
    /// writable the compositing helper lost its last caller, and it is deleted rather than kept "in
    /// case": a helper with no caller and a comment explaining when to use it is exactly what makes
    /// the next reader believe it is load-bearing.</para>
    /// </remarks>
    internal static class PngImage
    {
        /// <summary>
        /// Decodes <paramref name="png"/> to packed 8-bit RGB. <paramref name="alpha"/> is the
        /// per-pixel alpha plane, or null when the image's colour type cannot carry transparency.
        /// </summary>
        internal static bool TryDecode(byte[] png, out byte[] rgb, out byte[] alpha,
                                       out int width, out int height)
        {
            rgb = null;
            alpha = null;
            width = 0;
            height = 0;

            if (!PngImageDecoder.TryDecode(png, out var decoded)) return false;

            width = decoded.Width;
            height = decoded.Height;
            rgb = decoded.Rgb;
            alpha = decoded.Alpha;
            return true;
        }

        /// <summary>Re-compresses raw bytes as a PDF FlateDecode (zlib) stream.</summary>
        internal static byte[] FlateCompress(byte[] data)
            // Core's encoder produces the same zlib wrapping (2-byte header + deflate + Adler-32) at
            // the same Optimal level, and works on both legs. Builder's image path already uses it.
            => FlateEncodeFilter.Encode(data, CompressionLevel.Optimal);
    }
}
