// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Parsing
{
    /// <summary>
    /// Whether a syntax stream — an object stream or a cross-reference stream — names an image
    /// codec among its filters, which the parser refuses before any decode.
    /// </summary>
    /// <remarks>
    /// <para>An image codec's output is samples: a bitmap from JBIG2 or CCITT, a JPEG or JPEG 2000
    /// codestream passed through for an image decoder. No reading of samples is the object syntax
    /// an object stream holds (ISO 32000-1 §7.5.7) or the entries a cross-reference stream holds
    /// (§7.5.8), so a syntax stream naming one cannot be read whatever the codec answers — and
    /// asking costs the codec's whole budget (a JBIG2 generic region is sized by its header, not
    /// its bytes) at open, before any caller can decline, once for every open of the document.
    /// The parser refuses the stream by name instead; the document's syntax-stream budget
    /// (<see cref="PdfParser.SyntaxStreamWork"/>) bounds the codecs a syntax stream may carry.</para>
    /// <para>The names are the four image filters of ISO 32000-1 Table 6 and the two inline-image
    /// abbreviations <see cref="IO.Filters.FilterPipeline"/> accepts on any stream. A <c>/Filter</c>
    /// that is neither a name nor an array of names is left to the pipeline, which refuses it.</para>
    /// </remarks>
    internal static class SyntaxStreamFilters
    {
        /// <summary>The first image codec <paramref name="stream"/>'s <c>/Filter</c> names, or <c>null</c>.</summary>
        internal static string ImageCodecOf(PdfStream stream)
        {
            PdfObject filter = stream[PdfName.Filter];
            if (filter is PdfName name) return IsImageCodec(name) ? name.Value : null;
            if (filter is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                    if (array[i] is PdfName element && IsImageCodec(element)) return element.Value;
            }
            return null;
        }

        private static bool IsImageCodec(PdfName name)
            => name == PdfName.JBIG2Decode || name == PdfName.JPXDecode
            || name == PdfName.DCTDecode || name == PdfName.DCT
            || name == PdfName.CCITTFaxDecode || name == PdfName.CCF;
    }
}
