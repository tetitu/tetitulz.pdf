// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    public abstract class StreamFilter
    {
        /// <summary>
        /// Ceiling on the bytes this filter may produce for one <see cref="Decode"/> call.
        /// Defaults to <see cref="PdfLimits.MaxDecompressedBytes"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a settable property rather than a parameter.</b> <c>Decode</c> is
        /// <c>public abstract</c>, so adding an argument to it would break every implementor
        /// outside this assembly. A property with a default is additive.</para>
        ///
        /// <para><b>What it buys.</b> A caller that already knows it will refuse anything over
        /// <i>n</i> bytes would otherwise inflate the whole stream to find out how big it is —
        /// a filtered stream that declares no <c>/DL</c> cannot be sized any other way, which is
        /// what a compression filter <i>is</i>. Passing its own ceiling down makes the transient
        /// allocation the caller's number instead of Core's 512 MB one. The expanding filters —
        /// Flate, LZW, RunLength, <b>ASCII85</b>, <b>CCITTFax</b> and <b>JBIG2</b> — consult it.
        /// DCT, JPX and Crypt are <c>return data</c> passthroughs and ASCIIHex contracts 2:1, so
        /// those four are correctly absent.</para>
        ///
        /// <para><b>The two image codecs are in the list.</b> Both decode to a packed 1-bpp bitmap
        /// sized by the <i>parameters</i>, not by the input: unchecked, 1 000 bytes of G4 with
        /// <c>/Columns 8000 /Rows 8000</c> would produce 8 000 000 bytes against a 1 MiB ceiling,
        /// silently. Each already bounds its own transient
        /// allocation (CCITT 64 Mi pixels / 200k rows; JBIG2 2^26 pixels), so the overrun is capped
        /// near 8 MiB and this is a broken <i>contract</i> rather than a DoS — but a caller sizing
        /// its ceiling from <c>/DL</c> would be overrun by 8x with no exception. Their checks sit
        /// <b>outside</b> each filter's catch-all, which turns any exception into a raw-bytes
        /// passthrough and would otherwise report a refusal as a decode failure.</para>
        ///
        /// <para><b>ASCII85 belongs in that list.</b> "The rest cannot outgrow their input by enough
        /// to matter" is true of ASCIIHexDecode (two characters produce one byte) and false of
        /// ASCII85Decode: a full group
        /// contracts 5 to 4, but <c>z</c> is one character producing four bytes. A stream of nothing
        /// but <c>z</c> is a 4x amplification, which would overrun both the caller's ceiling and Core's own
        /// — silently, since a filter that never consults the limit never throws. A ceiling that is
        /// documented but unenforced reads exactly like one that holds.</para>
        ///
        /// <para><b>It bounds work, it does not certify size.</b> Exceeding it throws, exactly as
        /// the global limit does — a truncated buffer returned as if it were the whole stream is
        /// the failure mode this codebase refuses everywhere else (see
        /// <c>ContentScanResult.LimitExceeded</c>, which returns no tokens rather than some).</para>
        /// </remarks>
        public long MaxOutputBytes { get; set; } = PdfLimits.MaxDecompressedBytes;

        /// <summary>
        /// A budget the caller's whole operation spends — a page's image decoding — which a filter
        /// whose own work budget is sized by the stream's parameters rather than its bytes (JBIG2)
        /// gives its own budget as parent, so a page of streams that each spend their whole budget
        /// is bounded as a page. Null for a decode with no larger operation around it. A property,
        /// as <see cref="MaxOutputBytes"/> is, for the same reason.
        /// </summary>
        public Util.WorkBudget Work { get; set; }

        public abstract byte[] Decode(byte[] data, PdfDictionary parameters);

        /// <summary>
        /// The <c>(data, start, count)</c> run every <c>DecodeEmbedded</c> / <c>FindEnd</c> takes —
        /// a stream lying inside a longer byte run — refused before a byte of it is read.
        /// </summary>
        internal static void CheckEmbeddedRange(byte[] data, int start, int count)
        {
            if (data == null) throw new System.ArgumentNullException(nameof(data));
            if (start < 0 || start > data.Length)
                throw new System.ArgumentOutOfRangeException(nameof(start), start, "The start lies outside the data.");
            if (count < 0 || count > data.Length - start)
                throw new System.ArgumentOutOfRangeException(nameof(count), count, "The count runs past the end of the data.");
        }
    }
}
