// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Handles the JBIG2Decode stream filter per ISO 32000 §7.4.7.
    ///
    /// JBIG2 (ITU-T T.88 / ISO/IEC 14492) is a segment-based monochrome codec.
    /// A fully correct decoder covers generic / generic-refinement / text /
    /// halftone / pattern regions on top of a context-adaptive MQ arithmetic
    /// coder and symbol dictionaries — genuinely weeks of work to do right.
    ///
    /// This filter decodes what <see cref="Jbig2Decoder"/> implements: the segment framework and
    /// page information segment, generic regions in both <b>MMR (T.6)</b> and arithmetic coding,
    /// refinement regions, symbol dictionaries and text regions in the <b>arithmetic</b> coding
    /// mode, and pattern dictionaries with halftone regions.
    ///
    /// <para><b>The Huffman coding mode (SDHUFF=1 / SBHUFF=1) is not implemented</b>, and both
    /// halves refuse rather than producing something plausible — see the remarks on
    /// <c>Jbig2Decoder</c>.</para>
    ///
    /// <para><b>Failure is a silent passthrough, and callers must treat it as
    /// one.</b> Any decode error returns the <i>raw compressed bytes</i> so a
    /// host can hand the blob to an external library — but a renderer that
    /// treats the result as pixels draws noise. Check
    /// <see cref="LastFallbackReason"/> after every call; a non-null value means
    /// the returned array is not a bitmap. See GAPS.md §2.</para>
    ///
    /// /JBIG2Globals (when supplied via /DecodeParms) is concatenated before
    /// the stream data so referenced segments resolve in a single pass.
    ///
    /// Output is a packed 1-bit-per-pixel bitmap, MSB-first, in the <b>PDF sample</b> convention:
    /// <b>0 = black</b>, so it feeds DeviceGray (§8.9.5.2) and /ImageMask (§8.9.6.2) directly.
    /// T.88's internal bitmaps use the opposite polarity (1 = black) and <see cref="Jbig2Decoder"/>
    /// still speaks it; this filter inverts on the way out. <c>/BlackIs1</c> does NOT apply to
    /// JBIG2 — it is a CCITTFaxDecode parameter.
    ///
    /// <para>The output does not keep T.88's polarity, which would render every JBIG2 image in
    /// negative — a blank page painted solid black. The inversion belongs to the filter because nothing downstream can tell a JBIG2-sourced 1-bpc
    /// image from any other.</para>
    /// </summary>
    public sealed class Jbig2DecodeFilter : StreamFilter
    {
        /// <summary>
        /// Reason the most recent <see cref="Decode"/> call fell back to raw
        /// passthrough, or <c>null</c> if it succeeded.  Useful for diagnosing
        /// "image is garbage" reports — exposes the exact decoder error message
        /// instead of leaving callers guessing.
        /// </summary>
        [ThreadStatic]
        private static string _lastFallbackReason;
        public static string LastFallbackReason => _lastFallbackReason;

        /// <summary>
        /// Set when <c>/JBIG2Globals</c> could not be used, <b>whether or not the decode then
        /// succeeded</b>; <c>null</c> otherwise. A non-null value here with a <c>null</c>
        /// <see cref="LastFallbackReason"/> means the returned array IS a bitmap, decoded without
        /// the referenced symbol dictionary — correct for a self-contained page, and missing every
        /// text region for a page that needed it.
        /// </summary>
        /// <remarks>
        /// This exists because the globals failure written straight into
        /// <see cref="LastFallbackReason"/>, then falling through to a decode that can succeed,
        /// would leave the slot non-null beside a real bitmap and invert the documented contract —
        /// "a non-null value means the returned array is not a bitmap". A caller following that
        /// contract discarded a valid image. "Globals were dropped" and "these bytes are not
        /// pixels" are different facts and need different slots.
        /// </remarks>
        public static string LastGlobalsWarning => _lastGlobalsWarning;

        [ThreadStatic]
        private static string _lastGlobalsWarning;

        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            _lastFallbackReason = null;
            _lastGlobalsWarning = null;
            if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();

            // /JBIG2Globals — optional stream of segments that this stream may refer to.
            byte[] globals = null;
            if (parameters != null)
            {
                var globalsObj = parameters[PdfName.JBIG2Globals];
                if (globalsObj is PdfStream gs)
                {
                    try { globals = DecodeGlobals(gs); }
                    catch (WorkBudgetExceededException ex) when (Work != null && ex.Budget == Work)
                    {
                        // The caller's budget refused the globals: the caller's refusal, as below.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _lastGlobalsWarning = "Failed to decode /JBIG2Globals stream: " + ex.Message;
                        globals = null;
                    }
                }
                else if (globalsObj != null)
                {
                    // Present but not a stream — almost always an unresolved PdfReference, since a
                    // PDF stream can only exist as an indirect object and this dictionary's indexer
                    // does not follow one. PdfParser resolves it while parsing; a parameters
                    // dictionary built by hand, or reaching here by another route, will not have
                    // been through that. Decoding without the symbol dictionary yields a BLANK page
                    // indistinguishable from success, so say so.
                    _lastGlobalsWarning =
                        "/JBIG2Globals is present but did not resolve to a stream (" +
                        globalsObj.GetType().Name + "); the referenced symbol dictionary is " +
                        "unavailable, so any text region in this stream will decode to nothing.";
                }
            }

            byte[] bitmap;
            try
            {
                bitmap = Jbig2Decoder.Decode(data, globals, Work, out _, out _);

                // §7.4.7 output is consumed as ordinary 1-bit image samples: DeviceGray 0 is
                // black, the default /Decode is [0 1] (§8.9.5.2), and an /ImageMask paints where
                // the sample is 0 (§8.9.6.2). T.88 bitmaps are the opposite — a 1 bit is a black
                // (marked) pixel — so the conversion is owed HERE, at the PDF boundary, and not by
                // the two renderers, which cannot tell one 1-bpc image from another.
                //
                // Without it every JBIG2 image renders in negative: a blank page (an all-zero T.88
                // bitmap, which is what a page-information segment alone produces) paints SOLID
                // BLACK, and a scan comes out white-on-black. Every other reader inverts at exactly
                // this point — other filters invert each byte. The
                // passthrough below is deliberately NOT inverted: those bytes are the compressed
                // input, handed on for an external JBIG2 library, not pixels.
                //
                // No escape hatch emits the inverted polarity: a compatibility flag for a defect is
                // how the defect comes back, and there is no correct reason to take such a branch.
                for (int i = 0; i < bitmap.Length; i++)
                    bitmap[i] = (byte)~bitmap[i];
            }
            catch (WorkBudgetExceededException ex) when (Work != null && ex.Budget == Work)
            {
                // The caller's budget — a page's image decoding — refused, not this stream's: that
                // is the caller's refusal to report, so it is not turned into a raw-bytes fallback
                // that the caller would read as this stream failing on its own.
                throw;
            }
            catch (Exception ex)
            {
                // Any decode failure — unsupported segment type, malformed stream,
                // unknown-length segment — return raw bytes so upstream callers can
                // still hand the blob to an external JBIG2 library.
                //
                // An unreachable /JBIG2Globals recorded above is kept in front of this: it is the
                // ROOT CAUSE and this is very often its symptom, since a stream whose symbol
                // dictionary is missing fails in ways that look like a malformed stream. Reporting
                // only the symptom sends the reader to the wrong investigation.
                string decodeFailure = ex.GetType().Name + ": " + ex.Message;
                _lastFallbackReason = _lastGlobalsWarning == null
                    ? decodeFailure
                    : _lastGlobalsWarning + " Decoding then failed: " + decodeFailure;
                return data;
            }

            // Outside the catch deliberately: it converts ANY exception into a raw-bytes
            // passthrough, so a ceiling refusal thrown inside would reach the caller as
            // "decode failed, here are the compressed bytes" rather than "too big".
            if (bitmap.LongLength > MaxOutputBytes)
                throw new System.IO.InvalidDataException(
                    $"JBIG2Decode: output exceeded {MaxOutputBytes} bytes");

            return bitmap;
        }

        /// <summary>
        /// The <c>/JBIG2Globals</c> stream's segments, decoded under this filter's own ceiling and
        /// the caller's budget (<see cref="StreamFilter.MaxOutputBytes"/>, <see cref="StreamFilter.Work"/>):
        /// a globals stream is one more stream of the image's, and a decode of it outside both would
        /// let a file buy a whole budget per globals stream. Globals are segments, never samples, so
        /// one whose own <c>/Filter</c> names an image codec — a globals stream nesting another — is
        /// refused by name before anything is decoded.
        /// </summary>
        private byte[] DecodeGlobals(PdfStream globals)
        {
            string codec = Parsing.SyntaxStreamFilters.ImageCodecOf(globals);
            if (codec != null)
                throw new System.IO.InvalidDataException(
                    $"/JBIG2Globals is filtered /{codec}, an image codec; globals are segments, not samples, " +
                    "so the stream was not decoded.");

            byte[] raw = globals.RawData ?? Array.Empty<byte>();
            PdfObject filter = globals[PdfName.Filter];
            if (filter == null)
            {
                if (raw.LongLength > MaxOutputBytes)
                    throw new System.IO.InvalidDataException(
                        $"/JBIG2Globals holds {raw.LongLength} bytes, past the {MaxOutputBytes}-byte ceiling.");
                return raw;
            }
            return FilterPipeline.Decode(raw, filter, globals[PdfName.DecodeParms], MaxOutputBytes, Work);
        }
    }
}
