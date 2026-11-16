// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Stream filter names (PDF 32000-1:2008, §7.4, Table 6) and their abbreviations (Table 94, for
    /// inline images) as <c>const string</c>, so they can be used as <c>switch</c> case labels —
    /// <see cref="Objects.PdfName"/> cannot, because a <c>case</c> label needs a compile-time
    /// constant.
    ///
    /// <para>The sibling of <see cref="PdfColorSpaceNames"/>, and it exists for the same reason:
    /// filter names were spelled as bare literals at a dozen sites, and the ones that mattered most
    /// were the <see cref="IsImageCodec"/> set — code that peels a filter layer has to know which
    /// remaining filters carry an encoded image rather than plain bytes, and each site had answered
    /// that question with its own hand-written list.</para>
    /// </summary>
    public static class PdfFilterNames
    {
        // ---- Table 6 names ----

        /// <summary><c>/ASCIIHexDecode</c>.</summary>
        public const string AsciiHexDecode = "ASCIIHexDecode";
        /// <summary><c>/ASCII85Decode</c>.</summary>
        public const string Ascii85Decode = "ASCII85Decode";
        /// <summary><c>/LZWDecode</c>.</summary>
        public const string LzwDecode = "LZWDecode";
        /// <summary><c>/FlateDecode</c>.</summary>
        public const string FlateDecode = "FlateDecode";
        /// <summary><c>/RunLengthDecode</c>.</summary>
        public const string RunLengthDecode = "RunLengthDecode";
        /// <summary><c>/CCITTFaxDecode</c>.</summary>
        public const string CcittFaxDecode = "CCITTFaxDecode";
        /// <summary><c>/JBIG2Decode</c>.</summary>
        public const string Jbig2Decode = "JBIG2Decode";
        /// <summary><c>/DCTDecode</c>.</summary>
        public const string DctDecode = "DCTDecode";
        /// <summary><c>/JPXDecode</c>.</summary>
        public const string JpxDecode = "JPXDecode";
        /// <summary><c>/Crypt</c>.</summary>
        public const string Crypt = "Crypt";

        // ---- Table 94 inline-image abbreviations ----

        /// <summary><c>/AHx</c> — abbreviation of <see cref="AsciiHexDecode"/>.</summary>
        public const string AsciiHexAbbrev = "AHx";
        /// <summary><c>/A85</c> — abbreviation of <see cref="Ascii85Decode"/>.</summary>
        public const string Ascii85Abbrev = "A85";
        /// <summary><c>/LZW</c> — abbreviation of <see cref="LzwDecode"/>.</summary>
        public const string LzwAbbrev = "LZW";
        /// <summary><c>/Fl</c> — abbreviation of <see cref="FlateDecode"/>.</summary>
        public const string FlateAbbrev = "Fl";
        /// <summary><c>/RL</c> — abbreviation of <see cref="RunLengthDecode"/>.</summary>
        public const string RunLengthAbbrev = "RL";
        /// <summary><c>/CCF</c> — abbreviation of <see cref="CcittFaxDecode"/>.</summary>
        public const string CcittFaxAbbrev = "CCF";
        /// <summary><c>/DCT</c> — abbreviation of <see cref="DctDecode"/>.</summary>
        public const string DctAbbrev = "DCT";

        /// <summary>
        /// True when <paramref name="filterName"/> names either spelling of FlateDecode.
        /// </summary>
        public static bool IsFlate(string filterName) =>
            filterName == FlateDecode || filterName == FlateAbbrev;

        /// <summary>
        /// True when <paramref name="filterName"/> names either spelling of LZWDecode.
        /// </summary>
        public static bool IsLzw(string filterName) =>
            filterName == LzwDecode || filterName == LzwAbbrev;

        /// <summary>
        /// True when <paramref name="filterName"/> names either ASCII transport filter, in either
        /// spelling. These carry no image data of their own — they wrap whatever follows them.
        /// </summary>
        public static bool IsAscii(string filterName) =>
            filterName == Ascii85Decode || filterName == Ascii85Abbrev
            || filterName == AsciiHexDecode || filterName == AsciiHexAbbrev;

        /// <summary>
        /// True when <paramref name="filterName"/> names a filter whose decoded output is an
        /// <b>encoded image</b> rather than plain sample bytes — DCT, JPX, JBIG2 and CCITT.
        ///
        /// <para><b>This is the predicate that keeps a filter-peeling pass from destroying an
        /// image.</b> A stream written as <c>/Filter [/ASCII85Decode /DCTDecode]</c> is a JPEG
        /// wrapped in ASCII transport: stripping the ASCII layer is safe and worthwhile, but the
        /// remaining <c>/DCTDecode</c> must stay on the stream. Dropping the whole array — which is
        /// what a "read entry 0, then remove <c>/Filter</c>" pass does — leaves the JPEG bitstream
        /// declared as raw samples, and every reader paints noise. The damage is unrecoverable
        /// because the original encoding is gone.</para>
        ///
        /// <para>Note that only <c>DCTDecode</c> and <c>JPXDecode</c> are <i>pass-throughs</i> in
        /// this library's filter pipeline; <c>JBIG2Decode</c> and <c>CCITTFaxDecode</c> genuinely
        /// decode to samples. All four are grouped here anyway, because a caller peeling a layer
        /// wants to know "is there still a codec below me?" — the peeling pass must not treat a
        /// codec's output as plain sample bytes, whether or not this library can re-encode it.</para>
        ///
        /// <para>The CCITT grouping is not about having no encoder:
        /// <see cref="Tetitulz.Pdf.Core.IO.Filters.CcittFaxEncoder"/> is one, and the optimizer's
        /// <c>RecompressBilevelImagesAsCcitt</c> (<c>OptimizationOptions.UseCcittForBilevelImages</c>)
        /// does the deliberate re-encode in the other direction.</para>
        /// </summary>
        public static bool IsImageCodec(string filterName) =>
            filterName == DctDecode || filterName == DctAbbrev
            || filterName == JpxDecode
            || filterName == Jbig2Decode
            || filterName == CcittFaxDecode || filterName == CcittFaxAbbrev;
    }
}
