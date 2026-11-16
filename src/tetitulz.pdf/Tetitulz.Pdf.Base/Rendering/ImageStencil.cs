// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// §8.9.6.2 — the two questions a 1-bit stencil mask answers: <b>is this image a stencil</b>,
    /// and <b>does its <c>/Decode</c> reverse which samples paint</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>One rule, one implementation, reached from both renderers.</b> The stencil test was
    /// open-coded at four sites — three in <c>ContentStreamInterpreter.Images</c> and one in
    /// <c>SvgContentWriter.Images</c> — and the <c>/Decode</c> test at two, and <b>none of the six
    /// followed an indirect reference</b>. §7.3.10 permits one for either key.</para>
    ///
    /// <para><b>The <c>/Decode</c> one selects the opposite result rather than losing a
    /// refinement.</b> A stencil whose <c>/Decode</c> is <c>[1 0]</c> paints exactly the samples the
    /// default <c>[0 1]</c> leaves clear. Read as absent, both renderers painted the photographic
    /// negative of the mask — identically, so nothing that compares them could tell.</para>
    ///
    /// <para><b>The <c>/ImageMask</c> one is a stride error, not just a colour one.</b> A stencil
    /// read as "not a stencil" is not painted in the fill colour, and it then takes the 8-bpc
    /// default and a 3-component colour space — where Table 90 fixes 1 bpc and Table 89 forbids a
    /// <c>/ColorSpace</c> entirely. That is 24 bits per pixel read from a buffer holding 1: the rows
    /// do not merely look wrong, they are read from the wrong offsets. Both helpers'
    /// <i>own doc comments</i> already said the 8-bpc default must never reach a mask.</para>
    ///
    /// <para><b>A leaf by construction, not by assertion: this type has no static fields</b>, so it
    /// has no type initializer. That is deliberate and load-bearing — pointing one renderer at a
    /// static on the other once crashed the Base test host part-way through a parallel run, which
    /// reported <c>Passed!</c> while silently dropping ~190 tests. The same reasoning made
    /// <c>Type3Encoding</c> its own type.</para>
    /// </remarks>
    internal static class ImageStencil
    {
        /// <summary>
        /// True when <paramref name="image"/> is a 1-bit stencil mask painted in the current fill
        /// colour (§8.9.6.2 Table 89 <c>/ImageMask</c>, or <c>/IM</c> for an inline image).
        /// </summary>
        /// <remarks>
        /// Both spellings are accepted at every call site. Table 93 defines <c>/IM</c> only for
        /// inline images, so an XObject carrying it is malformed — but taking it there costs
        /// nothing and refusing it would silently drop the image, which is the worse of the two
        /// wrong answers for a key whose meaning is unambiguous.
        /// </remarks>
        internal static bool IsStencilMask(PdfDictionary image, PdfDocument doc)
        {
            if (image == null) return false;

            PdfObject value = image[PdfName.ImageMask] ?? image[PdfName.IM];
            return PdfResolve.Value(doc, value) is PdfBoolean { Value: true };
        }

        /// <summary>
        /// True when the stencil's <c>/Decode</c> (or inline <c>/D</c>) is <c>[1 0]</c> — i.e. it
        /// reverses which samples are painted and which are masked out.
        /// </summary>
        /// <remarks>
        /// The default is <c>[0 1]</c>: sample 1 is masked, sample 0 paints. Anything that is not a
        /// two-element array of numbers leaves the default standing, so a malformed entry degrades
        /// to the spec default rather than to the inverse.
        /// </remarks>
        internal static bool DecodeInverts(PdfDictionary stencil, PdfDocument doc)
        {
            if (stencil == null) return false;

            PdfObject value = stencil[PdfName.Decode] ?? stencil[PdfName.D];
            if (PdfResolve.Value(doc, value) is not PdfArray decode || decode.Count < 2) return false;

            // Elements may be indirect too — §7.3.10 is about values, and an array element is one.
            float d0 = PdfResolve.Value(doc, decode[0]) is PdfNumber n0 ? (float)n0.DoubleValue : 0f;
            float d1 = PdfResolve.Value(doc, decode[1]) is PdfNumber n1 ? (float)n1.DoubleValue : 1f;
            return d0 > d1;
        }

        /// <summary>
        /// Decodes packed 1-bit samples into an <see cref="SKBitmap"/> — a stencil painted in
        /// <paramref name="fillColor"/>, or a bilevel greyscale image using
        /// <paramref name="v0"/>/<paramref name="v1"/>.
        /// </summary>
        /// <param name="data">The packed samples, one row per scanline.</param>
        /// <param name="w">Width in samples.</param>
        /// <param name="h">Height in samples.</param>
        /// <param name="stride">Bytes per row, <c>(w + 7) / 8</c> for the caller's geometry.</param>
        /// <param name="paintOnSetBit">
        /// Which bit paints. <c>false</c> is the §8.9.6.2 default (<c>/Decode [0 1]</c>, a 0 bit
        /// paints); <c>true</c> is the inverted sense <see cref="DecodeInverts"/> reports.
        /// </param>
        /// <param name="v0">Grey value for a clear bit. Ignored for a stencil.</param>
        /// <param name="v1">Grey value for a set bit. Ignored for a stencil.</param>
        /// <param name="isImageMask">
        /// <c>true</c> for a stencil — painted bits take <paramref name="fillColor"/> and the rest
        /// are transparent. <c>false</c> for a bilevel grey image, which is opaque throughout.
        /// </param>
        /// <param name="fillColor">The stencil's ink, alpha included. Ignored for a grey image.</param>
        /// <remarks>
        /// <para><b>One loop, reached from both renderers.</b> The raster interpreter and the SVG
        /// writer each had their own, and they were not equivalent: the SVG copy handled only the
        /// stencil case, wrote through <c>SetPixel</c> per pixel rather than one buffer assignment,
        /// and — the part that mattered — <b>had no bounds test at all</b>, so a stencil whose CCITT
        /// or JBIG2 filter had failed (both report failure by returning their compressed input)
        /// indexed straight off the end of the array. The callers keep their own length guards,
        /// which are deliberately different, but the indexing arithmetic is now written once.</para>
        ///
        /// <para><b>The callers' length guards must stay theirs.</b> The explicit
        /// <c>/BitsPerComponent 1</c> branch accepts <c>&gt;=</c> because an
        /// <c>/EncodedByteAlign</c> stencil pads every row and routinely carries trailing bytes; the
        /// last-resort fallback demands <c>==</c> because it is guessing at a format the file never
        /// declared. Merging those into one test here is the careless version of this change and
        /// breaks one direction or the other.</para>
        ///
        /// <para><b>Alpha is the caller's, and the two differ on purpose.</b> The raster path passes
        /// the graphics state's fill colour, whose alpha participates; the SVG path passes an opaque
        /// ink, because its stencil becomes a PNG whose transparency is the mask itself.</para>
        /// </remarks>
        internal static SKBitmap DecodePackedBilevel(
            byte[] data, int w, int h, int stride,
            bool paintOnSetBit, byte v0, byte v1, bool isImageMask, SKColor fillColor)
        {
            var bmp = new SKBitmap(w, h, SKColorType.Rgba8888,
                isImageMask ? SKAlphaType.Premul : SKAlphaType.Opaque);
            Images.BitmapPixels.Fill(bmp, (row, y) =>
            {
                int rowStart = y * stride;
                for (int x = 0; x < w; x++)
                {
                    bool bit = (data[rowStart + x / 8] & (0x80 >> (x % 8))) != 0;
                    if (isImageMask)
                    {
                        row[x] = bit == paintOnSetBit ? fillColor : SKColors.Transparent;
                    }
                    else
                    {
                        byte v = bit ? v1 : v0;
                        row[x] = new SKColor(v, v, v);
                    }
                }
            });
            return bmp;
        }

        /// <summary>
        /// True when a stencil <c>/Mask</c> stream's declared geometry is inside
        /// <see cref="PdfLimits.MaxImageDimension"/> on both axes and
        /// <see cref="PdfLimits.MaxImagePixelArea"/> in total. Callers keep their own
        /// non-positive test and their own refusal action.
        /// </summary>
        /// <remarks>
        /// <para><b>The two renderers disagreed about a mask nobody can allocate, and the file drew
        /// as two different pictures.</b> The raster path refused an over-cap stencil and left the
        /// base image opaque; the SVG exporter had no cap here at all, so it proceeded into its
        /// sampling loop with the mask's declared stride. For <c>/Width 200000 /Height 1</c> that is
        /// a stride of 25 000: every row of the base image maps to mask row 0, and with the mask's
        /// <c>/Decode [1 0]</c> reversing the sense, every byte past the decoded data reads as bit 0
        /// and drives the whole image to alpha 0. <b>Raster: fully opaque. SVG: fully
        /// transparent.</b> Neither said anything.</para>
        /// <para>A shared callee rather than a hand-typed clause at each site: the clause a site
        /// leaves out is the defect. Its natural home is beside the two constants in
        /// <c>Core.Constants.PdfLimits</c>, so every site outside this assembly could reach it too;
        /// it is here because both stencil sites call into this leaf.</para>
        /// </remarks>
        internal static bool MaskGeometryWithinLimits(int width, int height)
            => width <= PdfLimits.MaxImageDimension
               && height <= PdfLimits.MaxImageDimension
               && (long)width * height <= PdfLimits.MaxImagePixelArea;
    }
}
