// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Handles the CCITTFaxDecode stream filter per ISO 32000 §7.4.6.
    /// Pure-managed T.4 (Group 3 1D) and T.6 (Group 4 MMR) implementations live
    /// in <see cref="CcittFaxDecoder"/>; this class just reads /DecodeParms and
    /// dispatches.  On any decode failure we fall back to returning the raw
    /// compressed bytes — matches the previous passthrough behaviour so callers
    /// that plug in an external CCITT library still work.
    ///
    /// /DecodeParms keys honoured:
    ///   /K                -1 → Group 4 (T.6 MMR);  0 → Group 3 1D;  >0 → Group 3 mixed (treated as 1D)
    ///   /Columns          default 1728
    ///   /Rows             default 0 (open — rely on EOFB / RTC)
    ///   /BlackIs1         default false (0 = black, 1 = white in output)
    ///   /EncodedByteAlign default false (G3 only — byte-align each line)
    ///   /EndOfBlock       default true  (stop on EOFB or 6 EOLs)
    ///   /EndOfLine        default false (G3 only — require EOL at line start)
    /// </summary>
    public sealed class CcittFaxDecodeFilter : StreamFilter
    {
        /// <summary>
        /// Why the most recent <see cref="Decode"/> call fell back to raw passthrough, or
        /// <c>null</c> if it decoded.
        ///
        /// <para><b>A non-null value means the returned array is compressed input, not pixels.</b>
        /// A renderer that treats it as samples draws noise — or, when the length happens to match
        /// the packed-1-bit stride, a plausible-looking bitmap of garbage. This slot tells the two
        /// apart; <c>Jbig2DecodeFilter.LastFallbackReason</c> is the same contract.</para>
        /// </summary>
        /// <remarks>
        /// <c>[ThreadStatic]</c>, as every <c>Last*</c> diagnostic in this tree must be — read it on
        /// the thread that ran the decode.
        /// </remarks>
        [ThreadStatic]
        private static string _lastFallbackReason;
        public static string LastFallbackReason => _lastFallbackReason;

        /// <summary>
        /// Set when at least one scanline of the most recent decode could not be decoded and was
        /// padded to full width instead; <c>null</c> when every row decoded cleanly.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="LastFallbackReason"/>, which means "the returned array is the
        /// raw input, not a bitmap". This one means the returned array IS a bitmap and some of its
        /// rows were reconstructed — a full-width scanline with the wrong pixels in it, which is
        /// otherwise indistinguishable from a clean decode. Read on the thread that decoded.
        /// </remarks>
        public static string LastRowRecovered => CcittFaxDecoder.LastRowRecovered;

        /// <summary>The /DecodeParms entries, with ISO 32000's defaults.</summary>
        private readonly struct Parameters
        {
            public readonly int K, Columns, Rows;
            public readonly bool BlackIs1, EncodedByteAlign, EndOfBlock, EndOfLine;

            public Parameters(PdfDictionary parameters)
            {
                K = 0; Columns = 1728; Rows = 0;
                BlackIs1 = false; EncodedByteAlign = false; EndOfBlock = true; EndOfLine = false;
                if (parameters == null) return;

                // IntValue saturates: `(int)LongValue` wrapped /Columns 2^32 + 8 to 8 and decoded a
                // declared four-billion-column image as an eight-column one. Saturated,
                // the value meets the cap below and is refused by name.
                if (parameters[PdfName.K]                is PdfNumber  kn) K                = kn.IntValue;
                if (parameters[PdfName.Columns]          is PdfNumber  cn) Columns          = cn.IntValue;
                if (parameters[PdfName.Rows]             is PdfNumber  rn) Rows             = rn.IntValue;
                if (parameters[PdfName.BlackIs1]         is PdfBoolean bi) BlackIs1         = bi.Value;
                if (parameters[PdfName.EncodedByteAlign] is PdfBoolean eb) EncodedByteAlign = eb.Value;
                if (parameters[PdfName.EndOfBlock]       is PdfBoolean eob) EndOfBlock      = eob.Value;
                if (parameters[PdfName.EndOfLine]        is PdfBoolean eol) EndOfLine       = eol.Value;
            }
        }

        /// <summary>
        /// Decodes the coded data that starts at <paramref name="start"/> of <paramref name="data"/> and
        /// lies within <paramref name="count"/> bytes, and says where it ended (<paramref name="end"/>):
        /// past the last of /Rows rows, or the EOFB (Group 4) or RTC (Group 3) that ends it, rounded up to
        /// a byte — what an inline data block needs, its end being in the coded data, as
        /// <see cref="FlateDecodeFilter.DecodeEmbedded"/> says for Flate. Null, with <paramref name="end"/>
        /// −1, where the data does not say: no /Rows and no end-of-block code inside the count, or
        /// /EndOfBlock false with no /Rows. Throws where the data cannot be decoded, or decodes to more
        /// than <paramref name="maxOutputBytes"/>.
        /// </summary>
        /// <param name="data">The bytes the block is in.</param>
        /// <param name="start">Where the block begins.</param>
        /// <param name="count">How many bytes it may occupy.</param>
        /// <param name="parameters">The filter's /DecodeParms, or null for the defaults.</param>
        /// <param name="maxOutputBytes">The most decoded bytes accepted.</param>
        /// <param name="end">The byte after the block, or −1.</param>
        public static byte[] DecodeEmbedded(byte[] data, int start, int count, PdfDictionary parameters,
                                            long maxOutputBytes, out int end)
        {
            end = -1;
            if (data == null || start < 0 || count < 0 || (long)start + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "the block lies outside the data");
            var p = new Parameters(parameters);
            if (p.Columns <= 0 || p.Columns > PdfLimits.MaxCcittColumns)
                throw new System.IO.InvalidDataException($"CCITTFaxDecode: /Columns {p.Columns} is outside 1 to {PdfLimits.MaxCcittColumns}");
            if (p.Rows <= 0 && !p.EndOfBlock) return null;

            // The window alone, so the decoder cannot read past what the block may occupy.
            var window = new byte[count];
            Array.Copy(data, start, window, 0, count);
            int endOffset;
            bool terminated;
            byte[] decoded = p.K < 0
                ? CcittFaxDecoder.DecodeGroup4(window, 0, p.Columns, p.Rows, p.EndOfBlock, out _, out endOffset, out terminated)
                : p.K == 0
                    ? CcittFaxDecoder.DecodeGroup3_1D(window, 0, p.Columns, p.Rows, p.EndOfBlock, p.EncodedByteAlign, p.EndOfLine,
                                                      out _, out endOffset, out terminated)
                    : CcittFaxDecoder.DecodeGroup3_2D(window, 0, p.Columns, p.Rows, p.EndOfBlock, p.EncodedByteAlign,
                                                      out _, out endOffset, out terminated);
            if (!terminated || endOffset > count) return null;
            if (decoded.LongLength > maxOutputBytes)
                throw new System.IO.InvalidDataException($"CCITTFaxDecode: output exceeded {maxOutputBytes} bytes");
            if (!p.BlackIs1) CcittFaxDecoder.InvertBytes(decoded);
            end = start + endOffset;
            return decoded;
        }

        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            _lastFallbackReason = null;
            if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();

            var p = new Parameters(parameters);
            int  k                = p.K;
            int  columns          = p.Columns;
            int  rows             = p.Rows;
            bool blackIs1         = p.BlackIs1;
            bool encodedByteAlign = p.EncodedByteAlign;
            bool endOfBlock       = p.EndOfBlock;
            bool endOfLine        = p.EndOfLine;

            if (columns <= 0)
            {
                _lastFallbackReason = $"/Columns {columns} is not positive";
                return data;
            }
            // Upper-bound /Columns: the decoder allocates a byte[] per scanline
            // (reference + current) sized by columns, for up to 65535 rows. An
            // adversarial /Columns near int.MaxValue would otherwise drive huge
            // per-line allocations. Real fax images are well under this.
            if (columns > PdfLimits.MaxCcittColumns)
            {
                _lastFallbackReason =
                    $"/Columns {columns} exceeds the {PdfLimits.MaxCcittColumns:N0} safety cap";
                return data;
            }

            byte[] decoded;
            try
            {
                // Output is sized by /Columns and the rows the data names, not by its bytes (a row of
                // one V0 code is a whole row), so each row is charged to the caller's budget — a
                // page's image decoding — before it is decoded.
                if (k < 0)
                {
                    decoded = CcittFaxDecoder.DecodeGroup4(data, columns, rows, endOfBlock, out _, Work);
                }
                else if (k == 0)
                {
                    decoded = CcittFaxDecoder.DecodeGroup3_1D(
                        data, columns, rows, endOfBlock, encodedByteAlign, endOfLine, out _, Work);
                }
                else
                {
                    // /K > 0 → mixed 1D/2D; each line's tag bit selects encoding.
                    decoded = CcittFaxDecoder.DecodeGroup3_2D(
                        data, columns, rows, endOfBlock, encodedByteAlign, out _, Work);
                }

                // Internal decoder uses "1 = black".  PDF default /BlackIs1 false means
                // the rendered bitmap treats 1 as white.  Invert unless BlackIs1 is on.
                if (!blackIs1) CcittFaxDecoder.InvertBytes(decoded);
            }
            catch (WorkBudgetExceededException ex) when (Work != null && ex.Budget == Work)
            {
                // The caller's budget refused, not this stream: the caller's refusal to report, never
                // turned into the raw-bytes fallback the caller would read as this stream failing.
                throw;
            }
            catch (Exception ex)
            {
                // Any decode failure → return raw compressed bytes so the caller's
                // downstream code path (e.g. external image library) can still try.
                _lastFallbackReason = ex.GetType().Name + ": " + ex.Message;
                return data;
            }

            // Outside the catch deliberately: it converts ANY exception into a raw-bytes
            // passthrough, so a ceiling refusal thrown inside would reach the caller as
            // "decode failed, here are the compressed bytes" rather than "too big".
            if (decoded.LongLength > MaxOutputBytes)
                throw new System.IO.InvalidDataException(
                    $"CCITTFaxDecode: output exceeded {MaxOutputBytes} bytes");

            return decoded;
        }
    }
}
