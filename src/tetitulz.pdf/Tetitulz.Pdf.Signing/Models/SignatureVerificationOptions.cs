// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Models
{
    /// <summary>
    /// Per-call verification policy for the signature verify/validate APIs.
    /// <para>
    /// Passing an instance of this class scopes the policy to a single call. A process-wide mutable
    /// static (<see cref="Cms.CmsSignatureVerifier.RejectWeakAlgorithms"/> is the one that remains)
    /// makes verification policy racy across concurrent operations and hostile to server hosts: one
    /// request can tighten — or silently relax — the policy applied to another running at the same
    /// time.
    /// </para>
    /// <para>
    /// An options instance is immutable in practice once handed to a verifier: the verify path
    /// only reads it. Reusing one instance across threads is safe as long as the caller does not
    /// mutate it afterwards.
    /// </para>
    /// </summary>
    public sealed class SignatureVerificationOptions
    {
        /// <summary>
        /// Shared defaults used when a caller passes <c>null</c>. Do not mutate this instance —
        /// build a new <see cref="SignatureVerificationOptions"/> instead.
        /// </summary>
        public static SignatureVerificationOptions Default { get; } = new SignatureVerificationOptions();

        /// <summary>
        /// When <c>true</c> — <b>the default</b> — a weak-but-not-fully-broken
        /// algorithm or an undersized key (see <see cref="MinimumRsaKeyBits"/> /
        /// <see cref="MinimumEcKeyBits"/>) makes the signature <b>invalid</b> rather than merely
        /// setting <c>WeakAlgorithm</c>. MD5/MD2 digests are always hard-rejected regardless.
        /// </summary>
        /// <remarks>
        /// <para>A SHA-1-signed document reports <c>IsValid == false</c>. Set this to <c>false</c>
        /// for the advisory behaviour in a host that must keep accepting legacy signatures — the
        /// weakness is reported on <c>WeakAlgorithm</c> / <c>WeakAlgorithmReason</c> either
        /// way.</para>
        /// </remarks>
        public bool RejectWeakAlgorithms { get; set; } = true;

        /// <summary>
        /// Minimum acceptable RSA/DSA modulus size in bits (default 2048). A signer certificate
        /// below this is flagged as weak; it is rejected outright only when
        /// <see cref="RejectWeakAlgorithms"/> is set. Set to 0 to skip the check.
        /// </summary>
        public int MinimumRsaKeyBits { get; set; } = 2048;

        /// <summary>
        /// Minimum acceptable EC key size in bits (default 224, i.e. P-224 and up; this rejects
        /// P-192). Flagged as weak, and rejected only when <see cref="RejectWeakAlgorithms"/> is
        /// set. Set to 0 to skip the check.
        /// </summary>
        public int MinimumEcKeyBits { get; set; } = 224;

        /// <summary>
        /// Trust anchors for the signer's X.509 chain. When <c>null</c> or empty, no chain can be
        /// anchored and the chain status is reported as
        /// <see cref="PdfSignatureChainStatus.UntrustedRoot"/> or
        /// <see cref="PdfSignatureChainStatus.SelfSigned"/>.
        /// </summary>
        public IEnumerable<X509Certificate>? TrustAnchors { get; set; }

        /// <summary>
        /// Trust anchors accepted for RFC 3161 timestamp tokens. Falls back to
        /// <see cref="TrustAnchors"/> when <c>null</c>, so most callers only set one list.
        /// <para>
        /// This matters: a timestamp token carries its own TSA certificate, which is as
        /// attacker-controlled as the signer certificate in the CMS. An unanchored token can
        /// claim any <c>GenTime</c>, so honouring it would let an expired signer certificate
        /// back-date itself into its own validity window. See
        /// <see cref="RequireTrustedTimestamp"/>.
        /// </para>
        /// </summary>
        public IEnumerable<X509Certificate>? TimestampTrustAnchors { get; set; }

        /// <summary>
        /// When <c>true</c> (the default), only a timestamp token whose TSA certificate chains to
        /// <see cref="TimestampTrustAnchors"/> may move the certificate-validity assessment time
        /// away from "now". An unanchored token is reported as a warning and ignored, so an
        /// expired certificate fails closed.
        /// <para>
        /// Set to <c>false</c> only for diagnostic/offline tooling that deliberately wants to read
        /// an untrusted token's claimed time. Doing so re-opens the back-dating hole.
        /// </para>
        /// </summary>
        public bool RequireTrustedTimestamp { get; set; } = true;

        /// <summary>
        /// When <c>true</c> (the default), a timestamp token moves the certificate-validity
        /// assessment time only while its authority can still be relied on: the TSA certificate's
        /// chain is valid at the current time as well as at the token's own, or a later document
        /// timestamp whose own authority can be relied on covers the token.
        /// </summary>
        /// <remarks>
        /// <para>A token's time is the TSA's assertion, and a certificate judged only at the time
        /// its own token claims vouches for itself: the key of a TSA certificate that has since
        /// expired — no longer guarded, no longer on any revocation list — signs a token carrying
        /// any time inside the certificate's old validity period, and with it back-dates a
        /// signature made today. Long-term validation answers this with a later timestamp over
        /// the earlier one (the document timestamps of PAdES-LTA), which is what the second half
        /// of the rule accepts.</para>
        /// <para>Set to <c>false</c> to accept a token whose authority was valid at the token's
        /// time alone — an archive whose timestamps were never renewed, read by a host that
        /// trusts its own custody of the files. A revoked TSA certificate is refused either way.</para>
        /// </remarks>
        public bool RequireCurrentTimestampAuthority { get; set; } = true;

        /// <summary>
        /// When <c>true</c> (the default), a signer certificate whose extended key usage names
        /// purposes, none of them one a document is signed under, makes the chain
        /// <see cref="PdfSignatureChainStatus.Invalid"/>. A certificate with no extended key usage
        /// is good for every purpose and passes.
        /// </summary>
        /// <remarks>
        /// The accepted purposes are every purpose, e-mail protection, code signing, document
        /// signing (RFC 9336) and Adobe's and Microsoft's document-signing purposes. Without the
        /// check a certificate issued for a web server signs a document under any anchor that also
        /// issues web-server certificates. Set to <c>false</c> for a private PKI whose signing
        /// certificates name other purposes.
        /// </remarks>
        public bool RequireSignerKeyPurpose { get; set; } = true;

        /// <summary>
        /// Maximum age of embedded revocation data that has no <c>nextUpdate</c> field, measured
        /// from its <c>thisUpdate</c> to the assessment time (default 365 days). Data older than
        /// this yields <see cref="PdfRevocationStatus.Unknown"/> rather than
        /// <see cref="PdfRevocationStatus.Good"/>. Set to <see cref="TimeSpan.Zero"/> to skip the
        /// age check.
        /// </summary>
        /// <remarks>
        /// The LTV embedder applies the default to a CRL it is about to write, so a document it
        /// builds does not carry a CRL that default verification reads as unknown.
        /// </remarks>
        public TimeSpan MaxRevocationDataAge { get; set; } = Cms.RevocationFreshness.DefaultMaxAgeWithoutNextUpdate;

        /// <summary>
        /// Tolerance applied to every revocation-data time comparison, to absorb clock differences
        /// between the responder and this host (default 5 minutes — the LTV embedder's too).
        /// </summary>
        public TimeSpan ClockSkew { get; set; } = Cms.RevocationFreshness.DefaultClockSkew;

        /// <summary>
        /// When <c>true</c> (the default), revocation status is also resolved for the
        /// intermediate CA certificates in the signer's chain, not just the leaf. A revoked
        /// intermediate invalidates everything beneath it, so skipping it leaves a real hole in
        /// LTV validation.
        /// </summary>
        public bool CheckIntermediateRevocation { get; set; } = true;

        /// <summary>
        /// Treat a <c>/Contents</c> string whose padding is not all zeros as a validation error.
        /// Off by default — the fact is always reported on
        /// <c>SignatureVerificationResult.HasNonZeroContentsPadding</c> and as a warning.
        /// </summary>
        /// <remarks>
        /// <para><b>What the padding is.</b> A signature's <c>/Contents</c> is written at a fixed
        /// reserve and the CMS inside it is always shorter, so the remainder lies in the
        /// <c>/ByteRange</c> gap: excluded from the digest, and therefore writable after signing
        /// without breaking the signature. Every writer this library knows of zero-fills it.</para>
        ///
        /// <para><b>Why it is off by default.</b> Nothing in ISO 32000-1 requires the padding to be
        /// zeros, so a non-zero tail is evidence of an unusual producer as readily as of tampering
        /// — and a verifier that rejects on it would call other people's valid documents
        /// invalid, which is the more damaging error for a library whose answer a caller acts on.
        /// The signature itself is unaffected either way: the CMS is parsed from the DER object and
        /// the tail never reaches it.</para>
        ///
        /// <para><b>When to turn it on.</b> A workflow that controls its own producers, or one
        /// treating the gap as a covert channel to be closed rather than a quirk to be tolerated.
        /// It is the same judgement the oversized-gap check already makes unconditionally, moved
        /// one level finer — that check sees bytes <i>outside</i> the hex string, and this one
        /// sees bytes inside it, where no verifier was looking.</para>
        /// </remarks>
        public bool RejectNonZeroContentsPadding { get; set; }

        /// <summary>
        /// When <c>true</c>, a certificate whose revocation status the document's own <c>/DSS</c> does
        /// not settle — <see cref="PdfRevocationStatus.NotChecked"/> or
        /// <see cref="PdfRevocationStatus.Unknown"/> — is also asked about online: an OCSP request to
        /// the responder its Authority Information Access names, then its CRL distribution points,
        /// for the leaf and (under <see cref="CheckIntermediateRevocation"/>) each intermediate. Off
        /// by default: verification stays deterministic and offline unless a caller asks.
        /// </summary>
        /// <remarks>
        /// <para><b>What comes back is judged exactly as embedded data is</b> — the responder's
        /// signature, the CRL's issuer and signature, the single response's subject, a CRL's scope,
        /// freshness — through the same functions, so an online "good" is held to the same standard
        /// as an embedded one. Online data is current, so it is judged at the current time, not at a
        /// timestamp's: it answers whether the certificate is revoked <i>now</i>, and the result says
        /// so in a warning. Nothing fetched is written to the document; a caller that wants the
        /// evidence kept embeds it with the LTV tools.</para>
        /// <para><b>The fetch is the LTV embedder's</b>: the URLs come from the untrusted certificate
        /// and go through the private-address guard, redirects are re-checked per hop, the body is
        /// capped, the whole fetch runs under the revocation budget, and a credential rides only
        /// when host-scoped (CLAUDE.md, SSRF). A fetch that fails leaves the status as the document
        /// left it and adds a warning; it never makes a signature invalid on its own.</para>
        /// </remarks>
        public bool OnlineRevocation { get; set; }

        /// <summary>
        /// Network settings for <see cref="OnlineRevocation"/>'s fetches — credentials (host-scoped
        /// only reach a revocation host), proxy, timeout. Null is the defaults.
        /// </summary>
        public SigningNetworkOptions? Network { get; set; }

        /// <summary>Returns <paramref name="options"/>, or <see cref="Default"/> when null.</summary>
        internal static SignatureVerificationOptions OrDefault(SignatureVerificationOptions? options)
            => options ?? Default;

        /// <summary>Timestamp anchors, falling back to the signer trust anchors.</summary>
        internal IEnumerable<X509Certificate>? EffectiveTimestampAnchors
            => TimestampTrustAnchors ?? TrustAnchors;
    }
}
