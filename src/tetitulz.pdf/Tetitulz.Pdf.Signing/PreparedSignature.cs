// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Everything needed to finish a signature that was started elsewhere — the output of
    /// <see cref="PdfDeferredSigner.Prepare"/> and the input to
    /// <see cref="PdfDeferredSigner.Complete"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Plain data on purpose.</b> Longs, ints, strings, byte arrays and one
    /// <see cref="DateTimeOffset"/> only — no streams,
    /// no delegates, no BouncyCastle types — because the whole point is that this survives a
    /// process or request boundary. That is the one thing <see cref="Models.IExternalSigner"/>
    /// cannot do: an awaited callback still holds the half-written output open inside a single
    /// <c>Sign</c> call, so it reaches an HSM client in the same process and still cannot reach a
    /// browser, a CSC service, or a stateless web request.</para>
    ///
    /// <para>Serialise it with whatever the host already uses. The fields are deliberately dull.</para>
    /// </remarks>
    public sealed class PreparedSignature
    {
        internal PreparedSignature(string fieldName, string digestAlgorithm,
                                   long contentsStart, long contentsEnd,
                                   int reservedBytes, long[] byteRange, byte[] signedBytes,
                                   string subFilter, DateTimeOffset? signingTime)
        {
            FieldName = fieldName;
            DigestAlgorithm = digestAlgorithm;
            ContentsStart = contentsStart;
            ContentsEnd = contentsEnd;
            ReservedBytes = reservedBytes;
            ByteRange = byteRange;
            SignedBytes = signedBytes;
            SubFilter = subFilter;
            SigningTime = signingTime;
        }

        /// <summary>The signature field this prepared document carries.</summary>
        public string FieldName { get; }

        /// <summary>
        /// The prepared signature dictionary's <c>/SubFilter</c> — <c>adbe.pkcs7.detached</c> or
        /// <c>ETSI.CAdES.detached</c>, read back from the document, since a seed value can decide it.
        /// </summary>
        /// <remarks>
        /// It decides whether the CMS may carry a <c>signing-time</c> attribute: a PAdES one must not
        /// (ETSI EN 319 142-1 §6.3, Table 1).
        /// </remarks>
        public string SubFilter { get; }

        /// <summary>
        /// The time the prepared signature dictionary's <c>/M</c> claims, or <see langword="null"/>
        /// when it holds none that reads as a date.
        /// </summary>
        /// <remarks>
        /// A CMS built for an <c>adbe.pkcs7.detached</c> signature carries the same instant as its
        /// <c>signing-time</c> attribute, so the document makes one claim, not two.
        /// </remarks>
        public DateTimeOffset? SigningTime { get; }

        /// <summary>The digest algorithm the CMS must use, as named in the signature dictionary.</summary>
        public string DigestAlgorithm { get; }

        /// <summary>
        /// Byte offset in the prepared document of the first hex digit inside
        /// <c>/Contents &lt;…&gt;</c>.
        /// </summary>
        public long ContentsStart { get; }

        /// <summary>Byte offset of the closing <c>&gt;</c>.</summary>
        public long ContentsEnd { get; }

        /// <summary>
        /// How many bytes of CMS the placeholder can hold. A CMS longer than this cannot be written
        /// without moving every byte after it, which would invalidate <see cref="ByteRange"/>.
        /// </summary>
        public int ReservedBytes { get; }

        /// <summary>The four <c>/ByteRange</c> values, already final in the prepared document.</summary>
        public long[] ByteRange { get; }

        /// <summary>
        /// The exact bytes the signature covers — everything but the <c>/Contents</c> hole.
        /// </summary>
        /// <remarks>
        /// <para><b>The bytes, not a digest.</b> A CMS with signed attributes does not sign the
        /// content directly: it signs the DER of the attribute set, one of whose members is the
        /// digest of this. So a signing service needs either these bytes or a digest computed with
        /// the algorithm it is going to name — and handing over a digest computed here would fix
        /// that choice before the service has been asked. <c>CertStoreExternalSigner</c> documents
        /// the same trap from the other side: <c>SignedCms</c> refuses a bare digest precisely
        /// because it must compute its own to build a matching <c>messageDigest</c> attribute.</para>
        ///
        /// <para>This is the whole document minus the hole, so it is roughly the file's size, and
        /// it is <b>not</b> what should cross a request boundary.
        /// <see cref="Cms.CmsDeferredSignatureBuilder.GetSignedAttributes(PreparedSignature, Org.BouncyCastle.X509.X509Certificate, string)"/> takes it and returns
        /// the few hundred bytes that should: the attribute DER a raw signing service actually has
        /// to sign.</para>
        /// </remarks>
        public byte[] SignedBytes { get; }
    }
}
