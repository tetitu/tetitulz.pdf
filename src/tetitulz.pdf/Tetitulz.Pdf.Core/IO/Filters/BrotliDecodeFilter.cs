// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO.Brotli;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// <c>/BrotliDecode</c>: a stream compressed with Brotli (RFC 7932), the filter the PDF Association
    /// has proposed for PDF 2.0 and readers already read.
    /// </summary>
    /// <remarks>
    /// <para><b>The stream is a raw Brotli stream</b>, with no container around it, and its
    /// <c>/DecodeParms</c> take the predictor entries <c>FlateDecode</c> takes (§7.4.4.4:
    /// <c>/Predictor</c>, <c>/Colors</c>, <c>/BitsPerComponent</c>, <c>/Columns</c>), applied after
    /// decompression through the one <see cref="PredictorHelper"/>. A <c>/D</c> entry — a custom
    /// dictionary for the decompressor — is refused by name: the decompressor this library carries
    /// takes none, and decoding without it would return a plausible wrong stream.</para>
    /// <para><b>One decompressor on both target frameworks</b>, this library's own
    /// (<see cref="BrotliDecoder"/>, written from RFC 7932) — the one the WOFF 2.0 reader uses:
    /// two implementations of a decoder for hostile input, chosen by the host, would be a divergence
    /// at a security boundary.</para>
    /// <para><b>The output is bounded by the stage's ceiling</b>, as Flate's is, and refused when it
    /// passes it: a stream of a few hundred bytes can expand without limit, and the ceiling must bound
    /// the work, not the answer. The decoder allocates no window of its own.</para>
    /// </remarks>
    public sealed class BrotliDecodeFilter : StreamFilter
    {
        private const int FirstSegmentBytes = 16 * 1024;
        private const int MaxSegmentBytes = 64 * 1024;

        /// <summary>The largest array .NET allocates for bytes.</summary>
        private const long MaxArrayLength = 0x7FFFFFC7;

        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (parameters?[PdfName.D] != null)
                throw new PdfFilterException(
                    "BrotliDecode: the stream names a custom dictionary (/D), which this decoder does not take; "
                    + "decoding without it would produce the wrong bytes.");

            byte[] decompressed = Decompress(data, MaxOutputBytes);
            return PredictorHelper.Apply(decompressed, parameters);
        }

        internal static byte[] Decompress(byte[] data, long maxOutputBytes)
        {
            if (data.Length == 0) return Array.Empty<byte>();
            try
            {
                return BrotliDecoder.Decode(data, Math.Min(maxOutputBytes, MaxArrayLength));
            }
            catch (PdfFilterException ex) when (ex.Message.IndexOf("byte limit", StringComparison.Ordinal) >= 0)
            {
                throw new PdfFilterException(maxOutputBytes > MaxArrayLength
                    ? $"BrotliDecode stream exceeds the {MaxArrayLength}-byte maximum length of a .NET array."
                    : $"BrotliDecode stream exceeds the {maxOutputBytes}-byte decode limit for this call.");
            }
        }
    }
}
