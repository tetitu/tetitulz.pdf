// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// A signature revision planned as objects — the output of <see cref="PdfSigner.Plan"/>, the
    /// input to <see cref="PdfSigner.Complete"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a signature is ever planned rather than written.</b>
    /// <see cref="PdfSigner.Sign(System.IO.Stream, System.IO.Stream, SignatureParameters)"/>
    /// writes its revision as raw bytes into the caller's output, which is right for a plaintext
    /// document and impossible for an encrypted one: there the appended objects must be encrypted
    /// under the document's own file key, which lives in <c>Tetitulz.Pdf.Encryption</c> and reaches a
    /// writer only as <see cref="IPdfObjectEncryptor"/> through <c>IncrementalUpdateWriter</c>. So
    /// the revision is handed over as <see cref="IndirectObject"/>s — the shape every encrypted
    /// in-place edit in this library takes — with a zero-filled <c>/Contents</c> and a fixed-width
    /// <c>/ByteRange</c>, and once a writer has put them in a file, <see cref="PdfSigner.Complete"/>
    /// locates the two placeholders, digests the file around the first and writes the CMS into it.</para>
    ///
    /// <para><b>What travels between the two halves is here, and only here.</b> The decisions a
    /// signature rests on — the certificate, the sub-filter and digest a seed value allowed, the
    /// reserve the placeholder was sized from — are settled by <c>Plan</c> before a byte is written
    /// and read by <c>Complete</c> from this object, never re-derived. Two derivations of the reserve
    /// would let a CMS that fits the hole be refused, or one that does not be written over it.</para>
    ///
    /// <para><b>Nothing in this object describes a file position.</b> A writer decides where the
    /// objects land; <c>Complete</c> reads that back out of the finished file's cross-reference
    /// table, so a plan can be appended by any writer that honours <see cref="IndirectObject"/>
    /// numbers.</para>
    /// </remarks>
    public sealed class SignaturePlan
    {
        internal SignaturePlan(
            IReadOnlyList<IndirectObject> objects,
            string fieldName,
            int signatureObjectNumber,
            int nextObjectNumber,
            int reserveBytes,
            SeedValueConstraints.Choices choices,
            CertificateData certificate,
            IExternalSigner? externalSigner,
            SignatureParameters parameters,
            DateTime signingTime)
        {
            SigningTime = signingTime;
            Objects = objects;
            FieldName = fieldName;
            SignatureObjectNumber = signatureObjectNumber;
            NextObjectNumber = nextObjectNumber;
            ReserveBytes = reserveBytes;
            Choices = choices;
            Certificate = certificate;
            ExternalSigner = externalSigner;
            Parameters = parameters;
        }

        /// <summary>
        /// The revision: the signature dictionary with its two placeholders, the signature field
        /// (new, or the adopted one superseded with a <c>/V</c>) and the catalog naming the field.
        /// </summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>
        /// The field the signature is written into — the caller's name unless it collided, in which
        /// case the renamed one (see <c>SignatureFieldRegistration.UniqueName</c>).
        /// </summary>
        public string FieldName { get; }

        /// <summary>The signature dictionary's object number, which <see cref="PdfSigner.Complete"/> finds it by.</summary>
        public int SignatureObjectNumber { get; }

        /// <summary>
        /// One past the highest object number the plan allocated — the value an appended revision's
        /// trailer <c>/Size</c> must be at least.
        /// </summary>
        public int NextObjectNumber { get; }

        /// <summary>How many bytes of CMS the <c>/Contents</c> placeholder holds.</summary>
        public int ReserveBytes { get; }

        /// <summary>
        /// Given the finished CMS and the digest of the bytes the signature covers, returns the CMS
        /// to actually write — ISO/TS 32004 §6.5.2's seam, and nothing else uses it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the hook is here and not in the caller.</b> A PDF MAC token for a signed
        /// revision must carry <c>dataDigest</c> (the bytes the signature's <c>/ByteRange</c>
        /// indicates) and <c>signatureDigest</c> (the SignerInfo's signature value), and then be
        /// affixed to that same SignerInfo as an unsigned attribute. Only
        /// <see cref="PdfSigner.Complete"/> holds all three things at once — the byte range it has
        /// just read, the CMS it has just built, and the placeholder it is about to write into.
        /// Re-reading the finished file to do this afterwards would digest a <i>different</i>
        /// document from the one the signature covers, because writing the CMS changes the file.</para>
        ///
        /// <para><b>Why enlarging the CMS here is safe.</b> An unsigned attribute is outside what
        /// the signature signs, so adding one does not invalidate it; and the CMS lives in the
        /// <c>/Contents</c> value, which <c>/ByteRange</c> excludes, so growing it moves no byte the
        /// digest covered. It must still fit the reserve, which <c>Complete</c> checks against the
        /// returned value rather than the original.</para>
        ///
        /// <para>Null on every ordinary plan. Internal because a caller who can replace the CMS can
        /// replace the signature.</para>
        /// </remarks>
        internal Func<byte[], byte[], byte[]>? CmsPostProcessor { get; set; }

        internal SeedValueConstraints.Choices Choices { get; }
        internal CertificateData Certificate { get; }
        internal IExternalSigner? ExternalSigner { get; }
        internal SignatureParameters Parameters { get; }

        /// <summary>The instant the planned dictionary's <c>/M</c> claims, which the CMS built at completion agrees with.</summary>
        internal DateTime SigningTime { get; }
    }
}
