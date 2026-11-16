// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// The §12.8.1 <c>/ByteRange</c> arithmetic: which bytes of a file a signature covers, given
    /// where its <c>/Contents</c> hex placeholder sits.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation.</b> This is the arithmetic a signature's validity rests on, for
    /// <c>PdfSigner</c>, <c>PdfDocumentTimestamper</c>, and the two test fixtures that stand in for
    /// the timestamper (whose token would otherwise have to come from a real TSA over HTTP).</para>
    ///
    /// <para><b>One byte matters.</b> <c>ByteRange[1] = contentsStart</c> would leave the opening
    /// <c>&lt;</c> inside the signed range; the hole begins at <c>contentsStart - 1</c>. The excluded hole
    /// must be the <c>/Contents</c> <i>string object</i>, and a PDF hex string is
    /// <c>&lt;…&gt;</c>: the delimiters are part of the object, not decoration around it. A
    /// verifier that checks the hole really is the Contents string — by asserting the gap equals
    /// <c>2 × rawLength + 2</c>, or that the byte at <c>ByteRange[1]</c> is <c>&lt;</c> — rejects
    /// the one-short version.</para>
    ///
    /// <para><b>Why a round trip cannot see it.</b> The missing byte is a constant at a fixed
    /// offset, so the digest stays self-consistent: a verifier reading that <c>/ByteRange</c> back
    /// hashes exactly the bytes the producer hashed, and the signature verifies. And a fixture that
    /// models the timestamper with arithmetic of its own reaches none of the production arithmetic;
    /// the fixtures call this type.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer.</para>
    /// </remarks>
    internal readonly struct SignatureByteRange
    {
        private SignatureByteRange(long beforeLength, long afterStart, long afterLength)
        {
            BeforeLength = beforeLength;
            AfterStart = afterStart;
            AfterLength = afterLength;
        }

        /// <summary><c>/ByteRange</c> element 1 — the length of the first signed range, which
        /// starts at 0 and ends at the <c>&lt;</c> that opens <c>/Contents</c>.</summary>
        internal long BeforeLength { get; }

        /// <summary><c>/ByteRange</c> element 2 — the offset of the first byte after the
        /// <c>&gt;</c> that closes <c>/Contents</c>.</summary>
        internal long AfterStart { get; }

        /// <summary><c>/ByteRange</c> element 3 — the length of the second signed range, which
        /// runs from <see cref="AfterStart"/> to end of file.</summary>
        internal long AfterLength { get; }

        /// <summary>How many bytes the signature covers in total — the size of the buffer a caller
        /// must allocate to concatenate the two ranges before digesting them.</summary>
        internal long SignedLength => BeforeLength + AfterLength;

        /// <summary>
        /// Computes the two signed ranges around a <c>/Contents</c> hex placeholder.
        /// </summary>
        /// <param name="contentsStart">
        /// Offset of the <b>first hex digit</b> — i.e. the byte immediately after the opening
        /// <c>&lt;</c>. This is the offset a writer naturally records right after emitting
        /// <c>" /Contents &lt;"</c>, which is why every copy of this arithmetic took it as the
        /// input, and why the one that forgot to step back over the <c>&lt;</c> looked right.
        /// </param>
        /// <param name="contentsEnd">Offset of the closing <c>&gt;</c>.</param>
        /// <param name="fileLength">Total length of the finished file.</param>
        internal static SignatureByteRange Compute(long contentsStart, long contentsEnd, long fileLength)
        {
            if (contentsStart < 1)
                throw new ArgumentOutOfRangeException(nameof(contentsStart),
                    "A /Contents placeholder must be preceded by its '<', so its first hex digit " +
                    "cannot be at offset 0.");
            if (contentsEnd < contentsStart)
                throw new ArgumentOutOfRangeException(nameof(contentsEnd),
                    "The closing '>' cannot precede the first hex digit.");
            if (fileLength < contentsEnd + 1)
                throw new ArgumentOutOfRangeException(nameof(fileLength),
                    "The file ends before the /Contents placeholder closes.");

            long beforeLength = contentsStart - 1;   // ends at (and excludes) the '<'
            long afterStart = contentsEnd + 1;       // the byte after the '>'
            return new SignatureByteRange(beforeLength, afterStart, fileLength - afterStart);
        }

        /// <summary>
        /// Renders the four-element array, right-padded to <paramref name="padTo"/> so it overwrites
        /// the fixed-width placeholder in place without moving any byte after it.
        /// </summary>
        internal string Format(int padTo)
            => string.Format(
                   CultureInfo.InvariantCulture,
                   "[0 {0} {1} {2}]", BeforeLength, AfterStart, AfterLength)
               .PadRight(padTo);
    }
}
